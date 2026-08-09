namespace triaxis.DuckPg.Tests;

/// A `NEWID()` default is one value for the whole run, because a view is bound on every execution
/// and a generated one would answer differently every scan. Asked for, it is *derived* instead: the
/// row's key in the low half of a uuid whose high half belongs to the column. That is per row, the
/// same on every scan, and the same after a restart -- which a generated one would not be.
public class DerivedIdTests
{
    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Json("base", "orders", """[{"order_id": 1}, {"order_id": 2}, {"order_id": 3}]""")
            .Stack("base")
            .WriteTo("local");

    static TestLake Started(TestLake lake, bool derive = true, bool materialized = false,
                           string type = "uniqueidentifier", string expression = "(newid())",
                           string[]? key = null)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel(
            "orders", [("order_id", "int"), ("token", type)], key ?? ["order_id"], [("token", expression)]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DeriveIds = derive;
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    static List<string> Tokens(TestLake lake) => lake.Query("SELECT token FROM lake.orders ORDER BY order_id");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryRowGetsItsOwn(bool materialized)
    {
        using var lake = Started(Lake(), materialized: materialized);

        var tokens = Tokens(lake);
        Assert.Equal(3, tokens.Distinct().Count());
    }

    /// What the flag is off for: one id for the run, which is what a lake did before it was asked.
    [Fact]
    public void WithoutItTheyAllShareTheRunsOne()
    {
        using var lake = Started(Lake(), derive: false);

        Assert.Single(Tokens(lake).Distinct());
    }

    /// A view is bound per execution, so the same scan asked twice has to answer the same -- which
    /// is exactly what generating them would have got wrong.
    [Fact]
    public void TheSameScanAnswersTheSameTwice()
    {
        using var lake = Started(Lake());

        Assert.Equal(Tokens(lake), Tokens(lake));
    }

    /// Derived from the key rather than from anything this process holds, so it survives a restart
    /// -- and that is what keeps the shutdown delta a delta instead of every row of the table.
    [Fact]
    public void TheySurviveARestart()
    {
        using var lake = Started(Lake());

        var before = Tokens(lake);
        lake.Restart();
        Assert.Equal(before, Tokens(lake));
    }

    /// The shape `NEWSEQUENTIALID()` has: one prefix for the column, the key counting up beneath it.
    [Fact]
    public void ConsecutiveKeysGiveConsecutiveIds()
    {
        using var lake = Started(Lake());

        var tokens = Tokens(lake);
        Assert.Single(tokens.Select(t => t[..19]).Distinct());
        Assert.EndsWith("-0000-000000000001", tokens[0]);
        Assert.EndsWith("-0000-000000000002", tokens[1]);
        Assert.EndsWith("-0000-000000000003", tokens[2]);
    }

    /// A row a file does carry keeps what the file says: the default fills gaps and nothing else.
    [Fact]
    public void AValueAFileCarriesIsLeftAlone()
    {
        using var lake = new TestLake(nameof(AValueAFileCarriesIsLeftAlone))
            .Json("base", "orders", """
                [{"order_id": 1, "token": "11111111-1111-1111-1111-111111111111"}, {"order_id": 2}]
                """)
            .Stack("base")
            .WriteTo("local");

        var tokens = Tokens(Started(lake));
        Assert.Equal("11111111-1111-1111-1111-111111111111", tokens[0]);
        Assert.NotEqual(tokens[0], tokens[1]);
    }

    /// A row written now has its own identity and is stamped with a real one, which is what the
    /// write layer's own DEFAULT already did.
    [Fact]
    public void AWrittenRowIsStampedRatherThanDerived()
    {
        using var lake = Started(Lake());

        lake.Execute("INSERT INTO lake.orders (order_id) VALUES (4)");
        var tokens = Tokens(lake);
        Assert.Equal(4, tokens.Distinct().Count());
        Assert.DoesNotContain("-0000-000000000004", tokens[3]);
    }

    /// A key of one narrow integer goes in as itself; anything else is hashed to the same 64 bits,
    /// since the padding would cut a wider one short and give two keys one id.
    [Fact]
    public void AKeyThatIsNotANarrowIntegerIsHashedInstead()
    {
        using var lake = new TestLake(nameof(AKeyThatIsNotANarrowIntegerIsHashedInstead))
            .Json("base", "orders", """[{"order_id": "a"}, {"order_id": "b"}, {"order_id": "c"}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel(
            "orders", [("order_id", "nvarchar"), ("token", "uniqueidentifier")], ["order_id"],
            [("token", "(newid())")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DeriveIds = true;
        lake.Start();

        var tokens = lake.Query("SELECT token FROM lake.orders ORDER BY order_id");
        Assert.Equal(3, tokens.Distinct().Count());
        Assert.DoesNotContain("-0000-", tokens[0]);
    }

    /// Two columns of the same table cannot answer alike: the half that is not the key belongs to
    /// the column.
    [Fact]
    public void TwoColumnsOfARowDoNotShareAnId()
    {
        using var lake = new TestLake(nameof(TwoColumnsOfARowDoNotShareAnId))
            .Json("base", "orders", """[{"order_id": 1}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel(
            "orders", [("order_id", "int"), ("a", "uniqueidentifier"), ("b", "uniqueidentifier")], ["order_id"],
            [("a", "(newid())"), ("b", "(newid())")]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DeriveIds = true;
        lake.Start();

        var row = lake.Query("SELECT a, b FROM lake.orders")[0].Split('|');
        Assert.NotEqual(row[0], row[1]);
    }

    /// Only `NEWID()`. `(getdate())` asks when a row was written, which a file cannot say -- so it
    /// keeps the one honest answer there is, the moment the lake was built.
    [Fact]
    public void ATimestampDefaultIsLeftFrozen()
    {
        using var lake = Started(Lake(), type: "datetime2", expression: "(getdate())");

        Assert.Single(Tokens(lake).Distinct());
    }

    /// A row with no identity has nothing to derive from, so the table keeps the run's one id and
    /// the lake says so at startup: one flag across a mixed lake should leave the odd table out
    /// rather than refuse to start, exactly as one `--key` does.
    [Fact]
    public void ATableWithNoKeyIsLeftAlone()
    {
        using var lake = Started(Lake(), key: []);

        Assert.Single(Tokens(lake).Distinct());
    }

    /// The payoff: a declared unique over such a column is enforceable, where a run's single id
    /// would have refused the whole lake at startup.
    [Fact]
    public void ADeclaredUniqueOverADerivedColumnHolds()
    {
        using var lake = new TestLake(nameof(ADeclaredUniqueOverADerivedColumnHolds))
            .Json("base", "orders", """[{"order_id": 1}, {"order_id": 2}]""")
            .Stack("base")
            .WriteTo("local");
        Dacpac.Write(lake.At("schema", "test.dacpac"), new Dacpac.TableModel(
            "orders", [("order_id", "int"), ("token", "uniqueidentifier")], ["order_id"],
            [("token", "(newid())")], Uniques: [("UQ_orders_token", ["token"], false)]));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Config.DeriveIds = true;
        lake.Materialized().Start();

        Assert.Equal(2, Tokens(lake).Distinct().Count());
    }
}
