using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    /// Hands the client the server's bytes a few at a time, which is what a network does to a big
    /// response anyway -- only not on demand. SqlClient reassembles a read that ended mid-packet by
    /// replaying the framing it had begun, so a value or a row cut across the seam between two
    /// packets loses it its place, and the failure surfaces later as a length read out of payload.
    /// Nothing here is timing-dependent once the split is forced.
    sealed class SplitDelivery(int target, int piece) : IDisposable
    {
        readonly TcpListener listener = Bind();

        static TcpListener Bind()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        readonly MemoryStream captured = new();

        /// Everything the server sent, for checking the framing rather than the client's tolerance
        /// of it -- SqlClient survives some violations and not others, which is how one of these
        /// went unnoticed.
        public byte[] Captured { get { lock (captured) return captured.ToArray(); } }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public SplitDelivery Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var server = new TcpClient();
                    await server.ConnectAsync(IPAddress.Loopback, target);
                    client.NoDelay = server.NoDelay = true;
                    _ = Copy(client.GetStream(), server.GetStream(), int.MaxValue);
                    _ = Copy(server.GetStream(), client.GetStream(), piece, captured);
                }
            });
            return this;
        }

        static async Task Copy(Stream from, Stream to, int piece, MemoryStream? capture = null)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer);
                    if (read == 0) return;
                    if (capture is not null) lock (capture) capture.Write(buffer, 0, read);
                    for (var at = 0; at < read; at += piece)
                    {
                        await to.WriteAsync(buffer.AsMemory(at, Math.Min(piece, read - at)));
                        await to.FlushAsync();
                    }
                }
            }
            catch (Exception) { }
        }

        public void Dispose() => listener.Stop();
    }

    /// Twenty columns of a few characters each: a row far shorter than a packet, but long enough
    /// that packets fill several rows in and land inside one unless a row ends them.
    static string WideRows(int rows) =>
        "SELECT " + string.Join(", ", Enumerable.Range(0, 20).Select(i => $"repeat('c', 12) AS c{i}")) +
        $" FROM range({rows})";

    [Theory]
    // Rows wider than a packet, rows far narrower, rows narrower but wide enough to straddle, and a
    // packet size that makes every row span several -- the ways a row and a packet can line up.
    [InlineData("SELECT repeat('x', 1000) AS s FROM range(60)", 60, 4096)]
    [InlineData("SELECT 'y' AS s FROM range(4000)", 4000, 4096)]
    [InlineData("WIDE", 400, 4096)]
    [InlineData("WIDE", 400, 8000)]
    [InlineData("SELECT repeat('x', 1000) AS s FROM range(20)", 20, 512)]
    public void LongResultsSurviveASplitRead(string sql, int expected, int packet)
    {
        if (sql == "WIDE") sql = WideRows(expected);

        using var delivery = new SplitDelivery(lake.TdsPort, piece: 13).Start();
        using var connection = new SqlConnection(
            $"Server=127.0.0.1,{delivery.Port};Database=lake;User ID=sa;Password=duckpg;" +
            $"Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;Pooling=false;Packet Size={packet}");
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        using var reader = command.ExecuteReader();

        var rows = 0;
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++) reader.GetString(i);
            rows++;
        }

        Assert.Equal(expected, rows);
    }

    /// The invariant behind the test above, checked on the bytes instead of on the client: a row
    /// that fits in a packet is inside one. What a client does with a row cut across the seam
    /// varies -- what the server sends should not.
    [Fact]
    public void PacketsEndWhereRowsDo()
    {
        using var delivery = new SplitDelivery(lake.TdsPort, piece: 4096).Start();
        using var connection = new SqlConnection(
            $"Server=127.0.0.1,{delivery.Port};Database=lake;User ID=sa;Password=duckpg;" +
            "Encrypt=False;TrustServerCertificate=True;Connect Timeout=15;Pooling=false;Packet Size=8000");
        connection.Open();

        using (var command = new SqlCommand(WideRows(400), connection))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { }

        Thread.Sleep(200);
        var (boundaries, rows) = Frame(delivery.Captured);

        Assert.True(rows.Count == 400, $"walked {rows.Count} rows");
        Assert.True(boundaries.Count > 20, $"only {boundaries.Count} packets");
        Assert.DoesNotContain(rows, row => boundaries.Any(at => row.Start < at && at < row.End));
    }

    /// Reassembles the longest response in a capture, and reports where its packets ended and where
    /// its rows did. Every column of `WideRows` is a MAX string, so a row is the row token and one
    /// PLP value per column: eight bytes of total length, chunks, and an empty chunk to end it.
    static (List<int> Boundaries, List<(int Start, int End)> Rows) Frame(byte[] captured)
    {
        List<int> boundaries = [], longest = [];
        List<byte> message = [], result = [];

        for (var at = 0; at + 8 <= captured.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(captured.AsSpan(at + 2));
            message.AddRange(captured[(at + 8)..(at + length)]);
            boundaries.Add(message.Count);

            if ((captured[at + 1] & 1) != 0) // end of message
            {
                if (message.Count > result.Count) (result, longest) = (message, boundaries);
                (message, boundaries) = ([], []);
            }
            at += length;
        }

        var body = result.ToArray();
        var rows = new List<(int, int)>();
        var columns = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        var i = 3; // past the COLMETADATA token and its column count

        for (var c = 0; c < columns; c++)
        {
            i += 4 + 2 + 1 + 2 + 5;      // user type, flags, NVARCHAR token, length, collation
            i += 1 + body[i] * 2;        // name
        }

        while (i < body.Length && body[i] == 0xD1)
        {
            var start = i++;
            for (var c = 0; c < columns; c++)
            {
                i += 8;                  // total length
                for (var chunk = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(i));
                     chunk != 0;
                     chunk = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(i)))
                    i += 4 + chunk;
                i += 4;                  // the empty chunk
            }
            rows.Add((start, i));
        }

        return (longest, rows);
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

    /// An application written against SQL Server casts what COUNT returns to `int`, and DuckDB
    /// counts in BIGINT -- so the count comes back narrowed, and COUNT_BIG is how to ask for the
    /// wide one, exactly as it is on the database this stands in for.
    [Fact]
    public void CountIsAnIntAndCountBigIsNot()
    {
        using var connection = Open();

        using var count = new SqlCommand("SELECT COUNT(*) FROM orders", connection);
        Assert.Equal(3, Assert.IsType<int>(count.ExecuteScalar()));

        using var wide = new SqlCommand("SELECT COUNT_BIG(*) FROM orders", connection);
        Assert.Equal(3L, Assert.IsType<long>(wide.ExecuteScalar()));
    }

    /// DuckDB sums anything integral into a HUGEINT, which is wider than every SQL Server integer --
    /// so it goes out as the widest thing that is still a number. It used to go out as text, which
    /// is a number to nobody.
    [Fact]
    public void SumsAndWideIntegersAreNumbers()
    {
        using var connection = Open();
        using var command = new SqlCommand(
            "SELECT SUM(order_id) AS s, SUM(amount) AS a, CAST(9223372036854775810 AS UBIGINT) AS u, " +
            "CAST(4294967295 AS UINTEGER) AS i, CAST('2026-08-04 10:11:12' AS TIMESTAMP_MS) AS t FROM orders",
            connection);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(6m, Assert.IsType<decimal>(reader.GetValue(0)));
        Assert.Equal(60.50m, Assert.IsType<decimal>(reader.GetValue(1)));
        Assert.Equal(9223372036854775810m, Assert.IsType<decimal>(reader.GetValue(2)));
        Assert.Equal(4294967295L, Assert.IsType<long>(reader.GetValue(3)));
        Assert.Equal(new DateTime(2026, 8, 4, 10, 11, 12), Assert.IsType<DateTime>(reader.GetValue(4)));
    }

    /// What EF Core sends for `WHERE x IN (list)`: the list travels as one JSON parameter and is
    /// turned back into rows by OPENJSON, inside the derived table its own translation wraps
    /// everything in.
    [Fact]
    public void RunsWhatEfCoreSendsForAListParameter()
    {
        using var connection = Open();
        using var command = new SqlCommand(
            """
            SELECT [s0].[order_id]
            FROM (
                SELECT DISTINCT [s].[order_id], [s].[amount]
                FROM [orders] AS [s]
                WHERE [s].[order_id] IN (
                    SELECT [f].[value]
                    FROM OPENJSON(@ids) WITH ([value] int '$') AS [f]
                ) AND [s].[amount] > @min
            ) AS [s0]
            ORDER BY [s0].[order_id]
            """, connection);
        command.Parameters.AddWithValue("@ids", "[1,3]");
        command.Parameters.AddWithValue("@min", 5m);

        using var reader = command.ExecuteReader();
        var rows = new List<int>();
        while (reader.Read()) rows.Add(reader.GetInt32(0));

        Assert.Equal([1, 3], rows);
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

        Assert.Equal(1, command.ExecuteScalar());
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

    /// The legacy LOB types. Nothing on this side produces them, but an old client sends them:
    /// LLBLGen on `System.Data.SqlClient` types a string parameter as NTEXT.
    [Fact]
    public void ParametersCarryTheLegacyLobTypes()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @n AS n, @t AS t, @i AS i, @e AS e", connection);
        command.Parameters.Add("@n", System.Data.SqlDbType.NText).Value = new string('n', 9000);
        command.Parameters.Add("@t", System.Data.SqlDbType.Text).Value = "text";
        command.Parameters.Add("@i", System.Data.SqlDbType.Image).Value = new byte[] { 1, 2, 3 };
        command.Parameters.Add("@e", System.Data.SqlDbType.NText).Value = DBNull.Value;

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(new string('n', 9000), reader.GetString(0));
        Assert.Equal("text", reader.GetString(1));
        Assert.Equal("010203", Convert.ToHexString((byte[])reader.GetValue(2)));
        Assert.True(reader.IsDBNull(3));
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

        Assert.Equal(3, command.ExecuteScalar());
        command.Parameters[0].Value = 25m;
        Assert.Equal(1, command.ExecuteScalar());
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

    /// What EF Core sends to update many rows at once: the rows travel as one JSON array and MERGE
    /// joins them to the target on the key, so the rewriting has to carry both the target's alias
    /// and the source into the statements that replace the rows.
    [Fact]
    public void MergesWhatAnOrmSendsForABulkUpdate()
    {
        using (var connection = Open())
        {
            using var merge = new SqlCommand(
                """
                MERGE INTO [orders] o
                USING OPENJSON(@rows) WITH (order_id INT '$[0]', amount DECIMAL(10,2) '$[1]', note NVARCHAR(50) '$[2]') f
                ON o.order_id = f.order_id
                WHEN MATCHED THEN UPDATE SET o.amount = f.amount, o.note = f.note
                """, connection);
            merge.Parameters.AddWithValue("@rows", """[[1,1.25,"merged one"],[3,3.75,"merged three"]]""");
            Assert.Equal(2, merge.ExecuteNonQuery());
        }

        lake.Restart();
        Assert.Equal(["1|1.25|merged one", "2|20.00|second", "3|3.75|merged three"],
            lake.Query("SELECT order_id, amount, note FROM lake.orders ORDER BY order_id"));
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
