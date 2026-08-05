using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// The declared schema, read straight out of a .dacpac. A dacpac is a zip whose `model.xml` holds
/// every table as a `SqlTable` element, so DacFx is not needed to get at it -- and DacFx would not
/// run here anyway.
///
/// One of these exists whether or not a dacpac does: without one it declares nothing, so a lake
/// with a schema and a lake without answer the same questions.
sealed class DacpacSchema
{
    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    readonly Dictionary<string, List<Column>> columns = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string[]> keys = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Table, string Column), string> defaults = new();
    readonly Dictionary<string, string> views = new(StringComparer.OrdinalIgnoreCase);

    public DacpacSchema(Config config, ILogger<DacpacSchema> logger)
    {
        var path = config.Dacpac ?? Layer.FindDacpac(
            [.. config.Layers, .. config.Write is null ? [] : (string[])[config.Write]], logger);

        // A dacpac named and not there is a configuration mistake, not a missing optional file --
        // one that was merely not found by the layer scan simply leaves the schema undeclared.
        if (config.Dacpac is { Length: > 0 } named && !File.Exists(named))
            throw new DuckPgConfigurationException($"dacpac not found: {named}");

        if (path is not null) Read(path, logger);
    }

    public IReadOnlyCollection<string> Tables => columns.Keys;

    public List<Column>? Columns(string table) => columns.GetValueOrDefault(table);

    public string[] Key(string table) => keys.GetValueOrDefault(table) ?? [];

    /// The T-SQL a column defaults to, as the dacpac spells it -- `(getdate())`, `((0))`.
    public string? Default(string table, string column) => defaults.GetValueOrDefault((table, column));

    /// Each declared view and the query it stands for, in the dialect it was written in.
    public IReadOnlyDictionary<string, string> Views => views;

    void Read(string path, ILogger logger)
    {
        using var archive = ZipFile.OpenRead(path);
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
                "SqlDefaultConstraint" => ReadDefault(element),
                "SqlView" => ReadView(element),
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
            logger.LogWarning("{Count} {Kind} elements in {Path} say nothing this build recognises",
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

            declared.Add(new Column(columnName, DuckDbType(specifier)));
        }

        if (declared.Count == 0) return false;

        columns[name] = declared;
        return true;
    }

    bool ReadKey(XElement constraint)
    {
        var name = Unqualify(Reference(constraint, "DefiningTable"));
        if (name is null) return false;

        var key = Related(constraint, "ColumnSpecifications")
            .SelectMany(c => c.Elements(Dac + "Relationship")
                .Where(r => r.Attribute("Name")?.Value == "Column")
                .Descendants(Dac + "References"))
            .Select(r => Unqualify(r.Attribute("Name")?.Value))
            .OfType<string>()
            .ToArray();

        if (key.Length == 0) return false;

        keys[name] = key;
        return true;
    }

    /// A view is its query; the header it was declared with is not in the model to begin with.
    bool ReadView(XElement view)
    {
        if (Unqualify(view.Attribute("Name")?.Value) is not { } name) return false;
        if (Property(view, "QueryScript") is not { Length: > 0 } query) return false;

        views[name] = query;
        return true;
    }

    /// A default belongs to the column it names, and the name says which table that is.
    bool ReadDefault(XElement constraint)
    {
        if (Property(constraint, "DefaultExpressionScript") is not { Length: > 0 } expression) return false;
        if (Parts(Reference(constraint, "ForColumn")) is not [.., var table, var column]) return false;

        defaults[(table, column)] = expression;
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

        string? Property(string name) => DacpacSchema.Property(specifier, name);

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
