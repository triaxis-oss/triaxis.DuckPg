using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;
using DuckDB.NET.Data.DataChunk.Reader;
using DuckDB.NET.Native;
using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg;

/// Everything the row writer asks of a result. Small on purpose: what DuckDB hands back is a
/// `DbDataReader`, and a reordered one is not worth forty members of stand-in to be. Disposable
/// because a reordered one rents its arrays; a reader as it comes owns nothing and does nothing.
interface IRows : IDisposable
{
    int FieldCount { get; }
    string GetName(int ordinal);
    string GetDataTypeName(int ordinal);
    DataTable? Schema { get; }
    bool Read();
    bool IsDBNull(int ordinal);
    object GetValue(int ordinal);

    /// The CLR type a column comes back as, and a read of it in that type. Together they are what
    /// lets a protocol pick how to write a column once and then never put a value in an `object`.
    Type GetFieldType(int ordinal);
    T Get<T>(int ordinal);
}

/// A reader as it comes, which is what every statement but a reordered one uses.
sealed class ReaderRows(DbDataReader reader) : IRows
{
    public int FieldCount => reader.FieldCount;
    public string GetName(int ordinal) => reader.GetName(ordinal);
    public string GetDataTypeName(int ordinal) => reader.GetDataTypeName(ordinal);
    public DataTable? Schema => reader.GetSchemaTable();
    public bool Read() => reader.Read();
    public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
    public object GetValue(int ordinal) => reader.GetValue(ordinal);
    public Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
    public T Get<T>(int ordinal) => reader.GetFieldValue<T>(ordinal);
    public void Dispose() { }
}

/// A result handed back in another order. Only ever built for a table the catalog counted and found
/// small, so what it holds is bounded by something known before the statement went out.
///
/// What it holds is an `int[]` of positions and the sort keys, and nothing else: a materialized
/// result already sits in DuckDB's own vectors, and `Chunk` reads a row straight out of them when
/// that row is written. Which is why the width of a table costs nothing here -- a hundred columns
/// nobody sorts by are never touched until the rows that survived the limit go out.
sealed class SortedRows : IRows
{
    readonly DbDataReader reader;
    readonly IValues values;
    readonly string[] names;
    readonly string[] types;
    readonly Type[] fields;
    readonly Sorted[] keys;

    /// Rented, so `count` rather than `order.Length` is how many rows there are -- a pool hands back
    /// an array at least as long as it was asked for, not one exactly that long.
    readonly int[] order;
    readonly int count;
    int at = -1;

    SortedRows(DbDataReader reader, IValues values, string[] names, string[] types, Type[] fields,
               Sorted[] keys, int[] order, int count) =>
        (this.reader, this.values, this.names, this.types, this.fields, this.keys, this.order, this.count) =
        (reader, values, names, types, fields, keys, order, count);

    public static SortedRows Of(DbDataReader reader, Reorder reorder)
    {
        var count = reader.FieldCount;
        var names = new string[count];
        var types = new string[count];
        var fields = new Type[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = reader.GetName(i);
            types[i] = reader.GetDataTypeName(i);
            fields[i] = reader.GetFieldType(i);
        }

        var keys = new Sorted[reorder.Keys.Length];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = Sorted.For(fields[reorder.Keys[i]], reorder.Rows);

        var values = Values.Of(reader, reorder.Rows);
        for (var i = 0; i < keys.Length; i++) keys[i].Fill(values, reorder.Keys[i]);

        var order = Order(values, keys, reorder);
        var limit = reorder.Limit is { } take ? Math.Min(take, values.Rows) : values.Rows;
        return new SortedRows(reader, values, names, types, fields, keys, order, limit);
    }

    /// Which position goes out where. The rows themselves never move: sorting an `int[]` touches one
    /// array however wide the result is, and the limit is where reading it stops.
    static int[] Order(IValues values, Sorted[] keys, Reorder reorder)
    {
        var order = ArrayPool<int>.Shared.Rent(values.Rows);
        for (var i = 0; i < values.Rows; i++) order[i] = i;

        var descending = reorder.Descending;
        // A span, because the rented array is longer than the result -- and because sorting one takes
        // the comparison as a delegate, where `Array.Sort` over a range would want an `IComparer`.
        order.AsSpan(0, values.Rows).Sort((left, right) =>
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var compared = keys[i].Compare(left, right, descending[i]);
                if (compared != 0) return compared;
            }
            return 0;
        });

        return order;
    }

    public int FieldCount => names.Length;
    public string GetName(int ordinal) => names[ordinal];
    public string GetDataTypeName(int ordinal) => types[ordinal];

    /// Asked for only when a column came back as a DECIMAL, whose precision and scale TDS has to
    /// declare and DuckDB reports nowhere else -- so the reader is kept rather than the table, which
    /// costs 0.16 ms to build on a hundred columns and is thrown away unread on almost all of them.
    /// It still answers once the rows have been read, since it describes the result and not a row.
    public DataTable? Schema => reader.GetSchemaTable();

    public bool Read() => ++at < count;
    public bool IsDBNull(int ordinal) => values.IsNull(ordinal, order[at]);
    public object GetValue(int ordinal) => values.Value(ordinal, order[at]) ?? DBNull.Value;
    public Type GetFieldType(int ordinal) => fields[ordinal];
    public T Get<T>(int ordinal) => values.Key<T>(ordinal, order[at]);

    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(order);
        foreach (var key in keys) key.Dispose();
    }
}

/// A result's values, addressed by column and position rather than read in order.
interface IValues
{
    int Rows { get; }
    bool IsNull(int column, int row);
    object? Value(int column, int row);
    T Key<T>(int column, int row);
}

static class Values
{
    /// A materialized result is chunks of at most 2048 rows and the reader keeps only the one it is
    /// on, so reading a row by position is the chunk's to answer and only while it is the current
    /// one. A table this path is taken for holds at most `FastOrder.Small` rows and comes back as a
    /// single chunk, which is what makes that chunk the whole result; a scan wide enough to run in
    /// parallel splits into many, so the count is asked before a row is read and the copy answers
    /// instead. Nothing has been consumed at that point, which is what lets the choice be made here.
    public static IValues Of(DbDataReader reader, int rows) =>
        reader is DuckDBDataReader duck && Chunk.Whole(duck) is { } chunk ? chunk : new Copy(reader, rows);
}

/// The rows where DuckDB left them.
sealed class Chunk(IDuckDBDataReader[] vectors, int rows) : IValues
{
    /// The vector readers are an array of a type this cannot name, so they are reached by reflection
    /// rather than by `UnsafeAccessor`, which needs a field's exact type -- and `UnsafeAccessorType`
    /// refuses an inaccessible array element. A literal `typeof` and a literal name is a shape the
    /// trimmer resolves and keeps the field for, so this stays AOT-clean; what it has to survive is
    /// the driver renaming the field, which is why a miss falls back rather than throwing.
    static readonly FieldInfo? vectorReaders = typeof(DuckDBDataReader)
        .GetField("vectorReaders", BindingFlags.NonPublic | BindingFlags.Instance);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentResult")]
    extern static ref DuckDBResult Result(DuckDBDataReader reader);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkRowCount")]
    extern static ref ulong Length(DuckDBDataReader reader);

    /// The whole result as one chunk, or nothing when it is not one.
    public static Chunk? Whole(DuckDBDataReader reader)
    {
        if (vectorReaders is null || NativeMethods.Types.DuckDBResultChunkCount(Result(reader)) != 1) return null;

        // The chunk is loaded by the first read and stays until the reader is asked past it, which
        // nothing here does -- every row after this one is taken by position.
        if (!reader.Read()) return new Chunk([], 0);
        return vectorReaders.GetValue(reader) is IDuckDBDataReader[] vectors
            ? new Chunk(vectors, (int)Length(reader))
            : null;
    }

    public int Rows => rows;
    public bool IsNull(int column, int row) => !vectors[column].IsValid((ulong)row);
    public object? Value(int column, int row) => IsNull(column, row) ? null : vectors[column].GetValue((ulong)row);
    public T Key<T>(int column, int row) => vectors[column].GetValue<T>((ulong)row);
}

/// The rows read out, for a result the chunk cannot answer for. Boxed and row-major, since this is
/// what a result too split to address in place costs and nothing on the ordinary path reaches it.
sealed class Copy : IValues
{
    readonly List<object?[]> rows;

    public Copy(DbDataReader reader, int expected)
    {
        rows = new List<object?[]>(expected);
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
    }

    public int Rows => rows.Count;
    public bool IsNull(int column, int row) => rows[row][column] is null;
    public object? Value(int column, int row) => rows[row][column];
    public T Key<T>(int column, int row) => (T)rows[row][column]!;
}

/// A sort key, taken once into an array of its own type: reading a value out of a vector costs ~30 ns
/// whichever way it is asked for, and a sort asks for each one about `log n` times.
abstract class Sorted : IDisposable
{
    public abstract void Fill(IValues values, int column);
    public abstract void Dispose();

    /// Where one row falls against another by this key alone. Nulls last whichever way the column is
    /// sorted, which is what DuckDB's `default_null_order` means -- so the direction is taken here
    /// rather than by negating an answer that already placed them.
    public abstract int Compare(int left, int right, bool descending);

    /// The types `FastOrder.Sortable` lets through, each with an array of its own; nothing else can
    /// be a key, and the fallback is what keeps that from being a crash if one ever is.
    public static Sorted For(Type type, int rows) =>
        type == typeof(int) ? new Sorted<int>(rows) :
        type == typeof(long) ? new Sorted<long>(rows) :
        type == typeof(short) ? new Sorted<short>(rows) :
        type == typeof(sbyte) ? new Sorted<sbyte>(rows) :
        type == typeof(uint) ? new Sorted<uint>(rows) :
        type == typeof(ulong) ? new Sorted<ulong>(rows) :
        type == typeof(ushort) ? new Sorted<ushort>(rows) :
        type == typeof(byte) ? new Sorted<byte>(rows) :
        type == typeof(BigInteger) ? new Sorted<BigInteger>(rows) :
        type == typeof(float) ? new Real<float>(rows) :
        type == typeof(double) ? new Real<double>(rows) :
        type == typeof(decimal) ? new Sorted<decimal>(rows) :
        type == typeof(DateOnly) ? new Sorted<DateOnly>(rows) :
        type == typeof(TimeOnly) ? new Sorted<TimeOnly>(rows) :
        type == typeof(DateTime) ? new Sorted<DateTime>(rows) :
        type == typeof(TimeSpan) ? new Sorted<TimeSpan>(rows) :
        new Boxed(rows);
}

class Sorted<T>(int rows) : Sorted where T : IComparable<T>
{
    T[] values = ArrayPool<T>.Shared.Rent(rows);
    bool[] nulls = ArrayPool<bool>.Shared.Rent(rows);

    /// Rented against the count the catalog holds, so renting again is the answer to that count being
    /// wrong rather than something the ordinary case does. Every slot up to the result's length is
    /// written here, which is what makes a pooled array safe to read: nothing keeps what was in it.
    public override void Fill(IValues source, int column)
    {
        if (source.Rows > values.Length)
        {
            Dispose();
            values = ArrayPool<T>.Shared.Rent(source.Rows);
            nulls = ArrayPool<bool>.Shared.Rent(source.Rows);
        }

        for (var i = 0; i < source.Rows; i++)
            if (!(nulls[i] = source.IsNull(column, i))) values[i] = source.Key<T>(column, i);
    }

    /// Cleared on the way back, since a key may be a reference and the pool outlives the result.
    public override void Dispose()
    {
        ArrayPool<T>.Shared.Return(values, clearArray: true);
        ArrayPool<bool>.Shared.Return(nulls);
    }

    public override int Compare(int left, int right, bool descending) =>
        nulls[left] ? (nulls[right] ? 0 : 1)
        : nulls[right] ? -1
        : descending ? Order(values[right], values[left]) : Order(values[left], values[right]);

    protected virtual int Order(T left, T right) => left.CompareTo(right);
}

/// DuckDB sorts NaN as the largest value there is -- ahead of infinity, behind only a null, and it
/// flips with the direction the way any other value does. .NET's `CompareTo` puts it below negative
/// infinity, so a column holding one comes back in an order DuckDB would never have produced.
sealed class Real<T>(int rows) : Sorted<T>(rows) where T : INumberBase<T>, IComparable<T>
{
    protected override int Order(T left, T right) =>
        T.IsNaN(left) ? (T.IsNaN(right) ? 0 : 1)
        : T.IsNaN(right) ? -1
        : left.CompareTo(right);
}

sealed class Boxed(int rows) : Sorted
{
    object?[] values = ArrayPool<object?>.Shared.Rent(rows);

    public override void Fill(IValues source, int column)
    {
        if (source.Rows > values.Length)
        {
            Dispose();
            values = ArrayPool<object?>.Shared.Rent(source.Rows);
        }
        for (var i = 0; i < source.Rows; i++) values[i] = source.Value(column, i);
    }

    public override void Dispose() => ArrayPool<object?>.Shared.Return(values, clearArray: true);

    public override int Compare(int left, int right, bool descending) =>
        values[left] is not { } a ? (values[right] is null ? 0 : 1)
        : values[right] is not { } b ? -1
        : descending ? Comparer<object>.Default.Compare(b, a) : Comparer<object>.Default.Compare(a, b);
}
