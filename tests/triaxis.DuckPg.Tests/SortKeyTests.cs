using DuckDB.NET.Data;

namespace triaxis.DuckPg.Tests;

/// A sort key has to order the way DuckDB orders it, or the fast path answers a question DuckDB was
/// asked and did not answer. `OrderingTests` compares the two paths over a lake, which is the real
/// guarantee -- and cannot reach a value no layer file can hold. NaN is one of those.
public class SortKeyTests
{
    /// DuckDB puts NaN ahead of infinity and behind only a null; .NET's `CompareTo` puts it below
    /// negative infinity. So a column holding one is the case where the two orders come apart, and
    /// they come apart in both directions.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARealOrdersTheWayDuckDbOrders(bool descending)
    {
        double[] values = [double.NaN, 1.0, double.NegativeInfinity, 0.0, double.PositiveInfinity, -1.0];

        var key = Sorted.For(typeof(double), values.Length);
        key.Fill(new Given(values), 0);
        var here = Enumerable.Range(0, values.Length).ToArray();
        Array.Sort(here, (left, right) => key.Compare(left, right, descending));

        Assert.Equal(Duck(values, descending), here);
    }

    /// The same values, ordered by DuckDB, as positions into the list they were given in.
    static int[] Duck(double[] values, bool descending)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        var rows = string.Join(",", values.Select((value, at) => $"({at}, {Literal(value)})"));
        command.CommandText =
            $"SELECT pos FROM (VALUES {rows}) v(pos, d) ORDER BY d{(descending ? " DESC" : "")}";

        using var reader = command.ExecuteReader();
        var order = new List<int>();
        while (reader.Read()) order.Add(reader.GetInt32(0));
        return [.. order];
    }

    static string Literal(double value) =>
        double.IsNaN(value) ? "'nan'::DOUBLE"
        : double.IsPositiveInfinity(value) ? "'inf'::DOUBLE"
        : double.IsNegativeInfinity(value) ? "'-inf'::DOUBLE"
        : $"{value:R}::DOUBLE";

    /// Values straight from an array, so a key can be filled without a result to read them out of.
    sealed class Given(double[] values) : IValues
    {
        public int Rows => values.Length;
        public bool IsNull(int column, int row) => false;
        public object? Value(int column, int row) => values[row];
        public T Key<T>(int column, int row) => (T)(object)values[row];
    }
}
