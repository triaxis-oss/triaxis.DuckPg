using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// The SQL Server functions duckpg answers in .NET rather than in SQL, because their meaning is
/// .NET's: a hash over UTF-16 text, and the date formats a CONVERT style names.
public class HostFunctionTests : IDisposable
{
    readonly TestLake lake = new TestLake("host")
        .Json("base", "people", """[{"id": 1, "name": "ada"}]""")
        .Stack("base")
        .WithTds();

    public HostFunctionTests() => lake.Start();

    public void Dispose() => lake.Dispose();

    static object? Scalar(TestLake lake, string sql)
    {
        using var connection = new SqlConnection(lake.SqlConnectionString());
        connection.Open();
        return new SqlCommand(sql, connection).ExecuteScalar();
    }

    /// The salt is in the hash, so the same password hashes differently every time and only a
    /// comparison against the stored hash can answer.
    [Fact]
    public void APasswordHashVerifiesAgainstItself()
    {
        var hash = (byte[])Scalar(lake, "SELECT pwdencrypt('correct horse')")!;

        Assert.Equal(70, hash.Length);
        Assert.Equal([0x02, 0x00], hash[..2]);
        Assert.NotEqual(hash, (byte[])Scalar(lake, "SELECT pwdencrypt('correct horse')")!);

        Assert.True((bool)Scalar(lake, $"SELECT pwdcompare('correct horse', {Literal(hash)})")!);
        Assert.False((bool)Scalar(lake, $"SELECT pwdcompare('battery staple', {Literal(hash)})")!);
    }

    /// The shape is SQL Server's, so a hash written by one verifies against the other. This is what
    /// one of its own looks like: version, salt, and SHA-512 of the password's UTF-16 bytes and salt.
    [Fact]
    public void AHashSqlServerWroteVerifiesHere()
    {
        byte[] salt = [0xDE, 0xAD, 0xBE, 0xEF];
        var hash = (byte[])[0x02, 0x00, .. salt,
            .. System.Security.Cryptography.SHA512.HashData(
                [.. System.Text.Encoding.Unicode.GetBytes("hunter2"), .. salt])];

        Assert.True((bool)Scalar(lake, $"SELECT pwdcompare('hunter2', {Literal(hash)})")!);
    }

    /// Anything that is not a hash of a shape this knows is said to be one, rather than answered
    /// `false` -- which would read as the wrong password rather than as a hash nobody understands.
    [Fact]
    public void SomethingThatIsNotAHashIsRefused()
    {
        var refused = Assert.Throws<SqlException>(() => Scalar(lake, "SELECT pwdcompare('x', 0x1234)"));
        Assert.Contains("not a password hash", refused.Message);
    }

    [Theory]
    [InlineData(120, "2026-08-06 14:03:09")]
    [InlineData(121, "2026-08-06 14:03:09.250")]
    [InlineData(112, "20260806")]
    [InlineData(103, "06/08/2026")]
    [InlineData(101, "08/06/2026")]
    [InlineData(126, "2026-08-06T14:03:09.250")]
    public void AStyleIsTheDateFormatItNames(int style, string expected) =>
        Assert.Equal(expected, Scalar(lake,
            $"SELECT CONVERT(nvarchar(30), CAST('2026-08-06 14:03:09.25' AS datetime2), {style})"));

    /// And the same style read back the other way, which is what it is for: text a lake was given
    /// in a format the caller names.
    [Fact]
    public void AStyleReadsTheTextItDescribes()
    {
        Assert.Equal(new DateTime(2026, 8, 6), Scalar(lake, "SELECT CONVERT(datetime, '06/08/2026', 103)"));
        Assert.Equal(new DateTime(2026, 8, 6), Scalar(lake, "SELECT CONVERT(datetime, '20260806', 112)"));

        var refused = Assert.Throws<SqlException>(() =>
            Scalar(lake, "SELECT CONVERT(datetime, '6 August 2026', 112)"));
        Assert.Contains("not a date in style", refused.Message);
    }

    /// A style nobody implemented is named rather than approximated: the wrong format applied
    /// quietly is a date read as another date.
    [Fact]
    public void AStyleNobodyKnowsIsRefused()
    {
        var refused = Assert.Throws<SqlException>(() =>
            Scalar(lake, "SELECT CONVERT(nvarchar(30), CAST('2026-08-06' AS datetime2), 131)"));
        Assert.Contains("style 131", refused.Message);
    }

    /// Registered on the database, so a session that connected long after startup finds them, and
    /// so does the other front door.
    [Fact]
    public void BothFrontDoorsFindThem()
    {
        Assert.Equal(["2026-08-06"],
            lake.Query("SELECT duckpg_convert_to_text(TIMESTAMP '2026-08-06 14:03:09', 23)"));
        Assert.Equal("2026-08-06", Scalar(lake,
            "SELECT CONVERT(nvarchar(30), CAST('2026-08-06 14:03:09' AS datetime2), 23)"));
    }

    static string Literal(byte[] value) => "0x" + Convert.ToHexString(value);
}
