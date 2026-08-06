using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A declared reference is a rule about rows that may live in any layer, so it is kept over the
/// merged view rather than declared on a table: DuckDB will not put a foreign key on a view, and a
/// write-layer constraint would only ever see the rows this process wrote.
public class ReferenceTests : IDisposable
{
    static readonly Dacpac.TableModel Orders = new("orders", [("id", "int"), ("note", "nvarchar")], ["id"]);

    static readonly Dacpac.TableModel Lines =
        new("lines", [("id", "int"), ("order_id", "int"), ("qty", "int")], ["id"]);

    readonly TestLake lake = new TestLake("references")
        // The parents are a read layer's, and so is one of the children: what points at a row is
        // not only what this process wrote.
        .Json("base", "orders", """[{"id": 1, "note": "kept"}, {"id": 2, "note": "free"}, {"id": 3, "note": "own"}]""")
        .Json("base", "lines", """[{"id": 10, "order_id": 1, "qty": 5}, {"id": 11, "order_id": null, "qty": 1}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public ReferenceTests()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Orders, Lines], [],
            [new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["id"])]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    /// The row a line points at cannot go while the line does, and the client is told the way SQL
    /// Server tells it -- error 547, naming the constraint.
    [Fact]
    public void DeletingAReferencedRowIsRefused()
    {
        using var connection = Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        Assert.Equal(547, refused.Number);
        Assert.Contains("REFERENCE constraint \"FK_lines_orders\"", refused.Message);
        Assert.Contains("table \"lines\"", refused.Message);

        // And nothing went: the check runs before the row is hidden, since a statement outside a
        // transaction commits each step as it goes.
        Assert.Equal(3, new SqlCommand("SELECT COUNT(*) FROM [orders]", connection).ExecuteScalar());
        lake.Restart();
        Assert.Equal(["1", "2", "3"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
    }

    /// Nothing points at the others, so they go as they always did -- including the one that a NULL
    /// reference does not hold down.
    [Fact]
    public void DeletingAnUnreferencedRowIsNot()
    {
        using var connection = Open();

        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 2", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["1", "3"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
    }

    /// A row written by this process points just as hard as one that came out of a file.
    [Fact]
    public void ARowWrittenHerePointsToo()
    {
        using var connection = Open();
        new SqlCommand("INSERT INTO [lines] ([id], [order_id], [qty]) VALUES (12, 3, 2)", connection)
            .ExecuteNonQuery();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 3", connection).ExecuteNonQuery());
        Assert.Equal(547, refused.Number);

        // Once the line goes, the row it held down is free.
        new SqlCommand("DELETE FROM [lines] WHERE [id] = 12", connection).ExecuteNonQuery();
        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 3", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
    }

    /// The other front door keeps the same rule, and says so in the terms its own clients read.
    [Fact]
    public void TheOtherFrontDoorKeepsItToo()
    {
        var refused = Assert.Throws<Npgsql.PostgresException>(() =>
            lake.Query("DELETE FROM lake.orders WHERE id = 1"));

        Assert.Equal("23503", refused.SqlState);
        Assert.Equal(["1", "2", "3"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
    }
}
