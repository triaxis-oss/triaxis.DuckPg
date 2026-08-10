using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// What the store fills in rather than the caller: a declared identity, and a declared default. EF
/// Core treats both as store-generated -- it leaves the column out of the INSERT and reads the value
/// back through OUTPUT -- so the asking and the answering are held to the client that does it.
public class IdentityTests
{
    static Dacpac.TableModel Orders(string type) => new("orders",
        [("id", type), ("amount", "decimal"), ("flag", "bit"), ("created", "datetime")], ["id"],
        Defaults: [("flag", "((1))"), ("created", "(getdate())")], Identity: ["id"]);

    static TestLake Lake() => new TestLake(nameof(IdentityTests))
        .Json("base", "orders", """[{"id": 41, "amount": 5}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    static TestLake Started(TestLake lake, string type = "bigint")
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders(type));
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

    /// A declared default is a value the lake knows, so a column left to it can be read back. EF
    /// Core omits every defaulted column from the INSERT and expects the server's value.
    [Fact]
    public void ADeclaredDefaultIsAnsweredFor()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id], INSERTED.[flag] VALUES (23)", connection);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(42L, Convert.ToInt64(reader.GetValue(0)));
            Assert.True(reader.GetBoolean(1));
        }

        // What was answered is what was stored: the default is stamped into the row being written
        // rather than worked out again afterwards.
        Assert.Equal(["42|23|True"], lake.Query("SELECT id, amount, flag FROM lake.orders WHERE id = 42"));
    }

    /// The same asked of a base, which has no dacpac to read a default off: the bake put it on the
    /// table, so the copy is holding it and the answer is the one the layers would have given.
    [Fact]
    public void ABaseAnswersForItsDefaultsToo()
    {
        using var lake = Lake();
        Dacpac.Write(lake.At("schema", "test.dacpac"), Orders("bigint"));
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Baked("baked.duckdb");

        lake.Config.Dacpac = null;
        lake.FromBase().Start();

        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id], INSERTED.[flag] VALUES (23)", connection);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(42L, Convert.ToInt64(reader.GetValue(0)));
            Assert.True(reader.GetBoolean(1));
        }

        Assert.Equal(["42|23|True"], lake.Query("SELECT id, amount, flag FROM lake.orders WHERE id = 42"));
    }

    /// A default that is not a constant has to be the same value in both places, or the row a caller
    /// holds is not the row the lake wrote.
    [Fact]
    public void AStampedDefaultIsAnsweredWithWhatWasStored()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[created] VALUES (1)", connection);
        var answered = (DateTime)command.ExecuteScalar()!;

        Assert.Equal([answered.ToString("yyyy-MM-dd HH:mm:ss.fff")],
            lake.Query("SELECT strftime(created, '%Y-%m-%d %H:%M:%S.%g') FROM lake.orders WHERE id = 42"));
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

    /// A sequence counts in BIGINT whatever the column was declared as, and the key is read back
    /// off the rows that were written rather than off the table -- so an application casting its own
    /// `int` key gets one, as it would from the database this stands in for.
    [Theory]
    [InlineData("int", typeof(int))]
    [InlineData("bigint", typeof(long))]
    public void TheKeyComesBackAsTheTypeItWasDeclared(string type, Type expected)
    {
        using var lake = Started(Lake(), type);
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id] VALUES (1)", connection);
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(expected, reader.GetFieldType(0));
        Assert.Equal(42L, Convert.ToInt64(reader.GetValue(0)));
    }

    /// The PostgreSQL side writes the same statement in DuckDB's own words, and is answered the
    /// same way -- the front door a client came through is not what decides where a key comes from.
    [Fact]
    public void TheOtherFrontDoorIsAnsweredToo()
    {
        using var lake = Started(Lake());

        Assert.Equal(["42"], lake.Query("INSERT INTO lake.orders (amount) VALUES (3) RETURNING id"));
        Assert.Equal(["41|5", "42|3"], lake.Query("SELECT id, amount FROM lake.orders ORDER BY id"));

        // And the plain form, which the write now hands its key back from -- what it reports is
        // still the rows it wrote, not the rows the key came back on.
        Assert.Equal(1, lake.Execute("INSERT INTO lake.orders (amount) VALUES (4)"));
        Assert.Equal(["43|4"], lake.Query("SELECT id, amount FROM lake.orders WHERE id = 43"));
    }

    // ---- reading the key back without an OUTPUT clause --------------------------------------------

    /// What a client that cannot use OUTPUT sends: the insert and the read-back in one batch, with
    /// the key coming home as a parameter rather than as a result set.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGeneratedKeyComesBackThroughAnOutputParameter(bool materialized)
    {
        var lake = Lake();
        if (materialized) lake.Materialized();
        lake.Config.SerializeTransactions = true;
        using var started = Started(lake);

        using var connection = new SqlConnection(started.SqlConnectionString());
        connection.Open();

        using var command = new SqlCommand(
            "INSERT INTO [orders] ([amount]) VALUES (@p1) ;SELECT  @id = SCOPE_IDENTITY()", connection);
        command.Parameters.AddWithValue("@p1", 7m);
        var id = command.Parameters.Add("@id", System.Data.SqlDbType.Int);
        id.Direction = System.Data.ParameterDirection.Output;

        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Equal(42, id.Value);
        Assert.Equal(["42"], started.Query("SELECT MAX(id) FROM lake.orders"));
    }

    /// Nothing generated, nothing to answer with -- and a zero would be a key.
    [Fact]
    public void ScopeIdentityIsNullBeforeAnythingIsInserted()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        Assert.Equal(DBNull.Value, new SqlCommand("SELECT SCOPE_IDENTITY()", connection).ExecuteScalar());
        Assert.Equal(DBNull.Value, new SqlCommand("SELECT @@IDENTITY", connection).ExecuteScalar());
    }

    /// The value belongs to the connection that generated it. A session reading another's key would
    /// hand a caller a row it never wrote, which is the whole reason SQL Server scopes it.
    [Fact]
    public void OneSessionDoesNotSeeAnothersKey()
    {
        using var lake = Started(Lake());

        using var writer = new SqlConnection(lake.SqlConnectionString());
        using var other = new SqlConnection(lake.SqlConnectionString());
        writer.Open();
        other.Open();

        new SqlCommand("INSERT INTO [orders] ([amount]) VALUES (1)", writer).ExecuteNonQuery();

        Assert.Equal(42m, new SqlCommand("SELECT SCOPE_IDENTITY()", writer).ExecuteScalar());
        Assert.Equal(DBNull.Value, new SqlCommand("SELECT SCOPE_IDENTITY()", other).ExecuteScalar());

        // What the table last handed out is not a session's, though: IDENT_CURRENT is the table's
        // and any connection may ask it.
        Assert.Equal(42m, new SqlCommand("SELECT IDENT_CURRENT('orders')", other).ExecuteScalar());
    }

    /// A pooled connection comes back out as a new session, and a key is that session's. Whoever
    /// gets it next must not be handed a row somebody else wrote -- what the table last generated is
    /// still there to ask about.
    [Fact]
    public void APooledConnectionDoesNotCarryTheKeyOver()
    {
        using var lake = Started(Lake());

        using (var connection = new SqlConnection(lake.SqlConnectionString()))
        {
            connection.Open();
            new SqlCommand("INSERT INTO [orders] ([amount]) VALUES (1)", connection).ExecuteNonQuery();
            Assert.Equal(42m, new SqlCommand("SELECT SCOPE_IDENTITY()", connection).ExecuteScalar());
        }

        using (var connection = new SqlConnection(lake.SqlConnectionString()))
        {
            connection.Open();
            Assert.Equal(DBNull.Value, new SqlCommand("SELECT SCOPE_IDENTITY()", connection).ExecuteScalar());
            Assert.Equal(42m, new SqlCommand("SELECT IDENT_CURRENT('orders')", connection).ExecuteScalar());
        }
    }

    /// A statement that wrote several rows reports the last of them, as SQL Server does.
    [Fact]
    public void AMultiRowInsertReportsTheLastKey()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        new SqlCommand("INSERT INTO [orders] ([amount]) VALUES (1), (2), (3)", connection).ExecuteNonQuery();
        Assert.Equal(44m, new SqlCommand("SELECT SCOPE_IDENTITY()", connection).ExecuteScalar());
    }

    /// An OUTPUT clause answers the caller directly and sets the same value on the way past, so a
    /// client using both is told the same thing twice rather than two different things.
    [Fact]
    public void AnInsertWithOutputSetsItToo()
    {
        using var lake = Started(Lake());
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        Assert.Equal(42L, new SqlCommand(
            "INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[id] VALUES (1)", connection).ExecuteScalar());
        Assert.Equal(42m, new SqlCommand("SELECT SCOPE_IDENTITY()", connection).ExecuteScalar());
    }

    /// All three answer numeric(38,0) in SQL Server, whatever the column was declared as -- an
    /// application reading the scalar as a decimal gets one.
    [Fact]
    public void TheKeyIsAnsweredAsNumeric()
    {
        using var lake = Started(Lake(), "int");
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        new SqlCommand("INSERT INTO [orders] ([amount]) VALUES (1)", connection).ExecuteNonQuery();

        foreach (var asked in (string[])["SCOPE_IDENTITY()", "@@IDENTITY", "IDENT_CURRENT('orders')"])
        {
            using var reader = new SqlCommand($"SELECT {asked}", connection).ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(typeof(decimal), reader.GetFieldType(0));
            Assert.Equal(42m, reader.GetValue(0));
        }
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
