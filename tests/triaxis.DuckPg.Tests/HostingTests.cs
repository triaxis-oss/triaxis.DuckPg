using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// The library as a consumer sees it: a service registration, a host, and a connection string.
/// Nothing here reaches past what the package makes public.
public class HostingTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"duckpg-host-{Guid.NewGuid():N}");

    public HostingTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "base"));
        File.WriteAllText(Path.Combine(root, "base", "orders.json"), """[{"order_id": 1, "amount": 10}]""");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// A pool of this test's own. Npgsql keys one by the connection string and hands a pooled
    /// connection out without testing it, and port 0 means the OS is free to give a lake starting
    /// the port a lake that stopped just handed back.
    NpgsqlConnection Connect(Lake lake) =>
        new(lake.ConnectionString() + $";Application Name={Path.GetFileName(root)}");

    /// The lake starts with the host and stops with it, and the port it bound is readable as soon
    /// as the host is up -- which is what makes port 0 usable from a fixture.
    [Fact]
    public async Task AHostStartsAndStopsTheLake()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddDuckPg(config =>
            {
                config.Listen = "127.0.0.1:0";
                config.Tds = "127.0.0.1:0";
                config.Layers = [Path.Combine(root, "base")];
            }))
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var lake = host.Services.GetRequiredService<Lake>();

        Assert.NotEqual(0, lake.Endpoint!.Port);
        Assert.NotNull(lake.TdsEndpoint);

        using (var pg = Connect(lake))
        {
            pg.Open();
            using var command = new NpgsqlCommand("SELECT amount FROM lake.orders", pg);
            Assert.Equal(10L, command.ExecuteScalar());
        }

        using (var tds = new SqlConnection(lake.SqlConnectionString()))
        {
            tds.Open();
            using var command = new SqlCommand("SELECT amount FROM orders", tds);
            Assert.Equal(10L, command.ExecuteScalar());
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
        await lake.Completion;
    }

    /// Bound from configuration, the way a `duckpg.yaml` or an appsettings section would.
    [Fact]
    public async Task ItBindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Listen"] = "127.0.0.1:0",
            ["Layers:0"] = Path.Combine(root, "base"),
            ["Schema"] = "warehouse",
        }).Build();

        var services = new ServiceCollection().AddDuckPg(configuration).BuildServiceProvider();
        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            using var pg = Connect(lake);
            pg.Open();
            using var command = new NpgsqlCommand("SELECT amount FROM warehouse.orders", pg);
            Assert.Equal(10L, command.ExecuteScalar());
        }
        finally
        {
            await lake.StopAsync(TestContext.Current.CancellationToken);
            services.Dispose();
        }
    }

    /// Asking for a DuckDB that is already there fetches nothing and changes nothing -- the option
    /// is about a machine that has none, and must not cost a download on one that does.
    [Fact]
    public async Task InstallDuckDbIsANoOpWhenOneIsAlreadyThere()
    {
        var services = new ServiceCollection()
            .AddDuckPg(config =>
            {
                config.Listen = "127.0.0.1:0";
                config.Layers = [Path.Combine(root, "base")];
                config.InstallDuckDb = true;
            })
            .BuildServiceProvider();

        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            using var pg = Connect(lake);
            pg.Open();
            using var command = new NpgsqlCommand("SELECT amount FROM lake.orders", pg);
            Assert.Equal(10L, command.ExecuteScalar());
        }
        finally
        {
            await lake.StopAsync(TestContext.Current.CancellationToken);
            services.Dispose();
        }
    }

    /// Without a TDS address there is no second front door, and asking for one says so rather than
    /// handing back a connection string that cannot connect.
    [Fact]
    public async Task TheTdsDoorIsOptIn()
    {
        var services = new ServiceCollection()
            .AddDuckPg(config => { config.Listen = "127.0.0.1:0"; config.Layers = [Path.Combine(root, "base")]; })
            .BuildServiceProvider();

        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(lake.TdsEndpoint);
        Assert.Contains("no TDS front door", Assert.Throws<InvalidOperationException>(() => lake.SqlConnectionString()).Message);

        await lake.StopAsync(TestContext.Current.CancellationToken);
        services.Dispose();
    }

    /// The database a client believes it is connected to is the lake's to name, and naming it moves
    /// nothing: the tables are still published into the schema, which the two are free to disagree
    /// about. Unnamed, it is the schema, which is what every client saw before there was a `database`.
    [Fact]
    public async Task TheDatabaseNameIsTheLakesToName()
    {
        var services = new ServiceCollection()
            .AddDuckPg(config =>
            {
                config.Listen = "127.0.0.1:0";
                config.Tds = "127.0.0.1:0";
                config.Schema = "warehouse";
                config.Database = "erp";
                config.Layers = [Path.Combine(root, "base")];
            })
            .BuildServiceProvider();

        var lake = services.GetRequiredService<Lake>();
        await lake.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            Assert.Contains("Database=erp;", lake.ConnectionString());
            Assert.Contains("Database=erp;", lake.SqlConnectionString());

            using var pg = Connect(lake);
            pg.Open();
            using var query = new NpgsqlCommand("SELECT amount FROM warehouse.orders", pg);
            Assert.Equal(10L, query.ExecuteScalar());

            // What the server sends back in ENVCHANGE, rather than what the connection string said.
            using var tds = new SqlConnection(lake.SqlConnectionString());
            tds.Open();
            Assert.Equal("erp", tds.Database);
        }
        finally
        {
            await lake.StopAsync(TestContext.Current.CancellationToken);
            services.Dispose();
        }
    }

    /// A lake per partition, from one factory: each has its own layers, its own everything, and is
    /// the only thing the caller has to hold or dispose.
    [Fact]
    public async Task AFactoryStartsALakePerConfiguration()
    {
        foreach (var partition in (string[])["one", "two"])
        {
            Directory.CreateDirectory(Path.Combine(root, partition));
            File.WriteAllText(Path.Combine(root, partition, "orders.json"),
                $$"""[{"order_id": 1, "amount": {{(partition == "one" ? 11 : 22)}}}]""");
        }

        var services = new ServiceCollection().AddDuckPgFactory().BuildServiceProvider();
        var factory = services.GetRequiredService<IDuckPgLakeFactory>();

        // Concurrently, because a fleet of lakes is the point of a factory.
        string[] partitions = ["one", "two"];
        var lakes = await Task.WhenAll(partitions.Select(async partition =>
            (Name: partition, Lake: await factory.StartAsync(new Config
            {
                Listen = null,
                Tds = "127.0.0.1:0",
                Layers = [Path.Combine(root, partition)],
                Writable = true,
            }, TestContext.Current.CancellationToken))));

        try
        {
            foreach (var one in lakes)
            {
                using var connection = new SqlConnection(one.Lake.SqlConnectionString());
                connection.Open();
                using var command = new SqlCommand("SELECT amount FROM orders", connection);
                Assert.Equal(one.Name == "one" ? 11L : 22L, command.ExecuteScalar());
            }

            Assert.NotEqual(lakes[0].Lake.TdsEndpoint!.Port, lakes[1].Lake.TdsEndpoint!.Port);
        }
        finally
        {
            // One thing to dispose, and it releases what it was built from.
            foreach (var one in lakes) await one.Lake.DisposeAsync();
            services.Dispose();
        }
    }

    /// A consumer speaking only SQL Server opens only that door -- no PostgreSQL listener it never
    /// uses, and no port taken.
    [Fact]
    public async Task TheTwoDoorsAreIndependentlyOptIn()
    {
        var services = new ServiceCollection().AddDuckPgFactory().BuildServiceProvider();
        var factory = services.GetRequiredService<IDuckPgLakeFactory>();

        await using var lake = await factory.StartAsync(new Config
        {
            Listen = null,
            Tds = "127.0.0.1:0",
            Layers = [Path.Combine(root, "base")],
        }, TestContext.Current.CancellationToken);

        Assert.Null(lake.Endpoint);
        Assert.NotNull(lake.TdsEndpoint);
        Assert.Contains("no PostgreSQL front door",
            Assert.Throws<InvalidOperationException>(() => lake.ConnectionString()).Message);

        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using var command = new SqlCommand("SELECT amount FROM orders", connection);
        Assert.Equal(10L, command.ExecuteScalar());

        services.Dispose();
    }

    /// A path that is wrong says so, naming it, before anything is opened -- rather than a lake that
    /// starts empty and a binder error much later.
    [Theory]
    [InlineData("layers", "layer directory not found")]
    [InlineData("dacpac", "dacpac not found")]
    [InlineData("doors", "no front door to open")]
    [InlineData("cache", "would read its own copies back")]
    public async Task WhatCannotWorkIsSaidBeforeAnythingOpens(string wrong, string expected)
    {
        var config = new Config { Listen = "127.0.0.1:0", Layers = [Path.Combine(root, "base")] };
        switch (wrong)
        {
            case "layers": config.Layers = [Path.Combine(root, "not-there")]; break;
            case "dacpac": config.Dacpac = Path.Combine(root, "not-there.dacpac"); break;
            case "doors": config.Listen = null; config.Tds = ""; break;
            case "cache": config.Cache = Path.Combine(root, "base", "cache"); break;
        }

        var services = new ServiceCollection().AddDuckPgFactory().BuildServiceProvider();
        var factory = services.GetRequiredService<IDuckPgLakeFactory>();

        var problem = await Assert.ThrowsAsync<DuckPgConfigurationException>(
            () => factory.StartAsync(config, TestContext.Current.CancellationToken));
        Assert.Contains(expected, problem.Message);
        services.Dispose();
    }

}
