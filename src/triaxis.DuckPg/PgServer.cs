using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

sealed class PgServer(Config config, Gateway gateway, DuckDBConnection root, ILoggerFactory loggers)
{
    readonly ILogger<PgServer> logger = loggers.CreateLogger<PgServer>();

    /// Clients gate features on this; DuckDB's own version goes out in the ParameterStatus banner.
    public const string ServerVersion = "16.0";

    readonly ConcurrentDictionary<int, PgSession> sessions = new();

    public void Register(PgSession session) => sessions[session.ProcessId] = session;

    public void Unregister(PgSession session) => sessions.TryRemove(session.ProcessId, out _);

    /// PostgreSQL cancellation arrives on a second connection; DuckDB's interrupt is the equivalent.
    public void CancelBackend(int processId, int secret)
    {
        if (sessions.TryGetValue(processId, out var session) && session.Secret == secret)
            session.Cancel();
    }

    /// A front door is opt-in, the same way the TDS one is: a consumer speaking only SQL Server
    /// should not have to open a PostgreSQL listener it never uses.
    readonly Doorway? door = config.Listen is { Length: > 0 } listen
        ? new Doorway(listen, 55432, loggers.CreateLogger<PgServer>())
        : null;

    public bool Enabled => door is not null;

    /// Where the server actually bound, which is not what was configured when the port was 0.
    public IPEndPoint Endpoint =>
        (door ?? throw new InvalidOperationException("no PostgreSQL front door")).Endpoint;

    /// Binding is separate from accepting so that a caller can learn the port before the first
    /// client arrives.
    public void Start()
    {
        if (door is null) return;

        door.Start();
        logger.LogInformation("duckpg listening on {Endpoint}", door.Endpoint);
    }

    public Task ListenAsync(CancellationToken cancellation) =>
        door?.ListenAsync(Serve, cancellation) ?? Task.CompletedTask;

    public void Close() => door?.Close();

    public Task DrainAsync(CancellationToken cancellation) =>
        door?.DrainAsync(cancellation) ?? Task.CompletedTask;

    void Serve(TcpClient client)
    {
        var connection = DuckDbSession.Of(root);
        connection.Open();
        DuckDbSession.SearchPath(connection, gateway.Config);
        using var session = new PgSession(client, gateway, connection, this, loggers.CreateLogger<PgSession>());
        session.Run();
    }
}
