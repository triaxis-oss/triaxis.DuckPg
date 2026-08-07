namespace triaxis.DuckPg.Tests;

/// What a lake says about the shape it publishes. A YAML or JSON file whose root is a mapping is
/// one row, which is the documented rule and stays the rule -- but it is also what the two most
/// common mistakes look like, and those are indistinguishable from the intended thing at runtime.
/// So nothing here changes what is published; all of it is signal.
public class ShapeTests : IDisposable
{
    readonly TestLake lake = new();

    public void Dispose() => lake.Dispose();

    List<string> Warnings => [.. lake.Logged.Where(m => m.StartsWith("Warning:"))];

    string Shape(string table) => lake.Catalog.Tables[table].Layers[0].Shape;

    // ---- the wrapper key -------------------------------------------------------------------------

    /// The rows written under a top-level key naming the table: one row, one column holding the
    /// whole list, and no way to tell that from a table that really has one list column.
    [Fact]
    public void AWrapperKeyIsWarnedAbout()
    {
        lake.Yaml("base", "widgets", """
            widgets:
              - id: 1
                name: Sprocket
              - id: 2
                name: Cog
            """).Stack("base").Start();

        var warning = Assert.Single(Warnings);
        Assert.Contains("widgets.yaml", warning);
        Assert.Contains("'widgets'", warning);
        Assert.Contains("a list of 2 objects", warning);
    }

    /// One of them is one object, which is the count the shape most often has.
    [Fact]
    public void AWrapperKeyOverOneObjectCountsItInTheSingular()
    {
        lake.Yaml("base", "widgets", """
            widgets:
              - id: 1
            """).Stack("base").Start();

        Assert.Contains("a list of 1 object:", Assert.Single(Warnings));
    }

    /// And what it publishes is exactly what it published before anything warned: one row of one
    /// column, which is what the rule says a mapping root is.
    [Fact]
    public void AWrapperKeyStillPublishesTheSingleRow()
    {
        lake.Yaml("base", "widgets", """
            widgets:
              - id: 1
                name: Sprocket
            """).Stack("base").Start();

        Assert.Equal(["widgets"], lake.Columns("lake.widgets").Select(c => c.Split(':')[0]));
        Assert.Single(lake.Query("SELECT * FROM lake.widgets"));
    }

    /// JSON is written the same way and read the same way, so it is warned about the same way.
    [Fact]
    public void AWrapperKeyInJsonIsWarnedAboutToo()
    {
        lake.Json("base", "widgets", """{"widgets": [{"id": 1}, {"id": 2}]}""").Stack("base").Start();

        Assert.Contains("widgets.json", Assert.Single(Warnings));
    }

    /// A row that really is one row says nothing: its columns are its own, and it is the shape an
    /// object-rooted JSON file has always had.
    [Fact]
    public void AGenuineSingleRowDoesNotWarn()
    {
        lake.Yaml("base", "settings", """
            id: 1
            name: Sprocket
            """).Stack("base").Start();

        Assert.Empty(Warnings);
        Assert.Equal(["1|Sprocket"], lake.Query("SELECT id, name FROM lake.settings"));
    }

    /// Including one whose only column is a list -- of anything but objects, which is the whole of
    /// what separates this from the mistake above.
    [Fact]
    public void ASingleRowHoldingAListOfScalarsDoesNotWarn()
    {
        lake.Yaml("base", "settings", """
            tags:
              - one
              - two
            """).Stack("base").Start();

        Assert.Empty(Warnings);
    }

    /// Nor does the ordinary shape.
    [Fact]
    public void ASequenceOfRowsDoesNotWarn()
    {
        lake.Yaml("base", "widgets", """
            - id: 1
              name: Sprocket
            - id: 2
              name: Cog
            """).Stack("base").Start();

        Assert.Empty(Warnings);
    }

    // ---- named rows with nowhere to put the name -------------------------------------------------

    /// A file written as one mapping entry per row needs a single key column for the entry keys to
    /// fill. Without one the same document is a single row whose columns are the entry names, which
    /// is the rule -- and is never what someone who wrote that file meant.
    [Fact]
    public void NamedRowsWithNoKeyColumnAreWarnedAbout()
    {
        lake.Yaml("base", "things", """
            test1:
              foo: 2
            test2:
              foo: 4
            """).Stack("base").Start();

        var warning = Assert.Single(Warnings);
        Assert.Contains("things.yaml", warning);
        Assert.Contains("things has no single key column", warning);
    }

    /// A composite key is no key for this: a mapping key is one value and cannot be two.
    [Fact]
    public void ACompositeKeyIsNoKeyForNamedRows()
    {
        lake.Yaml("base", "things", """
            test1:
              foo: 2
            """).Stack("base");
        lake.Config.DefaultKey = ["id", "foo"];
        lake.Start();

        Assert.Contains("no single key column", Assert.Single(Warnings));
    }

    /// With a key there is nothing wrong: the entries become rows, which is what the file says.
    [Fact]
    public void NamedRowsWithAKeyDoNotWarn()
    {
        lake.Yaml("base", "things", """
            test1:
              foo: 2
            test2:
              foo: 4
            """).Stack("base");
        lake.Config.DefaultKey = ["id"];
        lake.Start();

        Assert.Empty(Warnings);
        Assert.Equal(["test1|2", "test2|4"], lake.Query("SELECT id, foo FROM lake.things ORDER BY id"));
    }

    // ---- nothing at all --------------------------------------------------------------------------

    /// A lake with no layers serves, and answers every query with "no such table". Which directory
    /// was never named is not in that answer anywhere.
    [Fact]
    public void ALakeWithNoLayersAtAllIsWarnedAbout()
    {
        lake.Start();

        Assert.Contains("no layer directory was given", Assert.Single(Warnings));
        Assert.Empty(lake.Catalog.Tables);
    }

    /// A directory that was named and holds nothing is the same lake with a better reason.
    [Fact]
    public void AnEmptyLayerDirectorySaysWhichOne()
    {
        lake.Stack("base").Start();

        var warning = Assert.Single(Warnings);
        Assert.Contains("nothing was found in", warning);
        Assert.Contains(lake.At("base"), warning);
    }

    /// A lake that publishes something says nothing about being empty.
    [Fact]
    public void ALakeWithTablesDoesNotWarn()
    {
        lake.Yaml("base", "orders", "- id: 1\n").Stack("base").Start();

        Assert.Empty(Warnings);
    }

    // ---- the shape on the startup line -----------------------------------------------------------

    /// The line a lake prints as it starts carries what each layer turned out to publish. Nothing
    /// here is a guess, which is why it catches what no check anticipated.
    [Fact]
    public void ALayerSaysWhatItPublishes()
    {
        lake.Yaml("base", "widgets", """
            widgets:
              - id: 1
                name: Sprocket
            """)
            .Yaml("base", "orders", """
                - id: 1
                  amount: 10
                - id: 2
                  amount: 20
                """)
            .Stack("base").Start();

        Assert.Equal("(1 row, 1 column: widgets)", Shape("widgets"));
        Assert.Equal("(2 rows, 2 columns: id, amount)", Shape("orders"));
    }

    /// Past a handful the names are noise and only the count is worth reading.
    [Fact]
    public void AWideLayerIsCountedRatherThanNamed()
    {
        lake.Yaml("base", "wide", "- a: 1\n  b: 2\n  c: 3\n  d: 4\n").Stack("base").Start();

        Assert.Equal("(1 row, 4 columns)", Shape("wide"));
    }

    /// A parquet layer is not counted: it is scanned where it lies, and reading a lake's footers to
    /// answer a log line is not what starting up is for.
    [Fact]
    public void AParquetLayerSaysItsColumnsAndNotItsRows()
    {
        lake.Parquet("base", "orders", "SELECT 1 AS id, 10 AS amount").Stack("base").Start();

        Assert.Equal("(2 columns: id, amount)", Shape("orders"));
    }
}
