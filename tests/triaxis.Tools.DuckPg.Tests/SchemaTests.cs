namespace triaxis.Tools.DuckPg.Tests;

public class SchemaTests
{
    static readonly Dacpac.TableModel Orders = new("orders",
        [("order_id", "int"), ("amount", "decimal"), ("created", "datetime")], ["order_id"]);

    static readonly Dacpac.TableModel History = new("history", [("history_id", "bigint"), ("what", "nvarchar")], []);

    static readonly Dacpac.TableModel Stamped = new("orders",
        [("order_id", "int"), ("amount", "decimal"), ("status", "nvarchar"), ("created", "datetime")],
        ["order_id"],
        [("status", "('new')"), ("created", "(getdate())")]);

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
    public void DeclaredDefaultsFillInWhatARowLeavesOut()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """
                [{"order_id": 1, "amount": 5},
                 {"order_id": 2, "amount": 6, "status": "sent"}]
                """)
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), Stamped);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        var rows = lake.Query("SELECT order_id, status, created FROM lake.orders ORDER BY order_id");
        Assert.Equal(["1|new", "2|sent"], rows.Select(r => string.Join("|", r.Split('|')[..2])));

        // GETDATE() is answered when the lake is built, not when a row is read: one stamp for the
        // run, the same on every row and the same on the next query.
        var stamp = rows[0].Split('|')[2];
        Assert.NotEqual("", stamp);
        Assert.Equal(stamp, rows[1].Split('|')[2]);
        Assert.Equal(rows, lake.Query("SELECT order_id, status, created FROM lake.orders ORDER BY order_id"));
    }

    [Fact]
    public void ADefaultFillsAWrittenRowThatOmitsTheColumn()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5, "status": "sent"}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), Stamped);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (2, 6)"));

        // Without the dacpac there is nothing left to fill anything in, so what comes back is what
        // the write layer actually persisted.
        lake.Config.Dacpac = null;
        lake.Restart();

        Assert.Equal(["1|sent", "2|new"], lake.Query("SELECT order_id, status FROM lake.orders ORDER BY order_id"));
    }

    /// The frozen value stands in for rows that were already in a file when the lake was built.
    /// A row being written is there to be stamped, so it is stamped then -- `NEWID()` tells the two
    /// apart in a way `GETDATE()` within one test run cannot.
    [Fact]
    public void AWrittenRowIsStampedAsItIsWrittenAndTheLayersShareTheStartupStamp()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1}, {"order_id": 2}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("orders",
            [("order_id", "int"), ("token", "uniqueidentifier")], ["order_id"], [("token", "(newid())")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        lake.Execute("INSERT INTO lake.orders (order_id) VALUES (3)");
        lake.Execute("INSERT INTO lake.orders (order_id) VALUES (4)");
        // Written as a null on purpose: the write layer says what it holds, and nothing fills it in.
        lake.Execute("INSERT INTO lake.orders (order_id, token) VALUES (5, NULL)");

        var tokens = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
        Assert.Equal(tokens[0], tokens[1]);
        Assert.NotEqual(tokens[1], tokens[2]);
        Assert.NotEqual(tokens[2], tokens[3]);
        Assert.Equal("", tokens[4]);
        Assert.DoesNotContain("", tokens[..4]);
    }

    /// `SUSER_SNAME()` is a stock audit-column default, and duckpg answers it rather than declining
    /// to: nobody is connected when the lake is built, so the user is the account serving it.
    [Fact]
    public void AUserDefaultIsTheAccountServingTheLake()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            Stamped with { Defaults = [("status", "(suser_sname())")] });
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal([$"1|{Environment.UserName}"], lake.Query("SELECT order_id, status FROM lake.orders"));
    }

    [Fact]
    public void ADefaultDuckDbCannotAnswerIsDroppedRatherThanFatal()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            Stamped with { Defaults = [("status", "(newsequentialid())")] });
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(["1|"], lake.Query("SELECT order_id, status FROM lake.orders"));
    }

    /// The views are listed the wrong way round on purpose: `big` reads `sent`, which the model
    /// only declares afterwards, and nothing in the model says so.
    [Fact]
    public void DeclaredViewsArePublishedOverTheLayers()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """
                [{"order_id": 1, "amount": 5, "status": "sent"},
                 {"order_id": 2, "amount": 50, "status": "sent"},
                 {"order_id": 3, "amount": 70, "status": "draft"}]
                """)
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Stamped],
            new Dacpac.ViewModel("big", "SELECT [order_id], [amount] FROM [dbo].[sent] WHERE [amount] > 10"),
            new Dacpac.ViewModel("sent",
                "SELECT [order_id], [amount], ISNULL([status], 'none') AS [state] FROM [dbo].[orders] WHERE [status] = 'sent'"));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(["1|5|sent", "2|50|sent"], lake.Query("SELECT * FROM lake.sent ORDER BY order_id"));
        Assert.Equal(["2|50"], lake.Query("SELECT * FROM lake.big"));
    }

    [Fact]
    public void AViewTheParserCannotReadIsSkippedRatherThanFatal()
    {
        using var lake = new TestLake()
            .Json("base", "orders", """[{"order_id": 1, "amount": 5, "status": "sent"}]""")
            .Stack("base");
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Stamped],
            new Dacpac.ViewModel("broken", "SELECT * FROM [dbo].[orders] FOR XML PATH('o')"),
            new Dacpac.ViewModel("fine", "SELECT [order_id] FROM [dbo].[orders]"));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        Assert.Equal(["1"], lake.Query("SELECT * FROM lake.fine"));
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
