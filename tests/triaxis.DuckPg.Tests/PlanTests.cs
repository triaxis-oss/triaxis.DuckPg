using Microsoft.Data.SqlClient;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// What the gateway actually sends, and how to see it. A write against a layered lake is rewritten
/// into a plan that stands a write branch over the layers below; a materialized table has no layers
/// below, so the statement a client sent is the statement to run. `EXPLAIN` is how either can be
/// told apart from the other without a profiler.
public class PlanTests
{
    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Json("base", "orders", """
                [{"order_id": 1, "amount": 10, "note": "a"}, {"order_id": 2, "amount": 20, "note": "b"}]
                """)
            .Stack("base")
            .WriteTo("local");

    static TestLake Started(TestLake lake, bool materialized = true)
    {
        lake.Config.DefaultKey = ["order_id"];
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    static bool Rewritten(List<string> explained) => explained.Any(row => row.Contains("duckpg_"));

    // ---- the one-statement path ------------------------------------------------------------------

    [Fact]
    public void AMaterializedUpdateByKeyIsSentAsItArrived()
    {
        using var lake = Started(Lake());

        var explained = lake.Query("EXPLAIN UPDATE lake.orders SET note = 'probe' WHERE order_id = 1");

        Assert.False(Rewritten(explained));
        Assert.Contains(explained, row => row.Contains("UPDATE"));
    }

    [Fact]
    public void ALayeredUpdateIsStillRewritten()
    {
        using var lake = Started(Lake(), materialized: false);

        var explained = lake.Query("EXPLAIN UPDATE lake.orders SET note = 'probe' WHERE order_id = 1");

        Assert.True(Rewritten(explained));
        Assert.Contains(explained, row => row.Contains("duckpg_updated"));
    }

    /// The three things the plan is still there to do. Each one alone puts the statement back on it.
    [Theory]
    [InlineData("UPDATE lake.orders SET note = v.n FROM (VALUES (1, 'x')) AS v(id, n) WHERE order_id = v.id")]
    [InlineData("UPDATE lake.orders SET note = 'x' LIMIT 1")]
    [InlineData("UPDATE lake.orders SET order_id = 9 WHERE order_id = 1")]
    public void AMaterializedUpdateThePlanIsStillForKeepsIt(string sql)
    {
        using var lake = Started(Lake());

        Assert.True(Rewritten(lake.Query("EXPLAIN " + sql)));
    }

    [Fact]
    public void AMaterializedDeleteByKeyIsSentAsItArrived()
    {
        using var lake = Started(Lake());

        var explained = lake.Query("EXPLAIN DELETE FROM lake.orders WHERE order_id = 1");

        Assert.False(Rewritten(explained));
        Assert.Contains(explained, row => row.Contains("DELETE"));
    }

    [Fact]
    public void ALayeredDeleteIsStillRewritten()
    {
        using var lake = Started(Lake(), materialized: false);

        Assert.True(Rewritten(lake.Query("EXPLAIN DELETE FROM lake.orders WHERE order_id = 1")));
    }

    /// A delete that has to hand its keys to something else keeps the plan that writes them down.
    [Theory]
    [InlineData("DELETE FROM lake.orders LIMIT 1")]
    [InlineData("DELETE FROM lake.orders WHERE order_id = 1 RETURNING order_id")]
    public void AMaterializedDeleteThePlanIsStillForKeepsIt(string sql)
    {
        using var lake = Started(Lake());

        Assert.True(Rewritten(lake.Query("EXPLAIN " + sql)));
    }

    // ---- and it is still the same write ----------------------------------------------------------

    [Fact]
    public void TheOneStatementUpdateWritesWhatThePlanWouldHave()
    {
        using var lake = Started(Lake());

        Assert.Equal(1, lake.Execute("UPDATE lake.orders SET note = 'probe' WHERE order_id = 1"));
        Assert.Equal(["1|probe", "2|b"], lake.Query("SELECT order_id, note FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1|probe", "2|b"], lake.Query("SELECT order_id, note FROM lake.orders ORDER BY order_id"));
    }

    /// Touching nothing is nothing written, and still answers for the rows it did not touch.
    [Fact]
    public void TheOneStatementUpdateAnswersForNoRows()
    {
        using var lake = Started(Lake());

        Assert.Equal(0, lake.Execute("UPDATE lake.orders SET note = 'probe' WHERE order_id = 99"));
    }

    [Fact]
    public void TheOneStatementDeleteWritesWhatThePlanWouldHave()
    {
        using var lake = Started(Lake());

        Assert.Equal(1, lake.Execute("DELETE FROM lake.orders WHERE order_id = 1"));

        lake.Restart();
        Assert.Equal(["2|b"], lake.Query("SELECT order_id, note FROM lake.orders ORDER BY order_id"));
    }

    /// The rows a write hands back come off the write itself now rather than off a temp table it
    /// filled first -- and what it wrote still reaches the files, which counting a plan's steps to
    /// decide whether anything was written would have lost.
    [Fact]
    public void TheOneStatementUpdateStillAnswersWithWhatItWrote()
    {
        using var lake = Started(Lake());

        Assert.Equal(["1|probe"],
            lake.Query("UPDATE lake.orders SET note = 'probe' WHERE order_id = 1 RETURNING order_id, note"));

        lake.Restart();
        Assert.Equal(["1|probe", "2|b"], lake.Query("SELECT order_id, note FROM lake.orders ORDER BY order_id"));
    }

    // ---- EXPLAIN ---------------------------------------------------------------------------------

    /// A statement the gateway passes through is explained by DuckDB as it always was.
    [Fact]
    public void ExplainOfAQueryIsDuckDbsOwn()
    {
        using var lake = Started(Lake());

        Assert.Contains(lake.Query("EXPLAIN SELECT * FROM lake.orders"), row => row.Contains("SCAN"));
    }

    /// A plan of several statements cannot be explained by DuckDB -- every step but the first reads
    /// temp tables the one before it makes -- so it answers with the statements themselves.
    [Fact]
    public void ExplainOfARewrittenWriteListsTheStatements()
    {
        using var lake = Started(Lake(), materialized: false);

        var explained = lake.Query("EXPLAIN UPDATE lake.orders SET note = 'probe' WHERE order_id = 1");

        Assert.Contains(explained, row => row.StartsWith("step 1|"));
        Assert.Contains(explained, row => row.Contains("duckpg_keys"));
        Assert.Contains(explained, row => row.Contains("INSERT INTO"));
    }

    /// The checks a plan runs before any of its steps are part of what it does, and are listed too.
    [Fact]
    public void ExplainListsTheChecksAPlanRunsFirst()
    {
        using var lake = Started(Lake(), materialized: false);

        Assert.Contains(lake.Query("EXPLAIN UPDATE lake.orders SET order_id = 9 WHERE order_id = 1"),
                        row => row.StartsWith("check 1|"));
    }

    /// The SQL Server door refused it outright, which left a caller there no way to ask at all.
    [Fact]
    public void ExplainComesThroughTheSqlServerDoorToo()
    {
        using var lake = Started(Lake().WithTds());

        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using var command = new SqlCommand("EXPLAIN SELECT * FROM lake.orders", connection);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetValue(1)?.ToString() ?? "");
        Assert.Contains(rows, row => row.Contains("SCAN"));
    }

    [Fact]
    public void ExplainAnalyzeRunsTheStatementItExplains()
    {
        using var lake = Started(Lake());

        Assert.Contains(lake.Query("EXPLAIN ANALYZE SELECT * FROM lake.orders"),
                        row => row.Contains("Total Time", StringComparison.OrdinalIgnoreCase));
    }
}
