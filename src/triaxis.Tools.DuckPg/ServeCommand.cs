using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using triaxis.CommandLine;
using Microsoft.Extensions.Hosting;

namespace triaxis.Tools.DuckPg;

[Command(Description = "Serves a stack of YAML, JSON and parquet layers over the PostgreSQL wire protocol.")]
public class ServeCommand : LoggingCommand
{
    const string DefaultConfig = "duckpg.yaml";

    [Argument(Description = "Layer directories, lowest first. Overrides the configuration.")]
    public string[] Layers { get; set; } = [];

    [Option("--config", "-c", Description = "Configuration file.")]
    public string ConfigPath { get; set; } = DefaultConfig;

    [Option("--listen", "-l", Description = "Listen address.")]
    public string? ListenAddress { get; set; }

    [Option("--tds", Description = "Listen address for the TDS front door, which SqlClient speaks.")]
    public string? Tds { get; set; }

    [Option("--write", "-w", Description = "Directory holding the topmost layer, which accepts writes.")]
    public string? Write { get; set; }

    [Option("--write-format", Description = "Format a table is persisted in when the write layer has no file for it yet.")]
    public LayerFormat? WriteFormat { get; set; }

    [Option("--writable", Description = "Accept writes without a write directory; they are lost on exit.")]
    public bool Writable { get; set; }

    [Option("--schema", Description = "Schema the published views live in.")]
    public string? Schema { get; set; }

    [Option("--key", "-k", Description = "Column identifying a row, for tables that name no key of their own. Repeatable.")]
    public string[] Key { get; set; } = [];

    [Option("--dacpac", Description = "A .dacpac to take column names, order, types and keys from.")]
    public string? Dacpac { get; set; }

    [Option("--install-duckdb", Description = "Download the DuckDB library duckpg needs, and exit.")]
    public bool InstallDuckDb { get; set; }

    [Inject] private readonly IConfiguration _configuration = null!;
    [Inject] private readonly ILoggerFactory _loggers = null!;

    /// The configuration file is named on the command line, so the source can only be added once
    /// the arguments are parsed. Defaults alone are enough to serve a directory, so the file is
    /// only required when it was actually asked for.
    public static void Configure(IToolBuilder builder) =>
        builder.ConfigureConfiguration((context, configuration) =>
        {
            var parsed = context.GetInvocationContext().ParseResult;
            configuration.AddYamlFile(
                Path.GetFullPath(parsed.GetValue<string>("--config") ?? DefaultConfig),
                optional: parsed.GetResult("--config") is not OptionResult { Implicit: false },
                reloadOnChange: false);
        });

    public async Task ExecuteAsync(CancellationToken cancellation)
    {
        if (InstallDuckDb)
        {
            await DuckDbDownload.InstallAsync(Logger, cancellation);
            return;
        }

        var config = _configuration.Get<Config>() ?? new Config();
        // `layers:` may be written as a single directory; only the list form binds on its own.
        if (_configuration["layers"] is { Length: > 0 } single) config.Layers = [single];
        config.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!);

        // Arguments win over the file, and are relative to the working directory rather than to it.
        if (Layers.Length > 0) config.Layers = [.. Layers.Select(Path.GetFullPath)];
        if (ListenAddress is not null) config.Listen = ListenAddress;
        if (Tds is not null) config.Tds = Tds;
        if (Write is not null) config.Write = Path.GetFullPath(Write);
        if (WriteFormat is { } format) config.WriteFormat = format;
        if (Writable) config.Writable = true;
        if (Schema is not null) config.Schema = Schema;
        if (Key.Length > 0) config.DefaultKey = Key;
        if (Dacpac is not null) config.Dacpac = Path.GetFullPath(Dacpac);

        using var lake = Open(config);

        foreach (var table in lake.Catalog.Tables.Values)
            Logger.LogInformation("{Schema}.{Table} <- {Layers}{Writable}{Virtual}",
                table.Schema, table.Name,
                table.Layers.Count == 0 ? "(declared only)" : string.Join(" | ", table.Layers.Select(l => l.Source.Path)),
                table.Writable ? " [writable]" : "",
                table.Virtuals.Count > 0 ? " +" + string.Join(",", table.Virtuals.Select(v => v.Name)) : "");

        await lake.ListenAsync(cancellation);
    }

    /// The tool links against the machine's own DuckDB, and the first native call is where a
    /// missing one would otherwise surface: a DllNotFoundException out of the bindings, naming a
    /// library the reader never asked for by that name. Say where it was looked for instead, and
    /// what the ways out are.
    Lake Open(Config config)
    {
        try
        {
            // The first native call, so a missing library surfaces here rather than mid-query.
            if (DuckDbLibrary.LoadedVersion is { } loaded && loaded != DuckDbLibrary.Version)
                Logger.LogWarning(
                    "DuckDB {Loaded} loaded from {Path}, where these bindings speak {Expected}'s C " +
                    "API -- `duckpg --install-duckdb` fetches a matching one",
                    loaded,
                    DuckDbLibrary.LoadedFrom ?? "the loader's own search path",
                    DuckDbLibrary.Version);

            return new Lake(config, _loggers);
        }
        catch (DllNotFoundException)
        {
            throw new CommandErrorException(
                "DuckDB {Version} was not found. Looked in:" + Environment.NewLine + "{Searched}" +
                Environment.NewLine +
                "Install it (`brew install duckdb`, `apt install libduckdb-dev`), point " +
                DuckDbLibrary.PathVariable + " at the library, or run `duckpg --install-duckdb` to " +
                "fetch it into {Downloaded}.",
                DuckDbLibrary.Version,
                string.Join(Environment.NewLine, DuckDbLibrary.SearchPath.Select(path => "  " + path)),
                DuckDbLibrary.Downloaded)
            { ExitCode = 69 }; // EX_UNAVAILABLE: the tool is fine, what it needs is not here
        }
    }
}
