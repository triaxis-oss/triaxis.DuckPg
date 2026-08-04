namespace triaxis.Tools.DuckPg.Tests;

/// `db=<db>/<table>.parquet` — one view across many databases, the partition telling them apart.
public class PartitionTests
{
    [Fact]
    public void OneTablePerFileAcrossPartitionDirectories()
    {
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'one' AS what", file: "db=one/orders")
            .Parquet("live", "orders", "SELECT 2 AS order_id, 'two' AS what", file: "db=two/orders")
            .Parquet("live", "customers", "SELECT 1 AS customer_id", file: "db=one/customers")
            .Stack("live")
            .Start();

        Assert.Equal(["orders", "customers"], lake.Catalog.Tables.Keys.Order(StringComparer.Ordinal).Reverse());
        Assert.Equal(["1|one|one", "2|two|two"],
            lake.Query("SELECT order_id, what, db FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void EveryDatabaseKeepsItsOwnRowOne()
    {
        // The hazard the partition column exists to prevent: with `--key order_id` alone, one row 1
        // would shadow the other and a database would silently lose its rows.
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'one' AS what", file: "db=one/orders")
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'two' AS what", file: "db=two/orders")
            .Stack("live");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(["order_id", "db"], lake.Catalog.Tables["orders"].Key);
        Assert.Equal(["one|1|one", "two|1|two"],
            lake.Query("SELECT db, order_id, what FROM lake.orders ORDER BY db"));
    }

    [Fact]
    public void ALayerAbovePartitionsStillShadowsWithinOne()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", "SELECT 1 AS order_id, 'seeded' AS what", file: "db=one/orders")
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'current' AS what", file: "db=one/orders")
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'other' AS what", file: "db=two/orders")
            .Stack("base", "live");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(["one|current", "two|other"],
            lake.Query("SELECT db, what FROM lake.orders ORDER BY db"));
    }

    [Fact]
    public void PartitionsNestAndEachKeyBecomesAColumn()
    {
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id", file: "db=one/year=2026/orders")
            .Parquet("live", "orders", "SELECT 2 AS order_id", file: "db=two/year=2025/orders")
            .Stack("live")
            .Start();

        Assert.Equal(["1|one|2026", "2|two|2025"],
            lake.Query("SELECT order_id, db, year FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void YamlAndJsonPartitionTheSameWay()
    {
        using var lake = new TestLake()
            .Yaml("live", "customers", """
                - customer_id: 1
                  name: from one
                """, directory: "db=one")
            .Yaml("live", "customers", """
                - customer_id: 1
                  name: from two
                """, directory: "db=two")
            .Json("live", "notes", """[{"note_id": 1, "text": "from one"}]""", directory: "db=one")
            .Json("live", "notes", """[{"note_id": 1, "text": "from two"}]""", directory: "db=two")
            .Stack("live")
            .Start();

        Assert.Equal(["one|1|from one", "two|1|from two"],
            lake.Query("SELECT db, customer_id, name FROM lake.customers ORDER BY db"));
        Assert.Equal(["one|1|from one", "two|1|from two"],
            lake.Query("SELECT db, note_id, text FROM lake.notes ORDER BY db"));
    }

    [Fact]
    public void OneTableFromTwoFormatsIsAMistake()
    {
        // Not a merge: `customers` would have two sources in one layer with no way to order them.
        using var lake = new TestLake()
            .Yaml("live", "customers", "- customer_id: 1", directory: "db=one")
            .Json("live", "customers", """[{"customer_id": 2}]""", directory: "db=two")
            .Stack("live")
            .Start();

        Assert.Single(lake.Query("SELECT customer_id FROM lake.customers"));
    }

    [Fact]
    public void APlainDirectoryIsStillOneTable()
    {
        // Both layouts at once: `orders/` is a table of its own files, `db=…/` is a partition.
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id", file: "orders/part-1")
            .Parquet("live", "orders", "SELECT 2 AS order_id", file: "orders/part-2")
            .Parquet("live", "customers", "SELECT 1 AS customer_id", file: "db=one/customers")
            .Stack("live")
            .Start();

        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
        Assert.Equal(["1|one"], lake.Query("SELECT customer_id, db FROM lake.customers"));
        Assert.DoesNotContain(lake.Columns("lake.orders"), c => c.StartsWith("db:"));
    }

    [Fact]
    public void APartitionBelowATableDirectoryStillWorks()
    {
        using var lake = new TestLake()
            .Parquet("live", "events", "SELECT 'login' AS action", file: "events/dt=2026-08-01/part-1")
            .Stack("live")
            .Start();

        Assert.Equal(["login|2026-08-01"], lake.Query("SELECT action, dt FROM lake.events"));
    }

    [Fact]
    public void ThePathAboveTheLayerIsNotAPartition()
    {
        // The lake merely sits under `tenant=acme/`; that is where it lives, not what it holds.
        using var lake = new TestLake()
            .Parquet("tenant=acme", "orders", "SELECT 1 AS order_id", file: "live/db=one/orders")
            .Stack("tenant=acme/live")
            .Start();

        Assert.Equal(["order_id:integer", "db:text"], lake.Columns("lake.orders"));
    }

    [Fact]
    public void WritesToAPartitionedTableLandInTheWriteLayer()
    {
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id, 'one' AS what", file: "db=one/orders")
            .Stack("live")
            .WriteTo("local", LayerFormat.Json);
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        // The partition is part of the key, so a written row has to say which database it is for.
        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, what, db) VALUES (2, 'added', 'one')"));
        Assert.Equal(1, lake.Execute("UPDATE lake.orders SET what = 'edited' WHERE order_id = 1 AND db = 'one'"));

        lake.Restart();
        Assert.Equal(["one|1|edited", "one|2|added"],
            lake.Query("SELECT db, order_id, what FROM lake.orders ORDER BY order_id"));
    }
}
