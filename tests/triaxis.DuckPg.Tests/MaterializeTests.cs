using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A lake with the layers collapsed: every table evaluated once into a real DuckDB table, which is
/// then what the sessions read and write. There is no merge to bind on every read, no write branch
/// to earn and no tombstone to hide anything -- a delete deletes. What it holds is lost on exit,
/// save for the delta a write directory is given when the lake stops.
public class MaterializeTests : IDisposable
{
    readonly TestLake lake = new TestLake("materialize")
        .Json("base", "orders", """
            [{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}, {"order_id": 3, "note": "base"}]
            """)
        .Json("top", "orders", """[{"order_id": 2, "note": "shadowed"}]""")
        .Stack("base", "top")
        .WriteTo("local")
        .Materialized()
        .WithTds();

    public MaterializeTests()
    {
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    /// The stack is still the stack -- it is merged once, at build, instead of on every read.
    [Fact]
    public void TheLayersAreCollapsedIntoWhatIsPublished()
    {
        Assert.Equal(["base", "shadowed", "base"],
                     lake.Query("SELECT note FROM lake.orders ORDER BY order_id"));
    }

    /// And what is published is a table, not a view: that is the whole point of the mode.
    [Fact]
    public void WhatIsPublishedIsATable()
    {
        Assert.Equal(["BASE TABLE"], lake.Query(
            "SELECT table_type FROM information_schema.tables " +
            "WHERE table_schema = 'lake' AND table_name = 'orders'"));
    }

    /// A write goes where the reads come from. Nothing is promoted, because there is nothing to
    /// promote: no branch above the rows and none below them.
    [Fact]
    public void AWriteLandsInTheTableItself()
    {
        using var connection = Open();
        new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
            .ExecuteNonQuery();
        new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'new')", connection)
            .ExecuteNonQuery();

        Assert.Equal(["written", "shadowed", "base", "new"],
                     lake.Query("SELECT note FROM lake.orders ORDER BY order_id"));
    }

    /// A delete deletes. There is no tombstone table at all, and no row underneath to hide.
    [Fact]
    public void ADeleteTakesTheRowRatherThanHidingIt()
    {
        using var connection = Open();
        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 2", connection).ExecuteNonQuery());

        Assert.Equal(["1", "3"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
        Assert.Empty(lake.Query(
            "SELECT table_name FROM information_schema.tables WHERE table_name LIKE '%__del'"));
    }

    /// A key that moves needs no tombstone either, which is the one place the plan had to change.
    [Fact]
    public void AKeyMayMoveWithoutLeavingAnythingBehind()
    {
        using var connection = Open();
        new SqlCommand("UPDATE [orders] SET [order_id] = 9 WHERE [order_id] = 1", connection).ExecuteNonQuery();

        Assert.Equal(["2", "3", "9"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    /// What the lake was given goes out once, as the layer the write directory always held: the rows
    /// that are not what the layers said, and the keys that were there and are not.
    [Fact]
    public void WhatWasWrittenLeavesAsADeltaWhenTheLakeStops()
    {
        using (var connection = Open())
        {
            new SqlCommand("UPDATE [orders] SET [note] = 'written' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();
            new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'new')", connection)
                .ExecuteNonQuery();
        }

        // Restarting throws away everything in memory: what comes back is what the delta carried.
        lake.Restart();
        Assert.Equal(["1=written", "2=shadowed", "4=new"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// Twice is not once. The second run reads the first run's delta back as a layer, so the
    /// baseline the next delta is measured against must still be the *read* layers alone -- measured
    /// against a stack that already carried it, a delta shrinks to nothing and the write file is
    /// emptied of everything the run before it did.
    [Fact]
    public void ASecondRunKeepsWhatTheFirstOneWrote()
    {
        using (var connection = Open())
        {
            new SqlCommand("UPDATE [orders] SET [note] = 'first run' WHERE [order_id] = 1", connection)
                .ExecuteNonQuery();
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();
            new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (4, 'first run')", connection)
                .ExecuteNonQuery();
        }

        lake.Restart();
        using (var connection = Open())
            new SqlCommand("INSERT INTO [orders] ([order_id], [note]) VALUES (5, 'second run')", connection)
                .ExecuteNonQuery();

        lake.Restart();
        Assert.Equal(["1=first run", "2=shadowed", "4=first run", "5=second run"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }

    /// And a row hidden by the first run stays hidden however many times the lake is restarted --
    /// the tombstone is recomputed each time, so it has to be recomputed against the same baseline.
    [Fact]
    public void ADeletedRowDoesNotComeBackOnTheThirdRun()
    {
        using (var connection = Open())
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();

        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    /// A row-limited write is limited on the keys either way -- there is no tombstone here to hide
    /// what a limit left behind, which is the one step the two modes do not share.
    [Fact]
    public void ARowLimitedDeleteTakesTheRowsItSaysAndNoMore()
    {
        using (var connection = Open())
            Assert.Equal(2, new SqlCommand("DELETE TOP (2) FROM [orders]", connection).ExecuteNonQuery());

        Assert.Single(lake.Query("SELECT order_id FROM lake.orders"));
        lake.Restart();
        Assert.Single(lake.Query("SELECT order_id FROM lake.orders"));
    }

    /// The delta is the write layer's own format, so an ordinary lake reads it as the layer it is.
    [Fact]
    public void AndAnOrdinaryLakeReadsThatDeltaBack()
    {
        using (var connection = Open())
            new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 3", connection).ExecuteNonQuery();

        lake.Stop();
        lake.Config.Materialize = false;
        lake.Start();

        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }
}

/// A declared identity draws from a sequence seeded past what the lake already holds, and a
/// materialized lake holds the previous run's delta -- so the seeding has to see it. These check the
/// mechanism the layer machinery normally provides and this mode had to arrive at another way.
public class MaterializedIdentityTests : IDisposable
{
    readonly TestLake lake = new TestLake("materialize-identity")
        .Json("base", "things", """[{"id": 1, "note": "base"}]""")
        .Stack("base")
        .WriteTo("local")
        .Materialized()
        .WithTds();

    public MaterializedIdentityTests()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            [new Dacpac.TableModel("things", [("id", "int"), ("note", "nvarchar")], ["id"], Identity: ["id"])]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    int Insert(string note)
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return (int)new SqlCommand(
            $"INSERT INTO [things] ([note]) OUTPUT INSERTED.[id] VALUES ('{note}')", connection).ExecuteScalar()!;
    }

    /// A key handed out by the run before is one the files now hold, so the next run starts past it
    /// rather than over it -- which is the whole point of seeding from what is there.
    [Fact]
    public void ARestartCarriesOnFromTheKeysAlreadyHandedOut()
    {
        Assert.Equal(2, Insert("first run"));
        Assert.Equal(3, Insert("first run"));

        lake.Restart();
        Assert.Equal(4, Insert("second run"));

        lake.Restart();
        Assert.Equal(["1", "2", "3", "4"], lake.Query("SELECT id FROM lake.things ORDER BY id"));
    }
}

/// The rules a lake keeps are the catalog's rather than the layers', so collapsing the layers does
/// not put them down: a reference still refuses, and a cascade still cascades.
public class MaterializedReferenceTests : IDisposable
{
    readonly TestLake lake = new TestLake("materialize-references")
        .Json("base", "orders", """[{"id": 1, "note": "a"}, {"id": 2, "note": "b"}]""")
        .Json("base", "lines", """[{"id": 10, "order_id": 1, "qty": 5}, {"id": 11, "order_id": 2, "qty": 1}]""")
        .Stack("base")
        .WriteTo("local")
        .Materialized()
        .WithTds();

    void Declare(string onDelete)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"),
        [
            new Dacpac.TableModel("orders", [("id", "int"), ("note", "nvarchar")], ["id"]),
            new Dacpac.TableModel("lines", [("id", "int"), ("order_id", "int"), ("qty", "int")], ["id"]),
        ], [],
        [new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["id"], onDelete)]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    [Fact]
    public void AReferenceStillRefuses()
    {
        Declare("NoAction");
        using var connection = Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());
        Assert.Equal(547, refused.Number);
        Assert.Equal(["1", "2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
    }

    [Fact]
    public void ACascadeStillTakesWhatPointed()
    {
        Declare("Cascade");
        using var connection = Open();

        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        Assert.Equal(["2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["11"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
    }
}

/// What a shared table cannot carry. A filter and a session-variable column are answered per
/// session, so a mode that hands every session the same table has to say so rather than quietly
/// stop applying them.
public class MaterializeRefusalTests
{
    [Fact]
    public void AFilteredTableIsRefusedAtStartup()
    {
        using var lake = new TestLake("materialize-filter")
            .Json("base", "orders", """[{"order_id": 1, "tenant": "a"}]""")
            .Stack("base")
            .Materialized();
        lake.Config.Tables["orders"] = new TableConfig { Filter = "tenant = 'a'" };

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("filter", refused.Message);
        Assert.Contains("orders", refused.Message);
    }

    [Fact]
    public void SoIsASessionVariableColumn()
    {
        using var lake = new TestLake("materialize-variable")
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .Materialized();
        lake.Config.Columns.Add(new ColumnConfig { Name = "who", Expr = "getvariable('duckpg_user')" });

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Start());
        Assert.Contains("session variable", refused.Message);
    }
}

/// The lifecycle an embedder has, and the CLI with it: a lake from the factory, never told to stop,
/// just disposed -- or not even that, if a signal takes the process before the unwinding finishes.
/// Every one of those paths has to leave the delta behind, because none of them is unusual.
public class MaterializedShutdownTests
{
    static (Config Config, string Root) Lake()
    {
        var root = Path.Combine(Path.GetTempPath(), "duckpg-tests", $"shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "base"));
        Directory.CreateDirectory(Path.Combine(root, "local"));
        File.WriteAllText(Path.Combine(root, "base", "orders.json"), """[{"order_id": 1, "note": "base"}]""");

        return (new Config
        {
            Listen = "127.0.0.1:0",
            Layers = [Path.Combine(root, "base")],
            Write = Path.Combine(root, "local"),
            Materialize = true,
            DefaultKey = ["order_id"],
        }, root);
    }

    static void Write(Lake lake)
    {
        using var connection = new Npgsql.NpgsqlConnection(lake.ConnectionString("admin"));
        connection.Open();
        new Npgsql.NpgsqlCommand("UPDATE lake.orders SET note = 'written' WHERE order_id = 1", connection)
            .ExecuteNonQuery();
    }

    /// Disposed asynchronously, never stopped -- what `await using` gives an embedder.
    [Fact]
    public async Task ADisposedLakeLeavesItsDelta()
    {
        var (config, root) = Lake();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDuckPgFactory();
        await using var provider = services.BuildServiceProvider();

        var lake = await provider.GetRequiredService<IDuckPgLakeFactory>()
            .StartAsync(config, TestContext.Current.CancellationToken);
        Write(lake);
        await lake.DisposeAsync();

        Assert.NotEmpty(Directory.GetFiles(config.Write!));
        Directory.Delete(root, true);
    }

    /// And disposed synchronously, which is what a container teardown does -- and what the CLI's own
    /// shutdown came down to. This one wrote nothing at all until it was made to.
    [Fact]
    public async Task ASynchronouslyDisposedLakeLeavesItToo()
    {
        var (config, root) = Lake();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDuckPgFactory();
        await using var provider = services.BuildServiceProvider();

        var lake = await provider.GetRequiredService<IDuckPgLakeFactory>()
            .StartAsync(config, TestContext.Current.CancellationToken);
        Write(lake);
        lake.Dispose();

        Assert.NotEmpty(Directory.GetFiles(config.Write!));
        Directory.Delete(root, true);
    }
}

/// A lake checkpointed once it is built. DuckDB writes table data uncompressed and compresses it at
/// a checkpoint, which nothing drives an in-memory database to -- so this is the only thing between
/// a materialized lake and holding every column in its widest form.
public class CompressTests
{
    static TestLake Lake(bool compress)
    {
        var lake = new TestLake($"compress-{compress}")
            .Json("base", "orders", """
                [{"order_id": 1, "note": "base"}, {"order_id": 2, "note": "base"}, {"order_id": 3, "note": "base"}]
                """)
            .Stack("base")
            .Materialized();
        if (compress) lake.Compressed();
        lake.Config.DefaultKey = ["order_id"];
        return lake.Start();
    }

    /// What a table's segments are held as, counted by kind. Never empty: a table DuckDB knows
    /// nothing about answers with no rows, which every assertion below would read as a pass.
    static List<string> Segments(TestLake lake, string table)
    {
        var segments = lake.Query(
            $"SELECT compression, count(*) FROM pragma_storage_info('{table}') GROUP BY 1 ORDER BY 1");
        Assert.NotEmpty(segments);
        return segments;
    }

    /// Every segment of the collapsed table is held as something narrower than it was written as,
    /// and the table still says what it said.
    [Fact]
    public void WhatTheBuildLeftIsCompressed()
    {
        using var lake = Lake(compress: true);

        Assert.DoesNotContain(Segments(lake, "lake.orders"), s => s.StartsWith("Uncompressed"));
        Assert.Equal(["1", "2", "3"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    /// And without it nothing is, which is what says the test above measures the option rather than
    /// something DuckDB was doing anyway.
    [Fact]
    public void WithoutItNothingIs()
    {
        using var lake = Lake(compress: false);

        Assert.All(Segments(lake, "lake.orders"), s => Assert.StartsWith("Uncompressed", s));
    }

    /// A checkpoint is the whole database's, so the tables a JSON layer was read into are compressed
    /// too -- a layered lake keeps those in memory just as a materialized one keeps its own.
    [Fact]
    public void SoAreTheTablesALayerWasReadInto()
    {
        using var lake = new TestLake("compress-layers")
            .Json("base", "orders", """[{"order_id": 1, "note": "base"}]""")
            .Stack("base")
            .Compressed()
            .Start();

        Assert.DoesNotContain(Segments(lake, @"layer.""orders#0"""),
                              s => s.StartsWith("Uncompressed"));
    }

    /// Only what was there when the build ended: a row written after it is held as it arrived, and
    /// nothing checkpoints a second time. What matters is that it is still the row.
    [Fact]
    public void AWriteAfterTheBuildIsStillARow()
    {
        using var lake = Lake(compress: true);
        lake.Execute("INSERT INTO lake.orders (order_id, note) VALUES (4, 'new')");
        lake.Execute("UPDATE lake.orders SET note = 'written' WHERE order_id = 1");

        Assert.Equal(["1=written", "2=base", "3=base", "4=new"],
                     lake.Query("SELECT order_id || '=' || note FROM lake.orders ORDER BY order_id"));
    }
}
