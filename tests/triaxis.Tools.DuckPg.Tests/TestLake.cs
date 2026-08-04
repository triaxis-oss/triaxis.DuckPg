using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace triaxis.Tools.DuckPg.Tests;

/// A lake in a temporary directory: write the layer files, start it, connect to it. Restarting
/// throws away everything held in memory, which is how a test tells persistence from luck.
sealed class TestLake : IDisposable
{
    public string Root { get; }

    public Config Config { get; } = new() { Listen = "127.0.0.1:0" };

    Lake? lake;
    CancellationTokenSource? stopping;
    Task? serving;

    public TestLake([CallerMemberName] string name = "")
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "duckpg-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string At(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    // ---- laying out the layers -------------------------------------------------------------------

    public TestLake Text(string layer, string file, string content)
    {
        var path = At(layer, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    public TestLake Yaml(string layer, string table, string rows, string? directory = null) =>
        Text(layer, System.IO.Path.Combine(directory ?? "", table + ".yaml"), rows);

    public TestLake Json(string layer, string table, string rows, string? directory = null) =>
        Text(layer, System.IO.Path.Combine(directory ?? "", table + ".json"), rows);

    /// A parquet file written by DuckDB itself, which is the only honest way to make one.
    public TestLake Parquet(string layer, string table, string select, string? file = null)
    {
        var path = At(layer, (file ?? table) + ".parquet");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var command = duck.CreateCommand();
        command.CommandText = $"COPY ({select}) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
        return this;
    }

    public TestLake Stack(params string[] layers)
    {
        Config.Layers = [.. layers.Select(l => At(l))];
        return this;
    }

    public TestLake WriteTo(string layer, LayerFormat format = LayerFormat.Parquet)
    {
        Config.Write = At(layer);
        Config.WriteFormat = format;
        return this;
    }

    // ---- running it ------------------------------------------------------------------------------

    public TestLake Start()
    {
        lake = new Lake(Config, NullLoggerFactory.Instance);
        lake.Server.Start();
        stopping = new CancellationTokenSource();
        serving = lake.ListenAsync(stopping.Token);
        return this;
    }

    public void Stop()
    {
        stopping?.Cancel();
        try { serving?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        lake?.Dispose();
        lake = null;
    }

    /// Everything in memory goes; what the write layer put on disk is all that is left.
    public TestLake Restart()
    {
        Stop();
        return Start();
    }

    public Catalog Catalog => lake!.Catalog;

    public string ConnectionString(string user = "admin") =>
        $"Host=127.0.0.1;Port={lake!.Server.Endpoint.Port};Username={user};Database=lake;" +
        "SSL Mode=Disable;Include Error Detail=true";

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
}
