using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// One layer directory, several lakes. DuckDB cannot read YAML, so a layer of it is converted into
/// a scratch copy first -- and a copy named after the layer is one every lake over that layer is
/// inside at once, which is a race and not a cache: it is thrown away as soon as it has been read,
/// so there is never anything there to reuse. A copy nobody else can name has neither problem.
public class SharedLayerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"duckpg-shared-{Guid.NewGuid():N}");

    string Layer => Path.Combine(root, "base");

    public SharedLayerTests()
    {
        Directory.CreateDirectory(Layer);
        File.WriteAllText(Path.Combine(Layer, "orders.yaml"), """
            - order_id: 1
              amount: 10
            - order_id: 2
              amount: 20
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    static readonly string[] Rows = ["1|10", "2|20"];

    const string Query = "SELECT order_id, amount FROM lake.orders ORDER BY order_id";

    /// The collision itself: lakes started at once over one layer, each converting into the copy the
    /// others were converting into. Whichever lost the race used to die with the file being used by
    /// another process, and which one that was varied run to run.
    [Fact]
    public async Task LakesStartedTogetherOverOneLayerAllServe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var services = new ServiceCollection().AddDuckPgFactory().BuildServiceProvider();
        var factory = services.GetRequiredService<IDuckPgLakeFactory>();

        // On threads of their own, because a lake's conversion is over before its StartAsync first
        // awaits -- started in a loop they would simply take turns.
        var lakes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            factory.StartAsync(new Config { Listen = "127.0.0.1:0", Layers = [Layer] }, cancellation),
            cancellation)));

        try
        {
            foreach (var lake in lakes)
            {
                using var connection = new NpgsqlConnection(lake.ConnectionString());
                connection.Open();
                using var reader = new NpgsqlCommand(Query, connection).ExecuteReader();

                var rows = new List<string>();
                while (reader.Read()) rows.Add($"{reader.GetValue(0)}|{reader.GetValue(1)}");
                Assert.Equal(Rows, rows);
            }
        }
        finally
        {
            foreach (var lake in lakes) await lake.DisposeAsync();
            services.Dispose();
        }
    }

    /// The same two callers, without the threads: one conversion of a source held open while a
    /// second conversion of that same source runs inside it. Two trees, and each taken away with the
    /// read that made it -- which is the whole of what keeps concurrent lakes out of each other's
    /// files, and what a copy named after the layer could not do either half of.
    [Fact]
    public void EveryConversionGetsATreeOfItsOwn()
    {
        var source = new LayerSource(0, LayerFormat.Yaml, Path.Combine(Layer, "orders.yaml"), []);
        string outer = "", inner = "";

        DuckPg.Layer.Read(source, first =>
        {
            DuckPg.Layer.Read(source, second => (outer, inner) = (first, second));
            Assert.True(File.Exists(first));
        });

        Assert.NotEqual(Path.GetDirectoryName(outer), Path.GetDirectoryName(inner));
        Assert.False(Directory.Exists(Path.GetDirectoryName(outer)!));
        Assert.False(Directory.Exists(Path.GetDirectoryName(inner)!));
    }
}
