using System.Data;
using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A lake that publishes no views: what would have gone into one goes into every statement naming
/// the table. Only the SQL Server door has a parser to do that, so this is the whole of what an
/// inlined lake is held to -- the same answers as a lake with views, and nothing in DuckDB's
/// catalog to have got them from.
public class InlineTests : IDisposable
{
    static readonly Dacpac.TableModel Orders =
        new("orders", [("id", "int"), ("note", "nvarchar")], ["id"]);
    static readonly Dacpac.TableModel Lines =
        new("lines", [("id", "int"), ("order_id", "int"), ("qty", "int")], ["id"]);

    readonly TestLake lake = new TestLake("inline")
        .Json("base", "orders", """[{"id": 1, "note": "first"}, {"id": 2, "note": "second"}]""")
        .Json("base", "lines", """
            [{"id": 10, "order_id": 1, "qty": 5}, {"id": 11, "order_id": 2, "qty": 2}]
            """)
        .Stack("base")
        .WriteTo("local")
        .Inlined();

    /// Started with a schema, so the keys, the reference and the declared view are all in play --
    /// each of them a place the merge has to reach without a name to reach it by.
    TestLake Started(string onDelete = "NoAction")
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Orders, Lines],
            [new Dacpac.ViewModel("busy", "SELECT o.id, o.note, l.qty FROM dbo.orders AS o " +
                                          "JOIN dbo.lines AS l ON l.order_id = o.id")],
            [new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["id"], onDelete)]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        return lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    List<string> Rows(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString())));
        return rows;
    }

    int Execute(string sql)
    {
        using var connection = Open();
        using var command = new SqlCommand(sql, connection);
        return command.ExecuteNonQuery();
    }

    /// The bargain, stated: the tables answer, and DuckDB's catalog has never heard of them. The
    /// second half is the cost, so it is asserted rather than described -- a lake that quietly went
    /// back to publishing views would pass every other test here.
    [Fact]
    public void TheTablesAnswerAndTheCatalogHoldsNothing()
    {
        Started();

        Assert.Equal(["1|first", "2|second"], Rows("SELECT id, note FROM orders ORDER BY id"));
        Assert.Equal(["0"], Rows("SELECT count(*) FROM duckdb_tables() WHERE schema_name = 'lake'"));
        // The dacpac's own view is the one relation there is: it names the tables, and the merge
        // went into its body.
        Assert.Equal(["busy"], Rows("SELECT view_name FROM duckdb_views() WHERE schema_name = 'lake'"));
    }

    [Fact]
    public void AQualifiedColumnFindsTheTableItNames()
    {
        Started();

        Assert.Equal(["1|first"], Rows("SELECT dbo.orders.id, dbo.orders.note FROM dbo.orders WHERE dbo.orders.id = 1"));
        Assert.Equal(["2|second"], Rows("SELECT o.id, o.note FROM orders AS o WHERE o.id = 2"));
    }

    [Fact]
    public void AJoinReadsBothTables()
    {
        Started();

        Assert.Equal(["1|5", "2|2"],
            Rows("SELECT o.id, l.qty FROM orders AS o JOIN lines AS l ON l.order_id = o.id ORDER BY o.id"));
    }

    /// A view the schema declared, over tables that have no names of their own.
    [Fact]
    public void ADeclaredViewReadsTheMergeUnderIt()
    {
        Started();

        Assert.Equal(["1|first|5", "2|second|2"], Rows("SELECT id, note, qty FROM busy ORDER BY id"));
    }

    /// The write path end to end: the branch is earned by the first write, and what it holds is in
    /// the files afterwards rather than only in memory.
    [Fact]
    public void WritesLandInTheWriteLayer()
    {
        Started();

        Assert.Equal(1, Execute("INSERT INTO orders (id, note) VALUES (3, 'third')"));
        Assert.Equal(1, Execute("UPDATE orders SET note = 'changed' WHERE id = 1"));
        // Nothing points at 3, which is what makes the delete the write path rather than the
        // reference check `AReferenceRefusesTheDelete` is about.
        Assert.Equal(1, Execute("DELETE FROM orders WHERE id = 3"));

        Assert.Equal(["1|changed", "2|second"], Rows("SELECT id, note FROM orders ORDER BY id"));

        lake.Restart();
        Assert.Equal(["1|changed", "2|second"], Rows("SELECT id, note FROM orders ORDER BY id"));
    }

    /// The key check reads what the table publishes, which on an inlined lake is a subquery rather
    /// than a name -- and it still refuses the row.
    [Fact]
    public void ADuplicateKeyIsStillRefused()
    {
        Started();

        var error = Assert.Throws<SqlException>(() => Execute("INSERT INTO orders (id, note) VALUES (1, 'again')"));
        Assert.Contains("PRIMARY KEY", error.Message);
        Assert.Equal(["1|first", "2|second"], Rows("SELECT id, note FROM orders ORDER BY id"));
    }

    /// A reference is checked against the child's own merge, which is a second table with no name.
    [Fact]
    public void AReferenceRefusesTheDelete()
    {
        Started();

        var error = Assert.Throws<SqlException>(() => Execute("DELETE FROM orders WHERE id = 1"));
        Assert.Contains("REFERENCE constraint", error.Message);
    }

    /// And a cascade reaches through it, collecting the child's keys out of that same merge.
    [Fact]
    public void ACascadeReachesTheChild()
    {
        Started("Cascade");

        Assert.Equal(1, Execute("DELETE FROM orders WHERE id = 1"));
        Assert.Equal(["2"], Rows("SELECT id FROM orders ORDER BY id"));
        Assert.Equal(["11"], Rows("SELECT id FROM lines ORDER BY id"));
    }

    /// SqlBulkCopy asks the destination for its columns before it sends anything, and refuses an
    /// empty answer. `information_schema` has none to give here, so the merge is asked instead.
    [Fact]
    public void BulkCopyFindsTheDestinationsColumns()
    {
        Started();

        var rows = new DataTable();
        rows.Columns.Add("id", typeof(int));
        rows.Columns.Add("note", typeof(string));
        rows.Rows.Add(20, "bulk one");
        rows.Rows.Add(21, "bulk two");

        using (var connection = Open())
        {
            using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "orders" };
            bulk.WriteToServer(rows);
        }

        lake.Restart();
        Assert.Equal(["20|bulk one", "21|bulk two"], Rows("SELECT id, note FROM orders WHERE id >= 20 ORDER BY id"));
    }

    /// A name the lake does not publish is left exactly as it was written, so what comes back names
    /// the thing that was not found rather than something the rewrite invented.
    [Fact]
    public void AnUnknownNameIsStillTheClientsOwn()
    {
        Started();

        var error = Assert.Throws<SqlException>(() => Rows("SELECT * FROM nowhere"));
        Assert.Contains("nowhere", error.Message);
    }

    [Fact]
    public void ThePostgresDoorIsRefused()
    {
        var config = new Config { Inline = true, Listen = "127.0.0.1:0", Tds = "127.0.0.1:0" };
        var error = Assert.Throws<DuckPgConfigurationException>(config.Validate);
        Assert.Contains("PostgreSQL door", error.Message);
    }

    [Fact]
    public void ACollapsedLakeHasNothingToInline()
    {
        var config = new Config { Inline = true, Tds = "127.0.0.1:0", Materialize = true };
        var error = Assert.Throws<DuckPgConfigurationException>(config.Validate);
        Assert.Contains("nothing left to inline", error.Message);
    }
}
