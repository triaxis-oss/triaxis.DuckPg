using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A key the store fills in is the one value a lake makes up, and only where the declared schema
/// says so. It is what EF Core asks for back on every insert into such a table, so the asking and
/// the answering are held to the same client that does it.
public class IdentityTests
{
    static readonly Dacpac.TableModel Orders = new("orders",
        [("id", "bigint"), ("amount", "decimal")], ["id"], Identity: ["id"]);

    static TestLake Lake() => new TestLake(nameof(IdentityTests))
        .Json("base", "orders", """[{"id": 41, "amount": 5}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    static TestLake Started(TestLake lake)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        return lake.Start();
    }

    /// What EF Core sends for a batch: the rows in a USING clause and the keys asked for back,
    /// paired with the position of the row that got each one.
    [Fact]
    public void ABatchInsertIsAnsweredWithTheKeysItGenerated()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "MERGE [orders] USING (VALUES (@p0, 0), (@p1, 1)) AS i ([amount], _Position) ON 1=0 " +
            "WHEN NOT MATCHED THEN INSERT ([amount]) VALUES (i.[amount]) " +
            "OUTPUT INSERTED.[id], i._Position;", connection);
        command.Parameters.AddWithValue("@p0", 1m);
        command.Parameters.AddWithValue("@p1", 2m);

        var answered = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) answered.Add($"{reader.GetInt64(0)}|{reader.GetInt32(1)}");

        // The keys start after the highest the files already hold, and each row is told which of
        // the rows sent it was.
        Assert.Equal(["42|0", "43|1"], answered);

        lake.Restart();
        Assert.Equal(["41|5", "42|1", "43|2"], lake.Query("SELECT id, amount FROM lake.orders ORDER BY id"));
    }

    /// The single-row form, and the key it generates carrying on from the batch before it.
    [Fact]
    public void AnInsertWithoutTheKeyStillGetsOne()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id] VALUES (@p0)", connection);
        command.Parameters.AddWithValue("@p0", 9m);

        Assert.Equal(42L, command.ExecuteScalar());
        Assert.Equal(["41|5", "42|9"], lake.Query("SELECT id, amount FROM lake.orders ORDER BY id"));
    }

    /// Nothing else is made up. A column the rows do not carry and the schema does not generate is
    /// said to be unanswerable rather than answered with a null.
    [Fact]
    public void AColumnNothingGeneratesIsRefused()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("INSERT INTO [orders] ([id]) OUTPUT INSERTED.[amount] VALUES (77)", connection)
                .ExecuteNonQuery());

        Assert.Contains("cannot be answered", refused.Message);
    }

    /// The PostgreSQL side writes the same statement in DuckDB's own words, and is answered the
    /// same way -- the front door a client came through is not what decides where a key comes from.
    [Fact]
    public void TheOtherFrontDoorIsAnsweredToo()
    {
        using var lake = Started(Lake());

        Assert.Equal(["42"], lake.Query("INSERT INTO lake.orders (amount) VALUES (3) RETURNING id"));
        Assert.Equal(["41|5", "42|3"], lake.Query("SELECT id, amount FROM lake.orders ORDER BY id"));
    }

    /// A restart asks the files again rather than carrying on from what this process handed out,
    /// which is what keeps a key it generated from being generated twice.
    [Fact]
    public void KeysCarryOnFromWhatTheFilesHold()
    {
        using var lake = Started(Lake());

        using (var connection = new SqlConnection(lake.SqlConnectionString()))
        {
            connection.Open();
            new SqlCommand("INSERT INTO [orders] ([amount]) VALUES (1)", connection).ExecuteNonQuery();
        }

        lake.Restart();

        using (var connection = new SqlConnection(lake.SqlConnectionString()))
        {
            connection.Open();
            Assert.Equal(43L, new SqlCommand(
                "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id] VALUES (2)", connection).ExecuteScalar());
        }
    }
}
