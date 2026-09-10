using Npgsql;

namespace triaxis.DuckPg.Tests;

/// A declared `CHECK` is a rule about one row and nothing else, so unlike a key it costs a pass over
/// the rows a statement is about to write rather than a scan of the table -- and it is held over
/// those rows rather than by DuckDB, which would cover a materialized lake alone and refuse in words
/// naming the expression rather than the constraint.
public class CheckTests
{
    static Dacpac.TableModel Codes(params (string Name, string Expression)[] checks) =>
        new("codes", [("code_id", "int"), ("label", "nvarchar"), ("slot", "int")], ["code_id"],
            Checks: checks);

    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Json("base", "codes", """[{"code_id": 1, "label": "a", "slot": 1}]""")
            .Stack("base")
            .WriteTo("local");

    static TestLake Started(TestLake lake, Dacpac.TableModel table, bool materialized = false)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), table);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowBreakingADeclaredCheckIsRefused(bool materialized)
    {
        using var lake = Started(Lake($"{nameof(ARowBreakingADeclaredCheckIsRefused)}{materialized}"),
                                 Codes(("CK_codes_slot", "[slot] > 0")), materialized);

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (2, 'b', 0)"));

        Assert.Contains("CHECK constraint \"CK_codes_slot\"", refused.MessageText);
        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (2, 'b', 1)"));
    }

    /// The rule is about the rows the statement leaves behind, so an update that breaks it is
    /// refused as readily as an insert -- including the one-statement path a materialized table
    /// takes, where DuckDB is handed the statement as the client wrote it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUpdateBreakingOneIsRefusedToo(bool materialized)
    {
        using var lake = Started(Lake($"{nameof(AnUpdateBreakingOneIsRefusedToo)}{materialized}"),
                                 Codes(("CK_codes_slot", "[slot] > 0")), materialized);

        Assert.Throws<PostgresException>(() => lake.Execute("UPDATE lake.codes SET slot = -1"));
        Assert.Equal(["1"], lake.Query("SELECT slot FROM lake.codes"));
    }

    /// Nothing is written before the rule is asked: a plan outside a transaction commits each step
    /// as it goes, so a check that ran afterwards would have nothing left to undo.
    [Fact]
    public void NothingIsWrittenByAStatementTheCheckRefuses()
    {
        using var lake = Started(Lake(), Codes(("CK_codes_slot", "[slot] > 0")));

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (2, 'b', 1), (3, 'c', 0)"));

        Assert.Single(lake.Query("SELECT code_id FROM lake.codes"));
    }

    /// A predicate that comes out unknown passes, as it does on SQL Server -- and a column the
    /// statement left out holds what the schema declares it defaults to, which is what the rule is
    /// then about.
    [Fact]
    public void AColumnLeftOutIsCheckedAsWhatItDefaultsTo()
    {
        using var lake = Lake();
        var table = new Dacpac.TableModel("codes",
            [("code_id", "int"), ("label", "nvarchar"), ("slot", "int")], ["code_id"],
            [("slot", "((0))")], Checks: [("CK_codes_slot", "[slot] > 0")]);

        Started(lake, table);

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.codes (code_id, label) VALUES (2, 'b')"));
    }

    /// A check the translator cannot render, or one over a column the lake does not publish, leaves
    /// the rule unheld rather than failing every write to the table.
    [Fact]
    public void ACheckDuckPgCannotRenderIsNotHeld()
    {
        using var lake = Started(Lake(), Codes(("CK_codes_missing", "[missing] > 0")));

        Assert.Equal(1, lake.Execute("INSERT INTO lake.codes (code_id, label, slot) VALUES (2, 'b', 0)"));
    }
}
