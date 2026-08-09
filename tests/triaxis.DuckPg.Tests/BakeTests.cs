namespace triaxis.DuckPg.Tests;

/// `duckpg bake` — the layers written out as the parquet layer they publish, so the next run scans
/// one file a table instead of parsing YAML and inferring its types again.
public class BakeTests
{
    [Fact]
    public void BakedLayerPublishesWhatTheStackDid()
    {
        using var lake = new TestLake()
            .Yaml("base", "customers", """
                - customer_id: 1
                  name: seeded
                - customer_id: 2
                  name: kept
                """)
            .Json("tenant", "customers", """[{"customer_id": 1, "name": "overridden"}]""")
            .Stack("base", "tenant");
        lake.Config.DefaultKey = ["customer_id"];

        lake.Baked().Stack("baked").Start();

        Assert.Equal(["1|overridden", "2|kept"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));
        Assert.Equal(["customer_id:bigint", "name:text"], lake.Columns("lake.customers"));
    }

    /// One scan of one file, which is the whole point: nothing is parsed and nothing is inferred.
    [Fact]
    public void ABakedTableIsOneLayer()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", "- order_id: 1")
            .Yaml("tenant", "orders", "- order_id: 2")
            .Stack("base", "tenant");
        lake.Config.DefaultKey = ["order_id"];

        lake.Baked().Stack("baked").Start();

        var table = lake.Catalog.Tables["orders"];
        Assert.Equal(LayerFormat.Parquet, Assert.Single(table.Layers).Source.Format);
    }

    /// A partition column joins the key, so a table read across `db=…` has to be written back
    /// across `db=…`: flattened, one database's row 1 would shadow another's on the next run.
    [Fact]
    public void PartitionsSurviveTheBake()
    {
        using var lake = new TestLake()
            .Parquet("live", "stock", "SELECT 1 AS sku, 'one' AS what", file: "db=one/stock")
            .Parquet("live", "stock", "SELECT 1 AS sku, 'two' AS what", file: "db=two/stock")
            .Stack("live");
        lake.Config.DefaultKey = ["sku"];

        lake.Baked().Stack("baked").Start();

        Assert.Equal(["1|one|one", "1|two|two"],
            lake.Query("SELECT sku, what, db FROM lake.stock ORDER BY db"));
    }

    /// The write layer is the top of the stack, so what was written to it is in the bake and the
    /// keys it hid are not -- and the baked directory then needs no write layer to say so.
    [Fact]
    public void TheWriteLayerIsFoldedIn()
    {
        using var lake = new TestLake()
            .Yaml("base", "customers", """
                - customer_id: 1
                  name: seeded
                - customer_id: 2
                  name: doomed
                """)
            .Stack("base")
            .WriteTo("local");
        lake.Config.DefaultKey = ["customer_id"];
        lake.Start();

        lake.Execute("UPDATE lake.customers SET name = 'written' WHERE customer_id = 1");
        lake.Execute("DELETE FROM lake.customers WHERE customer_id = 2");
        lake.Execute("INSERT INTO lake.customers (customer_id, name) VALUES (3, 'added')");
        lake.Stop();

        lake.Baked();
        lake.Config.Write = null;
        lake.Stack("baked").Start();

        Assert.Equal(["1|written", "3|added"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));
    }

    /// A baked directory stands in for the layers and not for the configuration above them: the
    /// virtual column is projected by the next run as it was by this one, rather than baked into
    /// the file and then projected over itself.
    [Fact]
    public void VirtualColumnsAreLeftToTheConfiguration()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", "- order_id: 1")
            .Stack("base");
        lake.Config.Tables["orders"] = new TableConfig
        {
            Columns = [new ColumnConfig { Name = "currency", Const = "EUR" }],
        };

        lake.Baked().Stack("baked").Start();

        Assert.Equal(["1|EUR"], lake.Query("SELECT order_id, currency FROM lake.orders"));
    }

    [Fact]
    public void OutputInsideALayerIsRefused()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", "- order_id: 1")
            .Stack("base");

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Baked("base/inside"));
        Assert.Contains("read back what this one wrote", refused.Message);
    }

    /// A filter is answered per session, and a file every session reads cannot carry one.
    [Fact]
    public void AFilterIsRefused()
    {
        using var lake = new TestLake()
            .Yaml("base", "customers", "- customer_id: 1")
            .Stack("base");
        lake.Config.Tables["customers"] = new TableConfig { Filter = "customer_id > 0" };

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Baked());
        Assert.Contains("`duckpg bake` cannot fold", refused.Message);
    }

    /// A default that answers differently each run is what the file must not carry: the bake
    /// outlives the run that stamped it, so it is left empty and the run reading it stamps it.
    [Fact]
    public void ADeclaredDefaultIsStampedByTheReaderNotByTheBake()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1}, {"order_id": 2}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("orders",
            [("order_id", "int"), ("token", "uniqueidentifier")], ["order_id"], [("token", "(newid())")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");

        lake.Baked().Start();
        var served = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
        lake.Stop();

        // Nothing of the run that wrote it: the column is empty in the file itself.
        lake.Config.Dacpac = null;
        lake.Stack("baked").Start();
        Assert.Equal(["", ""], lake.Query("SELECT token FROM lake.orders ORDER BY order_id"));
        lake.Stop();

        // And with the schema still declaring it, the run reading the file stamps it -- its own
        // stamp, not the one the bake was built under.
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
        var baked = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
        Assert.Equal(baked[0], baked[1]);
        Assert.NotEqual(served[0], baked[0]);
    }

    /// An id answered per row is deferred like any other default, and derived rather than generated
    /// is what makes that safe: the run reading the file works out the same id from the same key.
    [Fact]
    public void ADerivedIdSurvivesTheBakeByBeingDerivedAgain()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1}, {"order_id": 2}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("orders",
            [("order_id", "int"), ("token", "uniqueidentifier")], ["order_id"], [("token", "(newid())")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DeriveIds = true;

        lake.Start();
        var served = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
        lake.Stop();
        Assert.NotEqual(served[0], served[1]);      // per row, which is the point of deriving them

        lake.Baked().Stack("baked").Start();
        Assert.Equal(served, lake.Query("SELECT token FROM lake.orders ORDER BY order_id"));
    }

    /// Except where the default is what a row is identified by, which the merge itself reads: left
    /// empty, the rows it keeps apart would collapse onto one key in the run that read the file.
    [Fact]
    public void ADefaultOnTheKeyIsWrittenOut()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"amount": 5}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("orders",
            [("order_id", "int"), ("amount", "int")], ["order_id"], [("order_id", "((7))")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");

        lake.Baked();
        lake.Config.Dacpac = null;
        lake.Stack("baked").Start();

        Assert.Equal(["7|5"], lake.Query("SELECT order_id, amount FROM lake.orders"));
    }

    /// Collapsing the layers is what a bake is, so a lake that collapses them into memory first is
    /// doing it twice -- and holding every declared default already stamped.
    [Fact]
    public void MaterializeIsRefused()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", "- order_id: 1")
            .Stack("base")
            .Materialized()
            .StoredAt();

        var refused = Assert.Throws<DuckPgConfigurationException>(() => lake.Baked());
        Assert.Contains("a bake serves nothing", refused.Message);
    }

    /// What an earlier bake left for a table this one no longer publishes goes on being a layer
    /// file, and the directory is the caller's to clean out rather than this one's.
    [Fact]
    public void WhatAnEarlierBakeLeftIsSaidSoAbout()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", "- order_id: 1")
            .Yaml("base", "gone", "- what: stale")
            .Stack("base");

        lake.Baked();
        File.Delete(lake.At("base", "gone.yaml"));
        lake.Baked();

        Assert.Contains(lake.Logged, line => line.StartsWith("Warning: ") && line.Contains("gone.parquet"));
    }
}
