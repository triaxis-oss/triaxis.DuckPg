using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// A lake in a temporary directory: write the layer files, start it, connect to it. Restarting
/// throws away everything held in memory, which is how a test tells persistence from luck.
sealed class TestLake : IDisposable
{
    public string Root { get; }

    public Config Config { get; } = new() { Listen = "127.0.0.1:0" };

    ServiceProvider? provider;
    Lake? lake;

    public TestLake([CallerMemberName] string name = "")
    {
        Root = Path.Combine(Path.GetTempPath(), "duckpg-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string At(params string[] parts) => Path.Combine([Root, .. parts]);

    // ---- laying out the layers -------------------------------------------------------------------

    public TestLake Text(string layer, string file, string content)
    {
        var path = At(layer, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public TestLake Yaml(string layer, string table, string rows, string? directory = null) =>
        Text(layer, Path.Combine(directory ?? "", table + ".yaml"), rows);

    public TestLake Json(string layer, string table, string rows, string? directory = null) =>
        Text(layer, Path.Combine(directory ?? "", table + ".json"), rows);

    /// A parquet file written by DuckDB itself, which is the only honest way to make one.
    public TestLake Parquet(string layer, string table, string select, string? file = null)
    {
        var path = At(layer, (file ?? table) + ".parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        DuckDbLibrary.Register();
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var command = duck.CreateCommand();
        command.CommandText = $"COPY ({select}) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
        return this;
    }

    /// The layer directories, lowest first. Created if they are not there: a fixture owns its
    /// temporary tree, and a layer a test names but never writes to is an empty layer rather than
    /// the typo a real consumer would want to hear about.
    public TestLake Stack(params string[] layers)
    {
        Config.Layers = [.. layers.Select(l => At(l))];
        foreach (var layer in Config.Layers) Directory.CreateDirectory(layer);
        return this;
    }

    public TestLake WriteTo(string layer, LayerFormat format = LayerFormat.Parquet)
    {
        Config.Write = At(layer);
        Config.WriteFormat = format;
        return this;
    }

    /// The TDS front door, which only opens when a test asks for it.
    /// Every layer collapsed into a real DuckDB table, written to directly. No merge, no write
    /// branch, no tombstones -- and nothing kept, unless a write directory is there to take the
    /// delta when the lake stops.
    public TestLake Materialized()
    {
        Config.Materialize = true;
        return this;
    }

    /// Held in a DuckDB database file under the lake's own directory rather than in memory. Kept by
    /// default, so a restart opens what the run before it left rather than collapsing the layers
    /// again; spilled, the file is only where the tables live and everything else is unchanged.
    public TestLake StoredAt(string file = "store.duckdb", StoreMode mode = StoreMode.Keep)
    {
        Config.Store = At(file);
        Config.StoreMode = mode;
        return this;
    }

    /// Checkpointed once the build is over, which is what makes DuckDB compress what it holds.
    public TestLake Compressed()
    {
        Config.Compress = true;
        return this;
    }

    public TestLake WithTds()
    {
        Config.Tds = "127.0.0.1:0";
        return this;
    }

    /// Merged copies of multi-layer tables, kept beside the lake rather than inside it.
    public TestLake WithCache()
    {
        Config.Cache = Path.Combine(Root, ".cache");
        return this;
    }

    // ---- running it ------------------------------------------------------------------------------

    /// Anything else the lake should be built with -- a logger that writes into the test output,
    /// say. Called before the services duckpg registers, so it can replace any of them.
    public Action<IServiceCollection>? Services { get; set; }

    /// What the lake said while it was starting, most recent start only. A warning is the whole of
    /// what some mistakes get -- the lake still publishes what it was always going to publish -- so
    /// a test has to be able to read one back.
    public List<string> Logged { get; } = [];

    public TestLake Start()
    {
        Logged.Clear();

        var services = new ServiceCollection();
        Services?.Invoke(services);
        services.AddDuckPg(Config);
        services.AddLogging(builder => builder.AddProvider(new Capture(Logged)));

        provider = services.BuildServiceProvider();
        lake = provider.GetRequiredService<Lake>();
        lake.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return this;
    }

    /// The layers written out as the parquet layer they publish, into a directory of the fixture's
    /// own. What the bake said lands in `Logged`, exactly as a start's own lines do -- and pointing
    /// `Stack` at the same directory afterwards is how a test tells a baked lake from the one it
    /// was baked from.
    public TestLake Baked(string directory = "baked")
    {
        Logged.Clear();

        // Through the service a consumer would use, not the internals behind it: baking from a
        // fixture is the embedding case, so the fixture is the one that has to keep working.
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new Capture(Logged)));
        services.AddDuckPgBaker();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDuckPgBaker>()
                .BakeAsync(Config, At(directory)).GetAwaiter().GetResult();
        return this;
    }

    /// Served from a database a bake wrote rather than from the layers it was baked from. The
    /// layers go with it: a base is a whole lake already collapsed, and one underneath it would
    /// have to be merged with it.
    public TestLake FromBase(string file = "baked.duckdb")
    {
        Config.Base = At(file);
        Config.Layers = [];
        return this;
    }

    public void Stop()
    {
        lake?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        provider?.Dispose();
        provider = null;
        lake = null;
    }

    /// Everything in memory goes; what the write layer put on disk is all that is left.
    public TestLake Restart()
    {
        Stop();
        return Start();
    }

    Lake Running => lake ?? throw new InvalidOperationException("the lake has not been started");

    internal Catalog Catalog => Running.Catalog;

    /// For Npgsql. `Include Error Detail` is on, because a test that fails wants the detail.
    public string ConnectionString(string user = "admin") =>
        Running.ConnectionString(user) + ";Include Error Detail=true";

    /// For Microsoft.Data.SqlClient, once `WithTds()` has opened that door.
    public string SqlConnectionString(string user = "sa") =>
        Running.SqlConnectionString(user) + ";Connect Timeout=15;Pooling=true";

    public int Port => Running.Endpoint?.Port
        ?? throw new InvalidOperationException("no PostgreSQL front door: Config.Listen is not set");

    public int TdsPort => Running.TdsEndpoint?.Port
        ?? throw new InvalidOperationException("no TDS front door: call WithTds() before Start()");

    public NpgsqlConnection Connect(string user = "admin")
    {
        var connection = new NpgsqlConnection(ConnectionString(user));
        connection.Open();
        return connection;
    }

    // ---- asking it things ------------------------------------------------------------------------

    /// One row per line, columns joined by `|`, so an expectation reads as a table.
    public List<string> Query(string sql, string user = "admin")
    {
        using var connection = Connect(user);
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "" : Render(reader.GetValue(i)))));
        return rows;
    }

    /// Dates go out in ISO rather than whatever the ambient culture prefers, so an expectation
    /// reads the way the SQL literal that produced it was written.
    static string Render(object value) => value switch
    {
        DateTime timestamp => timestamp.ToString(timestamp.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss"),
        DateOnly date => date.ToString("yyyy-MM-dd"),
        _ => value.ToString() ?? "",
    };

    public int Execute(string sql, string user = "admin")
    {
        using var connection = Connect(user);
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteNonQuery();
    }

    public List<string> Columns(string table)
    {
        using var connection = Connect();
        using var command = new NpgsqlCommand($"SELECT * FROM {table} LIMIT 0", connection);
        using var reader = command.ExecuteReader();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}:{reader.GetDataTypeName(i)}")];
    }

    public void Dispose()
    {
        Stop();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    /// Every message the lake logs, rendered, into a list a test can read.
    sealed class Capture(List<string> messages) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string category) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> message)
        {
            lock (messages) messages.Add($"{level}: {message(state, error)}");
        }

        public void Dispose() { }
    }
}
