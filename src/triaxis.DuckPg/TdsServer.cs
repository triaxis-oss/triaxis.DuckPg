using System.Net;
using System.Net.Sockets;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// The TDS front door. The same lake, the same gateway, a different protocol -- a client that
/// speaks SQL Server sees the layers a PostgreSQL client sees.
sealed class TdsServer(Config config, Gateway gateway, DuckDBConnection root, ILoggerFactory loggers)
{
    readonly ILogger<TdsServer> logger = loggers.CreateLogger<TdsServer>();

    /// A second front door is opt-in: without an address there is nothing to bind and nothing to
    /// serve, which is what lets the same registration cover a lake that only speaks PostgreSQL.
    readonly Doorway? door = config.Tds is { Length: > 0 } listen
        ? new Doorway(listen, 1433, loggers.CreateLogger<TdsServer>())
        : null;

    public bool Enabled => door is not null;

    public IPEndPoint Endpoint =>
        (door ?? throw new InvalidOperationException("no TDS front door")).Endpoint;

    public void Start()
    {
        if (door is null) return;

        door.Start();
        logger.LogInformation("duckpg listening for TDS on {Endpoint}", door.Endpoint);
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
        using var session = new TdsSession(client, gateway, connection, loggers.CreateLogger<TdsSession>());
        session.Run();
    }
}
