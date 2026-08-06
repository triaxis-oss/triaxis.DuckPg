using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// A `bit` is a BOOLEAN here, and T-SQL converts one to an integer to do arithmetic with it while
/// DuckDB refuses the operator. The conversion is written where the column can be resolved, so it
/// is a conversion rather than a guess.
public class BitTests : IDisposable
{
    readonly TestLake lake = new TestLake("bit")
        .Json("base", "obligations", """
            [{"pKey": 3, "NotificationRole": true, "weight": 2.5},
             {"pKey": 5, "NotificationRole": false, "weight": 1.5}]
            """)
        .Json("base", "roles", """[{"pKey": 3, "NotificationRole": true}]""")
        .Stack("base")
        .WithTds();

    public BitTests() => lake.Start();

    public void Dispose() => lake.Dispose();

    List<string> Rows(string sql)
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        using var reader = new SqlCommand(sql, connection).ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString())));
        return rows;
    }

    /// The reduced cases, and the aggregate the real query wraps them in.
    [Fact]
    public void ABitDoesArithmeticAsAnInteger()
    {
        Assert.Equal(["3", "0"], Rows("SELECT [pKey] * [NotificationRole] FROM [obligations] ORDER BY [pKey]"));
        Assert.Equal(["2", "1"], Rows("SELECT [NotificationRole] + 1 FROM [obligations] ORDER BY [pKey]"));
        Assert.Equal(["3,0"], Rows(
            "SELECT STRING_AGG(o.[pKey] * o.[NotificationRole], ',') FROM [obligations] AS o"));
    }

    /// A qualified reference is resolved under its qualifier, and one table's `bit` does not make
    /// another table's column of that name into one.
    [Fact]
    public void TheColumnIsResolvedBeforeItIsConverted()
    {
        Assert.Equal(["9"], Rows(
            "SELECT o.[pKey] * r.[NotificationRole] * 3 FROM [obligations] AS o " +
            "INNER JOIN [roles] AS r ON r.[pKey] = o.[pKey]"));

        // A column that is not a bit is left alone: converting one would truncate it.
        Assert.Equal(["7.5", "7.5"], Rows("SELECT [pKey] * [weight] FROM [obligations] ORDER BY [pKey]"));
    }

    /// Everything else a bit reaches already agreed between the two, and stays untouched.
    [Fact]
    public void OnlyArithmeticNeedsIt()
    {
        Assert.Equal(["1"], Rows("SELECT SUM(CAST([NotificationRole] AS int)) FROM [obligations]"));
        Assert.Equal(["3"], Rows("SELECT [pKey] FROM [obligations] WHERE [NotificationRole] = 1"));
        Assert.Equal(["yes", "no"], Rows(
            "SELECT CASE WHEN [NotificationRole] = 1 THEN 'yes' ELSE 'no' END FROM [obligations] ORDER BY [pKey]"));
        // Concatenation is still concatenation: `+` over text is not arithmetic.
        Assert.Equal(["x3", "x5"], Rows("SELECT 'x' + CAST([pKey] AS nvarchar) FROM [obligations] ORDER BY [pKey]"));
    }
}
