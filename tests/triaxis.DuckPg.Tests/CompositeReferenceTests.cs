using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A reference pointing at the parent's key *and more* is what a schema writes when it wants a
/// written column to agree with the parent's -- a room's type kept from disagreeing with the room's
/// own. Everything past the key is determined by the key, so a delete can collect it alongside, and
/// both the refusal and the cascade match on what it collected.
public class CompositeReferenceTests : IDisposable
{
    static readonly Dacpac.TableModel Rooms =
        new("rooms", [("pkey", "int"), ("type_pkey", "int"), ("name", "nvarchar")], ["pkey"],
            Uniques: [("UQ_rooms_pkey_type", ["pkey", "type_pkey"], false)]);

    static readonly Dacpac.TableModel Assignments =
        new("assignments", [("id", "int"), ("room_pkey", "int"), ("type_pkey", "int")], ["id"]);

    readonly TestLake lake = new TestLake("composite-references")
        .Json("base", "rooms", """
            [{"pkey": 1, "type_pkey": 7, "name": "going"}, {"pkey": 2, "type_pkey": 8, "name": "staying"}]
            """)
        .Json("base", "assignments", """
            [{"id": 10, "room_pkey": 1, "type_pkey": 7}, {"id": 11, "room_pkey": 2, "type_pkey": 8}]
            """)
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    void Declare(string onDelete)
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Rooms, Assignments], [],
        [
            new Dacpac.ReferenceModel("FK_assignments_rooms", "assignments", ["room_pkey", "type_pkey"],
                                      "rooms", ["pkey", "type_pkey"], onDelete),
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

    /// The cascade goes with the room, as it does on SQL Server -- the rows that pointed at it are
    /// matched on both columns, which is what the key set now carries.
    [Fact]
    public void ACascadeThroughItTakesTheRowsThatPointed()
    {
        Declare("Cascade");
        using var connection = Open();

        Assert.Equal(1, new SqlCommand("DELETE FROM [rooms] WHERE [pkey] = 1", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["2"], lake.Query("SELECT pkey FROM lake.rooms ORDER BY pkey"));
        Assert.Equal(["11"], lake.Query("SELECT id FROM lake.assignments ORDER BY id"));
    }

    /// And where it does not cascade it refuses, naming the constraint -- a reference dropped at
    /// build refuses nothing at all.
    [Fact]
    public void ItRefusesADeleteOfARowStillPointedAt()
    {
        Declare("NoAction");
        using var connection = Open();

        var refused = Assert.Throws<SqlException>(() =>
            new SqlCommand("DELETE FROM [rooms] WHERE [pkey] = 1", connection).ExecuteNonQuery());

        Assert.Equal(547, refused.Number);
        Assert.Contains("REFERENCE constraint \"FK_assignments_rooms\"", refused.Message);

        lake.Restart();
        Assert.Equal(["1", "2"], lake.Query("SELECT pkey FROM lake.rooms ORDER BY pkey"));
    }

    /// A row whose extra column disagrees with the parent's never pointed at it, so it holds nothing
    /// down and goes nowhere: the match is on every column the reference names.
    [Fact]
    public void ARowDisagreeingOnTheExtraColumnPointsAtNothing()
    {
        Declare("Cascade");
        using var connection = Open();
        new SqlCommand("INSERT INTO [assignments] ([id], [room_pkey], [type_pkey]) VALUES (12, 1, 9)",
                       connection).ExecuteNonQuery();

        Assert.Equal(1, new SqlCommand("DELETE FROM [rooms] WHERE [pkey] = 1", connection).ExecuteNonQuery());

        lake.Restart();
        Assert.Equal(["11", "12"], lake.Query("SELECT id FROM lake.assignments ORDER BY id"));
    }

    /// What still cannot be collected is a reference pointing past the key without it: a delete
    /// collects rows by the key, and columns that do not include one name rows it cannot find.
    [Fact]
    public void AReferenceWithoutTheKeyIsStillDropped()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"), [Rooms, Assignments], [],
        [
            new Dacpac.ReferenceModel("FK_assignments_types", "assignments", ["type_pkey"],
                                      "rooms", ["type_pkey"], "NoAction"),
        ]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        using var connection = Open();
        Assert.Equal(1, new SqlCommand("DELETE FROM [rooms] WHERE [pkey] = 1", connection).ExecuteNonQuery());
    }
}
