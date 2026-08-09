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
}
