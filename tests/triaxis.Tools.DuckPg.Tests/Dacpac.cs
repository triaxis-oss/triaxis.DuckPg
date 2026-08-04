using System.IO.Compression;
using System.Xml.Linq;

namespace triaxis.Tools.DuckPg.Tests;

/// A dacpac is a zip holding a model.xml, so a test can write one without DacFx anywhere near it.
static class Dacpac
{
    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    public record TableModel(string Name, (string Column, string Type)[] Columns, string[] Key);

    public static void Write(string path, params TableModel[] tables)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var model = new XElement(Dac + "DataSchemaModel",
            new XElement(Dac + "Model",
                tables.Select(Element).Concat(tables.Where(t => t.Key.Length > 0).Select(Key))));

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var entry = archive.CreateEntry("model.xml").Open();
        new XDocument(model).Save(entry);
    }

    static XElement Element(TableModel table) =>
        El("SqlTable", $"[dbo].[{table.Name}]",
            Rel("Columns", [.. table.Columns.Select(c =>
                El("SqlSimpleColumn", $"[dbo].[{table.Name}].[{c.Column}]",
                    Rel("TypeSpecifier", El("SqlTypeSpecifier", null, Rel("Type", Ref($"[{c.Type}]"))))))]));

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
