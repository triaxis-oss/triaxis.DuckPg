using Microsoft.Data.SqlClient;

namespace triaxis.DuckPg.Tests;

/// Sessions have a DuckDB connection each, but a lake keeps one of its own for the work that is
/// nobody's session -- persisting a table, seeding a sequence, rebuilding the catalog. A DuckDB
/// connection is not two threads' to share, so what runs on that one has to be serialised: what
/// concurrent writers see otherwise is not an error but a connection that dies mid-answer.
public class ConcurrencyTests
{
    const int Tables = 12, Writers = 8, Each = 8;

    static Dacpac.TableModel Table(int i) =>
        new($"t{i}", [("id", "int"), ("amount", "int")], ["id"], Identity: ["id"]);

    [Fact]
    public void ConcurrentWritersDoNotCollideOnTheLakesOwnConnection()
    {
        using var lake = new TestLake()
            .Stack("base")
            .WriteTo("local")
            .WithTds();

        // Every table is writable and carries nothing, so a first write to each has to promote it --
        // which seeds the sequence its identity draws from, on the lake's own connection.
        foreach (var i in Enumerable.Range(0, Tables))
            lake.Json("base", $"t{i}", $$"""[{"id": {{i + 1}}, "amount": 0}]""");

        Dacpac.Write(lake.At("schema", "test.dacpac"), [.. Enumerable.Range(0, Tables).Select(Table)]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, Writers, writer =>
        {
            try
            {
                using var connection = new SqlConnection(lake.SqlConnectionString());
                connection.Open();

                for (var round = 0; round < Each; round++)
                {
                    // A table nobody has written to yet, and one everybody is writing to: a
                    // promotion racing the persists of every other writer.
                    var table = $"t{1 + (writer * Each + round) % (Tables - 1)}";
                    new SqlCommand($"INSERT INTO [{table}] ([amount]) VALUES ({writer})", connection)
                        .ExecuteNonQuery();
                    new SqlCommand("INSERT INTO [t0] ([amount]) VALUES (1)", connection).ExecuteNonQuery();
                    new SqlCommand($"SELECT COUNT(*) FROM [{table}]", connection).ExecuteScalar();
                }
            }
            catch (Exception e)
            {
                failures.Add(e.Message.ReplaceLineEndings(" "));
            }
        });

        Assert.Empty(failures);

        // Every write landed, and the shared table got one row from every round of every writer.
        Assert.Equal([$"{1 + Writers * Each}"], lake.Query("SELECT COUNT(*) FROM lake.t0"));
    }
}
