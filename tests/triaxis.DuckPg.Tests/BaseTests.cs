namespace triaxis.DuckPg.Tests;

/// Serving a database a bake wrote: the copy is the lake, the base is never touched, and what a run
/// does to its copy leaves as a delta against it -- which is what a lake cut from layers does too.
public class BaseTests
{
    static TestLake Seeded([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var lake = new TestLake(name)
            .Yaml("base", "customers", """
                - customer_id: 1
                  name: seeded
                - customer_id: 2
                  name: doomed
                """)
            .Json("tenant", "customers", """[{"customer_id": 1, "name": "overridden"}]""")
            .Stack("base", "tenant");
        lake.Config.DefaultKey = ["customer_id"];
        return lake;
    }

    [Fact]
    public void ABaseServesWhatWasBakedIntoIt()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");

        // Nothing of what it was baked from: no layers, no key, no dacpac.
        lake.Config.DefaultKey = [];
        lake.FromBase().Start();

        Assert.Equal(["1|overridden", "2|doomed"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));

        // The key came with the file, so a write by key is one the lake can answer.
        Assert.Equal(1, lake.Execute("UPDATE lake.customers SET name = 'written' WHERE customer_id = 1"));
    }

    /// A thousand runs share one file, so a run that wrote to the base would be handing the next one
    /// a different lake than the one it was promised.
    [Fact]
    public void TheBaseItselfIsNeverWrittenTo()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");
        var before = File.ReadAllBytes(lake.At("baked.duckdb"));

        lake.FromBase().WriteTo("local").Start();
        lake.Execute("INSERT INTO lake.customers (customer_id, name) VALUES (3, 'added')");
        lake.Stop();

        Assert.Equal(before, File.ReadAllBytes(lake.At("baked.duckdb")));
    }

    /// The bargain a materialized lake makes, kept by a baked one: the copy is thrown away and what
    /// it holds that the base did not goes out as a layer the next run reads back.
    [Fact]
    public void WritesGoOutAsADeltaAgainstTheBase()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");

        lake.FromBase().WriteTo("local").Start();
        lake.Execute("INSERT INTO lake.customers (customer_id, name) VALUES (3, 'added')");
        lake.Execute("UPDATE lake.customers SET name = 'written' WHERE customer_id = 1");
        lake.Execute("DELETE FROM lake.customers WHERE customer_id = 2");
        lake.Stop();

        // Everything in memory is gone; the copy went with it. What is left is the base and a delta.
        Assert.True(Directory.EnumerateFiles(lake.At("local")).Any(), "the delta was not written");

        lake.Start();
        Assert.Equal(["1|written", "3|added"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));
    }

    /// And again, because a delta measured against a base that already carried it is a delta that
    /// takes the run before it back.
    [Fact]
    public void ADeltaSurvivesBeingRestartedTwice()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");

        lake.FromBase().WriteTo("local").Start();
        lake.Execute("DELETE FROM lake.customers WHERE customer_id = 2");
        lake.Restart();

        Assert.Equal(["1|overridden"], lake.Query("SELECT customer_id, name FROM lake.customers"));
        lake.Execute("INSERT INTO lake.customers (customer_id, name) VALUES (4, 'later')");
        lake.Restart();

        Assert.Equal(["1|overridden", "4|later"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));
    }

    /// Any DuckDB file attaches; only one duckpg baked says what a lake publishes.
    [Fact]
    public void ADatabaseThatIsNotABakeIsRefused()
    {
        using var lake = Seeded();
        lake.Parquet("base", "unrelated", "SELECT 1 AS id");

        using (var duck = new DuckDB.NET.Data.DuckDBConnection($"Data Source={lake.At("plain.duckdb")}"))
        {
            duck.Open();
            using var make = duck.CreateCommand();
            make.CommandText = "CREATE TABLE whatever (id INTEGER)";
            make.ExecuteNonQuery();
        }

        lake.FromBase("plain.duckdb");
        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("not a baked one", refused.Message);
    }

    /// The file is copied by every run that serves it, so what the build needed and the serving does
    /// not is weight on every one of them -- and a YAML layer left behind is every row twice.
    [Fact]
    public void ABakedDatabaseCarriesNoneOfTheBuildsScaffolding()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");

        using var duck = new DuckDB.NET.Data.DuckDBConnection($"Data Source={lake.At("baked.duckdb")}");
        duck.Open();

        using var command = duck.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT table_schema FROM information_schema.tables " +
            "WHERE table_schema NOT IN ('information_schema', 'pg_catalog') ORDER BY 1";
        using var reader = command.ExecuteReader();

        var schemas = new List<string>();
        while (reader.Read()) schemas.Add(reader.GetString(0));

        // What it serves and what says how, and nothing of how it was built: no `layer` table
        // holding a second copy of every YAML row, and no baseline view naming this machine's paths.
        Assert.Equal(["duckpg", "lake", "main"], schemas);
    }

    /// DuckDB names the database after the file, and the copy keeps the base's name -- so this is
    /// the ambiguity the same rule already refuses for a store.
    [Fact]
    public void ABaseNamedAfterTheSchemaIsRefused()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");
        File.Move(lake.At("baked.duckdb"), lake.At("lake.duckdb"));

        lake.FromBase("lake.duckdb");
        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("cannot tell the two apart", refused.Message);
    }

    [Fact]
    public void LayersUnderABaseAreRefused()
    {
        using var lake = Seeded();
        lake.Baked("baked.duckdb");
        lake.Config.Base = lake.At("baked.duckdb");

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("already collapsed", refused.Message);
    }

    /// The same layers, served the three ways a lake can serve them. Whatever reads one has to be
    /// able to swap `Layers` for a `Base` cut from them without its answers changing: a caller
    /// capturing rows from one and comparing them against rows from the other reads every difference
    /// as a row that changed. So the three are asked the same question rather than each being asked
    /// its own, and each is written to first, since a mode can also disagree about what it was just
    /// given.
    ///
    /// Every way a nullable column can come by a value is in here at once, because they are what the
    /// three modes can disagree about: `given` is what a layer file holds, `written` is what a client
    /// sends -- zero among them, which is the value a mode that lost it would answer NULL for -- and
    /// `stamped` is what a declared default fills in, both over a file's null and over a write that
    /// left the column out.
    [Fact]
    public void TheSameLayersAnswerAlikeServedEveryWay()
    {
        using var layered = Readings();
        using var materialized = Readings();
        using var based = Readings();

        layered.Start();
        materialized.Materialized().Start();
        based.Baked("baked.duckdb");
        // Nothing of what it was baked from: a base answers out of its own file or it cannot agree.
        based.Config.Dacpac = null;
        based.FromBase().Start();

        foreach (var lake in (TestLake[])[layered, materialized, based])
        {
            lake.Execute("UPDATE lake.readings SET written = 0 WHERE reading_id = 2");
            lake.Execute("INSERT INTO lake.readings (reading_id, given, written) VALUES (3, 7, 0)");
            lake.Execute("INSERT INTO lake.readings (reading_id) VALUES (4)");
        }

        string[] expected = [
            "1|4|11|kept",          // as the layer file has it
            "2|<null>|0|none",      // a written zero over a file's null, and the default over another
            "3|7|0|none",           // a written zero on a row no file carries
            "4|<null>|<null>|none", // and a write that named neither
        ];
        Assert.Equal(expected, Readings(layered));
        Assert.Equal(expected, Readings(materialized));
        Assert.Equal(expected, Readings(based));
    }

    static TestLake Readings([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var lake = new TestLake(name)
            .Json("base", "readings", """
                [{"reading_id": 1, "given": 4, "written": 11, "stamped": "kept"},
                 {"reading_id": 2, "given": null, "written": null, "stamped": null}]
                """)
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("readings",
            [("reading_id", "int"), ("given", "int"), ("written", "int"), ("stamped", "nvarchar")],
            ["reading_id"],
            Defaults: [("stamped", "('none')")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        return lake;
    }

    static List<string> Readings(TestLake lake) =>
        lake.Query("SELECT reading_id, coalesce(given::VARCHAR, '<null>'), " +
                   "coalesce(written::VARCHAR, '<null>'), coalesce(stamped, '<null>') " +
                   "FROM lake.readings ORDER BY reading_id");
}
