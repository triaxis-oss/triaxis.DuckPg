using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// The lake's schema goes in front of every session's search path, so an unqualified name finds the
/// table wherever it was published. DuckDB's own default is `main` and PostgreSQL's is `public`, so a
/// lake publishing into neither is one that every unqualified query misses -- which is what makes the
/// schema's name something a first query has to know.
public class SearchPathTests : IDisposable
{
    readonly TestLake lake = new TestLake("search-path")
        .Json("base", "orders", """[{"order_id": 1, "note": "one"}, {"order_id": 2, "note": "two"}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public SearchPathTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    [Fact]
    public void AnUnqualifiedNameFindsTheLakesTable()
    {
        Assert.Equal(["one", "two"], lake.Query("SELECT note FROM orders ORDER BY order_id"));
        Assert.Equal(["lake"], lake.Query("SELECT current_schema()"));
    }

    /// Whatever it was called. The name stops being something a first query has to know.
    [Fact]
    public void ItFollowsTheSchemaTheLakeWasGiven()
    {
        lake.Stop();
        lake.Config.Schema = "public";
        lake.Start();

        Assert.Equal(["one", "two"], lake.Query("SELECT note FROM orders ORDER BY order_id"));
        Assert.Equal(["one"], lake.Query("SELECT note FROM public.orders WHERE order_id = 1"));
    }

    /// `main` stays behind the lake, because the catalog shims are created there -- an unqualified
    /// `duckpg_pg_class` is what `pg_catalog.pg_class` is rewritten to, and every client that reads
    /// the catalog would stop finding it.
    [Fact]
    public void TheCatalogShimsAreStillReachable()
    {
        Assert.NotEmpty(lake.Query("SELECT relname FROM pg_catalog.pg_class WHERE relname = 'orders'"));
        Assert.Equal(["1"], lake.Query("SELECT 1 WHERE pg_catalog.pg_table_is_visible(0)"));
    }

    /// A write goes through several temp tables, and every one of them says TEMP -- so the path in
    /// front of `main` cannot land one in the published schema, where it would outlive the session.
    [Fact]
    public void AWriteStillRunsAndLeavesNothingBehind()
    {
        Assert.Equal(1, lake.Execute("UPDATE orders SET note = 'written' WHERE order_id = 1"));
        Assert.Equal(["written"], lake.Query("SELECT note FROM orders WHERE order_id = 1"));

        Assert.Empty(lake.Query(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'lake' AND table_name LIKE 'duckpg%'"));
    }

    /// The other door resolves its own way -- `[orders]` is written into the lake's schema by the
    /// T-SQL renderer rather than left to a path -- so this is that it still does.
    [Fact]
    public void TheOtherFrontDoorIsUnaffected()
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        Assert.Equal("one", new SqlCommand("SELECT [note] FROM [orders] WHERE [order_id] = 1", connection)
            .ExecuteScalar());
        Assert.Equal("one", new SqlCommand("SELECT [note] FROM [dbo].[orders] WHERE [order_id] = 1", connection)
            .ExecuteScalar());
    }
}
