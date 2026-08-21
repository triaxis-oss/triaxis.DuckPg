using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// What a lake leaves behind when it goes. A session that outlives its lake holds a DuckDB
/// connection onto the lake's database, and DuckDB keeps a database alive for as long as one
/// connection onto it is open -- so a client still on the wire is a whole lake nothing will ever
/// release, and a host that starts one lake per tenant accumulates them.
public class ShutdownTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"duckpg-shutdown-{Guid.NewGuid():N}");

    public ShutdownTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "orders.json"), """[{"order_id": 1, "amount": 10}]""");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// Unpooled throughout: what a stopped lake does to a client is the subject here, and a pool
    /// that kept the socket would be answering for it.
    ServiceProvider Services() => new ServiceCollection()
        .AddDuckPg(config =>
        {
            config.Listen = "127.0.0.1:0";
            config.Tds = "127.0.0.1:0";
            config.Layers = [Path.Combine(root, "base")];
        })
        .BuildServiceProvider();

    /// Both doors, and both of them shut on the way out.
    [Fact]
    public async Task StoppingALakeDisconnectsItsClients()
    {
        using var services = Services();
        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        using var pg = new NpgsqlConnection(lake.ConnectionString() + ";Pooling=false");
        pg.Open();
        using var tds = new SqlConnection(lake.SqlConnectionString() + ";Pooling=false");
        tds.Open();

        await lake.StopAsync(TestContext.Current.CancellationToken);

        Assert.ThrowsAny<Exception>(() => new NpgsqlCommand("SELECT 1", pg).ExecuteScalar());
        Assert.ThrowsAny<Exception>(() => new SqlCommand("SELECT 1", tds).ExecuteScalar());
    }

    /// A container teardown never reaches `StopAsync`, so it has to shut the doors itself.
    [Fact]
    public async Task DisposingALakeDisconnectsItsClients()
    {
        using var services = Services();
        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        using var pg = new NpgsqlConnection(lake.ConnectionString() + ";Pooling=false");
        pg.Open();

        await lake.DisposeAsync();

        Assert.ThrowsAny<Exception>(() => new NpgsqlCommand("SELECT 1", pg).ExecuteScalar());
    }

    /// A client that walked away mid-transaction holds the gateway's turn, and the flush at the end
    /// of a stop is on the other side of it: dropping the session is what gives it back.
    [Fact]
    public async Task AnOpenTransactionDoesNotWedgeTheStop()
    {
        using var services = Services();
        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        using var pg = new NpgsqlConnection(lake.ConnectionString() + ";Pooling=false");
        pg.Open();
        new NpgsqlCommand("BEGIN", pg).ExecuteNonQuery();

        await lake.StopAsync(TestContext.Current.CancellationToken);

        Assert.ThrowsAny<Exception>(() => new NpgsqlCommand("SELECT 1", pg).ExecuteScalar());
    }
}
