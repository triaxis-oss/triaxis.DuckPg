namespace triaxis.DuckPg.Tests;

/// A YAML or JSON layer written as one mapping entry per row -- the row's key as the entry's key,
/// the rest of the row as its value -- rather than as a sequence of rows. It is the same table
/// either way; what it saves is repeating the key column on every row of a hand-written layer.
public class KeyedLayerTests : IDisposable
{
    readonly TestLake lake = new TestLake("keyed");

    public void Dispose() => lake.Dispose();

    TestLake Keyed(string extension = "yaml", string content = """
        test1:
          foo: 2
          bar: 3
        test2:
          foo: 4
          bar: 6
        """)
    {
        lake.Text("base", $"things.{extension}", content).Stack("base");
        lake.Config.DefaultKey = ["id"];
        return lake.Start();
    }

    /// The mapping keys become the key column, and the entries' own fields the rest of the row.
    [Fact]
    public void TheMappingKeysBecomeTheKeyColumn()
    {
        Keyed();

        Assert.Equal(["test1|2|3", "test2|4|6"],
                     lake.Query("SELECT id, foo, bar FROM lake.things ORDER BY id"));
    }

    /// JSON says the same thing, and DuckDB would otherwise read the document as one row whose
    /// columns are the keys.
    [Fact]
    public void JsonSaysTheSameThing()
    {
        Keyed("json", """{"test1": {"foo": 2, "bar": 3}, "test2": {"foo": 4, "bar": 6}}""");

        Assert.Equal(["test1|2|3", "test2|4|6"],
                     lake.Query("SELECT id, foo, bar FROM lake.things ORDER BY id"));
    }

    /// It is a layer like any other: a keyed one above a sequence one shadows it by key, which is
    /// the whole point of the key being a real column rather than a name.
    [Fact]
    public void AKeyedLayerStacksOnASequenceOne()
    {
        lake.Yaml("base", "things", """
            - id: test1
              foo: 1
            - id: test3
              foo: 9
            """)
            .Text("top", "things.yaml", """
                test1:
                  foo: 99
                test2:
                  foo: 4
                """)
            .Stack("base", "top");
        lake.Config.DefaultKey = ["id"];
        lake.Start();

        Assert.Equal(["test1|99", "test2|4", "test3|9"],
                     lake.Query("SELECT id, foo FROM lake.things ORDER BY id"));
    }

    /// A key written plain is typed like any other plain scalar, so an integer key is an integer --
    /// which is what lets it match the same row in a layer written as a sequence.
    [Fact]
    public void APlainKeyIsTypedLikeAnyOtherScalar()
    {
        lake.Text("base", "things.yaml", """
            1:
              foo: a
            2:
              foo: b
            """).Stack("base");
        lake.Config.DefaultKey = ["id"];
        lake.Start();

        Assert.Equal(["BIGINT"], lake.Query(
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema = 'lake' AND table_name = 'things' AND column_name = 'id'"));
        Assert.Equal(["1|a", "2|b"], lake.Query("SELECT id, foo FROM lake.things ORDER BY id"));
    }

    /// And a quoted one is text, which is what keeps an id like `007` from turning into a number --
    /// the same rule every other YAML scalar is read by.
    [Fact]
    public void AQuotedKeyIsText()
    {
        lake.Text("base", "things.yaml", """
            "007":
              foo: a
            """).Stack("base");
        lake.Config.DefaultKey = ["id"];
        lake.Start();

        Assert.Equal(["007"], lake.Query("SELECT id FROM lake.things"));
    }

    /// JSON has no way to write a mapping key that is not a string, so there the quoting says
    /// nothing and the text is what the key is read from. Otherwise every keyed JSON layer would
    /// have a text key, and an id of 1 would not match the same row in the layer beneath it.
    [Fact]
    public void AJsonKeyIsReadFromItsTextBecauseJsonCannotSayOtherwise()
    {
        Keyed("json", """{"1": {"foo": "a"}, "2": {"foo": "b"}}""");

        Assert.Equal(["1|a", "2|b"], lake.Query("SELECT id, foo FROM lake.things ORDER BY id"));
    }

    /// The entry's key is what the file was organized by, so a row repeating it inside itself is a
    /// second answer to a question already answered, and the first one stands.
    [Fact]
    public void TheMappingKeyWinsOverOneWrittenInsideTheRow()
    {
        Keyed(content: """
            test1:
              id: something else
              foo: 2
            """);

        Assert.Equal(["test1|2"], lake.Query("SELECT id, foo FROM lake.things"));
    }
}

/// What is *not* keyed, and stays what it always was. A mapping root only names rows where the table
/// has one column for the names to fill and every value is itself a mapping; anything else is the
/// single row an object-rooted JSON file has always been.
public class UnkeyedMappingTests : IDisposable
{
    readonly TestLake lake = new TestLake("unkeyed");

    public void Dispose() => lake.Dispose();

    /// With no key declared there is no column for the mapping keys, so the document is one row.
    [Fact]
    public void WithoutAKeyAMappingIsASingleRow()
    {
        lake.Text("base", "things.json", """{"id": 1, "foo": 2}""").Stack("base").Start();

        Assert.Equal(["1|2"], lake.Query("SELECT id, foo FROM lake.things"));
    }

    /// A composite key cannot come out of a mapping key, which is one value.
    [Fact]
    public void ACompositeKeyLeavesTheMappingAlone()
    {
        lake.Text("base", "things.yaml", """
            test1:
              foo: 2
            """).Stack("base");
        lake.Config.DefaultKey = ["db", "id"];
        lake.Start();

        // Read as one row whose only column is the mapping's own key.
        Assert.Equal(["2"], lake.Query("SELECT test1.foo FROM lake.things"));
    }

    /// A value that is not a mapping means the document is a row whose columns happen to include a
    /// nested one -- which is what an object-rooted JSON file has always been read as.
    [Fact]
    public void AScalarAmongTheValuesMeansASingleRow()
    {
        lake.Text("base", "things.json", """{"id": 1, "nested": {"foo": 2}}""").Stack("base");
        lake.Config.DefaultKey = ["id"];
        lake.Start();

        Assert.Equal(["1|2"], lake.Query("SELECT id, nested.foo FROM lake.things"));
    }
}

/// A file read as mapping entries is written back as mapping entries. The shape belongs to the file,
/// and a lake that quietly rewrote one as a sequence would have changed something nobody asked it to.
public class KeyedWriteTests : IDisposable
{
    readonly TestLake lake = new TestLake("keyed-writes");

    public void Dispose() => lake.Dispose();

    TestLake Started(string extension, string content)
    {
        lake.Json("base", "things", """[{"id": "a", "foo": 1}]""")
            .Text("local", $"things.{extension}", content)
            .Stack("base")
            .WriteTo("local", extension == "json" ? LayerFormat.Json : LayerFormat.Yaml);
        lake.Config.DefaultKey = ["id"];
        return lake.Start();
    }

    [Theory]
    [InlineData("yaml", "b:\n  foo: 2\n")]
    [InlineData("json", "{\"b\": {\"foo\": 2}}")]
    public void AKeyedWriteLayerIsWrittenBackKeyed(string extension, string content)
    {
        Started(extension, content);

        lake.Execute("UPDATE lake.things SET foo = 20 WHERE id = 'b'");
        lake.Execute("INSERT INTO lake.things (id, foo) VALUES ('c', 3)");

        // The file itself, not just what the lake makes of it. A text key goes out quoted in both
        // formats -- it reads back as the same string either way, and quoted it also survives a key
        // that plain would be read as a number.
        var written = File.ReadAllText(lake.At("local", $"things.{extension}"));
        Assert.DoesNotContain("- ", written);
        Assert.Contains("\"c\"", written);

        lake.Restart();
        Assert.Equal(["a|1", "b|20", "c|3"], lake.Query("SELECT id, foo FROM lake.things ORDER BY id"));
    }

    /// A key that is text goes out quoted whatever it looks like -- quoted or plain it reads back as
    /// the same string, and quoted it also survives a `007`, which plain would read back as seven.
    [Fact]
    public void AKeyThatWouldBeMisreadGoesOutQuoted()
    {
        Started("yaml", "b:\n  foo: 2\n");

        lake.Execute("INSERT INTO lake.things (id, foo) VALUES ('007', 7)");
        lake.Restart();

        Assert.Equal(["007|7"], lake.Query("SELECT id, foo FROM lake.things WHERE foo = 7"));
    }
}
