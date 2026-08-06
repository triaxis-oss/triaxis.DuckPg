using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// The TDS front door. The same lake, the same gateway, a different protocol -- a client that
/// speaks SQL Server sees the layers a PostgreSQL client sees.
sealed class TdsServer(Config config, Gateway gateway, DuckDBConnection root, ILoggerFactory loggers)
{
    /// A second front door is opt-in: without an address there is nothing to bind and nothing to
    /// serve, which is what lets the same registration cover a lake that only speaks PostgreSQL.
    public bool Enabled => config.Tds is { Length: > 0 };

    readonly ILogger<TdsServer> logger = loggers.CreateLogger<TdsServer>();
    readonly ConcurrentDictionary<int, TdsSession> sessions = new();

    TcpListener? listener;

    public IPEndPoint Endpoint =>
        (IPEndPoint)(listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    public void Register(TdsSession session) => sessions[session.ProcessId] = session;

    public void Unregister(TdsSession session) => sessions.TryRemove(session.ProcessId, out _);

    public void Start()
    {
        if (!Enabled) return;

        var listen = config.Tds!;
        var colon = listen.LastIndexOf(':');
        var (host, port) = colon < 0 ? (listen, 1433) : (listen[..colon], int.Parse(listen[(colon + 1)..]));

        listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
        logger.LogInformation("duckpg listening for TDS on {Endpoint}", Endpoint);
    }

    public async Task ListenAsync(CancellationToken cancellation)
    {
        if (!Enabled) return;
        if (listener is null) Start();

        try
        {
            while (true)
            {
                var client = await listener!.AcceptTcpClientAsync(cancellation);
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("TDS listener shutting down");
        }
        finally
        {
            listener!.Stop();
        }
    }

    void Serve(TcpClient client)
    {
        client.NoDelay = true;
        try
        {
            var connection = DuckDbSession.Of(root);
            connection.Open();
            using var session = new TdsSession(client, gateway, connection, this, loggers.CreateLogger<TdsSession>());
            Register(session);
            session.Run();
        }
        catch (Exception e) when (e is IOException or SocketException or ProtocolException)
        {
            logger.LogDebug("connection dropped: {Reason}", e.Message);
            client.Dispose();
        }
    }
}
