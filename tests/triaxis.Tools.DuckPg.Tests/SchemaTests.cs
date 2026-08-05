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

    /// A table more than one layer carries is merged once into a parquet the view then scans,
    /// rather than merged again for every statement. The rows have to be the ones the merge would
    /// have produced -- shadowing, declared defaults and all -- or the copy is a different table.
    [Fact]
    public void AMergedTableIsCachedAsParquetAndStillMergesTheSameRows()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"duckpg-cache-{Guid.NewGuid():N}");
        try
        {
            using var lake = new TestLake()
                .Parquet("base", "orders", """
                    SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2)), (2, 20.00::DECIMAL(10,2))) t(order_id, amount)
                    """)
                .Json("top", "orders", """[{"order_id": 2, "amount": 99.5}, {"order_id": 3, "amount": 30}]""")
                .Stack("base", "top");
            lake.Config.DefaultKey = ["order_id"];
            lake.Config.Cache = cache;
            lake.Start();

            // The top layer shadows row 2 and adds row 3 -- what the merge says, not what one layer does.
            Assert.Equal(["1|10.00", "2|99.50", "3|30.00"],
                lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

            Assert.Single(Directory.GetFiles(cache, "orders-*.parquet"));

            // And the view is that file rather than the merge, which is the whole point.
            Assert.Contains("read_parquet", lake.Query(
                "SELECT sql FROM duckdb_views() WHERE schema_name = 'lake' AND view_name = 'orders'")[0]);
            Assert.DoesNotContain("UNION ALL BY NAME", lake.Query(
                "SELECT sql FROM duckdb_views() WHERE schema_name = 'lake' AND view_name = 'orders'")[0]);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    /// A writable table nobody has written to is published like any other -- cached, with no write
    /// branch to bind. The first write puts the branch there and the merge takes over, so the rows
    /// are the merged ones from that point on.
    [Fact]
    public void AWritableTableIsCachedUntilItIsWrittenTo()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"duckpg-cache-{Guid.NewGuid():N}");
        try
        {
            using var lake = new TestLake()
                .Parquet("base", "orders", "SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2))) t(order_id, amount)")
                .Json("top", "orders", """[{"order_id": 2, "amount": 20}]""")
                .Stack("base", "top")
                .WriteTo("local");
            lake.Config.DefaultKey = ["order_id"];
            lake.Config.Cache = cache;
            lake.Start();

            string Definition() => lake.Query(
                "SELECT sql FROM duckdb_views() WHERE schema_name = 'lake' AND view_name = 'orders'")[0];

            // Nothing written: no write branch to bind, and the copy stands in for the merge.
            Assert.Single(Directory.GetFiles(cache, "orders-*.parquet"));
            Assert.Contains("read_parquet", Definition());
            Assert.DoesNotContain("wr.", Definition());

            lake.Query("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30.00)");

            // Written: the branch is there, the tombstone check with it -- and the copy is still
            // underneath it, because a write does not touch the read layers the copy was made from.
            Assert.Contains("UNION ALL BY NAME", Definition());
            Assert.Contains("read_parquet", Definition());
            Assert.DoesNotContain("layer.", Definition());
            Assert.Equal(["1|10.00", "2|20.00", "3|30.00"],
                lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

            // And it survives the round trip to the files, which is what a write layer is for.
            lake.Restart();
            Assert.Equal(["1|10.00", "2|20.00", "3|30.00"],
                lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    /// The copy is keyed by what produced it, so a restart over unchanged files reuses it -- and a
    /// layer that did change lands on a different key rather than being answered with the old rows.
    [Fact]
    public void ACachedTableIsReusedUntilItsFilesChange()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"duckpg-cache-{Guid.NewGuid():N}");
        try
        {
            using var lake = new TestLake()
                .Parquet("base", "orders", "SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2))) t(order_id, amount)")
                .Json("top", "orders", """[{"order_id": 2, "amount": 20}]""")
                .Stack("base", "top");
            lake.Config.DefaultKey = ["order_id"];
            lake.Config.Cache = cache;
            lake.Start();

            var first = Directory.GetFiles(cache, "orders-*.parquet").Single();
            var written = File.GetLastWriteTimeUtc(first);

            // Nothing changed: the same key, and the file is not rewritten.
            lake.Restart();
            Assert.Equal([first], Directory.GetFiles(cache, "orders-*.parquet"));
            Assert.Equal(written, File.GetLastWriteTimeUtc(first));

            // A layer that changed has to produce a different key, and the stale copy goes.
            lake.Json("top", "orders", """[{"order_id": 2, "amount": 22}]""");
            lake.Restart();
            var second = Directory.GetFiles(cache, "orders-*.parquet").Single();
            Assert.NotEqual(first, second);
            Assert.Equal(["1|10.00", "2|22.00"],
                lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }


    /// A default that answers differently each run stays with the view rather than being written
    /// into the copy, so a reused copy is still stamped by whoever reads it -- which is what the
    /// stamp meant before there was a copy at all.
    [Fact]
    public void ADeferredDefaultIsStampedByTheReaderNotByTheCopy()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"duckpg-cache-{Guid.NewGuid():N}");
        try
        {
            using var lake = new TestLake()
                .Parquet("base", "orders", "SELECT * FROM (VALUES (1)) t(order_id)")
                .Json("top", "orders", """[{"order_id": 2}]""")
                .Stack("base", "top");
            Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel("orders",
                [("order_id", "int"), ("token", "uniqueidentifier")], ["order_id"], [("token", "(newid())")]));
            lake.Config.Dacpac = lake.At("schema", "test.dacpac");
            lake.Config.DefaultKey = ["order_id"];
            lake.Config.Cache = cache;
            lake.Start();

            var first = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
            var copy = Directory.GetFiles(cache, "orders-*.parquet").Single();
            Assert.Equal(first[0], first[1]);           // one stamp for the whole lake, as before

            // The copy carries no stamp of its own, so it survives while the stamp moves on.
            lake.Restart();
            Assert.Equal([copy], Directory.GetFiles(cache, "orders-*.parquet"));
            var second = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
            Assert.Equal(second[0], second[1]);
            Assert.NotEqual(first[0], second[0]);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }


    /// A promotion is part of the write that caused it. Rolled back, it has to be gone from DuckDB
    /// *and* from what the catalog believes, or the next write inserts into a table that is not
    /// there any more.
    [Fact]
    public void APromotionRolledBackIsMadeAgainByTheNextWrite()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", "SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2))) t(order_id, amount)")
            .Json("top", "orders", """[{"order_id": 2, "amount": 20}]""")
            .Stack("base", "top")
            .WriteTo("local");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        using (var connection = lake.Connect())
        {
            void Run(string sql)
            {
                using var command = new Npgsql.NpgsqlCommand(sql, connection);
                command.ExecuteNonQuery();
            }
            Run("BEGIN");
            Run("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30.00)");
            Run("ROLLBACK");
        }

        Assert.Equal(["1|10.00", "2|20.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        // The second write has to promote again rather than assume the first one's tables.
        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (4, 40.00)");
        Assert.Equal(["1|10.00", "2|20.00", "4|40.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1|10.00", "2|20.00", "4|40.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }


    /// A written row shadows the copy, a deleted one is hidden by a tombstone over it, and both
    /// survive a restart -- the copy standing in for the read layers has to behave as they would.
    [Fact]
    public void TheCopyStaysUnderTheWriteLayer()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"duckpg-cache-{Guid.NewGuid():N}");
        try
        {
            using var lake = new TestLake()
                .Parquet("base", "orders", """
                    SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2)), (2, 20.00::DECIMAL(10,2))) t(order_id, amount)
                    """)
                .Json("top", "orders", """[{"order_id": 3, "amount": 30}]""")
                .Stack("base", "top")
                .WriteTo("local");
            lake.Config.DefaultKey = ["order_id"];
            lake.Config.Cache = cache;
            lake.Start();

            lake.Execute("UPDATE lake.orders SET amount = 11.00 WHERE order_id = 1");   // shadows the copy
            lake.Execute("DELETE FROM lake.orders WHERE order_id = 2");                 // tombstones it
            lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (4, 40.00)");

            var expected = new[] { "1|11.00", "3|30.00", "4|40.00" };
            Assert.Equal(expected, lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

            lake.Restart();
            Assert.Equal(expected, lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }


    /// The tombstone check costs the same whatever a table looks like, and until something has
    /// actually been hidden below it answers nothing. An ordinary update does not hide anything --
    /// the rewritten row lands under the key it already had, where it shadows what is beneath it.
    [Fact]
    public void TheTombstoneCheckArrivesWithTheFirstRowItHides()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", """
                SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2)), (2, 20.00::DECIMAL(10,2))) t(order_id, amount)
                """)
            .Stack("base")
            .WriteTo("local");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        string Definition() => lake.Query(
            "SELECT sql FROM duckdb_views() WHERE schema_name = 'lake' AND view_name = 'orders'")[0];

        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30.00)");
        Assert.DoesNotContain("__del", Definition());

        lake.Execute("UPDATE lake.orders SET amount = 11.00 WHERE order_id = 1");
        Assert.DoesNotContain("__del", Definition());
        Assert.Equal(["1|11.00", "2|20.00", "3|30.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        // Moving a key leaves the old one behind with nothing above it, so it has to be hidden.
        lake.Execute("UPDATE lake.orders SET order_id = 9 WHERE order_id = 2");
        Assert.Contains("__del", Definition());
        Assert.Equal(["1|11.00", "3|30.00", "9|20.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1|11.00", "3|30.00", "9|20.00"],
            lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// A delete hides a row below, so it brings the check with it.
    [Fact]
    public void ADeleteBringsTheTombstoneCheck()
    {
        using var lake = new TestLake()
            .Parquet("base", "orders", """
                SELECT * FROM (VALUES (1, 10.00::DECIMAL(10,2)), (2, 20.00::DECIMAL(10,2))) t(order_id, amount)
                """)
            .Stack("base")
            .WriteTo("local");
        lake.Config.DefaultKey = ["order_id"];
        lake.Start();

        lake.Execute("DELETE FROM lake.orders WHERE order_id = 1");
        Assert.Contains("__del", lake.Query(
            "SELECT sql FROM duckdb_views() WHERE schema_name = 'lake' AND view_name = 'orders'")[0]);
        Assert.Equal(["2|20.00"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        // A deleted row that comes back needs no tombstone bookkeeping -- it simply shadows again.
        lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 99.00)");
        Assert.Equal(["1|99.00", "2|20.00"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));

        lake.Restart();
        Assert.Equal(["1|99.00", "2|20.00"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

}
