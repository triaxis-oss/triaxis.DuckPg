using Npgsql;

namespace triaxis.DuckPg.Tests;

public class WriteLayerTests
{
    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Parquet("base", "orders", """
                SELECT * FROM (VALUES (1, 10.0::DOUBLE), (2, 20.0::DOUBLE)) t(order_id, amount)
                """)
            .Stack("base")
            .WriteTo("local");

    [Fact]
    public void InsertedRowsSurviveARestart()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)"));
        Assert.True(File.Exists(lake.At("local", "orders.parquet")));

        lake.Restart();
        Assert.Equal(["1|10", "2|20", "3|30"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void UpdatingALowerLayerRowReplacesItEverywhere()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(1, lake.Execute("UPDATE lake.orders SET amount = 99 WHERE order_id = 1"));
        Assert.Equal(["1|99", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1|99", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void DeletingALowerLayerRowLeavesATombstone()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        Assert.Equal(1, lake.Execute("DELETE FROM lake.orders WHERE order_id = 1"));
        Assert.True(File.Exists(lake.At("local", ".deleted", "orders.parquet")));

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT order_id FROM lake.orders"));
    }

    [Fact]
    public void ADeletedRowCanBeWrittenAgain()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        lake.Execute("DELETE FROM lake.orders WHERE order_id = 1");
        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 11)");

        lake.Restart();
        Assert.Equal(["1|11", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void EmptyingATableTakesItsFileWithIt()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)");
        lake.Execute("DELETE FROM lake.orders WHERE order_id = 3");

        Assert.False(File.Exists(lake.At("local", "orders.parquet")));
        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void ARolledBackWriteNeverReachesTheDisk()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        using (var connection = lake.Connect())
        using (var transaction = connection.BeginTransaction())
        {
            new NpgsqlCommand("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)", connection, transaction)
                .ExecuteNonQuery();
            transaction.Rollback();
        }

        Assert.False(File.Exists(lake.At("local", "orders.parquet")));
        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void ACommittedTransactionIsPersistedOnce()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        using (var connection = lake.Connect())
        using (var transaction = connection.BeginTransaction())
        {
            new NpgsqlCommand("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)", connection, transaction)
                .ExecuteNonQuery();
            new NpgsqlCommand("UPDATE lake.orders SET amount = 31 WHERE order_id = 3", connection, transaction)
                .ExecuteNonQuery();
            transaction.Commit();
        }

        lake.Restart();
        Assert.Equal(["3|31"], lake.Query("SELECT order_id, amount FROM lake.orders WHERE order_id = 3"));
    }

    [Fact]
    public void ATableWrittenAsYamlStaysYaml()
    {
        using var lake = new TestLake()
            .Yaml("local", "notes", """
                - note_id: 1
                  text: first
                """)
            .Stack("base")
            .WriteTo("local");
        lake.Config.DefaultKey = ["note_id"];
        lake.Start();

        lake.Execute("INSERT INTO lake.notes (note_id, text) VALUES (2, 'second')");

        Assert.Contains("second", File.ReadAllText(lake.At("local", "notes.yaml")));
        Assert.False(File.Exists(lake.At("local", "notes.parquet")));

        lake.Restart();
        Assert.Equal(["1|first", "2|second"], lake.Query("SELECT note_id, text FROM lake.notes ORDER BY note_id"));
    }

    [Fact]
    public void JsonIsAWriteFormatToo()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Config.WriteFormat = LayerFormat.Json;
        lake.Start();

        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)");
        Assert.Contains("\"order_id\"", File.ReadAllText(lake.At("local", "orders.json")));

        lake.Restart();
        Assert.Equal(["3|30"], lake.Query("SELECT order_id, amount FROM lake.orders WHERE order_id = 3"));
    }

    [Fact]
    public void WritesWithoutAWriteLayerAreRefused()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", "SELECT 1 AS order_id")
            .Stack("base")
            .Start();

        var error = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id) VALUES (2)"));
        Assert.Equal("42809", error.SqlState);
    }

    [Fact]
    public void ChangingRowsNeedsAKey()
    {
        using var lake = Lake();
        lake.Start();

        var error = Assert.Throws<PostgresException>(() =>
            lake.Execute("UPDATE lake.orders SET amount = 1 WHERE order_id = 1"));
        Assert.Equal("0A000", error.SqlState);

        // Appending does not: a new row needs no identity to be told from an old one.
        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)"));
    }

    [Fact]
    public void VirtualColumnsAreReadOnly()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Config.Tables["orders"] = new TableConfig
        {
            Columns = [new ColumnConfig { Name = "currency", Const = "EUR" }],
        };
        lake.Start();

        var error = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id, currency) VALUES (3, 'USD')"));
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public void ATableCanBeOptedOutOfAWritableLake()
    {
        using var lake = Lake();
        lake.Config.DefaultKey = ["order_id"];
        lake.Config.Tables["orders"] = new TableConfig { Writable = false };
        lake.Start();

        var error = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)"));
        Assert.Equal("42809", error.SqlState);
    }

    [Fact]
    public void WithoutAWriteDirectoryWritesAreLostOnRestart()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", "SELECT 1 AS order_id")
            .Stack("base");
        lake.Config.Writable = true;
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        lake.Execute("INSERT INTO lake.orders (order_id) VALUES (2)");
        Assert.Equal(["1", "2"], lake.Query("SELECT order_id FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1"], lake.Query("SELECT order_id FROM lake.orders"));
    }
}
