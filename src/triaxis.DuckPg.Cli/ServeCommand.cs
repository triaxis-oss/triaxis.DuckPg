using System.Runtime.InteropServices;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace triaxis.DuckPg.Cli;

[Command(Description = "Serves a stack of YAML, JSON and parquet layers over the PostgreSQL wire protocol.")]
public class ServeCommand : LoggingCommand
{
    const string DefaultConfig = "duckpg.yaml";

    /// The command's own default; the library opens nothing it was not asked to.
    const string DefaultListen = "127.0.0.1:55432";

    [Argument(Description = "Layer directories, lowest first. Overrides the configuration.")]
    public string[] Layers { get; set; } = [];

    [Option("--config", "-c", Description = "Configuration file.")]
    public string ConfigPath { get; set; } = DefaultConfig;

    [Option("--pgwire", "-l", Description = "Listen address for the PostgreSQL front door. Default 127.0.0.1:55432 unless only --tds is given.")]
    public string? PgWire { get; set; }

    [Option("--tds", Description = "Listen address for the TDS front door, which SqlClient speaks.")]
    public string? Tds { get; set; }

    [Option("--write", "-w", Description = "Directory holding the topmost layer, which accepts writes.")]
    public string? Write { get; set; }

    [Option("--write-format", Description = "Format a table is persisted in when the write layer has no file for it yet.")]
    public LayerFormat? WriteFormat { get; set; }

    [Option("--writable", Description = "Accept writes without a write directory; they are lost on exit.")]
    public bool Writable { get; set; }

    [Option("--materialize", Description = "Collapse the layers into real DuckDB tables and serve those; nothing is kept but a delta at shutdown.")]
    public bool Materialize { get; set; }

    [Option("--store", Description = "Hold a materialized lake's tables in this DuckDB database file rather than in memory.")]
    public string? Store { get; set; }

    [Option("--store-mode", Description = "Whether the store is the lake's state (keep, the default) or only where its tables live (spill).")]
    public StoreMode? StoreMode { get; set; }

    [Option("--no-sort-small-tables", Description = "Leave a small materialized table's sorting and limiting to DuckDB, which is on by default.")]
    public bool NoSortSmallTables { get; set; }

    [Option("--no-check-keys", Description = "Let a write put two rows under one declared key, which is refused by default.")]
    public bool NoCheckKeys { get; set; }

    [Option("--serialize-transactions", Description = "Let one transaction run at a time, the next waiting for the one in front of it.")]
    public bool SerializeTransactions { get; set; }

    [Option("--schema", Description = "Schema the published views live in.")]
    public string? Schema { get; set; }

    [Option("--key", "-k", Description = "Column identifying a row, for tables that name no key of their own. Repeatable.")]
    public string[] Key { get; set; } = [];

    [Option("--dacpac", Description = "A .dacpac to take column names, order, types and keys from.")]
    public string? Dacpac { get; set; }

    [Option("--cache", Description = "Directory to write merged copies of multi-layer tables into, as ZSTD parquet. Trades build time and disk for read speed.")]
    public string? Cache { get; set; }

    [Option("--install-duckdb", Description = "Download the DuckDB library on the way up if none is found, rather than failing.")]
    public bool InstallDuckDb { get; set; }

    [Option("--install-duckdb-only", Description = "Download the DuckDB library if none is found, and exit without serving.")]
    public bool InstallDuckDbOnly { get; set; }

    [Inject] private readonly IConfiguration _configuration = null!;
    [Inject] private readonly IDuckDbInstaller _installer = null!;
    [Inject] private readonly IDuckPgLakeFactory _lakes = null!;

    /// The configuration file is named on the command line, so the source can only be added once
    /// the arguments are parsed. Defaults alone are enough to serve a directory, so the file is
    /// only required when it was actually asked for.
    public static void Configure(IToolBuilder builder)
    {
        builder.ConfigureConfiguration((context, configuration) =>
        {
            var parsed = context.GetInvocationContext().ParseResult;
            configuration.AddYamlFile(
                Path.GetFullPath(parsed.GetValue<string>("--config") ?? DefaultConfig),
                optional: parsed.GetResult("--config") is not OptionResult { Implicit: false },
                reloadOnChange: false);
        });

        // The factory rather than a lake: what to serve is only known once the arguments have won
        // over the file, which is after the host has been built.
        builder.ConfigureServices(services => services.AddDuckPgFactory());
    }

    public async Task ExecuteAsync(CancellationToken cancellation)
    {
        if (InstallDuckDbOnly)
        {
            await _installer.InstallAsync(cancellation);
            return;
        }

        var config = _configuration.Get<Config>() ?? new Config();
        // `layers:` may be written as a single directory; only the list form binds on its own.
        if (_configuration["layers"] is { Length: > 0 } single) config.Layers = [single];
        config.ResolvePaths(Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!);

        // Arguments win over the file, and are relative to the working directory rather than to it.
        if (Layers.Length > 0) config.Layers = [.. Layers.Select(Path.GetFullPath)];
        if (PgWire is not null) config.Listen = PgWire;
        if (Tds is not null) config.Tds = Tds;
        if (Write is not null) config.Write = Path.GetFullPath(Write);
        if (WriteFormat is { } format) config.WriteFormat = format;
        if (Writable) config.Writable = true;
        if (Materialize) config.Materialize = true;
        if (Store is not null) config.Store = Path.GetFullPath(Store);
        if (StoreMode is { } storeMode) config.StoreMode = storeMode;
        if (NoSortSmallTables) config.SortSmallTables = false;
        if (NoCheckKeys) config.CheckKeys = false;
        if (SerializeTransactions) config.SerializeTransactions = true;
        if (Schema is not null) config.Schema = Schema;
        if (Key.Length > 0) config.DefaultKey = Key;
        if (Dacpac is not null) config.Dacpac = Path.GetFullPath(Dacpac);
        if (Cache is not null) config.Cache = Path.GetFullPath(Cache);
        if (InstallDuckDb) config.InstallDuckDb = true;
        if (config.Listen is not { Length: > 0 } && config.Tds is not { Length: > 0 })
            config.Listen = DefaultListen;

        await using var lake = await Start(config, cancellation);

        foreach (var table in lake.Catalog.Tables.Values)
            Logger.LogInformation("{Schema}.{Table} <- {Layers}{Writable}{Virtual}",
                table.Schema, table.Name,
                table.Layers.Count == 0 ? "(declared only)"
                    : string.Join(" | ", table.Layers.Select(l => $"{l.Source.Path} {l.Shape}")),
                table.Writable ? " [writable]" : "",
                table.Virtuals.Count > 0 ? " +" + string.Join(",", table.Virtuals.Select(v => v.Name)) : "");

        // Serving until something stops it: Ctrl+C, SIGTERM, or a listener falling over. The signals
        // are taken here because nothing else takes them -- unhandled, SIGINT and SIGTERM end the
        // process where it stands, and a materialized lake's delta is written by the shutdown that
        // never runs. `Cancel` on the context is what keeps the runtime from ending it for us.
        using var signalled = new CancellationTokenSource();
        void Signal(PosixSignalContext context)
        {
            context.Cancel = true;
            Logger.LogInformation("{Signal}, stopping", context.Signal);
            signalled.Cancel();
        }

        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Signal);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Signal);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Signal);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(signalled.Token, cancellation);
        try
        {
            await lake.Completion.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Stopped rather than only disposed, so the listeners are waited for and whatever a
        // materialized lake was given is on disk before the process ends.
        await lake.StopAsync(CancellationToken.None);
    }

    /// The tool links against the machine's own DuckDB, and the first native call is where a
    /// missing one would otherwise surface: a DllNotFoundException out of the bindings, naming a
    /// library the reader never asked for by that name. Say where it was looked for instead, and
    /// what the ways out are.
    async Task<Lake> Start(Config config, CancellationToken cancellation)
    {
        try
        {
            // Started first: `installDuckDb` fetches a missing library here, and asking what is
            // loaded before that would be the native call that fails instead of the one that works.
            var lake = await _lakes.StartAsync(config, cancellation);

            if (DuckDbLibrary.LoadedVersion is { } loaded && loaded != DuckDbLibrary.Version)
                Logger.LogWarning(
                    "DuckDB {Loaded} loaded from {Path}, where these bindings speak {Expected}'s C " +
                    "API -- `--install-duckdb` fetches a matching one",
                    loaded,
                    DuckDbLibrary.LoadedFrom ?? "the loader's own search path",
                    DuckDbLibrary.Version);

            return lake;
        }
        catch (DuckPgConfigurationException problem)
        {
            // EX_USAGE: what it was told to serve does not add up, and the message names the part.
            throw new CommandErrorException("{Problem}", problem.Message) { ExitCode = 64 };
        }
        catch (DllNotFoundException)
        {
            throw new CommandErrorException(
                "DuckDB {Version} was not found. Looked in:" + Environment.NewLine + "{Searched}" +
                Environment.NewLine +
                "Install it (`brew install duckdb`, `apt install libduckdb-dev`), point " +
                DuckDbLibrary.PathVariable + " at the library, or add `--install-duckdb` to fetch " +
                "it into {Downloaded} on the way up.",
                DuckDbLibrary.Version,
                string.Join(Environment.NewLine, DuckDbLibrary.SearchPath.Select(path => "  " + path)),
                DuckDbLibrary.Downloaded)
            { ExitCode = 69 }; // EX_UNAVAILABLE: the tool is fine, what it needs is not here
        }
    }


}
