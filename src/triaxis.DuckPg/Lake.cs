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

        // The build is hundreds of small statements and no scan, and DuckDB's floor per statement
        // halves on one thread -- unless the build is the collapse, which wants the cores it will
        // serve with.
        Threads(config.Collapsed ? config.Threads : 1);

        using (var command = duck.CreateCommand())
        {
            command.CommandText = Shims.Macros;
            command.ExecuteNonQuery();
        }

        // Registered on the database rather than on this connection, so every session's own
        // connection finds them without registering anything of its own.
        HostFunctions.Register(duck);

        catalog.Build(duck);

        Threads(config.Threads);

        server.Start();
        tds.Start();

        stopping = new CancellationTokenSource();
        serving = Task.WhenAll(server.ListenAsync(stopping.Token), tds.ListenAsync(stopping.Token));
    }

    /// The database's, not this connection's: `threads` is a global setting, so every session
    /// borrowing a connection onto this database serves with what is set here.
    void Threads(int? count)
    {
        using var command = duck.CreateCommand();
        command.CommandText = count is { } threads ? $"SET threads = {threads}" : "RESET threads";
        command.ExecuteNonQuery();
    }

    public async Task StopAsync(CancellationToken cancellation)
    {
        if (stopping is null) return;

        await stopping.CancelAsync();
        try { await serving!.WaitAsync(cancellation); } catch (OperationCanceledException) { }
        await DrainAsync(cancellation);
        // Once nothing is serving, so nothing else is on this connection: a materialized lake keeps
        // nothing of its own, and this is where what it was given goes out as a layer.
        gateway.Flush();
        stopping.Dispose();
        stopping = null;
        serving = null;
    }

    /// The clients dropped, and waited for. Closing the listeners leaves every session that already
    /// connected running, and a session holds a DuckDB connection onto this lake's database -- so a
    /// lake stopped with a client still on it is a lake nothing will ever release.
    async Task DrainAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.WhenAll(server.DrainAsync(cancellation), tds.DrainAsync(cancellation));
        }
        catch (OperationCanceledException)
        {
            // A session inside a long query is not worth hanging a shutdown on. Its socket is
            // already closed, so it ends the moment DuckDB hands it back.
        }
    }

    /// Stops without waiting: the listeners are canceled and the sockets closed, but a serving
    /// loop mid-row is not awaited. `DisposeAsync` is the one that waits, which is why it exists.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        stopping?.Cancel();
        // The clients go with the listeners, since a session left on the wire holds a connection
        // onto this database and would outlive the last reference to the lake it is serving.
        server.Close();
        tds.Close();
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

        // Unconditionally, since a start that bound one door before failing on the other still has
        // to give that one back.
        server.Close();
        tds.Close();

        if (stopping is not null)
        {
            await stopping.CancelAsync();
            try { await serving!.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await DrainAsync(CancellationToken.None);
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
