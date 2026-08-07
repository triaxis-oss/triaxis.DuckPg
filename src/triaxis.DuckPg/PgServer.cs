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

    TcpListener? listener;

    /// A front door is opt-in, the same way the TDS one is: a consumer speaking only SQL Server
    /// should not have to open a PostgreSQL listener it never uses.
    public bool Enabled => config.Listen is { Length: > 0 };

    /// Where the server actually bound, which is not what was configured when the port was 0.
    public IPEndPoint Endpoint => (IPEndPoint)(listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    /// Binding is separate from accepting so that a caller can learn the port before the first
    /// client arrives.
    public void Start()
    {
        if (!Enabled) return;

        var (host, port) = Split(config.Listen!);
        listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
        logger.LogInformation("duckpg listening on {Endpoint}", Endpoint);
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
                // A session blocks on its socket and owns a DuckDB connection, so it gets a thread.
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("shutting down");
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
            DuckDbSession.SearchPath(connection, gateway.Config);
            using var session = new PgSession(client, gateway, connection, this, loggers.CreateLogger<PgSession>());
            session.Run();
        }
        catch (Exception e) when (e is IOException or SocketException or ProtocolException)
        {
            logger.LogDebug("connection dropped: {Reason}", e.Message);
            client.Dispose();
        }
    }

    static (string Host, int Port) Split(string listen)
    {
        var colon = listen.LastIndexOf(':');
        return colon < 0 ? (listen, 55432) : (listen[..colon], int.Parse(listen[(colon + 1)..]));
    }
}
