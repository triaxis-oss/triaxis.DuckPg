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

    /// `SET NULL` is not a cascade, and reading the model wrongly is how it would become one: the
    /// rows stay where they are, with what pointed emptied.
    [Fact]
    public void SetNullIsNotReadAsACascade()
    {
        Declare("SetNull", "SetNull");

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["10", "11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
        Assert.Equal(["10=", "11=", "12=2"],
                     lake.Query("SELECT id || '=' || COALESCE(order_id::VARCHAR, '') FROM lake.lines ORDER BY id"));

        // Nothing below it moved: a cleared row is still there, so nothing it held was orphaned.
        Assert.Equal(["100", "101"], lake.Query("SELECT id FROM lake.notes ORDER BY id"));
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

/// `ON DELETE SET NULL` and `SET DEFAULT` are the other thing a delete can do about the rows
/// pointing at it: leave them where they are and empty what pointed. That is an UPDATE rather than a
/// delete, so nothing is hidden and nothing recurses -- the rows are still there afterwards.
public class ClearTests : IDisposable
{
    static readonly Dacpac.TableModel Orders = new("orders", [("id", "int"), ("note", "nvarchar")], ["id"]);
    static readonly Dacpac.TableModel Lines =
        new("lines", [("id", "int"), ("order_id", "int"), ("qty", "int")], ["id"]);
    static readonly Dacpac.TableModel Notes =
        new("notes", [("id", "int"), ("line_id", "int"), ("text", "nvarchar")], ["id"]);

    readonly TestLake lake = new TestLake("clears")
        .Json("base", "orders", """[{"id": 1, "note": "going"}, {"id": 2, "note": "staying"}]""")
        .Json("base", "lines", """
            [{"id": 10, "order_id": 1, "qty": 5}, {"id": 11, "order_id": null, "qty": 1},
             {"id": 12, "order_id": 2, "qty": 2}]
            """)
        .Json("base", "notes", """[{"id": 100, "line_id": 10, "text": "a"}, {"id": 101, "line_id": 12, "text": "b"}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    void Declare(string lines, string notes, Dacpac.TableModel? linesModel = null)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Orders, linesModel ?? Lines, Notes], [],
        [
            new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["id"], lines),
            new Dacpac.ReferenceModel("FK_notes_lines", "notes", ["line_id"], "lines", ["id"], notes),
        ]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    List<string> Lined() =>
        lake.Query("SELECT id || '=' || COALESCE(order_id::VARCHAR, 'null') || '/' || qty FROM lake.lines ORDER BY id");

    /// The row stays, the rest of it is untouched, and the delete is not refused -- a reference that
    /// says what to do about the rows pointing at one that goes is not one that holds it down.
    [Fact]
    public void ClearingEmptiesWhatPointedAndKeepsEverythingElse()
    {
        Declare("SetNull", "NoAction");
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        Assert.Equal(1, new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT id FROM lake.orders ORDER BY id"));
        Assert.Equal(["10=null/5", "11=null/1", "12=2/2"], Lined());
    }

    /// `SET DEFAULT` writes the column's declared default instead, which is what the schema says a
    /// row with nothing to point at should hold. Row 11 was already null and the same default fills
    /// it in the read layer -- that is the declared-default machinery, and row 12 is what says the
    /// clear touched only what pointed.
    [Fact]
    public void SetDefaultWritesTheDeclaredDefault()
    {
        Declare("SetDefault", "NoAction", Lines with { Defaults = [("order_id", "((-1))")] });

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["10=-1/5", "11=-1/1", "12=2/2"], Lined());
    }

    /// And NULL where the column declares none, which is what SQL Server does with it too.
    [Fact]
    public void SetDefaultWithoutADefaultClearsToNull()
    {
        Declare("SetDefault", "NoAction");

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["10=null/5", "11=null/1", "12=2/2"], Lined());
    }

    /// A clear below a cascade is reached by it: the line goes with its order, and the note that
    /// pointed at the line stays with nothing to point at.
    [Fact]
    public void ACascadeReachesAClearBeneathIt()
    {
        Declare("Cascade", "SetNull");

        lake.Execute("DELETE FROM lake.orders WHERE id = 1");

        lake.Restart();
        Assert.Equal(["11", "12"], lake.Query("SELECT id FROM lake.lines ORDER BY id"));
        Assert.Equal(["100=null", "101=12"],
                     lake.Query("SELECT id || '=' || COALESCE(line_id::VARCHAR, 'null') FROM lake.notes ORDER BY id"));
    }

    /// A clear is a write, and a lake that will not write to the child cannot make one -- so the
    /// reference is kept as the refusal it would otherwise be, exactly as an unperformable cascade is.
    [Fact]
    public void AClearIntoAReadOnlyTableRefusesInstead()
    {
        lake.Config.Tables["lines"] = new TableConfig { Writable = false };
        Declare("SetNull", "NoAction");
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [orders] WHERE [id] = 1", connection).ExecuteNonQuery());

        Assert.Equal(547, refused.Number);
        Assert.Contains("REFERENCE constraint \"FK_lines_orders\"", refused.Message);
    }
}

/// A reference pointing with the child's own key cannot be cleared: emptying a key column collapses
/// every cleared row onto one key, and the rows they used to shadow come back uncovered. Refused
/// rather than done wrong, which is the same answer an unperformable cascade gets.
public class ClearKeyTests : IDisposable
{
    readonly TestLake lake = new TestLake("clear-keys")
        .Json("base", "users", """[{"id": 1}, {"id": 2}]""")
        .Json("base", "profiles", """[{"user_id": 1, "bio": "a"}, {"user_id": 2, "bio": "b"}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public ClearKeyTests()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"),
        [
            new Dacpac.TableModel("users", [("id", "int")], ["id"]),
            new Dacpac.TableModel("profiles", [("user_id", "int"), ("bio", "nvarchar")], ["user_id"]),
        ], [],
        [new Dacpac.ReferenceModel("FK_profiles_users", "profiles", ["user_id"], "users", ["id"], "SetNull")]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    [Fact]
    public void ClearingAKeyColumnIsRefusedRatherThanPerformed()
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [users] WHERE [id] = 1", connection).ExecuteNonQuery());
        Assert.Equal(547, refused.Number);

        // A user nothing points at goes as it always did.
        lake.Execute("DELETE FROM lake.profiles WHERE user_id = 2");
        Assert.Equal(1, new SqlCommand("DELETE FROM [users] WHERE [id] = 2", connection).ExecuteNonQuery());
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
