using DuckDB.NET.Data;

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

    /// Baked into a database rather than a directory, which is a materialized lake written down:
    /// the tables it would have served, their keys, and the rules DuckDB has nowhere to keep.
    [Fact]
    public void ADatabaseBakeHoldsTheCollapsedTables()
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
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("customers",
            [("customer_id", "int"), ("name", "nvarchar")], ["customer_id"]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");

        lake.Baked("baked.duckdb");

        using var duck = new DuckDBConnection($"Data Source={lake.At("baked.duckdb")}");
        duck.Open();

        Assert.Equal(["1|overridden", "2|kept"],
            Rows(duck, "SELECT customer_id, name FROM lake.customers ORDER BY customer_id"));

        // A table rather than a view over anything: there is nothing left to merge.
        Assert.Equal(["BASE TABLE"], Rows(duck,
            "SELECT table_type FROM information_schema.tables WHERE table_schema = 'lake' AND table_name = 'customers'"));

        // The declared key, held by DuckDB itself, which is what a copy of this file carries.
        Assert.Equal(["PRIMARY KEY"], Rows(duck,
            "SELECT constraint_type FROM duckdb_constraints() WHERE table_name = 'customers' AND constraint_type = 'PRIMARY KEY'"));

        // What makes copying one cheap, and it cannot be changed after the file is created: a block
        // is allocated whole, so a lake of many small tables is mostly blocks.
        Assert.Equal([Bake.DefaultBlockSize.ToString()], Rows(duck, "SELECT block_size FROM pragma_database_size()"));

        // In a schema of its own rather than in `main`, which is in every session's search path:
        // nothing here is a client's to find.
        Assert.Equal(["identity", "meta", "reference"], Rows(duck,
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{Bake.Schema}' " +
            "ORDER BY table_name"));

        Assert.Equal(["lake", "1"], Rows(duck, $"SELECT value FROM {Bake.Meta} ORDER BY key"));
        Assert.Empty(Rows(duck, $"SELECT * FROM {Bake.Referenced}"));
        Assert.Empty(Rows(duck, $"SELECT * FROM {Bake.Identified}"));
    }

    /// A reference is duckpg's own rule, checked over the merged view rather than by DuckDB, so a
    /// database that is to be served without the dacpac has to carry it.
    [Fact]
    public void ADatabaseBakeWritesDownTheRulesDuckDbCannotHold()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "customer_id": 1}]""")
            .Json("base", "customers", """[{"customer_id": 1}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            [new Dacpac.TableModel("customers", [("customer_id", "int")], ["customer_id"]),
             new Dacpac.TableModel("orders", [("order_id", "int"), ("customer_id", "int")], ["order_id"],
                                   Identity: ["order_id"])],
            [],
            [new Dacpac.ReferenceModel("FK_orders_customers", "orders", ["customer_id"],
                                       "customers", ["customer_id"], "Cascade")]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.Write = lake.At("local");

        lake.Baked("baked.duckdb");

        using var duck = new DuckDBConnection($"Data Source={lake.At("baked.duckdb")}");
        duck.Open();

        Assert.Equal(["FK_orders_customers|orders|customers|Cascade"], Rows(duck,
            $"SELECT name, \"table\", parent, on_delete FROM {Bake.Referenced}"));
        Assert.Equal(["orders|order_id"], Rows(duck, $"SELECT * FROM {Bake.Identified}"));
    }

    static List<string> Rows(DuckDBConnection duck, string sql)
    {
        using var command = duck.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString())));
        return rows;
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
