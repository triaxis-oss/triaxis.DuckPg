using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// A declared reference: which columns of one table point at which columns of another, under the
/// name the constraint was given. `OnDelete` is what the schema says should happen to the rows
/// pointing at one that goes.
sealed record Reference(string Name, string Table, string[] Columns,
                        string Parent, string[] ParentColumns, string OnDelete);

/// Declared uniqueness that is not the key: which columns of which table, under the name the
/// constraint or the index was given.
sealed record Unique(string Name, string Table, string[] Columns);

/// A declared scalar function: what it is called, what it takes in order, what it returns, and the
/// body it was written with. The `CREATE FUNCTION` header is not in the model at all -- `BodyScript`
/// holds the `BEGIN … END` alone -- so the parameters and the return type come from the model's own
/// relationships rather than from any text.
sealed record ScalarFunction(string Name, string[] Parameters, string ReturnType, string Body);

/// The declared schema, read straight out of a .dacpac. A dacpac is a zip whose `model.xml` holds
/// every table as a `SqlTable` element, so DacFx is not needed to get at it -- and DacFx would not
/// run here anyway.
///
/// One of these exists whether or not a dacpac does: without one it declares nothing, so a lake
/// with a schema and a lake without answer the same questions.
///
/// What a file declares is read once for the process and shared by every lake over that file. A
/// fleet of lakes over one schema -- one per exported database, say -- would otherwise parse the
/// same model on every start: a real model is several MB of XML, tens of milliseconds to read and
/// tens of MB of `XDocument` to collect afterwards, for an answer the first lake already has. The
/// reading is keyed by the file's bytes, so a dacpac rebuilt between two starts is read again.
sealed class DacpacSchema
{
    static readonly ConcurrentDictionary<string, (byte[] Hash, DacpacModel Model)> models =
        new(StringComparer.Ordinal);

    readonly DacpacModel model;

    public DacpacSchema(Config config, ILogger<DacpacSchema> logger)
    {
        var path = config.Dacpac ?? Layer.FindDacpac(
            [.. config.Layers, .. config.Write is null ? [] : (string[])[config.Write]], logger);

        // A dacpac named and not there is a configuration mistake, not a missing optional file --
        // one that was merely not found by the layer scan simply leaves the schema undeclared.
        if (config.Dacpac is { Length: > 0 } named && !File.Exists(named))
            throw new DuckPgConfigurationException($"dacpac not found: {named}");

        model = path is null ? new DacpacModel() : Shared(path, logger);
    }

    public IReadOnlyCollection<string> Tables => model.Columns.Keys;

    public List<Column>? Columns(string table) => model.Columns.GetValueOrDefault(table);

    public string[] Key(string table) => model.Keys.GetValueOrDefault(table) ?? [];

    /// The T-SQL a column defaults to, as the dacpac spells it -- `(getdate())`, `((0))`.
    public string? Default(string table, string column) =>
        model.Defaults.GetValueOrDefault((table, column));

    /// What the declared schema says points at what.
    public IReadOnlyList<Reference> References => model.References;

    /// Every uniqueness rule it declares that is not a table's key.
    public IReadOnlyList<Unique> Uniques => model.Uniques;

    /// The scalar functions it declares, in the order the model lists them.
    public IReadOnlyList<ScalarFunction> Functions => model.Functions;

    /// Each declared view and the query it stands for, in the dialect it was written in.
    public IReadOnlyDictionary<string, string> Views => model.Views;

    /// The file's reading, made once. The bytes are hashed rather than the file stamped: a model
    /// is a fraction of a millisecond to hash and tens to parse, and a stamp can miss a rewrite
    /// that lands within its granularity. One reading a path, so a file rebuilt in place replaces
    /// its own rather than growing the process. Two lakes starting at once may both read it, and
    /// either reading is the file's.
    static DacpacModel Shared(string path, ILogger logger)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        if (models.TryGetValue(path, out var held) && held.Hash.AsSpan().SequenceEqual(hash))
            return held.Model;

        var model = DacpacModel.Read(bytes, path, logger);
        models[path] = (hash, model);
        return model;
    }
}

/// What one file declares. Filled as it is read and never afterwards, which is what lets every
/// lake over the file hold the same one.
sealed class DacpacModel
{
    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    public readonly Dictionary<string, List<Column>> Columns = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string[]> Keys = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<(string Table, string Column), string> Defaults = new();
    public readonly Dictionary<string, string> Views = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<Reference> References = [];
    public readonly List<Unique> Uniques = [];
    public readonly List<ScalarFunction> Functions = [];

    public static DacpacModel Read(byte[] bytes, string path, ILogger logger)
    {
        var model = new DacpacModel();
        model.Read(new MemoryStream(bytes), path, logger);
        return model;
    }

    void Read(Stream file, string path, ILogger logger)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var model = archive.GetEntry("model.xml")
            ?? throw new InvalidOperationException($"{path} has no model.xml");
        using var stream = model.Open();

        var unread = new Dictionary<string, int>();
        foreach (var element in XDocument.Load(stream).Descendants(Dac + "Element"))
        {
            var type = element.Attribute("Type")?.Value;
            var read = type switch
            {
                "SqlTable" => ReadTable(element),
                "SqlPrimaryKeyConstraint" => ReadKey(element),
                "SqlUniqueConstraint" => ReadUnique(element),
                "SqlIndex" => ReadIndex(element),
                "SqlDefaultConstraint" => ReadDefault(element),
                "SqlView" => ReadView(element),
                "SqlForeignKeyConstraint" => ReadReference(element),
                "SqlScalarFunction" => ReadFunction(element),
                // Every other element is something this tool does not claim to read.
                _ => true,
            };
            if (!read) unread[type!] = unread.GetValueOrDefault(type!) + 1;
        }

        // An element of a kind this tool does read, that it then made nothing of, is worth saying
        // out loud: the shape of the format is the one thing here that is not in this repository's
        // gift, and a property DacFx spells differently is otherwise indistinguishable from a
        // schema that declares none.
        foreach (var (kind, count) in unread)
            logger.LogWarning("{Count} {Kind} elements in {Path} say nothing this build recognizes",
                count, kind, path);
    }

    bool ReadTable(XElement table)
    {
        var name = Unqualify(table.Attribute("Name")?.Value);
        if (name is null) return false;

        var declared = new List<Column>();
        foreach (var column in Related(table, "Columns"))
        {
            // Computed columns carry an expression rather than a type; nothing to publish from.
            if (column.Attribute("Type")?.Value != "SqlSimpleColumn") continue;
            if (Unqualify(column.Attribute("Name")?.Value) is not { } columnName) continue;
            if (Related(column, "TypeSpecifier").FirstOrDefault() is not { } specifier) continue;

            declared.Add(new Column(columnName, DuckDbType(specifier),
                                    Identity: Property(column, "IsIdentity") == "True"));
        }

        if (declared.Count == 0) return false;

        Columns[name] = declared;
        return true;
    }

    bool ReadKey(XElement constraint)
    {
        var name = Unqualify(Reference(constraint, "DefiningTable"));
        if (name is null) return false;

        var key = Indexed(constraint);
        if (key.Length == 0) return false;

        Keys[name] = key;
        return true;
    }

    /// A `UNIQUE` constraint, which the model writes exactly as it writes the key -- the same column
    /// specifications under the same relationship, differing only in the element's type.
    bool ReadUnique(XElement constraint) => ReadUnique(constraint, "DefiningTable");

    /// A unique index says the same thing about the rows as a `UNIQUE` constraint, so it is read as
    /// one. A plain index says nothing about them, and DacFx leaves the property out rather than
    /// writing it false -- so an absent one is not unique, and is understood rather than unread.
    bool ReadIndex(XElement index) =>
        Property(index, "IsUnique") != "True" || ReadUnique(index, "IndexedObject");

    /// An index carries the table in its own name and a constraint does not, so which relationship
    /// names the table is the only thing the two differ by.
    bool ReadUnique(XElement element, string relationship)
    {
        var table = Unqualify(Reference(element, relationship));
        var columns = Indexed(element);
        if (table is null || columns.Length == 0) return false;

        Uniques.Add(new Unique(Unqualify(element.Attribute("Name")?.Value) ?? $"UQ_{table}", table, columns));
        return true;
    }

    /// The columns a key, a unique constraint or an index is over, in the order the model lists them.
    static string[] Indexed(XElement element) =>
        [.. Related(element, "ColumnSpecifications")
            .SelectMany(c => c.Elements(Dac + "Relationship")
                .Where(r => r.Attribute("Name")?.Value == "Column")
                .Descendants(Dac + "References"))
            .Select(r => Unqualify(r.Attribute("Name")?.Value))
            .OfType<string>()];

    /// A declared reference: which columns of which table point at which columns of another, and the
    /// name the constraint was given -- an application matching on the message it fails with reads
    /// that name, so it is carried rather than made up.
    bool ReadReference(XElement constraint)
    {
        var child = Unqualify(Reference(constraint, "DefiningTable"));
        var parent = Unqualify(Reference(constraint, "ForeignTable"));
        if (child is null || parent is null) return false;

        var columns = Referenced(constraint, "Columns");
        var referenced = Referenced(constraint, "ForeignColumns");
        if (columns.Length == 0 || columns.Length != referenced.Length) return false;

        References.Add(new Reference(
            Unqualify(constraint.Attribute("Name")?.Value) ?? $"FK_{child}_{parent}",
            child, columns, parent, referenced,
            DeleteAction(Property(constraint, "OnDeleteAction"))));
        return true;
    }

    /// DacFx numbers the action and leaves the property out altogether when it is the default, so
    /// an absent one is `NO ACTION` -- which is why reading it by the wrong name looked like a
    /// schema where nothing cascades rather than like a schema that was not read.
    /// An unrecognized code is carried as itself: what a lake will not do it should at least name.
    static string DeleteAction(string? code) => code switch
    {
        null or "0" => "NoAction",
        "1" => "Cascade",
        "2" => "SetNull",
        "3" => "SetDefault",
        _ => $"OnDeleteAction={code}",
    };

    static string[] Referenced(XElement constraint, string relationship) =>
        [.. Related(constraint, relationship)
            .Concat(constraint.Elements(Dac + "Relationship")
                .Where(r => r.Attribute("Name")?.Value == relationship)
                .Descendants(Dac + "References"))
            .Select(element => Unqualify(element.Attribute("Name")?.Value))
            .OfType<string>()];

    /// A view is its query; the header it was declared with is not in the model to begin with.
    bool ReadView(XElement view)
    {
        if (Unqualify(view.Attribute("Name")?.Value) is not { } name) return false;
        if (Property(view, "QueryScript") is not { Length: > 0 } query) return false;

        Views[name] = query;
        return true;
    }

    /// A default belongs to the column it names, and the name says which table that is.
    bool ReadDefault(XElement constraint)
    {
        if (Property(constraint, "DefaultExpressionScript") is not { Length: > 0 } expression) return false;
        if (Parts(Reference(constraint, "ForColumn")) is not [.., var table, var column]) return false;

        Defaults[(table, column)] = expression;
        return true;
    }

    bool ReadFunction(XElement function)
    {
        if (Unqualify(function.Attribute("Name")?.Value) is not { } name) return false;
        if (Related(function, "FunctionBody").FirstOrDefault() is not { } implementation) return false;
        if (Property(implementation, "BodyScript") is not { Length: > 0 } body) return false;

        // A parameter is named `[dbo].[FullNote].[@note]`, and the order the model lists them in is
        // the order they were declared in -- there is no ordinal to sort by.
        var parameters = Related(function, "Parameters")
            .Where(p => p.Attribute("Type")?.Value == "SqlSubroutineParameter")
            .Select(p => Unqualify(p.Attribute("Name")?.Value)?.TrimStart('@'))
            .OfType<string>()
            .ToArray();

        // The specifier under the function's own `Type` is what it returns; the ones under its
        // parameters belong to them, which is why only a direct child will do.
        var returns = Related(function, "Type").FirstOrDefault() is { } specifier
            ? DuckDbType(specifier)
            : "VARCHAR";

        Functions.Add(new ScalarFunction(name, parameters, returns, body));
        return true;
    }


    /// Elements reached through a named relationship, which in this format always nests as
    /// Relationship / Entry / Element.
    static IEnumerable<XElement> Related(XElement parent, string relationship) =>
        parent.Elements(Dac + "Relationship")
            .Where(r => r.Attribute("Name")?.Value == relationship)
            .Elements(Dac + "Entry")
            .Elements(Dac + "Element");

    /// What a relationship points at, whether it holds the element itself or a reference to one.
    static string? Reference(XElement parent, string relationship) =>
        Related(parent, relationship).FirstOrDefault()?.Attribute("Name")?.Value
        ?? parent.Elements(Dac + "Relationship")
            .Where(r => r.Attribute("Name")?.Value == relationship)
            .Descendants(Dac + "References").FirstOrDefault()?.Attribute("Name")?.Value;

    /// A property is an attribute when it is a word and a nested `Value` when it is a script.
    static string? Property(XElement element, string name) =>
        element.Elements(Dac + "Property").FirstOrDefault(p => p.Attribute("Name")?.Value == name) is not { } property
            ? null
            : property.Attribute("Value")?.Value ?? property.Element(Dac + "Value")?.Value;

    /// Names are bracket-qualified: `[dbo].[TABLE].[Column]`.
    static List<string> Parts(string? name)
    {
        var parts = new List<string>();
        var at = 0;
        while (name is not null && (at = name.IndexOf('[', at)) >= 0)
        {
            var end = name.IndexOf(']', at + 1);
            if (end < 0) break;
            parts.Add(name[(at + 1)..end]);
            at = end + 1;
        }
        return parts;
    }

    /// The last part is the one that matters -- and a name that brackets nothing is already it.
    static string? Unqualify(string? name) =>
        string.IsNullOrEmpty(name) ? null : Parts(name) is [.., var last] ? last : name;

    static string DuckDbType(XElement specifier)
    {
        var sql = Unqualify(specifier.Elements(Dac + "Relationship")
            .Where(r => r.Attribute("Name")?.Value == "Type")
            .Descendants(Dac + "References").FirstOrDefault()?.Attribute("Name")?.Value) ?? "nvarchar";

        string? Property(string name) => DacpacModel.Property(specifier, name);

        return sql.ToLowerInvariant() switch
        {
            "bit" => "BOOLEAN",
            "tinyint" => "UTINYINT",
            "smallint" => "SMALLINT",
            "int" => "INTEGER",
            "bigint" => "BIGINT",
            "real" => "FLOAT",
            "float" => "DOUBLE",
            "money" => "DECIMAL(19,4)",
            "smallmoney" => "DECIMAL(10,4)",
            "decimal" or "numeric" => $"DECIMAL({Property("Precision") ?? "18"},{Property("Scale") ?? "0"})",
            "date" => "DATE",
            "time" => "TIME",
            "datetime" or "datetime2" or "smalldatetime" => "TIMESTAMP",
            "datetimeoffset" => "TIMESTAMPTZ",
            "uniqueidentifier" => "UUID",
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => "BLOB",
            _ => "VARCHAR", // char/nchar/varchar/nvarchar/text/ntext/xml/sql_variant
        };
    }
}
