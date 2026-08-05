using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

/// The declared schema, read straight out of a .dacpac. A dacpac is a zip whose `model.xml` holds
/// every table as a `SqlTable` element, so DacFx is not needed to get at it -- and DacFx would not
/// run here anyway.
///
/// One of these exists whether or not a dacpac does: without one it declares nothing, so the
/// catalog asks the same questions either way.
sealed class DacpacSchema
{
    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    readonly Dictionary<string, List<Column>> columns = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string[]> keys = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Tables => columns.Keys;

    public List<Column>? Columns(string table) => columns.GetValueOrDefault(table);

    public string[] Key(string table) => keys.GetValueOrDefault(table) ?? [];

    public DacpacSchema(Config config, ILogger<DacpacSchema> logger)
    {
        var path = config.Dacpac ?? Layer.FindDacpac(
            [.. config.Layers, .. config.Write is null ? [] : (string[])[config.Write]], logger);

        if (path is not null) Read(path);
    }

    void Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var model = archive.GetEntry("model.xml")
            ?? throw new InvalidOperationException($"{path} has no model.xml");
        using var stream = model.Open();

        foreach (var element in XDocument.Load(stream).Descendants(Dac + "Element"))
            switch (element.Attribute("Type")?.Value)
            {
                case "SqlTable": ReadTable(element); break;
                case "SqlPrimaryKeyConstraint": ReadKey(element); break;
            }
    }

    void ReadTable(XElement table)
    {
        var name = Unqualify(table.Attribute("Name")?.Value);
        if (name is null) return;

        var declared = new List<Column>();
        foreach (var column in Related(table, "Columns"))
        {
            // Computed columns carry an expression rather than a type; nothing to publish from.
            if (column.Attribute("Type")?.Value != "SqlSimpleColumn") continue;
            if (Unqualify(column.Attribute("Name")?.Value) is not { } columnName) continue;
            if (Related(column, "TypeSpecifier").FirstOrDefault() is not { } specifier) continue;

            declared.Add(new Column(columnName, DuckDbType(specifier)));
        }

        if (declared.Count > 0) columns[name] = declared;
    }

    void ReadKey(XElement constraint)
    {
        var table = Related(constraint, "DefiningTable").FirstOrDefault()
            ?.Attribute("Name")?.Value ?? constraint.Elements(Dac + "Relationship")
            .FirstOrDefault(r => r.Attribute("Name")?.Value == "DefiningTable")
            ?.Descendants(Dac + "References").FirstOrDefault()?.Attribute("Name")?.Value;

        var name = Unqualify(table);
        if (name is null) return;

        var key = Related(constraint, "ColumnSpecifications")
            .SelectMany(c => c.Elements(Dac + "Relationship")
                .Where(r => r.Attribute("Name")?.Value == "Column")
                .Descendants(Dac + "References"))
            .Select(r => Unqualify(r.Attribute("Name")?.Value))
            .OfType<string>()
            .ToArray();

        if (key.Length > 0) keys[name] = key;
    }

    /// Elements reached through a named relationship, which in this format always nests as
    /// Relationship / Entry / Element.
    static IEnumerable<XElement> Related(XElement parent, string relationship) =>
        parent.Elements(Dac + "Relationship")
            .Where(r => r.Attribute("Name")?.Value == relationship)
            .Elements(Dac + "Entry")
            .Elements(Dac + "Element");

    /// Names are bracket-qualified: `[dbo].[TABLE].[Column]`. The last part is the one that matters.
    static string? Unqualify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var last = name.LastIndexOf('[');
        var end = name.IndexOf(']', last + 1);
        return last < 0 || end < 0 ? name : name[(last + 1)..end];
    }

    static string DuckDbType(XElement specifier)
    {
        var sql = Unqualify(specifier.Elements(Dac + "Relationship")
            .Where(r => r.Attribute("Name")?.Value == "Type")
            .Descendants(Dac + "References").FirstOrDefault()?.Attribute("Name")?.Value) ?? "nvarchar";

        string? Property(string name) => specifier.Elements(Dac + "Property")
            .FirstOrDefault(p => p.Attribute("Name")?.Value == name)?.Attribute("Value")?.Value;

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
