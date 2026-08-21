using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A materialized lake that collapses a table when a statement first names it, rather than every
/// table when it is built. What it publishes in the meantime is the merge a layered lake publishes,
/// so the rows are the same rows whether or not anything spotted the name -- which is the whole
/// bargain, and what these check.
public class LazyTests : IDisposable
{
    readonly TestLake lake = new TestLake("lazy")
        .Json("base", "orders", """
            [{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}, {"order_id": 3, "note": "base"}]
            """)
        .Json("top", "orders", """[{"order_id": 2, "note": "shadowed"}]""")
        .Json("base", "customers", """[{"customer_id": 1, "name": "acme"}]""")
        .Stack("base", "top")
        .WriteTo("local")
        .Materialized()
        .Lazily()
        .WithTds();

    public LazyTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Config.Tables["customers"] = new TableConfig { Key = ["customer_id"] };
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    bool Collapsed(string table) => lake.Catalog.Tables[table].Materialized;

    /// What DuckDB itself is holding under the name, which is the difference the whole mode is
    /// about: a view over the layers until something asks, a table afterwards.
    string Held(string table) =>
        lake.Query($"SELECT table_type FROM information_schema.tables WHERE table_schema = 'lake' " +
                   $"AND table_name = '{table}'").Single();

    [Fact]
    public void NothingIsCollapsedUntilAStatementNamesIt()
    {
        Assert.False(Collapsed("orders"));
        Assert.Equal("VIEW", Held("orders"));

        Assert.Equal(["1=base", "2=shadowed", "3=base"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));

        Assert.True(Collapsed("orders"));
        Assert.Equal("BASE TABLE", Held("orders"));
    }

    /// And the table beside it is left exactly as it was: this is per table, not a switch the first
    /// statement throws for the whole lake.
    [Fact]
    public void ATableNothingNamedIsLeftAsTheMerge()
    {
        lake.Query("SELECT order_id FROM lake.orders");

        Assert.False(Collapsed("customers"));
        Assert.True(lake.Catalog.Deferring);
        Assert.Equal(["1=acme"], lake.Query("SELECT customer_id || '=' || name FROM lake.customers"));
        Assert.False(lake.Catalog.Deferring);
    }

    /// The rows are the rows either way -- the same stack, shadowing and all, collapsed at a
    /// different moment.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRowsAreWhatTheLayersSay(bool lazily)
    {
        using var other = new TestLake(nameof(TheRowsAreWhatTheLayersSay))
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}]""")
            .Json("top", "orders", """[{"order_id": 2, "note": "shadowed"}]""")
            .Stack("base", "top")
            .Materialized();
        if (lazily) other.Lazily();
        other.Config.DefaultKey = ["order_id"];
        other.Start();

        Assert.Equal(["1=base", "2=shadowed"],
                     other.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// A write names its table, so it is collapsed before the statement it arrived with runs -- and
    /// what a write to a collapsed table means is what it means in an eager lake: a write to the
    /// table the reads come from, and a delta at shutdown.
    [Fact]
    public void AWriteCollapsesTheTableFirst()
    {
        using (var connection = Open())
        {
            new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();
        }

        Assert.True(Collapsed("orders"));
        Assert.Equal(["1=written", "2=shadowed"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1=written", "2=shadowed"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// A run that never names a table leaves that table's files alone -- including the delta the run
    /// before it left there. Nothing was written to it, so there is nothing to write out, and a
    /// shutdown that measured one anyway would measure the previous run's writes away.
    [Fact]
    public void ARunThatNamesNothingKeepsTheDeltaBeforeIt()
    {
        using (var connection = Open())
            new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();

        lake.Restart();
        lake.Query("SELECT customer_id FROM lake.customers");
        Assert.False(Collapsed("orders"));

        lake.Restart();
        Assert.Equal(["1=written", "2=shadowed", "3=base"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// Layers that break a declared key are what an eager lake refuses to start over. Deferred, the
    /// refusal is the statement that names the table -- and it is the same refusal every time,
    /// rather than the key being quietly dropped so the next statement can pass.
    [Fact]
    public void LayersBreakingTheKeyAreRefusedWhenTheTableIsNamed()
    {
        using var broken = new TestLake(nameof(LayersBreakingTheKeyAreRefusedWhenTheTableIsNamed))
            .Json("base", "orders", """[{"order_id": 1, "note": "a"}, {"order_id": 1, "note": "b"}]""")
            .Stack("base")
            .Materialized()
            .Lazily();
        broken.Config.DefaultKey = ["order_id"];
        broken.Start();

        Assert.ThrowsAny<Exception>(() => broken.Query("SELECT order_id FROM lake.orders"));
        Assert.ThrowsAny<Exception>(() => broken.Query("SELECT order_id FROM lake.orders"));
    }

    /// A statement through a declared view names the view and not the tables under it, so what the
    /// view reads is followed rather than left merged.
    [Fact]
    public void ADeclaredViewCollapsesWhatItReads()
    {
        using var viewed = new TestLake(nameof(ADeclaredViewCollapsesWhatItReads))
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .Materialized()
            .Lazily();
        Dacpac.Write(viewed.At("schema", "test.dacpac"),
            [new Dacpac.TableModel("orders", [("order_id", "int"), ("note", "nvarchar")], ["order_id"])],
            new Dacpac.ViewModel("recent", "SELECT order_id, note FROM dbo.orders"));
        viewed.Config.Dacpac = viewed.At("schema", "test.dacpac");
        viewed.Start();

        Assert.False(viewed.Catalog.Tables["orders"].Materialized);
        Assert.Equal(["1"], viewed.Query("SELECT order_id FROM lake.recent"));
        Assert.True(viewed.Catalog.Tables["orders"].Materialized);
    }

    /// The names are read out of the statement rather than parsed out of it, so what it does with a
    /// table's name written somewhere it is not a table's name is worth pinning: a literal is not a
    /// reference, and a quoted identifier is.
    [Fact]
    public void ANameInAStringIsNotAReference()
    {
        Assert.Equal(["customers"], lake.Query("SELECT 'customers' -- customers"));
        Assert.False(Collapsed("customers"));

        lake.Query("""SELECT "customer_id" FROM lake."customers" """);
        Assert.True(Collapsed("customers"));
    }

    /// A store that is the state has the collapse behind it already: its table is what the lake
    /// serves, and the layers are not read for it whether or not anything names it.
    [Fact]
    public void AStoreThatAlreadyHoldsTheTableIsNotDeferred()
    {
        using var stored = new TestLake(nameof(AStoreThatAlreadyHoldsTheTableIsNotDeferred))
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .Materialized()
            .Lazily()
            .StoredAt();
        stored.Config.DefaultKey = ["order_id"];
        stored.Start();

        Assert.False(stored.Catalog.Tables["orders"].Materialized);
        Assert.Equal(1, stored.Execute("INSERT INTO lake.orders (order_id, note) VALUES (2, 'written')"));

        stored.Restart();
        Assert.True(stored.Catalog.Tables["orders"].Materialized);
        Assert.Equal(["1", "2"], stored.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    /// A store that is only where the tables live is rebuilt from the layers on every start, so what
    /// the last run collapsed into it is deferred again -- and the table it left has to make way for
    /// the view, since DuckDB will not publish one over a name a table holds.
    [Fact]
    public void ASpilledTableIsDeferredAgain()
    {
        using var spilled = new TestLake(nameof(ASpilledTableIsDeferredAgain))
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .Materialized()
            .Lazily()
            .StoredAt(mode: StoreMode.Spill);
        spilled.Config.DefaultKey = ["order_id"];
        spilled.Start();
        spilled.Query("SELECT order_id FROM lake.orders");

        spilled.Restart();
        Assert.False(spilled.Catalog.Tables["orders"].Materialized);
        Assert.Equal(["1"], spilled.Query("SELECT order_id FROM lake.orders"));
    }

    /// The views a lazy run leaves in a store are what an eager run after it finds under those
    /// names, and it collapses them rather than tripping over them.
    [Fact]
    public void AStoreLeftWithViewsIsCollapsedByAnEagerRun()
    {
        using var stored = new TestLake(nameof(AStoreLeftWithViewsIsCollapsedByAnEagerRun))
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .Materialized()
            .Lazily()
            .StoredAt();
        stored.Config.DefaultKey = ["order_id"];
        stored.Start();
        stored.Stop();

        stored.Config.Lazy = false;
        stored.Start();
        Assert.True(stored.Catalog.Tables["orders"].Materialized);
        Assert.Equal(["1"], stored.Query("SELECT order_id FROM lake.orders"));
    }

    /// What `lazy` defers is what `materialize` does, and a lake doing neither has nothing to put
    /// off -- said at startup rather than by quietly serving views nothing ever collapses.
    [Fact]
    public void LazyWithoutMaterializeIsRefused()
    {
        using var neither = new TestLake(nameof(LazyWithoutMaterializeIsRefused))
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .Lazily();

        var refused = Assert.Throws<DuckPgConfigurationException>(() => neither.Start());
        Assert.Contains("materialize", refused.Message);
    }
}
