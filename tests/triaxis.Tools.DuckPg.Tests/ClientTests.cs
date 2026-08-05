using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace triaxis.Tools.DuckPg.Tests;

/// Npgsql is the client held to a conformance bar: the paths a real application takes, not just
/// SELECT 1. The open-ended cost of a PostgreSQL frontend is emulating pg_catalog well enough for
/// each client, and against one known client that surface is finite -- so this is the spec.
public class ClientTests : IDisposable
{
    readonly TestLake lake = new TestLake("client")
        .Parquet("base", "orders", """
            SELECT * FROM (VALUES
                (1, 10.50::DECIMAL(10,2), DATE '2026-08-01'),
                (2, 20.00::DECIMAL(10,2), DATE '2026-08-02'),
                (3, 30.00::DECIMAL(10,2), DATE '2026-08-03')
            ) t(order_id, amount, ordered_on)
            """)
        .Stack("base")
        .WriteTo("local");

    readonly NpgsqlDataSource db;

    public ClientTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();
        db = new NpgsqlDataSourceBuilder(lake.ConnectionString()).Build();
    }

    public void Dispose()
    {
        db.Dispose();
        lake.Dispose();
    }

    [Fact]
    public void TypedReadsComeBackTyped()
    {
        using var command = db.CreateCommand(
            "SELECT order_id, amount, ordered_on FROM lake.orders WHERE amount > $1 ORDER BY order_id");
        command.Parameters.AddWithValue(15m);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}/{reader.GetDecimal(1)}/{reader.GetDateTime(2):yyyy-MM-dd}");

        Assert.Equal(["2/20.00/2026-08-02", "3/30.00/2026-08-03"], rows);
    }

    /// Rows go out through a buffer that is flushed when full and a row buffer that grows to fit
    /// the widest value, and neither seam shows on a three-row result: this one is wide enough to
    /// grow the row, long enough to flush many times, and multi-byte so the two cannot agree on a
    /// length by accident.
    [Fact]
    public void LongResultsStreamPastTheBuffers()
    {
        var wide = new string('č', 2000);
        using var command = db.CreateCommand(
            $"SELECT i, repeat('č', 2000) || i, CASE WHEN i % 2 = 0 THEN NULL ELSE i * 1.5 END " +
            "FROM range(5000) t(i)");
        using var reader = command.ExecuteReader();

        var rows = 0;
        while (reader.Read())
        {
            Assert.Equal(rows, reader.GetInt64(0));
            Assert.Equal(wide + rows, reader.GetString(1));
            Assert.Equal(rows % 2 == 0, reader.IsDBNull(2));
            if (!reader.IsDBNull(2)) Assert.Equal(rows * 1.5, reader.GetDouble(2));
            rows++;
        }

        Assert.Equal(5000, rows);
    }

    [Fact]
    public void ColumnSchemaCarriesTheRealTypes()
    {
        using var command = db.CreateCommand("SELECT order_id, amount, ordered_on FROM lake.orders LIMIT 1");
        using var reader = command.ExecuteReader();

        Assert.Equal(["integer:Int32", "numeric:Decimal", "date:DateOnly"],
            reader.GetColumnSchema().Select(c => $"{c.DataTypeName}:{c.DataType?.Name}"));
    }

    /// The reader names its types its own way -- `UnsignedBigInt`, `TimestampMs` -- and a name this
    /// side does not know goes out as text. A summed column is a HUGEINT, and arrived as one.
    [Fact]
    public void TheWiderNumbersAndTheSubSecondStampsAreThemselves()
    {
        using var command = db.CreateCommand(
            "SELECT SUM(order_id) AS s, CAST(9223372036854775810 AS UBIGINT) AS u, " +
            "CAST(4294967295 AS UINTEGER) AS i, CAST('2026-08-04 10:11:12' AS TIMESTAMP_MS) AS t " +
            "FROM lake.orders");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(["numeric", "numeric", "bigint", "timestamp without time zone"],
            reader.GetColumnSchema().Select(c => c.DataTypeName));
        Assert.Equal(6m, reader.GetDecimal(0));
        Assert.Equal(9223372036854775810m, reader.GetDecimal(1));
        Assert.Equal(4294967295L, reader.GetInt64(2));
        Assert.Equal(new DateTime(2026, 8, 4, 10, 11, 12), reader.GetDateTime(3));
    }

    [Fact]
    public void ParametersRoundTripInEveryTypeNpgsqlSends()
    {
        using var command = db.CreateCommand(
            "SELECT $1::BIGINT, $2::VARCHAR, $3::BOOLEAN, $4::DOUBLE, $5::DATE, $6::TIMESTAMP, $7::UUID, $8::BLOB, $9::VARCHAR");
        command.Parameters.AddWithValue(9_000_000_000L);
        command.Parameters.AddWithValue("text");
        command.Parameters.AddWithValue(true);
        command.Parameters.AddWithValue(1.5d);
        command.Parameters.AddWithValue(new DateTime(2026, 8, 4));
        command.Parameters.AddWithValue(new DateTime(2026, 8, 4, 12, 30, 0));
        command.Parameters.AddWithValue(Guid.Parse("00000000-0000-0000-0000-0000000000ff"));
        command.Parameters.AddWithValue(new byte[] { 1, 2, 3 });
        command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });

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
    public void PrepareLearnsTheShapeWithoutRunningTheQuery()
    {
        using var connection = db.OpenConnection();
        using var command = new NpgsqlCommand("SELECT count(*) FROM lake.orders WHERE amount > $1", connection);
        command.Parameters.AddWithValue(0m);
        command.Prepare();

        Assert.True(command.IsPrepared);
        Assert.Equal(3L, command.ExecuteScalar());

        command.Parameters[0].Value = 1000m;
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void TransactionsCommitAndRollBack()
    {
        using var connection = db.OpenConnection();

        using (var transaction = connection.BeginTransaction())
        {
            Insert(connection, transaction, 7001);
            transaction.Rollback();
        }
        Assert.Equal(3L, Count(connection));

        using (var transaction = connection.BeginTransaction())
        {
            Insert(connection, transaction, 7002);
            transaction.Commit();
        }
        Assert.Equal(4L, Count(connection));
    }

    [Fact]
    public void DmlReportsWhatItTouched()
    {
        using var insert = db.CreateCommand("INSERT INTO lake.orders (order_id, amount) VALUES (7003, 42.50)");
        Assert.Equal(1, insert.ExecuteNonQuery());

        using var update = db.CreateCommand("UPDATE lake.orders SET amount = $1 WHERE order_id = 7003");
        update.Parameters.AddWithValue(43.5m);
        Assert.Equal(1, update.ExecuteNonQuery());

        using var wide = db.CreateCommand("UPDATE lake.orders SET amount = amount WHERE order_id < 3");
        Assert.Equal(2, wide.ExecuteNonQuery());

        using var delete = db.CreateCommand("DELETE FROM lake.orders WHERE order_id = 7003");
        Assert.Equal(1, delete.ExecuteNonQuery());
    }

    [Fact]
    public void BatchesRunAsOneRoundTrip()
    {
        using var batch = db.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT count(*) FROM lake.orders"));
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SELECT max(order_id) FROM lake.orders"));

        using var reader = batch.ExecuteReader();
        var results = new List<string>();
        do
        {
            while (reader.Read()) results.Add(reader.GetValue(0).ToString()!);
        } while (reader.NextResult());

        Assert.Equal(["3", "3"], results);
    }

    [Fact]
    public void PortalsSuspendAndResume()
    {
        using var connection = db.OpenConnection();
        using var command = new NpgsqlCommand("SELECT order_id FROM lake.orders ORDER BY order_id", connection);
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SingleRow);

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
    }

    [Fact]
    public void AFailedStatementLeavesTheConnectionUsable()
    {
        using var connection = db.OpenConnection();

        var error = Assert.Throws<PostgresException>(() =>
            new NpgsqlCommand("SELECT * FROM lake.no_such_table", connection).ExecuteScalar());
        // Npgsql closes the connection on the catch-all XX000, so an honest SQLSTATE is what makes
        // the failure recoverable.
        Assert.Equal("42P01", error.SqlState);

        Assert.Equal(1, new NpgsqlCommand("SELECT 1", connection).ExecuteScalar());
    }

    [Fact]
    public void ASyntaxErrorIsASyntaxError()
    {
        using var connection = db.OpenConnection();
        var error = Assert.Throws<PostgresException>(() =>
            new NpgsqlCommand("SELECT FROM WHERE", connection).ExecuteScalar());
        Assert.Equal("42601", error.SqlState);
    }

    [Fact]
    public void PoolingReturnsAUsableConnection()
    {
        // Returning a connection to the pool fires SET SESSION AUTHORIZATION DEFAULT, RESET ALL,
        // CLOSE ALL, UNLISTEN * and pg_advisory_unlock_all(); any one erroring poisons the pool.
        for (var round = 0; round < 3; round++)
        {
            using var connection = db.OpenConnection();
            Assert.Equal(3L, Count(connection));
        }
    }

    [Fact]
    public void CancellationInterruptsTheQuery()
    {
        using var command = db.CreateCommand("SELECT count(*) FROM range(100000000000)");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => command.ExecuteScalarAsync(cancellation.Token).GetAwaiter().GetResult());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"cancellation took {watch.Elapsed}");
    }

    [Fact]
    public void NestedTypesArriveAsJson()
    {
        using var command = db.CreateCommand("SELECT [1,2,3] AS list, {'a': 1} AS record, NULL::INTEGER AS missing");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal("[1,2,3]", reader.GetString(0));
        Assert.Equal("""{"a":1}""", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void ManyRowsStreamThrough()
    {
        using var command = db.CreateCommand("SELECT i, i * 2 AS doubled FROM range(200000) t(i)");
        using var reader = command.ExecuteReader();

        long sum = 0;
        while (reader.Read()) sum += reader.GetInt64(1);
        Assert.Equal(199999L * 200000, sum);
    }

    [Fact]
    public void SettingsClientsPokeAtAreAnswered()
    {
        Assert.Equal("UTF8", db.CreateCommand("SHOW client_encoding").ExecuteScalar());

        using var connection = db.OpenConnection();
        new NpgsqlCommand("SET extra_float_digits = 3", connection).ExecuteNonQuery();
        Assert.Equal(1, new NpgsqlCommand("SELECT 1", connection).ExecuteScalar());
    }

    static void Insert(NpgsqlConnection connection, NpgsqlTransaction transaction, int id)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO lake.orders (order_id, amount) VALUES ($1, 5)", connection, transaction);
        command.Parameters.AddWithValue(id);
        command.ExecuteNonQuery();
    }

    static long Count(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand("SELECT count(*) FROM lake.orders", connection);
        return (long)command.ExecuteScalar()!;
    }
}
