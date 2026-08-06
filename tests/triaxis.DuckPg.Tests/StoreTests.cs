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
}
