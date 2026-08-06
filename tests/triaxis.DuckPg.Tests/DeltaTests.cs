namespace triaxis.DuckPg.Tests;

/// What a lake leaves in its write directory has to mean the same thing when it is read back as a
/// layer: the state a restart serves must be the state the run before it served, however many times
/// it is restarted, and whether the write layer was kept row by row or worked out at shutdown.
///
/// The two mechanisms are not the same. A layered lake writes a delta it accumulated as it went; a
/// materialized one has only the finished table and reconstructs the delta by subtracting the read
/// layers from it. Both are exercised here against the same mutations and the same assertions.
public class DeltaTests
{
    /// Every shape a written row can take against what the layers already said. Each is applied,
    /// and the answer is then required to survive being written out and read back, twice.
    public static TheoryData<string, string> Mutations => new()
    {
        { "changes a row", "UPDATE lake.orders SET note = 'changed' WHERE order_id = 1" },
        { "changes a row back", "UPDATE lake.orders SET note = 'a' WHERE order_id = 1" },
        { "writes a null over a value", "UPDATE lake.orders SET note = NULL WHERE order_id = 1" },
        { "writes a value over a null", "UPDATE lake.orders SET note = 'was null' WHERE order_id = 3" },
        { "deletes a row the layers hold", "DELETE FROM lake.orders WHERE order_id = 2" },
        { "deletes every row", "DELETE FROM lake.orders" },
        { "inserts a row", "INSERT INTO lake.orders (order_id, note) VALUES (4, 'new')" },
        { "deletes a row it just inserted",
          "INSERT INTO lake.orders (order_id, note) VALUES (4, 'new'); DELETE FROM lake.orders WHERE order_id = 4" },
        { "puts a deleted key back",
          "DELETE FROM lake.orders WHERE order_id = 2; INSERT INTO lake.orders (order_id, note) VALUES (2, 'again')" },
        { "moves a key", "UPDATE lake.orders SET order_id = 9 WHERE order_id = 1" },
        { "moves a key onto one that exists",
          "DELETE FROM lake.orders WHERE order_id = 2; UPDATE lake.orders SET order_id = 2 WHERE order_id = 1" },
        { "does several things at once",
          "UPDATE lake.orders SET note = 'changed' WHERE order_id = 1; " +
          "DELETE FROM lake.orders WHERE order_id = 3; " +
          "INSERT INTO lake.orders (order_id, note) VALUES (4, 'new'); " +
          "UPDATE lake.orders SET note = NULL WHERE order_id = 2" },
    };

    static TestLake Lake(bool materialized, string name)
    {
        var lake = new TestLake(name)
            .Json("base", "orders", """
                [{"order_id": 1, "note": "a"}, {"order_id": 2, "note": "b"}, {"order_id": 3, "note": null}]
                """)
            .Json("top", "orders", """[{"order_id": 2, "note": "shadowed"}]""")
            .Stack("base", "top")
            .WriteTo("local");

        lake.Config.DefaultKey = ["order_id"];
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    static List<string> State(TestLake lake) =>
        lake.Query("SELECT order_id || '=' || coalesce(note, '<null>') FROM lake.orders ORDER BY order_id");

    [Theory]
    [MemberData(nameof(Mutations))]
    public void ALayeredLakeSurvivesEveryRestart(string what, string mutation) =>
        Survives(materialized: false, what, mutation);

    [Theory]
    [MemberData(nameof(Mutations))]
    public void AMaterializedLakeSurvivesEveryRestart(string what, string mutation) =>
        Survives(materialized: true, what, mutation);

    static void Survives(bool materialized, string what, string mutation)
    {
        using var lake = Lake(materialized, $"delta-{(materialized ? "materialized" : "layered")}");

        foreach (var statement in mutation.Split(';', StringSplitOptions.RemoveEmptyEntries))
            lake.Execute(statement.Trim());

        var served = State(lake);

        // Three times, because one restart cannot tell a delta measured against the layers from one
        // measured against the layers plus itself -- they agree exactly once.
        for (var restart = 1; restart <= 3; restart++)
        {
            lake.Restart();
            Assert.Equal(served, State(lake));
        }
    }

    /// A second run writing on top of the first. Each run's delta has to carry what the run before
    /// it left as well as what it did itself, since the write layer is a layer and not a journal.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritesFromSeveralRunsAccumulate(bool materialized)
    {
        using var lake = Lake(materialized, $"delta-runs-{(materialized ? "materialized" : "layered")}");

        lake.Execute("UPDATE lake.orders SET note = 'first' WHERE order_id = 1");
        lake.Restart();
        lake.Execute("DELETE FROM lake.orders WHERE order_id = 3");
        lake.Restart();
        lake.Execute("INSERT INTO lake.orders (order_id, note) VALUES (4, 'third')");
        lake.Restart();

        Assert.Equal(["1=first", "2=shadowed", "4=third"], State(lake));
        lake.Restart();
        Assert.Equal(["1=first", "2=shadowed", "4=third"], State(lake));
    }

    /// And the delta one mode writes is the layer the other reads: the write directory is a layer,
    /// not a private format, so which mode wrote it is not something the next run has to know.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EitherModeReadsWhatTheOtherWrote(bool wrote, bool reads)
    {
        using var lake = Lake(wrote, $"delta-across-{wrote}");

        lake.Execute("UPDATE lake.orders SET note = 'changed' WHERE order_id = 1");
        lake.Execute("DELETE FROM lake.orders WHERE order_id = 3");
        lake.Execute("INSERT INTO lake.orders (order_id, note) VALUES (4, 'new')");
        var served = State(lake);

        lake.Stop();
        lake.Config.Materialize = reads;
        lake.Start();

        Assert.Equal(served, State(lake));
    }
}

/// Where a delta stops being able to say what happened. A delete is remembered by its key, and a
/// materialized delta is a set difference -- so what a keyless table can round-trip is worth
/// knowing exactly rather than approximately.
public class KeylessDeltaTests
{
    static TestLake Lake(bool materialized, string name)
    {
        var lake = new TestLake(name)
            .Json("base", "notes", """[{"text": "one"}, {"text": "two"}]""")
            .Stack("base")
            .WriteTo("local");
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    /// Nothing is lost to a delete a keyless table cannot record, because the delete never happens:
    /// it is refused for want of a key, in either mode, before anything is written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeleteWithoutAKeyIsRefusedRatherThanForgotten(bool materialized)
    {
        using var lake = Lake(materialized, $"delta-keyless-delete-{materialized}");

        var refused = Assert.Throws<Npgsql.PostgresException>(() =>
            lake.Execute("DELETE FROM lake.notes WHERE text = 'one'"));
        Assert.Contains("needs a `key`", refused.Message);

        lake.Restart();
        Assert.Equal(["one", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));
    }

    /// An insert needs no key -- a new row needs no identity to be told from an old one -- and comes
    /// back in either mode.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInsertWithoutAKeySurvives(bool materialized)
    {
        using var lake = Lake(materialized, $"delta-keyless-insert-{materialized}");

        lake.Execute("INSERT INTO lake.notes (text) VALUES ('three')");
        lake.Restart();
        Assert.Equal(["one", "three", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));
    }

    /// And where the two modes part. A layered lake keeps the row it was given, duplicate or not.
    /// A materialized one reconstructs its delta by subtracting the layers from the table, and a set
    /// difference has no way to say "one more of these" -- so a row identical to one the layers
    /// already hold is not in the difference, and does not come back.
    [Fact]
    public void ALayeredLakeKeepsARowIdenticalToOneTheLayersHold()
    {
        using var lake = Lake(materialized: false, "delta-keyless-duplicate-layered");

        lake.Execute("INSERT INTO lake.notes (text) VALUES ('one')");
        Assert.Equal(["one", "one", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));

        lake.Restart();
        Assert.Equal(["one", "one", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));
    }

    [Fact]
    public void AMaterializedLakeLosesThatDuplicate()
    {
        using var lake = Lake(materialized: true, "delta-keyless-duplicate-materialized");

        lake.Execute("INSERT INTO lake.notes (text) VALUES ('one')");
        Assert.Equal(["one", "one", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));

        lake.Restart();
        Assert.Equal(["one", "two"], lake.Query("SELECT text FROM lake.notes ORDER BY text"));
    }
}
