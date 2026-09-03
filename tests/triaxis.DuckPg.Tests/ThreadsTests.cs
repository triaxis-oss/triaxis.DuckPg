using DuckDB.NET.Data;

namespace triaxis.DuckPg.Tests;

/// `threads` is DuckDB's own setting and global to the database, so what a lake serves with is
/// what it was configured to -- and the one thread its catalog was built on is not left behind.
public class ThreadsTests
{
    [Fact]
    public void TheLakeServesWithTheThreadsItWasGiven()
    {
        using var lake = new TestLake().Json("base", "orders", """[{"order_id": 1}]""").Stack("base");
        lake.Config.Threads = 2;
        lake.Start();

        Assert.Equal(["2"], lake.Query("SELECT current_setting('threads')"));
    }

    /// Left alone, a lake serves with DuckDB's own default, whatever its build ran on.
    [Fact]
    public void TheBuildsOneThreadIsNotWhatItServesWith()
    {
        DuckDbLibrary.Register();
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var command = duck.CreateCommand();
        command.CommandText = "SELECT current_setting('threads')";
        var own = Convert.ToString(command.ExecuteScalar()) ?? "";

        using var lake = new TestLake().Json("base", "orders", """[{"order_id": 1}]""").Stack("base");
        lake.Start();

        Assert.Equal([own], lake.Query("SELECT current_setting('threads')"));
    }

    [Fact]
    public void NoThreadsAtAllIsRefused()
    {
        var config = new Config { Listen = "127.0.0.1:0", Threads = 0 };
        Assert.Throws<DuckPgConfigurationException>(config.Validate);
    }
}
