using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A declared scalar function, published as a DuckDB macro beside the tables. The dacpac these read
/// is DacFx's own output rather than the suite's hand-written one -- `Schema/sample.sqlproj` builds
/// it -- because the shape of `model.xml` is the one thing here that is not this repository's to
/// decide, and a helper that writes what the reader expects proves only that they agree.
public class FunctionTests : IDisposable
{
    readonly TestLake lake = new TestLake("functions")
        .Json("base", "orders", """
            [{"order_id": 1, "amount": 10.5, "note": "first"},
             {"order_id": 2, "amount": 20.0, "note": null}]
            """)
        .Json("base", "lines", """[{"line_id": 10, "order_id": 1, "qty": 5}]""")
        .Stack("base")
        .WriteTo("local")
        .WithTds();

    public FunctionTests()
    {
        lake.Config.Dacpac = Path.Combine(AppContext.BaseDirectory, "Schema", "sample.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    SqlConnection Open()
    {
        var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return connection;
    }

    object? Scalar(string sql)
    {
        using var connection = Open();
        return new SqlCommand(sql, connection).ExecuteScalar();
    }

    /// The body is translated on the tree like any other T-SQL, and `@value` is the macro's own
    /// parameter rather than a value the caller bound.
    [Fact]
    public void AScalarFunctionAnswersOverItsArguments()
    {
        Assert.Equal(42, Scalar("SELECT [dbo].[Doubled](21)"));
    }

    /// What it was declared to return is what it returns. `Small` is declared `SMALLINT` over
    /// arithmetic DuckDB answers as an INTEGER, so without the cast the caller reads the wrong type
    /// -- the same difference that makes COUNT an int in SQL Server and a BIGINT here.
    [Fact]
    public void ItAnswersInTheTypeItDeclared()
    {
        using var connection = Open();
        using var reader = new SqlCommand("SELECT [dbo].[Small](21)", connection).ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(typeof(short), reader.GetFieldType(0));
        Assert.Equal((short)42, reader.GetValue(0));
    }

    /// `+` over text concatenates, `ISNULL` is a coalesce and a CAST to NVARCHAR is a VARCHAR --
    /// every one of them a difference the renderer already knew about.
    [Fact]
    public void TheDialectIsTranslatedInTheBodyToo()
    {
        Assert.Equal("first#1", Scalar("SELECT [dbo].[NoteOf]([note], [order_id]) FROM [orders] WHERE [order_id] = 1"));
        Assert.Equal("none#2", Scalar("SELECT [dbo].[NoteOf]([note], [order_id]) FROM [orders] WHERE [order_id] = 2"));
    }

    /// DuckDB binds a macro when it is created rather than when it is called, so one that calls
    /// another has to be made second. The model lists its elements alphabetically and this one is
    /// named to arrive first, so the order it can be created in is not the order it is read in.
    [Fact]
    public void OneFunctionMayCallAnother()
    {
        Assert.Equal(20, Scalar("SELECT [dbo].[Amplified](5)"));
    }

    [Fact]
    public void AFunctionMayTakeNoArgumentsAtAll()
    {
        Assert.Equal(42, Scalar("SELECT [dbo].[Answer]()"));
    }

    /// A macro is an expression, and a body with a variable and a branch is not one. It is left
    /// undeclared -- and says so at startup -- rather than being half-translated.
    [Fact]
    public void ABodyThatIsNotAnExpressionIsNotPublished()
    {
        var refused = Assert.Throws<SqlException>(() => Scalar("SELECT [dbo].[Bucketed](11)"));
        Assert.Contains("Bucketed", refused.Message);
    }

    /// A declared view may call one, which is only true if the macros are made before the views.
    [Fact]
    public void ADeclaredViewMayCallOne()
    {
        Assert.Equal(["2", "4"], lake.Query("SELECT doubled FROM lake.doubled_orders ORDER BY order_id"));
    }

    /// The other front door reaches the same macro, by the name the lake publishes it under.
    [Fact]
    public void TheOtherFrontDoorReachesItToo()
    {
        Assert.Equal(["42"], lake.Query("""SELECT lake."Doubled"(21)"""));
    }

    /// Whatever else the real dacpac carries is still read: the shape did not change underneath the
    /// readers that were written against the suite's own writer.
    [Fact]
    public void TheRestOfTheModelIsReadFromTheSameFile()
    {
        // Declared column order and types, the primary key, and an identity that draws from a
        // sequence -- all off DacFx's own output.
        Assert.Equal(["order_id:integer", "amount:numeric", "note:text"], lake.Columns("lake.orders"));

        using var connection = Open();
        new SqlCommand("INSERT INTO [lines] ([order_id], [qty]) VALUES (1, 7)", connection).ExecuteNonQuery();
        Assert.Equal(["10", "11"], lake.Query("SELECT line_id FROM lake.lines ORDER BY line_id"));

        // And the cascade the model declares, which is only read correctly if OnDeleteAction is.
        new SqlCommand("DELETE FROM [orders] WHERE [order_id] = 1", connection).ExecuteNonQuery();
        Assert.Empty(lake.Query("SELECT line_id FROM lake.lines"));
    }
}
