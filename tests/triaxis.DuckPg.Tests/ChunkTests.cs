using DuckDB.NET.Data;
using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg.Tests;

/// The fast path reads rows straight out of DuckDB's vectors, which it can only do while the chunk
/// holding them is the reader's current one. Everything above answers the same either way, so these
/// ask which way it went -- a driver that renames the field the vectors are reached through would
/// otherwise cost the whole point of the path and pass every other test.
public class ChunkTests
{
    static DuckDBConnection Rows(int rows)
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var make = connection.CreateCommand();
        make.CommandText =
            $"CREATE TABLE t AS SELECT i::INTEGER k, ('n' || i) v, CASE WHEN i % 3 = 0 THEN NULL ELSE i END n " +
            $"FROM range({rows}) s(i)";
        make.ExecuteNonQuery();
        return connection;
    }

    static IValues Values(DuckDBConnection connection, int rows)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM t";
        return triaxis.DuckPg.Values.Of(command.ExecuteReader(), rows);
    }

    /// A table small enough for the fast path comes back as one chunk, which is what makes reading a
    /// row by position the whole result rather than part of it.
    [Fact]
    public void ASmallResultIsAddressedWhereDuckDbLeftIt()
    {
        using var connection = Rows((int)FastOrder.Small);
        var values = Values(connection, (int)FastOrder.Small);

        Assert.IsType<Chunk>(values);
        Assert.Equal(FastOrder.Small, values.Rows);
        Assert.Equal(999, values.Key<int>(0, 999));
        Assert.Equal("n999", values.Value(1, 999));
        Assert.True(values.IsNull(2, 999));
        Assert.False(values.IsNull(2, 998));
    }

    /// Past a chunk the reader keeps only the one it is on, so the rows have to be taken out. Nothing
    /// on the ordinary path reaches this, since a table that big never qualifies -- but a result that
    /// is split for any other reason must not come back short.
    [Fact]
    public void AResultOfSeveralChunksIsReadOutInstead()
    {
        using var connection = Rows(5000);
        var values = Values(connection, 5000);

        Assert.IsType<Copy>(values);
        Assert.Equal(5000, values.Rows);
        Assert.Equal(4999, values.Key<int>(0, 4999));
        Assert.Equal("n4999", values.Value(1, 4999));
        Assert.True(values.IsNull(2, 4998));
    }
}
