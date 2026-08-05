namespace triaxis.DuckPg.Tests;

public class LayerTests
{
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
