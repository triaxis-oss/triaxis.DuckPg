using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// A front door: the listener, the connections it accepted, and the closing of both. None of that
/// is a protocol's business, which is why it is here rather than twice over in the two servers.
///
/// Closing the connections is what this exists for. A session blocks on its socket on a thread of
/// its own and holds a DuckDB connection onto the lake's database, and DuckDB keeps a database
/// alive for as long as one connection onto it is open -- so a stop that cancels only the accept
/// loop hands the whole lake to a client nobody is waiting for any more, and nothing will ever
/// come back for it.
sealed class Doorway(string address, int defaultPort, ILogger logger)
{
    /// Every accepted connection and the completion of the thread serving it, so that dropping the
    /// sockets can be waited for rather than only asked for. The socket rather than the `TcpClient`
    /// it came from, since that hands back a null one the moment the session thread disposes it.
    readonly ConcurrentDictionary<Socket, TaskCompletionSource> live = new();

    TcpListener? listener;
    volatile bool closed;

    public IPEndPoint Endpoint =>
        (IPEndPoint)(listener ?? throw new InvalidOperationException("not started")).LocalEndpoint;

    /// Binding is separate from accepting so that a caller can learn the port before the first
    /// client arrives, which is what makes port 0 usable.
    public void Start()
    {
        var colon = address.LastIndexOf(':');
        var (host, port) = colon < 0
            ? (address, defaultPort)
            : (address[..colon], int.Parse(address[(colon + 1)..]));

        listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
    }

    public async Task ListenAsync(Action<TcpClient> serve, CancellationToken cancellation)
    {
        if (listener is null) Start();

        try
        {
            while (true) Accept(await listener!.AcceptTcpClientAsync(cancellation), serve);
        }
        // `closed` covers the door being shut under a pending accept, which is what a synchronous
        // dispose does: the socket faults rather than cancels, and neither is a fault of the lake's.
        catch (Exception e) when (e is OperationCanceledException || closed)
        {
            logger.LogDebug("listener shutting down");
        }
        finally
        {
            listener?.Stop();
        }
    }

    /// A session blocks on its socket and owns a DuckDB connection, so it gets a thread of its own.
    /// Registered before that thread starts, so a close landing between the two still finds it.
    void Accept(TcpClient client, Action<TcpClient> serve)
    {
        client.NoDelay = true;

        var socket = client.Client;
        var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live[socket] = served;
        if (closed) Drop(socket);

        new Thread(() =>
        {
            try
            {
                serve(client);
            }
            // A session's failure is one client's and never the lake's, and a thread that throws
            // takes the process with it -- so nothing gets out of here. A dropped connection is
            // ordinary enough to say quietly; anything else is worth reading.
            catch (Exception e)
            {
                if (e is IOException or SocketException or ObjectDisposedException or ProtocolException)
                    logger.LogDebug("connection dropped: {Reason}", e.Message);
                else
                    logger.LogError(e, "session failed");
            }
            finally
            {
                live.TryRemove(socket, out _);
                client.Dispose();
                served.SetResult();
            }
        }) { IsBackground = true }.Start();
    }

    /// The door shut and every live connection dropped, which is what ends the thread blocked on
    /// one. Not waited for -- `DrainAsync` is the one that waits.
    public void Close()
    {
        closed = true;
        listener?.Stop();
        foreach (var socket in live.Keys) Drop(socket);
    }

    /// The same, awaited: once this returns, nothing but the lake itself is on its database.
    public async Task DrainAsync(CancellationToken cancellation)
    {
        Close();
        // Snapshotted after the close, since nothing more is accepted past it.
        await Task.WhenAll(live.Values.Select(served => served.Task)).WaitAsync(cancellation);
    }

    /// Shut down rather than disposed, so a session reads the end of its socket as the end of its
    /// client -- which is a case every protocol here already answers. Closing the stream under a
    /// thread inside it is that same ending arriving as whichever of half a dozen exceptions the
    /// timing lands on instead.
    static void Drop(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }
}
