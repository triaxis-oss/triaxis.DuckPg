using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

/// The TDS front door. The same lake, the same gateway, a different protocol -- a client that
/// speaks SQL Server sees the layers a PostgreSQL client sees.
sealed class TdsServer(string listen, Gateway gateway, DuckDBConnection root, ILoggerFactory loggers)
{
    readonly ILogger<TdsServer> logger = loggers.CreateLogger<TdsServer>();
    readonly ConcurrentDictionary<int, TdsSession> sessions = new();

    TcpListener? listener;

    public IPEndPoint Endpoint =>
        (IPEndPoint)(listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    public void Register(TdsSession session) => sessions[session.ProcessId] = session;

    public void Unregister(TdsSession session) => sessions.TryRemove(session.ProcessId, out _);

    public void Start()
    {
        var colon = listen.LastIndexOf(':');
        var (host, port) = colon < 0 ? (listen, 1433) : (listen[..colon], int.Parse(listen[(colon + 1)..]));

        listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
        logger.LogInformation("duckpg listening for TDS on {Endpoint}", Endpoint);
    }

    public async Task ListenAsync(CancellationToken cancellation)
    {
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
            var connection = (DuckDBConnection)root.Duplicate();
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
