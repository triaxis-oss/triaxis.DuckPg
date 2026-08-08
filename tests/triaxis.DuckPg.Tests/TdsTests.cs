using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

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

    /// EF Core sends a batch of rows as a MERGE whose condition cannot match: the rows are the
    /// USING clause and the insert branch takes all of them. It lands in the write layer like any
    /// other insert, one statement and one count.
    [Fact]
    public void BatchInsertsArriveAsOneStatement()
    {
        using (var connection = Open())
        {
            using var command = new SqlCommand(
                "MERGE [orders] USING (VALUES (@p0, @p1, 0), (@p2, @p3, 1)) AS i ([order_id], [amount], _Position) " +
                "ON 1=0 WHEN NOT MATCHED THEN INSERT ([order_id], [amount]) VALUES (i.[order_id], i.[amount]);",
                connection);
            command.Parameters.AddWithValue("@p0", 7010);
            command.Parameters.AddWithValue("@p1", 1.25m);
            command.Parameters.AddWithValue("@p2", 7011);
            command.Parameters.AddWithValue("@p3", 2.50m);

            Assert.Equal(2, command.ExecuteNonQuery());
        }

        lake.Restart();
        Assert.Equal(["7010|1.25", "7011|2.50"],
            lake.Query("SELECT order_id, amount FROM lake.orders WHERE order_id >= 7010 ORDER BY order_id"));
    }

    /// What EF Core's ExecuteDelete writes: the target is the alias, and the FROM clause is what
    /// binds it to a table.
    [Fact]
    public void ADeleteCanNameItsTargetByAlias()
    {
        using var connection = Open();

        Assert.Equal(2, new SqlCommand(
            "DELETE FROM [s] FROM [orders] AS [s] WHERE [s].[order_id] < 3", connection).ExecuteNonQuery());
        Assert.Equal(1, Count(connection));

        lake.Restart();
        Assert.Equal(["3"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    /// The other write that can name its target by an alias, which is what EF Core's ExecuteUpdate
    /// writes.
    [Fact]
    public void AnUpdateCanNameItsTargetByAlias()
    {
        using var connection = Open();

        Assert.Equal(2, new SqlCommand(
            "UPDATE [o] SET [note] = 'aliased' FROM [orders] AS [o] WHERE [o].[order_id] < 3",
            connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["aliased", "aliased", ""],
            lake.Query("SELECT coalesce(note, '') FROM lake.orders ORDER BY order_id"));
    }

    /// A write filtered through a join: the target is an alias, and the other table is what says
    /// which of its rows to touch. Both tables carry an `order_id`, so the keys the plan collects
    /// have to say which side they came from.
    [Fact]
    public void AWriteCanBeFilteredThroughAJoin()
    {
        using var connection = Open();
        new SqlCommand("SELECT o.order_id, o.note INTO #tagged FROM orders o WHERE o.order_id = 2",
                       connection).ExecuteNonQuery();

        Assert.Equal(1, new SqlCommand(
            "UPDATE [s] SET [note] = 'joined' FROM [orders] AS [s] " +
            "INNER LOOP JOIN [#tagged] AS [t] ON [t].[order_id] = [s].[order_id]", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["first", "joined", ""],
            lake.Query("SELECT coalesce(note, '') FROM lake.orders ORDER BY order_id"));
    }

    /// `OUTPUT 1` counts the rows a statement touched, which is all EF Core reads it for -- so it is
    /// one row per row written, and the count SqlClient reports is that many.
    [Fact]
    public void AWriteAnswersWithARowPerRowItTouched()
    {
        using var connection = Open();

        // The batch EF sends it in, session options and all.
        Assert.Equal(2, new SqlCommand(
            "SET IMPLICIT_TRANSACTIONS OFF; SET NOCOUNT ON; " +
            "UPDATE [orders] SET [note] = 'x' OUTPUT 1 WHERE [order_id] < 3;", connection).ExecuteNonQuery());

        Assert.Equal(["x", "x"], Rows(connection, "SELECT note FROM orders WHERE order_id < 3 ORDER BY order_id"));

        Assert.Equal(1, new SqlCommand(
            "DELETE FROM [orders] OUTPUT 1 WHERE [order_id] = 3", connection).ExecuteNonQuery());
        Assert.Equal(2, Count(connection));
    }

    static List<string> Rows(SqlConnection connection, string sql)
    {
        var rows = new List<string>();
        using var reader = new SqlCommand(sql, connection).ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetValue(0)?.ToString() ?? "");
        return rows;
    }

    /// The shape a tool takes a snapshot with: drop what a previous run left, select the rows into
    /// a scratch table, work against it. The temporary table belongs to the connection, so it also
    /// has to be gone from the next session to be handed this one.
    [Fact]
    public void TemporaryTablesAreMadeAndDropped()
    {
        using var connection = Open();

        new SqlCommand("DROP TABLE IF EXISTS #snapshot", connection).ExecuteNonQuery();
        new SqlCommand("SELECT o.order_id, o.amount INTO #snapshot FROM orders o WHERE o.order_id < 3",
                       connection).ExecuteNonQuery();

        Assert.Equal(2, new SqlCommand("SELECT COUNT(*) FROM #snapshot", connection).ExecuteScalar());
        Assert.Equal(30.50m, new SqlCommand(
            "SELECT SUM(s.amount) FROM #snapshot s JOIN orders o ON o.order_id = s.order_id",
            connection).ExecuteScalar());

        new SqlCommand("DROP TABLE #snapshot", connection).ExecuteNonQuery();
        Assert.Throws<SqlException>(() =>
            new SqlCommand("SELECT COUNT(*) FROM #snapshot", connection).ExecuteScalar());
    }

    /// A pooled connection is handed back out as a new session, and what the old one made goes with
    /// it -- SqlClient resets the connection it reuses, and the reset is where the dropping happens.
    [Fact]
    public void PooledConnectionsDoNotInheritTemporaryTables()
    {
        using (var connection = Open())
            new SqlCommand("SELECT o.order_id INTO #left_behind FROM orders o", connection).ExecuteNonQuery();

        using (var connection = Open())
            Assert.Throws<SqlException>(() =>
                new SqlCommand("SELECT COUNT(*) FROM #left_behind", connection).ExecuteScalar());
    }

    /// EF Core marks a savepoint whenever it saves inside a transaction of the caller's, and rolls
    /// back to it when that save fails. Marking one is free; rolling back to one is refused, since
    /// the alternative is keeping writes the caller asked to be rid of.
    [Fact]
    public void SavepointsAreMarkedAndNotRolledBackTo()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        new SqlCommand("SAVE TRANSACTION __ef_savepoint", connection, transaction).ExecuteNonQuery();
        new SqlCommand("INSERT INTO orders (order_id, amount) VALUES (7004, 1)", connection, transaction)
            .ExecuteNonQuery();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("ROLLBACK TRANSACTION __ef_savepoint", connection, transaction).ExecuteNonQuery());
        Assert.Contains("cannot be honoured", refused.Message);

        transaction.Commit();
        Assert.Equal(4, Count(connection));
    }

    /// An application takes one to serialize itself against the other connections of a database,
    /// which a lake has none of; what it must not do is fail, since the work is inside the
    /// transaction the lock was taken for.
    [Fact]
    public void ApplicationLocksAreGranted()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        new SqlCommand("EXEC sp_getapplock @Resource = 'orders:reload', " +
                       "@LockMode = 'Exclusive', @LockOwner = 'Transaction'", connection, transaction)
            .ExecuteNonQuery();

        new SqlCommand("INSERT INTO orders (order_id, amount) VALUES (7003, 1)", connection, transaction)
            .ExecuteNonQuery();

        new SqlCommand("EXEC sp_releaseapplock @Resource = 'orders:reload', " +
                       "@LockOwner = 'Transaction'", connection, transaction)
            .ExecuteNonQuery();

        transaction.Commit();
        Assert.Equal(4, Count(connection));
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

    /// A session value goes into the statement as a literal, so a batch has to be rendered a
    /// statement at a time -- rendered all at once, the second one answers with what the first had
    /// not yet changed. This is the same rule `INSERT …; SELECT SCOPE_IDENTITY()` depends on.
    [Fact]
    public void ASessionValueIsWhatItIsWhenTheStatementRuns()
    {
        using var connection = Open();
        using var reader = new SqlCommand("SELECT * FROM orders; SELECT @@ROWCOUNT", connection).ExecuteReader();

        while (reader.Read()) { }
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
    }

    /// `SELECT @out = …` assigns rather than returns, and what it assigned goes back in the call's
    /// return values -- which is the only way a caller running `ExecuteNonQuery` ever sees it.
    [Fact]
    public void AnAssignmentSelectAnswersTheOutputParameter()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @out = 42", connection);
        var parameter = command.Parameters.Add("@out", System.Data.SqlDbType.Int);
        parameter.Direction = System.Data.ParameterDirection.Output;

        command.ExecuteNonQuery();
        Assert.Equal(42, parameter.Value);
    }

    /// The value comes back in the type the caller declared rather than the one DuckDB answered in,
    /// since an application reads it as what it asked for.
    [Theory]
    [InlineData(System.Data.SqlDbType.Int, "42", 42)]
    [InlineData(System.Data.SqlDbType.BigInt, "42", 42L)]
    [InlineData(System.Data.SqlDbType.NVarChar, "'text'", "text")]
    [InlineData(System.Data.SqlDbType.Bit, "CAST(1 AS BIT)", true)]
    public void AnOutputParameterKeepsTheTypeItWasDeclared(System.Data.SqlDbType type, string value, object expected)
    {
        using var connection = Open();
        using var command = new SqlCommand($"SELECT @out = {value}", connection);
        var parameter = command.Parameters.Add("@out", type, 50);
        parameter.Direction = System.Data.ParameterDirection.Output;

        command.ExecuteNonQuery();
        Assert.Equal(expected, parameter.Value);
    }

    /// A query that found nothing assigns nothing, and the parameter comes back as it went in --
    /// which is what an input/output parameter of a procedure that never touched it does.
    [Fact]
    public void AnUnassignedOutputParameterComesBackUnchanged()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @out = order_id FROM orders WHERE order_id = 999", connection);
        var parameter = command.Parameters.Add("@out", System.Data.SqlDbType.Int);
        parameter.Direction = System.Data.ParameterDirection.InputOutput;
        parameter.Value = 7;

        command.ExecuteNonQuery();
        Assert.Equal(7, parameter.Value);
    }

    /// Several at once, and each to its own name: the answer is matched by the name the caller gave
    /// the parameter, not by the order the values were assigned in.
    [Fact]
    public void SeveralOutputParametersAreAnsweredByName()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @b = 2, @a = 1", connection);
        var a = command.Parameters.Add("@a", System.Data.SqlDbType.Int);
        var b = command.Parameters.Add("@b", System.Data.SqlDbType.Int);
        a.Direction = System.Data.ParameterDirection.Output;
        b.Direction = System.Data.ParameterDirection.Output;

        command.ExecuteNonQuery();
        Assert.Equal(1, a.Value);
        Assert.Equal(2, b.Value);
    }

    /// The last row wins, which is what SQL Server does with a select that assigns from several.
    [Fact]
    public void AnAssignmentSelectTakesTheLastRow()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @out = order_id FROM orders ORDER BY order_id", connection);
        var parameter = command.Parameters.Add("@out", System.Data.SqlDbType.Int);
        parameter.Direction = System.Data.ParameterDirection.Output;

        command.ExecuteNonQuery();
        Assert.Equal(3, parameter.Value);
    }

    /// Assigning and returning at once is neither, and SQL Server refuses it too.
    [Fact]
    public void ASelectCannotBothAssignAndReturn()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT @out = 1, 2", connection);
        var parameter = command.Parameters.Add("@out", System.Data.SqlDbType.Int);
        parameter.Direction = System.Data.ParameterDirection.Output;

        var error = Assert.Throws<SqlException>(() => command.ExecuteNonQuery());
        Assert.Contains("assigns to variables or returns rows", error.Message);
    }

    /// Three tables an entity's relation graph joins, one entry pointing at a batch that is not
    /// there. That row is the point of the fixture: it is in the result of a LEFT JOIN and not of an
    /// INNER one, so a rewrite that confused the two would quietly leave it alone.
    static TestLake Related([CallerMemberName] string name = "")
    {
        var lake = new TestLake(name)
            .Json("base", "categories", """[{"pKey": 1, "name": "first"}, {"pKey": 2, "name": "second"}]""")
            .Json("base", "batches", """[{"pKey": 10, "label": "one"}]""")
            .Json("base", "entries", """
                [{"pKey": 100, "category_pKey": 1, "batch_pKey": 10,   "revision_pKey": 7},
                 {"pKey": 101, "category_pKey": 2, "batch_pKey": 999,  "revision_pKey": 7},
                 {"pKey": 102, "category_pKey": 1, "batch_pKey": 10,   "revision_pKey": 8}]
                """)
            .Stack("base")
            .WriteTo("local")
            .WithTds();
        lake.Config.DefaultKey = ["pKey"];
        return lake.Start();
    }

    /// The delete an ORM renders for an entity with relations: every name in full, the target inside
    /// its own parenthesised join tree rather than aliased beside it, and the entity's whole relation
    /// graph written out -- including a LEFT JOIN the statement never reads.
    [Fact]
    public void TheJoinTreeAnOrmWritesOutDeletesTheRightRows()
    {
        using var lake = Related();
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand("""
            DELETE FROM [lake].[dbo].[entries]
            FROM (([lake].[dbo].[categories]
                   INNER JOIN [lake].[dbo].[entries]
                     ON [lake].[dbo].[categories].[pKey]=[lake].[dbo].[entries].[category_pKey])
                  LEFT JOIN [lake].[dbo].[batches]
                     ON [lake].[dbo].[batches].[pKey]=[lake].[dbo].[entries].[batch_pKey])
            WHERE ([lake].[dbo].[entries].[revision_pKey] = @p1)
            """, connection);
        command.Parameters.AddWithValue("@p1", 7);

        // Both rows of revision 7 go, the one matching no batch among them.
        Assert.Equal(2, command.ExecuteNonQuery());
        Assert.Equal(["102|8"], lake.Query("SELECT pKey, revision_pKey FROM lake.entries ORDER BY pKey"));

        // And it survives the restart, which is what says the tombstones went to the files.
        lake.Restart();
        Assert.Equal(["102|8"], lake.Query("SELECT pKey, revision_pKey FROM lake.entries ORDER BY pKey"));
    }

    /// The same shape on the other write, since an ORM that renders a relation graph for a delete
    /// renders one for an update too.
    [Fact]
    public void TheJoinTreeAnOrmWritesOutUpdatesTheRightRows()
    {
        using var lake = Related();
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand("""
            UPDATE [lake].[dbo].[entries] SET [revision_pKey] = @p0
            FROM (([lake].[dbo].[categories]
                   INNER JOIN [lake].[dbo].[entries]
                     ON [lake].[dbo].[categories].[pKey]=[lake].[dbo].[entries].[category_pKey])
                  LEFT JOIN [lake].[dbo].[batches]
                     ON [lake].[dbo].[batches].[pKey]=[lake].[dbo].[entries].[batch_pKey])
            WHERE ([lake].[dbo].[entries].[revision_pKey] = @p1)
            """, connection);
        command.Parameters.AddWithValue("@p0", 9);
        command.Parameters.AddWithValue("@p1", 7);

        Assert.Equal(2, command.ExecuteNonQuery());
        Assert.Equal(["100|9", "101|9", "102|8"],
            lake.Query("SELECT pKey, revision_pKey FROM lake.entries ORDER BY pKey"));
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

    /// DuckDB names an unaliased column after the expression that produced it, and COLMETADATA
    /// counts a name in a single byte: at 256 characters the length wraps to zero, the client reads
    /// the bytes after it as the next token, and the connection dies rather than the query failing.
    /// SQL Server caps an identifier at 128, which is where this cuts.
    [Theory]
    [InlineData(23)]    // a 252-character name: what already fitted
    [InlineData(24)]    // 263, whose length wrapped to 7
    [InlineData(60)]    // 659, and well past it
    public void ALongColumnNameDoesNotDerailTheTokenStream(int terms)
    {
        using var connection = Open();
        var expression = string.Join(" + ", Enumerable.Repeat("[order_id]", terms));
        using var command = new SqlCommand($"SELECT {expression} FROM [orders] WHERE [order_id] = 1", connection);
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(terms, reader.GetInt32(0));
        Assert.InRange(reader.GetName(0).Length, 1, 128);
        Assert.False(reader.Read());
    }

    // DuckDB counts in BIGINT, so the value arrives as a long rather than SQL Server's int.
    static int Count(SqlConnection connection) =>
        Convert.ToInt32(new SqlCommand("SELECT COUNT(*) FROM orders", connection).ExecuteScalar());
}
