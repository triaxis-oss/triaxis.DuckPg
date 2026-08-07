using Microsoft.Data.SqlClient;
using Npgsql;

namespace triaxis.DuckPg.Tests;

/// A declared key is a rule about rows that may live in any layer, so it is kept over the merged
/// view rather than left to the write branch's own constraint -- which only ever sees the rows this
/// process wrote, and which a materialized lake does not have at all.
public class KeyTests
{
    static TestLake Lake([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        new TestLake(name)
            .Parquet("base", "orders", """
                SELECT * FROM (VALUES (1, 10.0::DOUBLE), (2, 20.0::DOUBLE)) t(order_id, amount)
                """)
            .Stack("base")
            .WriteTo("local");

    static TestLake Started(TestLake lake, bool materialized)
    {
        lake.Config.DefaultKey = ["order_id"];
        if (materialized) lake.Materialized();
        return lake.Start();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertingAKeyALowerLayerHoldsIsRefused(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 99)"));
        Assert.Equal("23505", refused.SqlState);

        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// The write branch's own copy is no different: a key this process wrote is published just the
    /// same, and a second insert of it is the same mistake.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertingAKeyThisProcessWroteIsRefused(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)"));
        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 31)"));

        Assert.Equal(["1|10", "2|20", "3|30"],
                     lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// One statement carrying the same key twice is the same violation, and it is caught before any
    /// of the rows land -- a statement outside a transaction commits each step as it goes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneStatementRepeatingAKeyIsRefused(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Throws<PostgresException>(() =>
            lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30), (3, 31)"));

        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// What was deleted is not there, whatever layer it came out of -- so writing it again is an
    /// insert like any other rather than a duplicate.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeletedKeyCanBeInsertedAgain(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Equal(1, lake.Execute("DELETE FROM lake.orders WHERE order_id = 1"));
        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 11)"));

        Assert.Equal(["1|11", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewKeyIsWrittenAsItAlwaysWas(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (3, 30)"));
        Assert.Equal(["1|10", "2|20", "3|30"],
                     lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// An insert asked what it wrote is checked like any other -- the rows go down first there, so a
    /// duplicate caught afterwards would be caught after it had landed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInsertAskedWhatItWroteIsCheckedToo(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Throws<PostgresException>(() =>
            lake.Query("INSERT INTO lake.orders (order_id, amount) VALUES (1, 99) RETURNING order_id"));

        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// SqlClient reads the number, and 2627 is what SQL Server raises for this one.
    [Fact]
    public void TheClientIsToldTheWaySqlServerTellsIt()
    {
        using var lake = Lake().WithTds();
        Started(lake, materialized: false);

        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("INSERT INTO [orders] ([order_id], [amount]) VALUES (1, 99)", connection)
                .ExecuteNonQuery());

        Assert.Equal(2627, refused.Number);
        Assert.Contains("PRIMARY KEY", refused.Message);
        Assert.Contains("orders", refused.Message);
    }

    /// The batch an ORM sends is a MERGE over rows that cannot match, which is an insert -- and the
    /// key it repeats is one nothing has written yet, so only the batch itself can tell.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABatchRepeatingAKeyIsRefused(bool materialized)
    {
        using var lake = Lake().WithTds();
        Started(lake, materialized);

        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("MERGE [orders] USING (VALUES (3, 30.0, 0), (3, 31.0, 1)) AS i ([order_id], [amount], _Position) " +
                           "ON 1=0 WHEN NOT MATCHED THEN INSERT ([order_id], [amount]) VALUES (i.[order_id], i.[amount]) " +
                           "OUTPUT INSERTED.[order_id], i._Position;", connection).ExecuteReader());

        Assert.Equal(2627, refused.Number);
        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// A key moved onto one the lake already publishes would leave two rows under one key: layered,
    /// the moved row shadows the one it landed on; materialized, both stay, and the delta written at
    /// shutdown then fails on the write layer's own PRIMARY KEY.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAKeyOntoOneThatIsThereIsRefused(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        var refused = Assert.Throws<PostgresException>(() =>
            lake.Execute("UPDATE lake.orders SET order_id = 2 WHERE order_id = 1"));
        Assert.Equal("23505", refused.SqlState);

        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
        lake.Restart();
        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// A key moved somewhere free moves, and the old one is left hidden behind it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAKeySomewhereFreeStillWorks(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Equal(1, lake.Execute("UPDATE lake.orders SET order_id = 3 WHERE order_id = 1"));

        lake.Restart();
        Assert.Equal(["2|20", "3|10"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// A key going is a key free to land on, so rows that shift up en masse are not each other's
    /// collision -- which is the difference between asking the published table and asking it for the
    /// keys this statement is not taking away.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftingEveryKeyUpIsNotACollision(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Equal(2, lake.Execute("UPDATE lake.orders SET order_id = order_id + 1"));

        lake.Restart();
        Assert.Equal(["2|10", "3|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// A join around the target may match a row twice, which writes it twice under the one key even
    /// though nothing moved. The write branch refuses that on its own; a materialized table has
    /// nothing to refuse it with.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJoinMatchingARowTwiceIsRefused(bool materialized)
    {
        using var lake = Started(Lake(), materialized);

        Assert.Throws<PostgresException>(() =>
            lake.Execute("UPDATE lake.orders SET amount = v.a FROM (VALUES (1, 5.0), (1, 6.0)) AS v(id, a) " +
                         "WHERE order_id = v.id"));

        Assert.Equal(["1|10", "2|20"], lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id"));
    }

    /// Turned off, the lake is what it was before the rule was kept -- and the two modes do not even
    /// agree about what that is: layered, the written row shadows the one below it, so the key is
    /// still one row; materialized, nothing is below anything and both stay. That divergence is the
    /// reason the rule is on by default, and what the flag buys is the scan of the merge behind it.
    [Theory]
    [InlineData(false, new[] { "1|99", "2|20" })]
    [InlineData(true, new[] { "1|10", "1|99", "2|20" })]
    public void TheRuleCanBeTurnedOff(bool materialized, string[] published)
    {
        using var lake = Lake();
        lake.Config.CheckKeys = false;
        Started(lake, materialized);

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 99)"));
        Assert.Equal(published, lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id, amount"));
    }

    /// A table with no declared key has no rule to keep: a file's rows have no identity without one,
    /// which is what makes every insert into it a new row.
    [Fact]
    public void AKeylessTableTakesWhateverItIsGiven()
    {
        using var lake = Lake().Start();

        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (order_id, amount) VALUES (1, 10)"));
        Assert.Equal(["1|10", "1|10", "2|20"],
                     lake.Query("SELECT order_id, amount FROM lake.orders ORDER BY order_id, amount"));
    }
}
