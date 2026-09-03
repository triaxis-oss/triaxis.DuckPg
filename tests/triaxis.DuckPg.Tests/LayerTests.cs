using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace triaxis.DuckPg.Tests;

public class LayerTests
{
    /// The whole of what reading the footers promises: the same answer describing would have given,
    /// for every shape a layer directory comes in. It is the shapes it declines to answer for that
    /// make this worth asserting -- a reading that quietly stopped covering the ordinary one would
    /// still pass a test that only compared what it did answer.
    [Fact]
    public void ReadingAFooterSaysWhatDescribingWouldHave()
    {
        using var lake = new TestLake()
            .Parquet("layer", "flat", "SELECT 1::INTEGER AS id, 'a' AS nm, 1.5::DECIMAL(9,2) AS amount")
            .Parquet("layer", "nested", "SELECT 1 AS id, [1, 2] AS xs, {'a': 1, 'b': 'x'} AS s")
            .Parquet("layer", "gathered", "SELECT 1 AS id, 'a' AS nm", "gathered/one")
            .Parquet("layer", "gathered", "SELECT 2 AS id, 'b' AS nm", "gathered/deep/two")
            .Parquet("layer", "ragged", "SELECT 1::INTEGER AS id", "ragged/one")
            .Parquet("layer", "ragged", "SELECT 2::BIGINT AS id, 9 AS extra", "ragged/two")
            .Parquet("layer", "orders", "SELECT 1 AS id, 'o' AS nm", "db=one/y=2020/orders")
            .Parquet("layer", "orders", "SELECT 2 AS id, 'p' AS nm", "db=two/y=2021/orders");

        DuckDbLibrary.Register();
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();

        var scanned = Layer.Scan(lake.At("layer"), 0, NullLogger.Instance).ToList();
        var footers = Layer.Footers(duck, scanned.Select(s => s.Source));

        foreach (var (table, source) in scanned)
            Assert.Equal(Layer.Columns(duck, source), Layer.Columns(duck, source, footers));

        // `flat`, `gathered` and `orders`. A nested column is not in one footer row, and `ragged`'s
        // files disagree -- what `union_by_name` makes of that is the binder's arithmetic.
        Assert.Equal(3, footers.Count);
        Assert.Equal(["flat", "gathered", "nested", "orders", "ragged"],
            scanned.Select(s => s.Table).Order(StringComparer.Ordinal));
    }

    /// An export drops a report beside its tables; `ignore` is how it is told that the report is
    /// not one. A name alone reaches into every partition, and a path is held to the layer.
    [Fact]
    public void AnIgnoredFileIsNotATable()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Yaml("base", "_report", "run: 1\nrows: [1, 2]\n")
            .Yaml("base", "_report", "run: 2\n", "db=one")
            .Json("base", "notes", """[{"note_id": 1}]""", "extras")
            .Stack("base");
        lake.Config.Ignore = ["_*.yaml", "extras/**"];
        lake.Start();

        Assert.Equal(["orders"], lake.Catalog.Tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["1"], lake.Query("SELECT order_id FROM lake.orders"));
    }

    /// A rooted pattern reaches one layer and not another: the same file name is a table where the
    /// pattern does not point and nothing where it does.
    [Fact]
    public void ARootedPatternNamesOneLayersFiles()
    {
        using var lake = new TestLake()
            .Yaml("lower", "notes", "- note_id: 1\n")
            .Yaml("upper", "notes", "- note_id: 2\n")
            .Stack("lower", "upper");
        lake.Config.Ignore = ["./lower/*.yaml"];
        lake.Config.ResolvePaths(lake.Root);
        lake.Start();

        Assert.Equal(["2"], lake.Query("SELECT note_id FROM lake.notes"));
        Assert.Equal([1], lake.Catalog.Tables["notes"].Layers.Select(l => l.Source.Seq));
    }

    /// A table that is a directory of parquet is ignored by its directory, and a pattern is not a
    /// prefix: `orders` does not take `orders_archive` with it.
    [Fact]
    public void AnIgnoredDirectoryIsNotATableEither()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", "SELECT 1 AS id", "orders/one")
            .Parquet("base", "orders_archive", "SELECT 2 AS id", "orders_archive/one")
            .Stack("base");
        lake.Config.Ignore = ["orders"];
        lake.Start();

        Assert.Equal(["orders_archive"], lake.Catalog.Tables.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FormatsStackInOneList()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", """
                - order_id: 1
                  amount: 10
                - order_id: 2
                  amount: 20
                """)
            .Json("tenant", "orders", """[{"order_id": 2, "amount": 22}]""")
            .Parquet("live", "orders", "SELECT 3 AS order_id, 33.0::DOUBLE AS amount")
            .Stack("base", "tenant", "live");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(["1|10", "2|22", "3|33"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void HigherLayerShadowsRowByKey()
    {
        using var lake = new TestLake()
            .Yaml("base", "customers", """
                - customer_id: 1
                  name: seeded
                - customer_id: 2
                  name: kept
                """)
            .Yaml("tenant", "customers", """
                - customer_id: 1
                  name: overridden
                """)
            .Stack("base", "tenant");
        lake.Config.DefaultKey = ["customer_id"];
        lake.Start();

        Assert.Equal(["1|overridden", "2|kept"],
            lake.Query("SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));
    }

    [Fact]
    public void WithoutAKeyLayersConcatenate()
    {
        using var lake = new TestLake()
            .Yaml("base", "events", "- what: a")
            .Yaml("tenant", "events", "- what: a")
            .Stack("base", "tenant")
            .Start();

        Assert.Equal(2, lake.Query("SELECT what FROM lake.events").Count);
    }

    [Fact]
    public void ParquetDecidesTheTypeItsColumnsHave()
    {
        // JSON inference reads every integer as BIGINT; the parquet layer knows better.
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1::INTEGER AS order_id, 'x' AS label")
            .Json("base", "orders", """[{"order_id": 2, "note": "only here"}]""")
            .Stack("base", "live")
            .Start();

        Assert.Equal(["order_id:integer", "label:text", "note:text"], lake.Columns("lake.orders"));
    }

    [Fact]
    public void ColumnsAreTheUnionOfTheLayers()
    {
        using var lake = new TestLake()
            .Yaml("base", "orders", """
                - order_id: 1
                  legacy_note: kept
                """)
            .Parquet("live", "orders", "SELECT 2 AS order_id, 5.0::DOUBLE AS amount")
            .Stack("base", "live")
            .Start();

        Assert.Equal(["1||kept", "2|5|"],
            lake.Query("SELECT order_id, amount, legacy_note FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void ADirectoryOfParquetIsOneTable()
    {
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id", file: "orders/part-1")
            .Parquet("live", "orders", "SELECT 2 AS order_id", file: "orders/part-2")
            .Stack("live")
            .Start();

        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void HivePartitionsBecomeColumnsButThePathAboveDoesNot()
    {
        using var lake = new TestLake()
            .Parquet("db=acme", "events", "SELECT 'login' AS action", file: "events/dt=2026-08-01/part-1")
            .Stack("db=acme")
            .Start();

        Assert.Equal(["login|2026-08-01"], lake.Query("SELECT action, dt FROM lake.events"));
        Assert.DoesNotContain(lake.Columns("lake.events"), c => c.StartsWith("db:"));
    }

    [Fact]
    public void VirtualColumnsAreProjectedLast()
    {
        using var lake = new TestLake()
            .Parquet("live", "orders", "SELECT 1 AS order_id, 10.0::DOUBLE AS amount")
            .Stack("live");
        lake.Config.Tables["orders"] = new TableConfig
        {
            Columns =
            [
                new ColumnConfig { Name = "currency", Const = "EUR" },
                new ColumnConfig { Name = "cents", Expr = "amount * 100", Type = "BIGINT" },
            ],
        };
        lake.Start();

        Assert.Equal(["order_id:integer", "amount:double precision", "currency:text", "cents:bigint"],
            lake.Columns("lake.orders"));
        Assert.Equal(["1|10|EUR|1000"], lake.Query("SELECT * FROM lake.orders"));
    }

    [Fact]
    public void SessionVariablesFilterRowsPerUser()
    {
        using var lake = new TestLake()
            .Yaml("base", "customers", """
                - customer_id: 1
                  region: emea
                - customer_id: 2
                  region: apac
                """)
            .Stack("base");
        lake.Config.SessionVariables["tenant"] = "user";
        lake.Config.Tables["customers"] = new TableConfig
        {
            Filter = "getvariable('tenant') = 'admin' OR region = getvariable('tenant')",
        };
        lake.Start();

        Assert.Equal(2, lake.Query("SELECT customer_id FROM lake.customers").Count);
        Assert.Equal(["1"], lake.Query("SELECT customer_id FROM lake.customers", user: "emea"));
    }
}
