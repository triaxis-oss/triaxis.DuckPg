using System.Numerics;
using System.Text;
using static triaxis.DuckPg.TdsTypes;

namespace triaxis.DuckPg;

/// How one column's values are written, decided once for a result rather than per row. Both halves
/// of the decision are already fixed by the column's DuckDB type -- which TDS token it goes out as,
/// and which CLR type the reader hands it back in -- so choosing here is what lets the value be read
/// in its own type and written without ever being an `object`.
///
/// `Objects` is what a type this does not know falls to, and it is where the old behaviour lives:
/// read boxed, converted by whatever it turns out to be. Nothing DuckDB commonly returns reaches it.
abstract class TdsField
{
    public void Write(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload)
    {
        if (rows.IsDBNull(ordinal)) WriteNull(msg, column);
        else Value(msg, column, rows, ordinal, payload);
    }

    protected abstract void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload);

    /// The token decides the encoding and the CLR type decides the read, and they agree by
    /// construction -- both come from the one DuckDB type the column was described from.
    public static TdsField For(Type clr, TdsColumn column) => column.Token switch
    {
        BitN when clr == typeof(bool) => new Bools(),

        IntN when clr == typeof(sbyte) => new Ints<sbyte>(),
        IntN when clr == typeof(byte) => new Ints<byte>(),
        IntN when clr == typeof(short) => new Ints<short>(),
        IntN when clr == typeof(ushort) => new Ints<ushort>(),
        IntN when clr == typeof(int) => new Ints<int>(),
        IntN when clr == typeof(uint) => new Ints<uint>(),
        IntN when clr == typeof(long) => new Ints<long>(),

        FloatN when clr == typeof(float) => new Singles(),
        FloatN when clr == typeof(double) => new Doubles(),

        // A HUGEINT and an unsigned BIGINT are both declared DECIMAL(38,0), since no SQL Server
        // integer is wide enough -- so they go out as the integers they already are, unscaled.
        DecimalN or NumericN when clr == typeof(decimal) => new Decimals(),
        DecimalN or NumericN when clr == typeof(BigInteger) => new Huge(),
        DecimalN or NumericN when clr == typeof(ulong) => new Unsigned(),

        TdsTypes.Guid when clr == typeof(Guid) => new Uuids(),
        Date when clr == typeof(DateOnly) => new Dates(),
        Time when clr == typeof(TimeOnly) => new Times(),
        Time when clr == typeof(TimeSpan) => new Spans(),
        DateTime2 when clr == typeof(DateTime) => new Stamps(),
        TdsTypes.DateTimeOffset when clr == typeof(DateTimeOffset) => new Zoned(),
        TdsTypes.DateTimeOffset when clr == typeof(DateTime) => new ZonedStamps(),

        // A string and a blob are references already, so nothing is saved by typing them -- only a
        // string is here at all, and only to skip the type test the object path would do per value.
        NVarChar when clr == typeof(string) => new Texts(),

        _ => new Objects(),
    };


    sealed class Bools : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            msg.U8(1).U8(rows.Get<bool>(ordinal) ? (byte)1 : (byte)0);
    }

    /// Every integer DuckDB has that fits one, widened to the length the column declared. One class for
    /// all of them: `IBinaryInteger` is what makes the widening a typed conversion rather than a boxed
    /// `Convert.ToInt64`.
    sealed class Ints<T> : TdsField where T : IBinaryInteger<T>
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteInteger(msg, column.Length, long.CreateChecked(rows.Get<T>(ordinal)));
    }

    sealed class Singles : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            msg.U8(4).F32(rows.Get<float>(ordinal));
    }

    sealed class Doubles : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            msg.U8(8).F64(rows.Get<double>(ordinal));
    }

    sealed class Decimals : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteDecimal(msg, column, rows.Get<decimal>(ordinal));
    }

    sealed class Huge : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteInteger(msg, column, rows.Get<BigInteger>(ordinal));
    }

    sealed class Unsigned : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteInteger(msg, column, new BigInteger(rows.Get<ulong>(ordinal)));
    }

    sealed class Uuids : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            msg.U8(16).Raw(rows.Get<Guid>(ordinal).ToByteArray());
    }

    sealed class Dates : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteDate(msg, rows.Get<DateOnly>(ordinal));
    }

    sealed class Times : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteTime(msg, rows.Get<TimeOnly>(ordinal).ToTimeSpan());
    }

    sealed class Spans : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteTime(msg, rows.Get<TimeSpan>(ordinal));
    }

    sealed class Stamps : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteDateTime2(msg, rows.Get<DateTime>(ordinal));
    }

    sealed class Zoned : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteDateTimeOffset(msg, rows.Get<DateTimeOffset>(ordinal));
    }

    sealed class ZonedStamps : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteDateTimeOffset(msg, new DateTimeOffset(rows.Get<DateTime>(ordinal), TimeSpan.Zero));
    }

    sealed class Texts : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WritePlp(msg, Encoding.Unicode.GetBytes(rows.Get<string>(ordinal)), payload);
    }

    /// Anything with no typed pair: read as it comes and converted from whatever it turns out to be,
    /// which is what every column did before.
    sealed class Objects : TdsField
    {
        protected override void Value(TdsMsg msg, TdsColumn column, IRows rows, int ordinal, int payload) =>
            WriteValue(msg, column, rows.GetValue(ordinal), payload);
    }
}
