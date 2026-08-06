using System.IO.Compression;
using System.Xml.Linq;

namespace triaxis.DuckPg.Tests;

/// A dacpac is a zip holding a model.xml, so a test can write one without DacFx anywhere near it.
static class Dacpac
{
    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    public record TableModel(string Name, (string Column, string Type)[] Columns, string[] Key,
                            (string Column, string Expression)[]? Defaults = null,
                            string[]? Identity = null);

    /// A view is modelled as its query alone -- the `CREATE VIEW` header never reaches model.xml.
    public record ViewModel(string Name, string Query);

    /// What one table's columns say about another's, as DacFx writes a foreign key.
    public record ReferenceModel(string Name, string Table, string[] Columns, string Parent, string[] ParentColumns,
                                 string OnDelete = "NoAction");

    public static void Write(string path, params TableModel[] tables) => Write(path, tables, []);

    public static void Write(string path, TableModel[] tables, params ViewModel[] views) =>
        Write(path, tables, views, []);

    public static void Write(string path, TableModel[] tables, ViewModel[] views, ReferenceModel[] references)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var model = new XElement(Dac + "DataSchemaModel",
            new XElement(Dac + "Model",
                tables.Select(Element)
                      .Concat(tables.Where(t => t.Key.Length > 0).Select(Key))
                      .Concat(tables.SelectMany(Defaults))
                      .Concat(views.Select(View))
                      .Concat(references.Select(Reference))));

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var entry = archive.CreateEntry("model.xml").Open();
        new XDocument(model).Save(entry);
    }

    static XElement Element(TableModel table) =>
        El("SqlTable", $"[dbo].[{table.Name}]",
            Rel("Columns", [.. table.Columns.Select(c =>
                El("SqlSimpleColumn", $"[dbo].[{table.Name}].[{c.Column}]",
                    Identity(table, c.Column),
                    Rel("TypeSpecifier", El("SqlTypeSpecifier", null, Rel("Type", Ref($"[{c.Type}]"))))))]));

    /// A store-generated column says so with a property of its own, as DacFx writes it.
    static object[] Identity(TableModel table, string column) =>
        table.Identity?.Contains(column) == true
            ? [new XElement(Dac + "Property", new XAttribute("Name", "IsIdentity"), new XAttribute("Value", "True"))]
            : [];

    static XElement View(ViewModel view) =>
        El("SqlView", $"[dbo].[{view.Name}]", Script("QueryScript", view.Query));

    /// A default is its own element, pointing back at the column it belongs to.
    static IEnumerable<XElement> Defaults(TableModel table) =>
        (table.Defaults ?? []).Select(d =>
            El("SqlDefaultConstraint", $"[dbo].[DF_{table.Name}_{d.Column}]",
                Script("DefaultExpressionScript", d.Expression),
                Rel("DefiningTable", Ref($"[dbo].[{table.Name}]")),
                Rel("ForColumn", Ref($"[dbo].[{table.Name}].[{d.Column}]"))));

    /// DacFx writes a script as a nested `Value`, in CDATA -- not as a property attribute.
    static XElement Script(string name, string sql) =>
        new(Dac + "Property", new XAttribute("Name", name), new XElement(Dac + "Value", new XCData(sql)));

    /// A foreign key names the table it defines on, the columns it is over, and the table and
    /// columns it points at.
    /// DacFx numbers the delete action and omits the property when it is the default, so a model a
    /// test writes has to do the same -- a helper that spelled it out would test nothing.
    static readonly string[] DeleteActions = ["NoAction", "Cascade", "SetNull", "SetDefault"];

    static XElement Reference(ReferenceModel reference) =>
        El("SqlForeignKeyConstraint", $"[dbo].[{reference.Name}]",
            Array.IndexOf(DeleteActions, reference.OnDelete) is var action and > 0
                ? [new XElement(Dac + "Property", new XAttribute("Name", "OnDeleteAction"),
                                new XAttribute("Value", action.ToString()))]
                : (object[])[],
            Rel("DefiningTable", Ref($"[dbo].[{reference.Table}]")),
            Rel("ForeignTable", Ref($"[dbo].[{reference.Parent}]")),
            Rel("Columns", [.. reference.Columns.Select(c => Ref($"[dbo].[{reference.Table}].[{c}]"))]),
            Rel("ForeignColumns", [.. reference.ParentColumns.Select(c => Ref($"[dbo].[{reference.Parent}].[{c}]"))]));

    static XElement Key(TableModel table) =>
        El("SqlPrimaryKeyConstraint", $"[dbo].[PK_{table.Name}]",
            Rel("DefiningTable", Ref($"[dbo].[{table.Name}]")),
            Rel("ColumnSpecifications", [.. table.Key.Select(k =>
                El("SqlIndexedColumnSpecification", null, Rel("Column", Ref($"[dbo].[{table.Name}].[{k}]"))))]));

    /// Everything in this format is an Element reached through a named Relationship, one Entry deep.
    static XElement El(string type, string? name, params object[] content) =>
        new(Dac + "Element", new XAttribute("Type", type),
            name is null ? null : new XAttribute("Name", name), content);

    static XElement Rel(string name, params XElement[] entries) =>
        new(Dac + "Relationship", new XAttribute("Name", name),
            entries.Select(e => new XElement(Dac + "Entry", e)));

    static XElement Ref(string name) => new(Dac + "References", new XAttribute("Name", name));
}
