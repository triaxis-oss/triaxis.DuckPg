using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace triaxis.Tools.DuckPg.Tests;

/// Microsoft.Data.SqlClient is the client the TDS side is held to, the way Npgsql is on the
/// PostgreSQL side: the paths an application takes, against a lake made of files.
public class TdsTests : IDisposable
{
    readonly TestLake lake = new TestLake("tds")
        .Parquet("base", "orders", """
            SELECT * FROM (VALUES
                (1, 10.50::DECIMAL(10,2), DATE '2026-08-01', 'first'),
                (2, 20.00::DECIMAL(10,2), DATE '2026-08-02', 'second'),
                (3, 30.00::DECIMAL(10,2), DATE '2026-08-03', NULL)
            ) t(order_id, amount, ordered_on, note)
            """)
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public TdsTests()
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

    [Fact]
    public void ConnectsAndAnswers()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT 1", connection);
        Assert.Equal(1, command.ExecuteScalar());
    }

    [Fact]
    public void ReadsTypedColumns()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT order_id, amount, ordered_on, note FROM orders ORDER BY order_id", connection);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}/{reader.GetDecimal(1)}/{reader.GetDateTime(2):yyyy-MM-dd}/" +
                     $"{(reader.IsDBNull(3) ? "null" : reader.GetString(3))}");

        Assert.Equal(["1/10.50/2026-08-01/first", "2/20.00/2026-08-02/second", "3/30.00/2026-08-03/null"], rows);
    }

    /// The login name, since that is the only user a lake of files has.
    [Fact]
    public void SaysWhoIsAsking()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT SUSER_SNAME()", connection);
        Assert.Equal("sa", command.ExecuteScalar());
    }

    [Fact]
    public void TranslatesTheDialectItIsSent()
    {
        using var connection = Open();
        using var command = new SqlCommand(
            "SELECT TOP 2 [order_id], ISNULL([note], 'none') AS n FROM [dbo].[orders] ORDER BY [order_id] DESC",
            connection);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetInt32(0)}/{reader.GetString(1)}");
        Assert.Equal(["3/none", "2/second"], rows);
    }

    [Fact]
    public void RunsWhatAnOrmSends()
    {
        // LLBLGen Pro qualifies every column by schema and table, and pages with a row-numbered
        // derived table. Rendering it is one thing; DuckDB accepting it is the test.
        using var connection = Open();
        using var command = new SqlCommand(
            "SELECT TOP(@count) [LPA_L1].[order_id], [LPA_L1].[note] FROM " +
            "(SELECT ROW_NUMBER() OVER(ORDER BY [dbo].[orders].[order_id] ASC) AS [__rn], " +
            "[dbo].[orders].[order_id], [dbo].[orders].[note] FROM [dbo].[orders] " +
            "WHERE ([dbo].[orders].[amount] > @min)) [LPA_L1] WHERE [LPA_L1].[__rn] > @skip",
            connection);
        command.Parameters.AddWithValue("@count", 2);
        command.Parameters.AddWithValue("@min", 5m);
        command.Parameters.AddWithValue("@skip", 1);

        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetInt32(0)}/{(reader.IsDBNull(1) ? "null" : reader.GetString(1))}");

        Assert.Equal(["2/second", "3/null"], rows);
    }

    [Fact]
    public void WritesTheWayAnOrmWritesThem()
    {
        using var connection = Open();

        using var update = new SqlCommand(
            "UPDATE [dbo].[orders] SET [note] = @note WHERE ([dbo].[orders].[order_id] = @id)", connection);
        update.Parameters.AddWithValue("@note", "by the orm");
        update.Parameters.AddWithValue("@id", 1);
        Assert.Equal(1, update.ExecuteNonQuery());

        using var check = new SqlCommand("SELECT [dbo].[orders].[note] FROM [dbo].[orders] WHERE [order_id] = 1", connection);
        Assert.Equal("by the orm", check.ExecuteScalar());
    }

    [Fact]
    public void ParameterisedCommandsGoThroughRpc()
    {
        using var connection = Open();
        using var command = new SqlCommand(
            "SELECT COUNT(*) FROM orders WHERE amount > @min AND ordered_on <= @until", connection);
        command.Parameters.AddWithValue("@min", 15m);
        command.Parameters.AddWithValue("@until", new DateTime(2026, 8, 2));

        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void ParametersCarryEveryTypeSqlClientSends()
    {
        using var connection = Open();
        using var command = new SqlCommand(
            "SELECT @i AS i, @s AS s, @b AS b, @f AS f, @d AS d, @t AS t, @g AS g, @v AS v, @n AS n", connection);
        command.Parameters.AddWithValue("@i", 9_000_000_000L);
        command.Parameters.AddWithValue("@s", "text");
        command.Parameters.AddWithValue("@b", true);
        command.Parameters.AddWithValue("@f", 1.5d);
        command.Parameters.AddWithValue("@d", new DateTime(2026, 8, 4));
        command.Parameters.AddWithValue("@t", new DateTime(2026, 8, 4, 12, 30, 0));
        command.Parameters.AddWithValue("@g", Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        command.Parameters.AddWithValue("@v", new byte[] { 1, 2, 3 });
        command.Parameters.AddWithValue("@n", DBNull.Value);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(9_000_000_000L, reader.GetInt64(0));
        Assert.Equal("text", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(1.5d, reader.GetDouble(3));
        Assert.Equal(new DateTime(2026, 8, 4), reader.GetDateTime(4));
        Assert.Equal(new DateTime(2026, 8, 4, 12, 30, 0), reader.GetDateTime(5));
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000ff"), reader.GetGuid(6));
        Assert.Equal("010203", Convert.ToHexString((byte[])reader.GetValue(7)));
        Assert.True(reader.IsDBNull(8));
    }

    [Fact]
    public void PreparedCommandsRunTwice()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM orders WHERE amount > @min", connection);
        var min = command.Parameters.Add("@min", System.Data.SqlDbType.Decimal);
        (min.Precision, min.Scale) = (10, 2);
        min.Value = 0m;
        command.Prepare();

        Assert.Equal(3L, command.ExecuteScalar());
        command.Parameters[0].Value = 25m;
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void WritesLandInTheWriteLayer()
    {
        using (var connection = Open())
        {
            using var insert = new SqlCommand("INSERT INTO orders (order_id, amount, note) VALUES (@id, @amount, @note)", connection);
            insert.Parameters.AddWithValue("@id", 4);
            insert.Parameters.AddWithValue("@amount", 44.00m);
            insert.Parameters.AddWithValue("@note", "written over TDS");
            Assert.Equal(1, insert.ExecuteNonQuery());

            using var update = new SqlCommand("UPDATE orders SET note = 'edited' WHERE order_id = 1", connection);
            Assert.Equal(1, update.ExecuteNonQuery());

            using var delete = new SqlCommand("DELETE FROM orders WHERE order_id = 2", connection);
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        // The same files the PostgreSQL side would have written, so a restart sees them.
        lake.Restart();
        Assert.Equal(["1|edited", "3|", "4|written over TDS"],
            lake.Query("SELECT order_id, note FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void TransactionsCommitAndRollBack()
    {
        using var connection = Open();

        using (var transaction = connection.BeginTransaction())
        {
            var insert = new SqlCommand("INSERT INTO orders (order_id, amount) VALUES (7001, 1)", connection, transaction);
            insert.ExecuteNonQuery();
            transaction.Rollback();
        }
        Assert.Equal(3, Count(connection));

        using (var transaction = connection.BeginTransaction())
        {
            var insert = new SqlCommand("INSERT INTO orders (order_id, amount) VALUES (7002, 1)", connection, transaction);
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        Assert.Equal(4, Count(connection));
    }

    [Fact]
    public void MultiStatementBatchesReportEachResult()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT 1; SELECT 2; SELECT 3", connection);
        using var reader = command.ExecuteReader();

        var results = new List<int>();
        do
        {
            while (reader.Read()) results.Add(reader.GetInt32(0));
        } while (reader.NextResult());

        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public void SessionOptionsAreAccepted()
    {
        using var connection = Open();
        new SqlCommand("SET NOCOUNT ON", connection).ExecuteNonQuery();
        new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", connection).ExecuteNonQuery();
        Assert.Equal(1, new SqlCommand("SELECT 1", connection).ExecuteScalar());
    }

    [Fact]
    public void SessionValuesAnswer()
    {
        using var connection = Open();
        Assert.Contains("duckpg", (string)new SqlCommand("SELECT @@VERSION", connection).ExecuteScalar());
    }

    [Fact]
    public void AFailedStatementLeavesTheConnectionUsable()
    {
        using var connection = Open();

        var error = Assert.Throws<SqlException>(() =>
            new SqlCommand("SELECT * FROM no_such_table", connection).ExecuteScalar());
        Assert.Equal(208, error.Number);

        Assert.Equal(1, new SqlCommand("SELECT 1", connection).ExecuteScalar());
    }

    [Fact]
    public void AStatementItCannotTranslateIsASyntaxError()
    {
        using var connection = Open();
        var error = Assert.Throws<SqlException>(() =>
            new SqlCommand("CREATE TABLE t (a INT)", connection).ExecuteNonQuery());
        Assert.Equal(102, error.Number);
        Assert.Contains("unsupported statement", error.Message);
    }

    [Fact]
    public void PoolingReturnsAUsableConnection()
    {
        for (var round = 0; round < 3; round++)
        {
            using var connection = Open();
            Assert.Equal(3, Count(connection));
        }
    }

    [Fact]
    public void ManyRowsStreamThrough()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT i, i * 2 AS doubled FROM range(50000) t(i)", connection);
        using var reader = command.ExecuteReader();

        long sum = 0;
        while (reader.Read()) sum += reader.GetInt64(1);
        Assert.Equal(49999L * 50000, sum);
    }

    [Fact]
    public void CancellationStopsTheQuery()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM range(100000000000)", connection) { CommandTimeout = 2 };
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(() => command.ExecuteScalar());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"cancellation took {watch.Elapsed}");
    }

    // DuckDB counts in BIGINT, so the value arrives as a long rather than SQL Server's int.
    static int Count(SqlConnection connection) =>
        Convert.ToInt32(new SqlCommand("SELECT COUNT(*) FROM orders", connection).ExecuteScalar());
}
