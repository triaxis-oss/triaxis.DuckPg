namespace triaxis.Tools.DuckPg.Tests;

public class SchemaTests
{
    static readonly Dacpac.TableModel Orders = new("orders",
        [("order_id", "int"), ("amount", "decimal"), ("created", "datetime")], ["order_id"]);

    static readonly Dacpac.TableModel History = new("history", [("history_id", "bigint"), ("what", "nvarchar")], []);

    [Fact]
    public void TheDeclaredShapeIsWhatTheViewPublishes()
    {
        using var lake = new TestLake()
            // Layer order and types disagree with the schema, and it carries a column no one declared.
            .Json("base", "orders", """[{"amount": 5, "order_id": 1, "stray": "dropped"}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(["order_id:integer", "amount:numeric", "created:timestamp without time zone"],
            lake.Columns("lake.orders"));
        Assert.Equal(["1|5|"], lake.Query("SELECT order_id, amount, created FROM lake.orders"));
    }

    [Fact]
    public void ThePrimaryKeyComesWithIt()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        // No --key anywhere: without the declared key an UPDATE would be refused.
        Assert.Equal(1, lake.Execute("UPDATE lake.orders SET amount = 6 WHERE order_id = 1"));
        Assert.Equal(["1|6"], lake.Query("SELECT order_id, amount FROM lake.orders"));
    }

    [Fact]
    public void ADeclaredTableNoLayerCarriesIsPublishedEmptyAndWritable()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders, History);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DefaultKey = ["history_id"];
        lake.Start();

        Assert.Equal(["history_id:bigint", "what:text"], lake.Columns("lake.history"));
        Assert.Empty(lake.Query("SELECT * FROM lake.history"));

        Assert.Equal(1, lake.Execute("INSERT INTO lake.history (history_id, what) VALUES (1, 'happened')"));
        lake.Restart();
        Assert.Equal(["1|happened"], lake.Query("SELECT history_id, what FROM lake.history"));
    }

    [Fact]
    public void ASingleDacpacInALayerIsFoundOnItsOwn()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base");
        Dacpac.Write(lake.At("base", "test.dacpac"), Orders);
        lake.Start();

        Assert.Equal(["order_id:integer", "amount:numeric", "created:timestamp without time zone"],
            lake.Columns("lake.orders"));
    }

    [Fact]
    public void SeveralDacpacsMeanNoneIsAssumed()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base");
        Dacpac.Write(lake.At("base", "one.dacpac"), Orders);
        Dacpac.Write(lake.At("base", "two.dacpac"), Orders);
        lake.Start();

        // The layer's own inference, not the schema: no `created` column.
        Assert.Equal(["order_id:bigint", "amount:bigint"], lake.Columns("lake.orders"));
    }

    [Fact]
    public void ASeedColumnTakesItsTypeFromTheSchemaRatherThanFromInference()
    {
        using var lake = new TestLake()
            .Yaml("base", "history", """
                - history_id: 7
                  what: seeded
                """)
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), History);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(["history_id:bigint", "what:text"], lake.Columns("lake.history"));
        Assert.Equal(["7|seeded"], lake.Query("SELECT history_id, what FROM lake.history"));
    }
}
