using System.Net;
using DuckDB.NET.Data;
using Microsoft.Extensions.Hosting;

namespace triaxis.DuckPg;

/// A configured lake, ready to serve: the DuckDB catalog behind it and the listeners in front.
/// Views live in an in-memory catalog rebuilt from the layer directories on every start, so the
/// files are the state and this is only what is currently made of them.
///
/// Hosted, so whatever owns the host owns the lake. The listeners bind during `StartAsync` rather
/// than when serving begins, which is what makes `Endpoint` answer the moment the host is up --
/// and what makes port 0 usable, since the port to connect to is the one the OS just chose.
public sealed class Lake : IHostedService, IDisposable, IAsyncDisposable
{
    readonly Config config;
    readonly Catalog catalog;
    readonly Gateway gateway;
    readonly PgServer server;
    readonly TdsServer tds;
    readonly DuckDBConnection duck;
    readonly IDuckDbInstaller installer;

    /// Assembled by `AddDuckPg` rather than by a caller: what a lake is made of -- the catalog, the
    /// gateway, the two front doors -- is not a surface worth publishing, and freezing it would
    /// make every part of it an API.
    internal Lake(Config config, Catalog catalog, Gateway gateway, PgServer server, TdsServer tds,
                  DuckDBConnection duck, IDuckDbInstaller installer)
    {
        this.config = config;
        this.catalog = catalog;
        this.gateway = gateway;
        this.server = server;
        this.tds = tds;
        this.duck = duck;
        this.installer = installer;
    }

    CancellationTokenSource? stopping;
    Task? serving;
    IDisposable? owned;
    bool disposed;

    /// What the factory built this lake from, released when the lake is. A lake resolved from
    /// someone else's container owns nothing and disposes nothing of theirs.
    internal void Owns(IDisposable scope) => owned = scope;

    internal Catalog Catalog => catalog;

    /// Serving, once started: completes when the lake is stopped, or faults if a listener does.
    public Task Completion => serving ?? Task.CompletedTask;

    /// Where the PostgreSQL front door is listening, or null when none was configured.
    public IPEndPoint? Endpoint => server.Enabled ? server.Endpoint : null;

    /// Where the TDS front door is listening, or null when none was configured.
    public IPEndPoint? TdsEndpoint => tds.Enabled ? tds.Endpoint : null;

    /// Ready to hand to Npgsql.
    public string ConnectionString(string user = "admin") =>
        Endpoint is { } endpoint
            ? $"Host={endpoint.Address};Port={endpoint.Port};Username={user};Database={config.DatabaseName};SSL Mode=Disable"
            : throw new InvalidOperationException("no PostgreSQL front door: set `listen` in the configuration");

    /// Ready to hand to Microsoft.Data.SqlClient. `Encrypt=False` is part of the contract rather
    /// than a shortcut: TDS encrypts the login packet even in a plaintext session, and duckpg
    /// answers PRELOGIN with ENCRYPT_NOT_SUP.
    public string SqlConnectionString(string user = "sa") =>
        TdsEndpoint is { } endpoint
            ? $"Server={endpoint.Address},{endpoint.Port};Database={config.DatabaseName};User ID={user};" +
              "Password=duckpg;Encrypt=False;TrustServerCertificate=True"
            : throw new InvalidOperationException("no TDS front door: set `tds` in the configuration");

    public async Task StartAsync(CancellationToken cancellation)
    {
        // Before anything is opened, so a path that is wrong is still the subject of the answer.
        config.Validate();

        // Before the first native call, since that is where a missing library would surface as a
        // DllNotFoundException naming something the reader never asked for.
        if (config.InstallDuckDb && !DuckDbLibrary.SearchPath.Any(DuckDbLibrary.Usable))
            await installer.InstallAsync(cancellation);

        duck.Open();
        using (var command = duck.CreateCommand())
        {
            command.CommandText = Shims.Macros;
            command.ExecuteNonQuery();
        }

        // Registered on the database rather than on this connection, so every session's own
        // connection finds them without registering anything of its own.
        HostFunctions.Register(duck);

        catalog.Build(duck);

        server.Start();
        tds.Start();

        stopping = new CancellationTokenSource();
        serving = Task.WhenAll(server.ListenAsync(stopping.Token), tds.ListenAsync(stopping.Token));
    }

    public async Task StopAsync(CancellationToken cancellation)
    {
        if (stopping is null) return;

        await stopping.CancelAsync();
        try { await serving!.WaitAsync(cancellation); } catch (OperationCanceledException) { }
        // Once nothing is serving, so nothing else is on this connection: a materialized lake keeps
        // nothing of its own, and this is where what it was given goes out as a layer.
        gateway.Flush();
        stopping.Dispose();
        stopping = null;
        serving = null;
    }

    /// Stops without waiting: the listeners are canceled and the sockets closed, but a serving
    /// loop mid-row is not awaited. `DisposeAsync` is the one that waits, which is why it exists.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        stopping?.Cancel();
        // Not waited for -- that is what `DisposeAsync` is -- but the writes still have to get out.
        // The listeners are cancelled above, and `Flush` runs on this connection under the gateway's
        // lock, which is what a session's own commit takes to reach it.
        gateway.Flush();
        stopping?.Dispose();
        stopping = null;
        duck.Dispose();
        owned?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        if (stopping is not null)
        {
            await stopping.CancelAsync();
            try { await serving!.ConfigureAwait(false); } catch (OperationCanceledException) { }
            gateway.Flush();
            stopping.Dispose();
            stopping = null;
        }

        duck.Dispose();

        // The container holds this lake, so disposing it comes back here -- which `disposed` above
        // has already answered.
        switch (owned)
        {
            case IAsyncDisposable asynchronous: await asynchronous.DisposeAsync(); break;
            case { } synchronous: synchronous.Dispose(); break;
        }
    }
}
