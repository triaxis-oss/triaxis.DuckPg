using Npgsql;

namespace triaxis.DuckPg.Tests;

/// A `UNIQUE` constraint or a unique index declared past the key is a rule about rows, and a
/// materialized table is a table with nothing below it -- so DuckDB holds it, as it holds the key.
/// A layered lake publishes views and has nowhere to put one, which is the one place the two modes
/// answer differently.
public class UniqueTests
{
    static Dacpac.TableModel Codes(params (string Name, string[] Columns, bool AsIndex)[] uniques) =>
        new("codes", [("code_id", "int"), ("label", "nvarchar"), ("slot", "int")], ["code_id"],
            Uniques: uniques);

    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Json("base", "codes", """
                [{"code_id": 1, "label": "a", "slot": 1}, {"code_id": 2, "label": "b", "slot": 2}]
                """)
            .Stack("base")
            .WriteTo("local");

    static TestLake Started(TestLake lake, Dacpac.TableModel table, bool materialized = true)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), table);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    [Fact]
    public void ADeclaredUniqueConstraintRefusesASecondRowUnderIt()
    {
        using var lake = Started(Lake(), Codes(("UQ_codes_label", ["label"], false)));

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 3)"));
        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'c', 3)"));
    }

    /// A unique index says the same thing about the rows as a constraint does, and is held the same.
    [Fact]
    public void ADeclaredUniqueIndexIsTheSameRule()
    {
        using var lake = Started(Lake(), Codes(("IX_codes_slot", ["slot"], true)));

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'c', 1)"));
    }

    /// An application reports which rule refused its write, so a refusal has to name one. DuckDB's
    /// own message names the kind of rule and never the rule, and there may be several.
    [Fact]
    public void AUniqueRefusalNamesTheConstraint()
    {
        using var lake = Started(Lake(), Codes(("UQ_codes_label", ["label"], false)));

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 3)"));

        Assert.Equal("23505", refused.SqlState);
        Assert.Contains("Violation of UNIQUE KEY constraint 'UQ_codes_label'", refused.MessageText);
    }

    /// SQL Server refuses a unique index in different words from a constraint -- 2601 rather than
    /// 2627 -- so which of the two the schema declared is carried through to the message.
    [Fact]
    public void AUniqueIndexRefusalNamesTheIndex()
    {
        using var lake = Started(Lake(), Codes(("IX_codes_slot", ["slot"], true)));

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'c', 1)"));

        Assert.Contains("with unique index 'IX_codes_slot'", refused.MessageText);
    }

    /// DuckDB refuses a batch in words that name only the values, so the rule is worked out by
    /// asking each of them about the rows the statement was about to write.
    [Fact]
    public void ABatchedRefusalNamesTheConstraintToo()
    {
        using var lake = Started(Lake(), Codes(("UQ_codes_label", ["label"], false)));

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'c', 3), (4, 'c', 4)"));

        Assert.Contains("Violation of UNIQUE KEY constraint 'UQ_codes_label'", refused.MessageText);
    }

    /// An update breaks the same rules a write does, and reaches the table without anything being
    /// asked first -- so the words it is refused in are DuckDB's to give and duckpg's to name.
    [Fact]
    public void AnUpdateRefusalNamesTheConstraint()
    {
        using var lake = Started(Lake(), Codes(("UQ_codes_label", ["label"], false)));

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("UPDATE lake.codes SET label = 'a' WHERE code_id = 2"));

        Assert.Contains("Violation of UNIQUE KEY constraint 'UQ_codes_label'", refused.MessageText);
    }

    /// The key is a declared constraint like any other and is named the same way -- the dacpac says
    /// what it is called, and a made-up name is one an application matching on it would not find.
    [Fact]
    public void AKeyRefusalNamesTheKeysConstraint()
    {
        using var lake = Started(Lake(), Codes());

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (1, 'z', 3)"));

        Assert.Contains("Violation of PRIMARY KEY constraint 'PK_codes'", refused.MessageText);
    }

    /// Nothing the dacpac declares is a rule until it says unique: a plain index is an instruction
    /// about lookups, and reading one as a constraint would refuse rows the schema allows.
    [Fact]
    public void APlainIndexDeclaresNothing()
    {
        using var lake = Started(Lake(), Codes());

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 1)"));
    }

    /// The rule is over what the table publishes, so the layers have to already keep it -- and where
    /// they do not, the lake says so at startup rather than serving rows it would have refused.
    [Fact]
    public void LayersAlreadyBreakingItRefuseToMaterialize()
    {
        using var lake = new TestLake(nameof(LayersAlreadyBreakingItRefuseToMaterialize))
            .Json("base", "codes", """
                [{"code_id": 1, "label": "a", "slot": 1}, {"code_id": 2, "label": "a", "slot": 2}]
                """)
            .Stack("base")
            .WriteTo("local");

        Assert.ThrowsAny<Exception>(() => Started(lake, Codes(("UQ_codes_label", ["label"], false))));
        lake.Dispose();
    }

    /// A lake showing a subset of a declared table loses the rules it cannot see rather than failing
    /// on them -- the same bargain the declared key strikes.
    [Fact]
    public void AUniqueOverAColumnTheLakeDoesNotPublishIsSkipped()
    {
        using var lake = new TestLake(nameof(AUniqueOverAColumnTheLakeDoesNotPublishIsSkipped))
            .Json("base", "codes", """[{"code_id": 1, "label": "a"}, {"code_id": 2, "label": "b"}]""")
            .Stack("base")
            .WriteTo("local");

        var table = new Dacpac.TableModel("codes", [("code_id", "int"), ("label", "nvarchar")], ["code_id"],
                                          Uniques: [("UQ_codes_slot", ["slot"], false)]);

        Assert.Equal(2, Started(lake, table).Query("SELECT code_id FROM lake.codes").Count);
        lake.Dispose();
    }

    /// Layered, there is no table to hold it: the lake publishes views, and the rule is not one the
    /// gateway keeps over the merge the way it keeps the key.
    [Fact]
    public void ALayeredLakeDoesNotHoldIt()
    {
        using var lake = Started(Lake(), Codes(("UQ_codes_label", ["label"], false)), materialized: false);

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 3)"));
    }

    /// A filtered index is a rule about the rows its filter matches, and says nothing about the
    /// others: two rows outside it under one label is what the schema allows, and refusing that is
    /// refusing rows SQL Server writes.
    [Fact]
    public void AFilteredUniqueIndexHoldsOnlyOverTheRowsItFilters()
    {
        using var lake = Started(Lake(), Codes() with
        {
            Filtered = [("UIX_codes_label", ["label"], "[slot] = 1")],
        });

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 2)"));
        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (4, 'a', 3)"));
    }

    /// And it does hold over the rows it does match -- including a row an update moves into them,
    /// which is where an index over the filtered columns differs from no index at all.
    [Fact]
    public void AFilteredUniqueIndexStillRefusesWithinItsFilter()
    {
        using var lake = Started(Lake(), Codes() with
        {
            Filtered = [("UIX_codes_label", ["label"], "[slot] = 1")],
        });

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 1)"));

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 2)"));
        Assert.Throws<PostgresException>(() =>
            lake.Execute("UPDATE lake.codes SET slot = 1 WHERE code_id = 3"));
    }

    /// And a refusal under it names the index, like any other -- including on the append path, where
    /// the rule is worked out by asking each of them about the rows the statement was about to
    /// write, and where a rule the filter puts those rows outside of must not be the one named.
    [Fact]
    public void AFilteredRefusalNamesTheIndex()
    {
        using var lake = Started(Lake(), Codes() with
        {
            Filtered = [("UIX_codes_label", ["label"], "[slot] = 1")],
        });

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'q', 1), (4, 'q', 1)"));

        Assert.Contains("with unique index 'UIX_codes_label'", refused.MessageText);

        // And two of the same outside it break nothing, so no rule is named because none was broken.
        Assert.Equal(2, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'q', 2), (4, 'q', 3)"));
    }

    /// A filter this cannot render leaves the rule unheld rather than held over every row: refusing
    /// rows on a rule read wrong is worse than not holding it.
    [Fact]
    public void AFilterDuckPgCannotRenderDropsTheRule()
    {
        using var lake = Started(Lake(), Codes() with
        {
            Filtered = [("UIX_codes_label", ["label"], "[missing] = 1")],
        });

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (3, 'a', 1)"));
    }

    /// A column no read layer carries is the same value in every row those layers produce -- a
    /// declared default is frozen at build, so `(newid())` is one id for the whole run. There is
    /// nothing there to be unique, and refusing the lake for it would be refusing it for data it was
    /// never given: the rule is dropped instead.
    [Fact]
    public void AUniqueOverAColumnNoLayerCarriesIsDropped()
    {
        using var lake = new TestLake(nameof(AUniqueOverAColumnNoLayerCarriesIsDropped))
            .Json("base", "codes", """[{"code_id": 1}, {"code_id": 2}]""")
            .Stack("base")
            .WriteTo("local");

        var table = new Dacpac.TableModel("codes", [("code_id", "int"), ("token", "uniqueidentifier")], ["code_id"],
                                          [("token", "(newid())")],
                                          Uniques: [("UQ_codes_token", ["token"], false)]);

        Assert.Equal(2, Started(lake, table).Query("SELECT code_id FROM lake.codes").Count);
        lake.Dispose();
    }

    /// The whole rule goes, not the columns it can still see: uniqueness over two columns is not
    /// uniqueness over the one of them the layers happen to carry, and holding that would refuse
    /// rows the schema allows.
    [Fact]
    public void ACompositeUniqueGoesWithAnyColumnNoLayerCarries()
    {
        using var lake = new TestLake(nameof(ACompositeUniqueGoesWithAnyColumnNoLayerCarries))
            .Json("base", "codes", """[{"code_id": 1, "label": "a"}, {"code_id": 2, "label": "a"}]""")
            .Stack("base")
            .WriteTo("local");

        var table = new Dacpac.TableModel("codes",
            [("code_id", "int"), ("label", "nvarchar"), ("token", "uniqueidentifier")], ["code_id"],
            [("token", "(newid())")], Uniques: [("UQ_codes_label_token", ["label", "token"], false)]);

        Assert.Equal(2, Started(lake, table).Query("SELECT code_id FROM lake.codes").Count);
        lake.Dispose();
    }

    /// A lake with no read layers has nothing frozen -- every row arrives as a write, stamped as it
    /// is written -- so the rule is worth keeping and is kept.
    [Fact]
    public void ATableNoLayerCarriesAtAllStillHoldsItsUniques()
    {
        using var lake = new TestLake(nameof(ATableNoLayerCarriesAtAllStillHoldsItsUniques))
            .Json("base", "other", """[{"id": 1}]""")
            .Stack("base")
            .WriteTo("local");

        Dacpac.Write(lake.At("schema", "test.dacpac"),
            new Dacpac.TableModel("other", [("id", "int")], ["id"]),
            new Dacpac.TableModel("codes", [("code_id", "int"), ("label", "nvarchar")], ["code_id"],
                                  Uniques: [("UQ_codes_label", ["label"], false)]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Materialized().Start();

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label) VALUES (1, 'a')"));
        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label) VALUES (2, 'a')"));
    }
}
