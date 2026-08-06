using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// Sessions have a DuckDB connection each, but a lake keeps one of its own for the work that is
/// nobody's session -- persisting a table, seeding a sequence, rebuilding the catalog. A DuckDB
/// connection is not two threads' to share, so what runs on that one has to be serialized: what
/// concurrent writers see otherwise is not an error but a connection that dies mid-answer.
/// What two writers do to each other is the other half, and an option: DuckDB refuses the second
/// one rather than making it wait.
public class ConcurrencyTests
{
    const int Tables = 12, Writers = 8, Each = 8;

    static Dacpac.TableModel Table(int i) =>
        new($"t{i}", [("id", "int"), ("amount", "int")], ["id"], Identity: ["id"]);

    [Fact]
    public void ConcurrentWritersDoNotCollideOnTheLakesOwnConnection()
    {
        using var lake = new TestLake()
            .Stack("base")
            .WriteTo("local")
            .WithTds();

        // Every table is writable and carries nothing, so a first write to each has to promote it --
        // which seeds the sequence its identity draws from, on the lake's own connection.
        foreach (var i in Enumerable.Range(0, Tables))
            lake.Json("base", $"t{i}", $$"""[{"id": {{i + 1}}, "amount": 0}]""");

        Dacpac.Write(lake.At("schema", "test.dacpac"), [.. Enumerable.Range(0, Tables).Select(Table)]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        // Every table earns its write branch here rather than in the race below. Two transactions
        // creating the same branch is a catalog write-write conflict -- DuckDB's own answer, which
        // `serializeTransactions` is what prevents, and not what this test is about. What it is
        // about is the lake's single connection, which the persists and the sequence seeding below
        // still hammer from eight threads at once.
        foreach (var i in Enumerable.Range(0, Tables))
            lake.Execute($"UPDATE lake.t{i} SET amount = 0 WHERE id = {i + 1}");

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, Writers, writer =>
        {
            try
            {
                using var connection = new SqlConnection(lake.SqlConnectionString());
                connection.Open();

                for (var round = 0; round < Each; round++)
                {
                    // A table nobody has written to yet, and one everybody is writing to: a
                    // promotion racing the persists of every other writer.
                    var table = $"t{1 + (writer * Each + round) % (Tables - 1)}";
                    new SqlCommand($"INSERT INTO [{table}] ([amount]) VALUES ({writer})", connection)
                        .ExecuteNonQuery();
                    new SqlCommand("INSERT INTO [t0] ([amount]) VALUES (1)", connection).ExecuteNonQuery();
                    new SqlCommand($"SELECT COUNT(*) FROM [{table}]", connection).ExecuteScalar();
                }
            }
            catch (Exception e)
            {
                failures.Add(e.Message.ReplaceLineEndings(" "));
            }
        });

        Assert.Empty(failures);

        // Every write landed, and the shared table got one row from every round of every writer.
        Assert.Equal([$"{1 + Writers * Each}"], lake.Query("SELECT COUNT(*) FROM lake.t0"));
    }

    static TestLake Rows(bool serialize, bool promoted = true, [CallerMemberName] string name = "")
    {
        var lake = new TestLake(name)
            .Json("base", "t", """[{"id": 1, "amount": 0}, {"id": 2, "amount": 0}]""")
            .Stack("base")
            .WriteTo("local")
            .WithTds();
        lake.Config.DefaultKey = ["id"];
        lake.Config.SerializeTransactions = serialize;
        lake.Start();

        // A first write earns the table its write branch, and that is DDL two transactions conflict
        // over as surely as a row does. Earned here, so what follows is about the row -- except
        // where the test is about the branch being made, and then it has to still be missing.
        if (promoted) lake.Execute("UPDATE lake.t SET amount = 0 WHERE id = 1");
        return lake;
    }

    SqlConnection Writer(TestLake lake, string sql)
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        new SqlCommand("BEGIN TRANSACTION", connection).ExecuteNonQuery();
        new SqlCommand(sql, connection).ExecuteNonQuery();
        return connection;
    }

    /// What a lake serving one application does by default: DuckDB refuses the second writer's
    /// statement outright rather than making it wait, which is the behavior the option exists for.
    [Fact]
    public void ASecondWriterIsRefusedWhileTheFirstIsOpen()
    {
        using var lake = Rows(serialize: false);
        using var first = Writer(lake, "UPDATE [t] SET [amount] = 1 WHERE [id] = 1");
        using var second = new SqlConnection(lake.SqlConnectionString());
        second.Open();
        new SqlCommand("BEGIN TRANSACTION", second).ExecuteNonQuery();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("UPDATE [t] SET [amount] = 2 WHERE [id] = 1", second).ExecuteNonQuery());
        Assert.Contains("Conflict on tuple", refused.Message);
    }

    /// Asked to serialize, the lake makes it wait instead -- and once the transaction in front of it
    /// commits, the write it was refused before goes through.
    [Fact]
    public async Task ASerializedLakeMakesItWaitInstead()
    {
        using var lake = Rows(serialize: true);
        using var first = Writer(lake, "UPDATE [t] SET [amount] = 1 WHERE [id] = 1");

        // The whole of the second transaction waits, its BEGIN included -- that is the point: a
        // transaction that began here would take its catalog snapshot before the first one commits.
        var cancellation = TestContext.Current.CancellationToken;
        var waiting = Task.Run(() =>
        {
            using var second = new SqlConnection(lake.SqlConnectionString());
            second.Open();
            new SqlCommand("BEGIN TRANSACTION", second).ExecuteNonQuery();
            var affected = new SqlCommand("UPDATE [t] SET [amount] = 2 WHERE [id] = 1", second).ExecuteNonQuery();
            new SqlCommand("COMMIT", second).ExecuteNonQuery();
            return affected;
        }, cancellation);

        // It is the first transaction's turn until it ends, and nothing hurries it along.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation);
        Assert.False(waiting.IsCompleted);
        new SqlCommand("COMMIT", first).ExecuteNonQuery();

        Assert.Same(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        Assert.Equal(1, await waiting);

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT amount FROM lake.t WHERE id = 1"));
    }

    /// A session that already has the turn to write does not queue behind itself: it takes the lock
    /// at its first write and holds it, so every write after that in the same transaction is one it
    /// already has the right to make.
    [Fact]
    public async Task ASecondWriteInTheSameTransactionDoesNotWaitForItself()
    {
        using var lake = Rows(serialize: true);
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        new SqlCommand("BEGIN TRANSACTION", connection).ExecuteNonQuery();

        var cancellation = TestContext.Current.CancellationToken;
        var writing = Task.Run(() =>
        {
            new SqlCommand("UPDATE [t] SET [amount] = 1 WHERE [id] = 1", connection).ExecuteNonQuery();
            new SqlCommand("UPDATE [t] SET [amount] = 2 WHERE [id] = 1", connection).ExecuteNonQuery();
            new SqlCommand("COMMIT", connection).ExecuteNonQuery();
        }, cancellation);

        Assert.Same(writing, await Task.WhenAny(writing, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        await writing;

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT amount FROM lake.t WHERE id = 1"));
    }

    /// And the other front door takes the turn the same way, since the rule is the gateway's rather
    /// than one protocol's.
    [Fact]
    public async Task NeitherDoesTheOtherFrontDoor()
    {
        using var lake = Rows(serialize: true);
        var cancellation = TestContext.Current.CancellationToken;
        var writing = Task.Run(() =>
        {
            using var connection = lake.Connect();
            using var transaction = connection.BeginTransaction();
            foreach (var amount in (int[])[1, 2])
                new Npgsql.NpgsqlCommand($"UPDATE lake.t SET amount = {amount} WHERE id = 1",
                                         connection, transaction).ExecuteNonQuery();
            transaction.Commit();
        }, cancellation);

        Assert.Same(writing, await Task.WhenAny(writing, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        await writing;

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT amount FROM lake.t WHERE id = 1"));
    }

    /// What ordering the writes alone could never fix, and why the turn is taken at BEGIN rather
    /// than at the first write: a DuckDB transaction fixes its catalog snapshot when it begins, so a
    /// write branch created by anybody afterwards is invisible to it -- and it cannot create one
    /// itself either, which DuckDB calls a catalog write-write conflict. Here the transaction has
    /// not written anything yet, and the write behind it still has to wait.
    [Fact]
    public async Task ATransactionTakesTheTurnBeforeItHasWrittenAnything()
    {
        using var lake = Rows(serialize: true, promoted: false);
        var cancellation = TestContext.Current.CancellationToken;

        using var first = new SqlConnection(lake.SqlConnectionString());
        first.Open();
        new SqlCommand("BEGIN TRANSACTION", first).ExecuteNonQuery();
        new SqlCommand("SELECT COUNT(*) FROM [t]", first).ExecuteScalar();

        // Nobody has written to this table yet, so this write is the one that creates its branch.
        var promoting = Task.Run(() =>
        {
            using var second = new SqlConnection(lake.SqlConnectionString());
            second.Open();
            new SqlCommand("UPDATE [t] SET [amount] = 9 WHERE [id] = 1", second).ExecuteNonQuery();
        }, cancellation);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation);
        Assert.False(promoting.IsCompleted);

        // And the transaction that was already open writes through a branch it made itself.
        new SqlCommand("UPDATE [t] SET [amount] = 2 WHERE [id] = 1", first).ExecuteNonQuery();
        new SqlCommand("COMMIT", first).ExecuteNonQuery();

        Assert.Same(promoting, await Task.WhenAny(promoting, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        await promoting;

        lake.Restart();
        Assert.Equal(["9"], lake.Query("SELECT amount FROM lake.t WHERE id = 1"));
    }

    /// And what happens when it does not wait, which is the whole reason the option is about
    /// transactions rather than writes: the two writes never overlap, and the second one still
    /// fails -- against a branch that exists, committed, and that this transaction cannot see.
    [Fact]
    public void AWriteBranchMadeAfterBeginIsInvisibleToTheTransaction()
    {
        using var lake = Rows(serialize: false, promoted: false);

        using var first = new SqlConnection(lake.SqlConnectionString());
        first.Open();
        new SqlCommand("BEGIN TRANSACTION", first).ExecuteNonQuery();
        new SqlCommand("SELECT COUNT(*) FROM [t]", first).ExecuteScalar();

        using (var second = new SqlConnection(lake.SqlConnectionString()))
        {
            second.Open();
            new SqlCommand("UPDATE [t] SET [amount] = 9 WHERE [id] = 1", second).ExecuteNonQuery();
        }

        // A row of its own, so what fails is the branch it cannot see rather than a row conflict.
        var failed = Assert.Throws<SqlException>(() =>
            new SqlCommand("UPDATE [t] SET [amount] = 2 WHERE [id] = 2", first).ExecuteNonQuery());
        Assert.Contains("does not exist", failed.Message);
    }

    /// A turn taken at BEGIN is a turn a client can walk away from: the ways a transaction ends
    /// other than by COMMIT all have to give it back, or the next writer waits on a session that is
    /// no longer there. A pooled connection handed back with one open is the ordinary way that
    /// happens, and a statement that failed inside one is the other.
    [Theory]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("BEGIN TRANSACTION; UPDATE [t] SET [amount] = 7 WHERE [id] = 1")]
    public async Task ATurnIsGivenBackByAConnectionThatWalksAway(string abandoned)
    {
        using var lake = Rows(serialize: true);
        var cancellation = TestContext.Current.CancellationToken;

        // Opened, left mid-transaction, and handed back to the pool.
        using (var walker = new SqlConnection(lake.SqlConnectionString()))
        {
            walker.Open();
            foreach (var statement in abandoned.Split(';'))
                new SqlCommand(statement, walker).ExecuteNonQuery();
        }

        var next = Task.Run(() =>
        {
            using var connection = new SqlConnection(lake.SqlConnectionString());
            connection.Open();
            new SqlCommand("UPDATE [t] SET [amount] = 3 WHERE [id] = 1", connection).ExecuteNonQuery();
        }, cancellation);

        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        await next;
    }

    /// The same on the other front door, whose reset is a statement rather than a packet header.
    [Fact]
    public async Task SoIsOneOnTheOtherFrontDoor()
    {
        using var lake = Rows(serialize: true);
        var cancellation = TestContext.Current.CancellationToken;

        using (var walker = lake.Connect())
            new Npgsql.NpgsqlCommand("BEGIN", walker).ExecuteNonQuery();

        var next = Task.Run(() =>
        {
            using var connection = lake.Connect();
            new Npgsql.NpgsqlCommand("UPDATE lake.t SET amount = 3 WHERE id = 1", connection).ExecuteNonQuery();
        }, cancellation);

        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10), cancellation)));
        await next;
    }

    /// A reader is not a writer: only the turn to write is handed round, so a session that never
    /// writes is never made to wait for one that does.
    [Fact]
    public void AReaderNeverWaits()
    {
        using var lake = Rows(serialize: true);
        using var first = Writer(lake, "UPDATE [t] SET [amount] = 1 WHERE [id] = 1");
        Assert.Equal(["0"], lake.Query("SELECT amount FROM lake.t WHERE id = 1"));
        new SqlCommand("COMMIT", first).ExecuteNonQuery();
    }
}
