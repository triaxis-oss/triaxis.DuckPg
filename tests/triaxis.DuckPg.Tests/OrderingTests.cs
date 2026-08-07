using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg.Tests;

/// The fast path answers instead of DuckDB, so the only test worth having is the one that asks both
/// and compares. Every shape here runs twice against the same rows -- once with the ordering left to
/// DuckDB and once with it lifted out -- and the two have to agree exactly, ties, nulls and all.
public class OrderingTests : IDisposable
{
    readonly List<TestLake> lakes = [];

    public void Dispose() { foreach (var lake in lakes) lake.Dispose(); }

    const string Rows = """
        [{"id": 1, "grp": 2, "amount": 10.5,  "when": "2026-01-03", "note": "c"},
         {"id": 2, "grp": 1, "amount": null,  "when": "2026-01-01", "note": "a"},
         {"id": 3, "grp": 2, "amount": 10.5,  "when": null,         "note": "b"},
         {"id": 4, "grp": 1, "amount": -3.25, "when": "2026-01-02", "note": "d"},
         {"id": 5, "grp": 1, "amount": null,  "when": "2026-01-04", "note": "e"},
         {"id": 6, "grp": 2, "amount": 99.0,  "when": "2026-01-01", "note": "f"}]
        """;

    TestLake Lake(bool fast, string rows = Rows)
    {
        var lake = new TestLake($"ordering-{(fast ? "fast" : "duck")}-{lakes.Count}")
            .Json("base", "rows", rows)
            .Stack("base")
            .Materialized()
            .WithTds();
        lake.Config.DefaultKey = ["id"];
        lake.Config.SortSmallTables = fast;
        lakes.Add(lake.Start());
        return lake;
    }

    static List<string> Ask(TestLake lake, string sql)
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using var reader = new SqlCommand(sql, connection).ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString())));
        return rows;
    }

    [Theory]
    [InlineData("SELECT [id], [amount] FROM [rows] ORDER BY [amount]")]
    [InlineData("SELECT [id], [amount] FROM [rows] ORDER BY [amount] DESC")]
    [InlineData("SELECT TOP 1 [id], [amount] FROM [rows] ORDER BY [amount] DESC")]
    [InlineData("SELECT TOP 3 [id], [amount] FROM [rows] ORDER BY [amount]")]
    [InlineData("SELECT [id], [when] FROM [rows] ORDER BY [when]")]
    [InlineData("SELECT [id], [when] FROM [rows] ORDER BY [when] DESC")]
    [InlineData("SELECT [id], [grp], [amount] FROM [rows] ORDER BY [grp], [amount] DESC")]
    [InlineData("SELECT [id], [grp], [amount] FROM [rows] ORDER BY [grp] DESC, [amount]")]
    [InlineData("SELECT TOP 2 [id], [grp] FROM [rows] WHERE [grp] = 1 ORDER BY [id] DESC")]
    [InlineData("SELECT [id] FROM [rows] WHERE [amount] > 0 ORDER BY [id]")]
    [InlineData("SELECT TOP 0 [id] FROM [rows] ORDER BY [id]")]
    [InlineData("SELECT TOP 100 [id] FROM [rows] ORDER BY [id]")]
    public void BothPathsAnswerTheSame(string sql)
    {
        Assert.Equal(Ask(Lake(false), sql), Ask(Lake(true), sql));
    }

    /// A tie has to break the same way too, or a TOP 1 picks a different row from each path. Ordered
    /// by a column with duplicates, so what decides the rest is whatever each path does with equals.
    [Fact]
    public void TiesComeBackInTheSameOrder()
    {
        const string sql = "SELECT [id], [amount] FROM [rows] ORDER BY [amount]";
        Assert.Equal(Ask(Lake(false), sql), Ask(Lake(true), sql));
    }

    /// A kept row lands in the slot of the one it displaced, so a value has to overwrite whatever
    /// the row before it left there -- including its nulls. Ordered ascending, these five put a null
    /// in a slot and then win it back with the smallest value in the table: read as still null, that
    /// row is dropped and the answer is the two it should have displaced.
    [Fact]
    public void AReusedSlotForgetsTheNullsOfTheRowBeforeIt()
    {
        const string rows = """
            [{"id": 1, "amount": 5},    {"id": 2, "amount": null}, {"id": 3, "amount": 3},
             {"id": 4, "amount": null}, {"id": 5, "amount": 1}]
            """;
        const string sql = "SELECT TOP 2 [id], [amount] FROM [rows] ORDER BY [amount]";
        Assert.Equal(Ask(Lake(false, rows), sql), Ask(Lake(true, rows), sql));
    }

    /// Text is left to DuckDB: a collation is its own, and `string.CompareTo` is not it. So this one
    /// must not take the fast path at all -- which shows up as the two agreeing, since one of them
    /// simply is the other.
    [Fact]
    public void TextOrderingIsLeftAlone()
    {
        const string sql = "SELECT [id], [note] FROM [rows] ORDER BY [note] DESC";
        Assert.Equal(Ask(Lake(false), sql), Ask(Lake(true), sql));
    }
}

/// The tests above agree whether or not the fast path ever fires, which is the point of them and
/// also their blind spot. This one reads what was actually sent to DuckDB.
public class OrderingUseTests : IDisposable
{
    sealed class Lines : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string category) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> message)
        {
            lock (Messages) Messages.Add(message(state, error));
        }
        public void Dispose() { }
    }

    readonly Lines lines = new();
    readonly TestLake lake = new TestLake("ordering-use")
        .Json("base", "rows", """[{"id": 1, "note": "a"}, {"id": 2, "note": "b"}]""")
        .Stack("base")
        .Materialized()
        .WithTds();

    public OrderingUseTests()
    {
        lake.Config.DefaultKey = ["id"];
        lake.Config.SortSmallTables = true;
        lake.Config.Writable = true;
        lake.Services = services => services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(lines));
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    string Sent(string sql)
    {
        lock (lines.Messages) lines.Messages.Clear();
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using (var reader = new SqlCommand(sql, connection).ExecuteReader())
            while (reader.Read()) { }

        lock (lines.Messages)
            return lines.Messages.Last(m => m.Contains("FROM", StringComparison.OrdinalIgnoreCase)
                                         && m.Contains("rows", StringComparison.OrdinalIgnoreCase));
    }

    /// A qualifying statement goes out without the clauses this took over.
    [Fact]
    public void TheOrderingAndTheLimitAreTakenOffTheStatement()
    {
        var sent = Sent("SELECT TOP 1 [id] FROM [rows] ORDER BY [id] DESC");
        Assert.DoesNotContain("ORDER BY", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", sent, StringComparison.OrdinalIgnoreCase);
    }

    /// And one sorted by text does not: a collation is DuckDB's own.
    [Fact]
    public void ATextSortIsLeftForDuckDbToDo()
    {
        var sent = Sent("SELECT TOP 1 [id], [note] FROM [rows] ORDER BY [note] DESC");
        Assert.Contains("ORDER BY", sent, StringComparison.OrdinalIgnoreCase);
    }

    /// So does a statement this cannot take apart -- a grouped one, whose ordering is over the
    /// groups rather than over the table's rows.
    [Fact]
    public void AGroupedStatementIsLeftAlone()
    {
        var sent = Sent("SELECT [note], COUNT(*) AS [n] FROM [rows] GROUP BY [note] ORDER BY [note]");
        Assert.Contains("ORDER BY", sent, StringComparison.OrdinalIgnoreCase);
    }

    /// A table is counted once when it is built, so what an insert wrote has to be told to the
    /// catalog -- a table that grew past the threshold and is still taken over is a whole scan
    /// standing in for a limit, which is slower than what it replaced rather than faster.
    [Fact]
    public void ATableThatOutgrowsTheThresholdIsHandedBack()
    {
        Assert.DoesNotContain("ORDER BY", Sent("SELECT TOP 1 [id] FROM [rows] ORDER BY [id] DESC"),
                              StringComparison.OrdinalIgnoreCase);

        using (var connection = new SqlConnection(lake.SqlConnectionString()))
        {
            connection.Open();
            var values = string.Join(",", Enumerable.Range(3, (int)FastOrder.Small)
                .Select(i => $"({i}, 'n{i}')"));
            new SqlCommand($"INSERT INTO [rows] ([id], [note]) VALUES {values}", connection).ExecuteNonQuery();
        }

        Assert.Contains("ORDER BY", Sent("SELECT TOP 1 [id] FROM [rows] ORDER BY [id] DESC"),
                        StringComparison.OrdinalIgnoreCase);
    }
}
