using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace triaxis.DuckPg.Cli;

[Command(Description = "Serves a stack of YAML, JSON and parquet layers over the PostgreSQL wire protocol.")]
public class ServeCommand : LakeCommand
{
    /// The command's own default; the library opens nothing it was not asked to.
    const string DefaultListen = "127.0.0.1:55432";

    [Option("--pgwire", "-l", Description = "Listen address for the PostgreSQL front door. Default 127.0.0.1:55432 unless only --tds is given.")]
    public string? PgWire { get; set; }

    [Option("--tds", Description = "Listen address for the TDS front door, which SqlClient speaks.")]
    public string? Tds { get; set; }

    [Option("--write-format", Description = "Format a table is persisted in when the write layer has no file for it yet.")]
    public LayerFormat? WriteFormat { get; set; }

    [Option("--writable", Description = "Accept writes without a write directory; they are lost on exit.")]
    public bool Writable { get; set; }

    [Option("--base", Description = "A baked database to serve instead of layers, copied on the way up and never written to. " +
                                    "Needs no dacpac, no key and no configuration: the file carries them.")]
    public string? Base { get; set; }

    [Option("--materialize", Description = "Collapse the layers into real DuckDB tables and serve those; nothing is kept but a delta at shutdown.")]
    public bool Materialize { get; set; }

    [Option("--store", Description = "Hold a materialized lake's tables in this DuckDB database file rather than in memory.")]
    public string? Store { get; set; }

    [Option("--store-mode", Description = "Whether the store is the lake's state (keep, the default) or only where its tables live (spill).")]
    public StoreMode? StoreMode { get; set; }

    [Option("--compress", Description = "Checkpoint once the lake is built, so DuckDB compresses what it holds in memory.")]
    public bool Compress { get; set; }

    [Option("--no-sort-small-tables", Description = "Leave a small materialized table's sorting and limiting to DuckDB, which is on by default.")]
    public bool NoSortSmallTables { get; set; }

    [Option("--no-check-keys", Description = "Let a write put two rows under one declared key, which is refused by default.")]
    public bool NoCheckKeys { get; set; }

    [Option("--derive-ids", Description = "Give every row its own value for a NEWID() default, derived from its key, " +
                                          "rather than one value for the whole run.")]
    public bool DeriveIds { get; set; }

    [Option("--serialize-transactions", Description = "Let one transaction run at a time, the next waiting for the one in front of it.")]
    public bool SerializeTransactions { get; set; }

    [Option("--schema", Description = "Schema the published views live in.")]
    public string? Schema { get; set; }

    [Option("--cache", Description = "Directory to write merged copies of multi-layer tables into, as ZSTD parquet. Trades build time and disk for read speed.")]
    public string? Cache { get; set; }

    [Option("--install-duckdb-only", Description = "Download the DuckDB library if none is found, and exit without serving.")]
    public bool InstallDuckDbOnly { get; set; }

    [Inject] private readonly IDuckDbInstaller _installer = null!;
    [Inject] private readonly IDuckPgLakeFactory _lakes = null!;

    public static void Configure(IToolBuilder builder)
    {
        AddConfigFile(builder);

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

        var config = Configured();

        // Arguments win over the file, and are relative to the working directory rather than to it.
        if (PgWire is not null) config.Listen = PgWire;
        if (Tds is not null) config.Tds = Tds;
        if (WriteFormat is { } format) config.WriteFormat = format;
        if (Writable) config.Writable = true;
        if (Base is not null) config.Base = Path.GetFullPath(Base);
        if (Materialize) config.Materialize = true;
        if (Store is not null) config.Store = Path.GetFullPath(Store);
        if (StoreMode is { } storeMode) config.StoreMode = storeMode;
        if (Compress) config.Compress = true;
        if (NoSortSmallTables) config.SortSmallTables = false;
        if (NoCheckKeys) config.CheckKeys = false;
        if (DeriveIds) config.DeriveIds = true;
        if (SerializeTransactions) config.SerializeTransactions = true;
        if (Schema is not null) config.Schema = Schema;
        if (Cache is not null) config.Cache = Path.GetFullPath(Cache);
        if (config.Listen is not { Length: > 0 } && config.Tds is not { Length: > 0 })
            config.Listen = DefaultListen;

        // Started inside the guard: `installDuckDb` fetches a missing library there, and asking what
        // is loaded before that would be the native call that fails instead of the one that works.
        await using var lake = await Guarded(() => _lakes.StartAsync(config, cancellation));

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
}
