using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A materialized lake given a database file to live in. The layers are collapsed into it once, and
/// every start after that opens what is already there -- so a write survives by having been written
/// rather than by being worked out again at shutdown, and the layers are only consulted for a table
/// the file does not yet carry.
public class StoreTests : IDisposable
{
    readonly TestLake lake = new TestLake("store")
        .Json("base", "orders", """
            [{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}, {"order_id": 3, "note": "base"}]
            """)
        .Stack("base")
        .Materialized()
        .StoredAt()
        .WithTds();

    public StoreTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    /// The layers are what the file is made from, the first time and only then.
    [Fact]
    public void TheLayersAreCollapsedIntoTheFile()
    {
        Assert.Equal(["1", "2", "3"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
        Assert.True(File.Exists(lake.At("store.duckdb")));
    }

    /// And the writes are simply there on the next start: nothing is exported, nothing recomputed.
    [Fact]
    public void WhatWasWrittenIsSimplyStillThere()
    {
        using (var connection = Open())
        {
            new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();
            new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'new')", connection)
                .ExecuteNonQuery();
        }

        lake.Restart();
        Assert.Equal(["1=written", "2=base", "4=new"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));

        // And again, because a mode that only survives one restart is the bug just fixed above.
        lake.Restart();
        Assert.Equal(["1=written", "2=base", "4=new"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// The file is the state, so the layers no longer are: a lake that re-read them would undo every
    /// write made since, which is the whole reason it does not.
    [Fact]
    public void TheLayersAreNotReadAgainForATableTheFileCarries()
    {
        using (var connection = Open())
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();

        lake.Json("base", "orders", """[{"order_id": 1, "note": "changed underneath"}]""");
        lake.Restart();

        Assert.Equal(["1=base", "2=base"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// A table the file has never held is still made from the layers, so a lake that grows a table
    /// does not need its store thrown away.
    [Fact]
    public void ATableTheFileHasNeverHeldIsStillMadeFromTheLayers()
    {
        lake.Restart();
        lake.Json("base", "extras", """[{"extra_id": 1}]""");
        lake.Restart();

        Assert.Equal(["1"], lake.Query("SELECT extra_id FROM lake.extras"));
    }

    /// A reload over a store finds the tables it left there and keeps them, key and all -- the store
    /// is the state, and a reload is not a way of asking the layers again. The key is the half that
    /// can only go wrong once: it is already on the table, and asking DuckDB for it a second time is
    /// an error rather than a no-op.
    [Fact]
    public void AReloadKeepsWhatTheStoreHolds()
    {
        using (var connection = Open())
            new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();

        lake.Query("CALL duckpg_reload()");

        Assert.Equal(["1=written", "2=base", "3=base"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
        Assert.Equal(["1"], lake.Query(
            "SELECT count(*) FROM duckdb_constraints() WHERE schema_name = 'lake' " +
            "AND table_name = 'orders' AND constraint_type = 'PRIMARY KEY'"));
    }

    /// A store made of another schema is refused by name. Rebuilding it here would be the one thing
    /// a store exists to prevent, and serving it would fail on the first query naming the column.
    [Fact]
    public void AStoreOfAnotherShapeIsRefused()
    {
        lake.Stop();
        lake.Json("base", "orders", """[{"order_id": 1, "note": "base", "extra": 5}]""");

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("orders", refused.Message);
        Assert.Contains("another", refused.Message);
    }

    /// Nothing is written back to the layers: the file is where it is kept, and a delta beside it
    /// would be a second answer to the same question.
    [Fact]
    public void NoDeltaIsWrittenBesideAStore()
    {
        using var stored = new TestLake("store-and-write")
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .WriteTo("local")
            .Materialized()
            .StoredAt()
            .WithTds();
        stored.Config.DefaultKey = ["order_id"];
        stored.Start();

        using (var connection = new SqlConnection(stored.SqlConnectionString()))
        {
            connection.Open();
            new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (2, 'new')", connection)
                .ExecuteNonQuery();
        }
        stored.Stop();

        Assert.Empty(Directory.GetFiles(stored.At("local")));
    }
}

/// The same file, meaning nothing. A spilled store is somewhere for the tables to live and not the
/// lake's state: the layers are collapsed into it on every start and the delta goes out at shutdown,
/// exactly as an in-memory materialized lake behaves. What the file buys is the memory.
public class SpillTests : IDisposable
{
    readonly TestLake lake = new TestLake("spill")
        .Json("base", "orders", """
            [{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}, {"order_id": 3, "note": "base"}]
            """)
        .Stack("base")
        .WriteTo("local")
        .Materialized()
        .StoredAt(mode: StoreMode.Spill)
        .WithTds();

    public SpillTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    void Write(string sql)
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        new SqlCommand(sql, connection).ExecuteNonQuery();
    }

    List<string> Rows() => lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id");

    /// The tables are in the file rather than in memory -- which is the only thing the mode is for.
    [Fact]
    public void TheTablesLiveInTheFile()
    {
        Assert.True(File.Exists(lake.At("store.duckdb")));
        Assert.Equal(["BASE TABLE"], lake.Query(
            "SELECT table_type FROM information_schema.tables " +
            "WHERE table_schema = 'lake' AND table_name = 'orders'"));
    }

    /// Every start makes the tables again, so every start makes their keys again: the table the last
    /// run left carried one, and the one this run replaced it with is a different table. A lake that
    /// took the file's word for it would publish a table with no key and no sign of it.
    [Fact]
    public void TheKeyIsMadeAgainWithTheTable()
    {
        lake.Restart();

        Assert.Equal(["1"], lake.Query(
            "SELECT count(*) FROM duckdb_constraints() WHERE schema_name = 'lake' " +
            "AND table_name = 'orders' AND constraint_type = 'PRIMARY KEY'"));
    }

    /// The file is not the state, so the layers still are: a change underneath is read on the next
    /// start. This is the whole difference from a kept store, which refuses to look.
    [Fact]
    public void TheLayersAreReadAgainOnEveryStart()
    {
        lake.Json("base", "orders", """[{"order_id": 1, "note": "changed underneath"}]""");
        lake.Restart();

        Assert.Equal(["1=changed underneath"], Rows());
    }

    /// And a write survives by being written down, the way an in-memory materialized lake's does.
    [Fact]
    public void TheDeltaIsWrittenAtShutdownAndReadBackOnTheNextStart()
    {
        Write("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1");
        Write("DELETE FROM [orders] WHERE [order_id] = 3");
        Write("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'new')");

        lake.Stop();
        Assert.NotEmpty(Directory.GetFiles(lake.At("local")));

        lake.Start();
        Assert.Equal(["1=written", "2=base", "4=new"], Rows());

        // Twice, because a delta measured against a baseline that already carried it comes out empty
        // and takes the run before it with it -- and one restart cannot tell the two apart.
        lake.Restart();
        Assert.Equal(["1=written", "2=base", "4=new"], Rows());
    }

    /// A run whose store the previous one left behind starts from the layers plus that delta, never
    /// from the tables in the file -- otherwise every write would be applied twice, once out of the
    /// file and once out of the delta it was flushed to.
    [Fact]
    public void WhatTheFileHoldsIsRebuiltRatherThanKept()
    {
        Write("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'new')");
        lake.Restart();

        // The layers no longer say what they said, and a kept store would still be answering '4=new'
        // off the tables it holds.
        lake.Json("base", "orders", """[{"order_id": 1, "note": "rewritten"}]""");
        lake.Restart();

        Assert.Equal(["1=rewritten", "4=new"], Rows());
    }
}

/// A store keeps tables, and only a materialized lake has any.
public class StoreRefusalTests
{
    [Fact]
    public void AStoreWithoutMaterializeIsRefused()
    {
        using var lake = new TestLake("store-unmaterialized")
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .StoredAt();

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("materialize", refused.Message);
    }

    /// DuckDB names the database after the file, so the obvious name for a lake's store is the one
    /// that collides with the schema it publishes into -- and what DuckDB says about it names
    /// neither.
    [Fact]
    public void AStoreNamedAfterTheSchemaIsRefused()
    {
        using var lake = new TestLake("store-named-lake")
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .Materialized()
            .StoredAt($"{new Config().Schema}.duckdb");

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("schema", refused.Message);
    }

    /// Saying what the file is for, without naming one, is a mode that would silently do nothing.
    [Fact]
    public void AStoreModeWithoutAStoreIsRefused()
    {
        using var lake = new TestLake("spill-unstored")
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .Materialized();
        lake.Config.StoreMode = StoreMode.Spill;

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("store", refused.Message);
    }
}
