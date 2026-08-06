using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// `ON DELETE CASCADE` is performed as what it means: the same delete against the table pointing at
/// the one deleted from, and again against whatever points at that. What it takes may live in any
/// layer, so a cascade tombstones exactly as a delete does -- and where it cannot be performed, the
/// reference is kept as one that refuses, since orphaning the rows is wrong either way.
public class CascadeTests : IDisposable
{
    static readonly Dacpac.TableModel Orders = new("orders", [("id", "int"), ("note", "nvarchar")], ["id"]);
    static readonly Dacpac.TableModel Lines =
        new("lines", [("id", "int"), ("order_id", "int"), ("qty", "int")], ["id"]);
    static readonly Dacpac.TableModel Notes =
        new("notes", [("id", "int"), ("line_id", "int"), ("text", "nvarchar")], ["id"]);

    readonly TestLake lake = new TestLake("cascades")
        .Json("base", "orders", """[{"id": 1, "note": "going"}, {"id": 2, "note": "staying"}]""")
        .Json("base", "lines", """
            [{"id": 10, "order_id": 1, "qty": 5}, {"id": 11, "order_id": null, "qty": 1},
             {"id": 12, "order_id": 2, "qty": 2}]
            """)
        .Json("base", "notes", """[{"id": 100, "line_id": 10, "text": "a"}, {"id": 101, "line_id": 12, "text": "b"}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    /// The chain the tests vary: what `lines` and `notes` declare about a row going above them.
    void Declare(string lines, string notes)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Orders, Lines, Notes], [],
        [
            new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["id"], lines),
            new Dacpac.ReferenceModel("FK_notes_lines", "notes", ["line_id"], "lines", ["id"], notes),
        ]);
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

    /// A delete reaches every table below it, through rows that came out of files rather than out of
    /// this process -- and the count answered for is the target's own, as SQL Server answers it.
    [Fact]
    public void ACascadeReachesThroughTheChain()
    {
        Declare("Cascade", "Cascade");
        using var connection = Open();

        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
        Assert.Equal(["101"], lake.Query("SELECT id FROM lake.notes ORDER BY id"));
    }

    /// A row nothing points at is untouched by the cascade above it: the rows taken are the ones
    /// that pointed, not the table.
    [Fact]
    public void ACascadeTakesOnlyWhatPointed()
    {
        Declare("Cascade", "Cascade");
        using var connection = Open();

        new SqlCommand("DELETE FROM [orders] WHERE [id] = 2", connection).ExecuteNonQuery();

        lake.Restart();
        Assert.Equal(["1"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["10", "11"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
        Assert.Equal(["100"], lake.Query("SELECT id FROM lake.notes ORDER BY id"));
    }

    /// A row two tables down may be held by a reference nothing cascades, and the delete that would
    /// orphan it is refused -- before anything goes, so the row above it stays too.
    [Fact]
    public void ARowHeldBelowTheCascadeRefusesIt()
    {
        Declare("Cascade", "NoAction");
        using var connection = Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        Assert.Equal(547, refused.Number);
        Assert.Contains("REFERENCE constraint \"FK_notes_lines\"", refused.Message);

        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["10", "11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
    }

    /// And an order whose line nothing holds still goes, cascade and all -- the refusal is about the
    /// rows the statement reaches, not about the reference existing.
    [Fact]
    public void ARowWithNothingHeldBelowItStillGoes()
    {
        Declare("Cascade", "NoAction");
        using var connection = Open();

        new SqlCommand("DELETE FROM [notes] WHERE [id] = 100", connection).ExecuteNonQuery();
        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
    }

    /// A cascade cannot delete from a table this lake will not write to, so the reference is kept as
    /// the refusal it would otherwise be -- the row is held down rather than left pointing at nothing.
    [Fact]
    public void ACascadeIntoAReadOnlyTableRefusesInstead()
    {
        lake.Config.Tables["lines"] = new TableConfig { Writable = false };
        Declare("Cascade", "Cascade");
        using var connection = Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        Assert.Equal(547, refused.Number);
        Assert.Contains("REFERENCE constraint \"FK_lines_orders\"", refused.Message);
    }

    /// `SET NULL` is not a cascade, and reading the model wrongly is how it would become one. It is
    /// still not performed -- what a lake does about it is warned about at startup and no more.
    [Fact]
    public void SetNullIsNotReadAsACascade()
    {
        Declare("SetNull", "SetNull");

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["10", "11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
    }

    /// The other front door performs the same cascade: both speak to one lake.
    [Fact]
    public void TheOtherFrontDoorCascadesToo()
    {
        Declare("Cascade", "Cascade");

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
        Assert.Equal(["101"], lake.Query("SELECT id FROM lake.notes ORDER BY id"));
    }
}

/// A cascade that could reach the table it started from would not terminate. SQL Server refuses to
/// declare one at all; a lake demotes it to the refusal a plain reference gets, which is the same
/// answer arrived at later.
public class CascadeCycleTests : IDisposable
{
    readonly TestLake lake = new TestLake("cascade-cycles")
        .Json("base", "nodes", """
            [{"id": 1, "parent_id": null}, {"id": 2, "parent_id": 1}, {"id": 3, "parent_id": null}]
            """)
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public CascadeCycleTests()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            [new Dacpac.TableModel("nodes", [("id", "int"), ("parent_id", "int")], ["id"])], [],
            [new Dacpac.ReferenceModel("FK_nodes_nodes", "nodes", ["parent_id"], "nodes", ["id"], "Cascade")]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    [Fact]
    public void ASelfReferencingCascadeIsRefusedRatherThanFollowed()
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [nodes] WHERE [id] = 1", connection).ExecuteNonQuery());
        Assert.Equal(547, refused.Number);

        // A node nothing points at goes as it always did.
        Assert.Equal(1, new SqlCommand("DELETE FROM [nodes] WHERE [id] = 3", connection).ExecuteNonQuery());
    }
}
