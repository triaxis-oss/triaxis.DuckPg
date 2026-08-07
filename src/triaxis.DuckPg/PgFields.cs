using System.Data.Common;
using System.Numerics;
using static triaxis.DuckPg.PgTypes;

namespace triaxis.DuckPg;

/// How one column's values go onto the PostgreSQL wire, decided once for a result rather than per
/// row -- the same move `TdsField` makes, and for the same reason: the OID and the CLR type the
/// reader hands a column back in are both fixed by its DuckDB type, so a value can be read in its
/// own type instead of through an `object`.
///
/// A column asked for in text and one asked for in binary are different fields, since the client
/// chooses per column and the encoding is all that differs.
abstract class PgField
{
    public abstract void Write(Msg msg, DbDataReader reader, int ordinal);

    public static PgField For(Type clr, int oid, bool binary) =>
        binary ? Binary(clr, oid) : Text(clr);

    static PgField Binary(Type clr, int oid) => oid switch
    {
        Bool when clr == typeof(bool) => new Bools(),

        // The OID says how wide the integer goes out, the CLR type says how it is read, and DuckDB's
        // unsigned types are published one size up -- so the two do not always match.
        Int2 or Int4 or Int8 => Integers(clr, oid) ?? Rendered(clr),

        Float4 when clr == typeof(float) => new Singles(),
        Float8 when clr == typeof(double) => new Doubles(),

        Numeric when clr == typeof(decimal) => new Decimals(),
        Numeric when clr == typeof(BigInteger) => new Huge(),
        Numeric when clr == typeof(ulong) => new Unsigned(),

        Uuid when clr == typeof(Guid) => new Uuids(),
        Date when clr == typeof(DateOnly) => new Days(),
        Timestamp or TimestampTz when clr == typeof(DateTime) => new Stamps(),
        Timestamp or TimestampTz when clr == typeof(DateTimeOffset) => new Zoned(),
        Time when clr == typeof(TimeOnly) => new Clock(),
        Time when clr == typeof(TimeSpan) => new Spans(),
        Interval when clr == typeof(TimeSpan) => new Intervals(),

        // A blob goes out as its bytes rather than as its rendering, and arrives as a reference
        // either way -- so this one reads untyped and still never boxes.
        Bytea => new Bytes(),

        // Text, JSON and the JSON-rendered nested types, none of which is a value type either.
        _ => Rendered(clr),
    };

    static PgField? Integers(Type clr, int oid) =>
        clr == typeof(sbyte) ? new Ints<sbyte>(oid) :
        clr == typeof(byte) ? new Ints<byte>(oid) :
        clr == typeof(short) ? new Ints<short>(oid) :
        clr == typeof(ushort) ? new Ints<ushort>(oid) :
        clr == typeof(int) ? new Ints<int>(oid) :
        clr == typeof(uint) ? new Ints<uint>(oid) :
        clr == typeof(long) ? new Ints<long>(oid) :
        null;

    /// The text rendering, which binary falls back to for everything neither side encodes.
    static PgField Text(Type clr) =>
        clr == typeof(string) ? new Strings() :
        clr == typeof(bool) ? new Flags() :
        clr == typeof(DateTime) ? new Written<DateTime>(TimestampFormat) :
        clr == typeof(DateOnly) ? new Written<DateOnly>(DateFormat) :
        clr == typeof(float) ? new Reals<float>() :
        clr == typeof(double) ? new Reals<double>() :
        clr == typeof(sbyte) ? new Written<sbyte>() :
        clr == typeof(byte) ? new Written<byte>() :
        clr == typeof(short) ? new Written<short>() :
        clr == typeof(ushort) ? new Written<ushort>() :
        clr == typeof(int) ? new Written<int>() :
        clr == typeof(uint) ? new Written<uint>() :
        clr == typeof(long) ? new Written<long>() :
        clr == typeof(ulong) ? new Written<ulong>() :
        clr == typeof(decimal) ? new Written<decimal>() :
        clr == typeof(BigInteger) ? new Written<BigInteger>() :
        clr == typeof(Guid) ? new Written<Guid>() :
        new Objects();

    static PgField Rendered(Type clr) => Text(clr);


    sealed class Bools : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.U8((byte)(reader.GetFieldValue<bool>(ordinal) ? 1 : 0));
    }

    /// Every integer, written at the width its OID declared. `IBinaryInteger` is what makes the
    /// widening a typed conversion rather than a boxed `Convert.ToInt64`.
    sealed class Ints<T>(int oid) : PgField where T : IBinaryInteger<T>
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal)
        {
            var value = long.CreateChecked(reader.GetFieldValue<T>(ordinal));
            switch (oid)
            {
                case PgTypes.Int2: msg.I16((short)value); break;
                case PgTypes.Int4: msg.I32((int)value); break;
                default: msg.I64(value); break;
            }
        }
    }

    sealed class Singles : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.F32(reader.GetFieldValue<float>(ordinal));
    }

    sealed class Doubles : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.F64(reader.GetFieldValue<double>(ordinal));
    }

    sealed class Decimals : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            WriteNumeric(msg, reader.GetFieldValue<decimal>(ordinal));
    }

    sealed class Huge : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            WriteNumeric(msg, reader.GetFieldValue<BigInteger>(ordinal));
    }

    sealed class Unsigned : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            WriteNumeric(msg, new BigInteger(reader.GetFieldValue<ulong>(ordinal)));
    }

    sealed class Uuids : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.Raw(reader.GetFieldValue<Guid>(ordinal).ToByteArray(bigEndian: true));
    }

    sealed class Days : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.I32((int)(reader.GetFieldValue<DateOnly>(ordinal).ToDateTime(TimeOnly.MinValue) - PgEpoch).TotalDays);
    }

    sealed class Stamps : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.I64((reader.GetFieldValue<DateTime>(ordinal) - PgEpoch).Ticks / 10);
    }

    sealed class Zoned : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.I64((reader.GetFieldValue<DateTimeOffset>(ordinal).UtcDateTime - PgEpoch).Ticks / 10);
    }

    sealed class Bytes : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal)
        {
            switch (reader.GetValue(ordinal))
            {
                case byte[] bytes: msg.Raw(bytes); break;
                case Stream stream: msg.Raw(ReadAll(stream)); break;
                case var other: WriteText(msg, other); break;
            }
        }
    }

    sealed class Clock : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.I64(reader.GetFieldValue<TimeOnly>(ordinal).Ticks / 10);
    }

    sealed class Spans : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.I64(reader.GetFieldValue<TimeSpan>(ordinal).Ticks / 10);
    }

    sealed class Intervals : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal)
        {
            var interval = reader.GetFieldValue<TimeSpan>(ordinal);
            msg.I64((interval.Ticks - interval.Days * TimeSpan.TicksPerDay) / 10)
               .I32(interval.Days)
               .I32(0); // months, which a TimeSpan cannot carry
        }
    }

    sealed class Strings : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.Utf8(reader.GetString(ordinal));
    }

    sealed class Flags : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.U8(reader.GetFieldValue<bool>(ordinal) ? (byte)'t' : (byte)'f');
    }

    /// Anything that writes itself as UTF-8 in place, which is most of what a result set is made of.
    sealed class Written<T>(string? format = null) : PgField where T : IUtf8SpanFormattable
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            msg.Format(reader.GetFieldValue<T>(ordinal), format);
    }

    /// A float that is not finite has no UTF-8 rendering PostgreSQL accepts, so those two go through
    /// the one place a type's text is decided and the rest format in place.
    sealed class Reals<T> : PgField where T : IUtf8SpanFormattable, IFloatingPoint<T>
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal)
        {
            var value = reader.GetFieldValue<T>(ordinal);
            if (T.IsFinite(value)) msg.Format(value, "R");
            else msg.Utf8(Render(value));
        }
    }

    /// A type with no pair of its own -- read as it comes, which for a reference costs nothing anyway.
    sealed class Objects : PgField
    {
        public override void Write(Msg msg, DbDataReader reader, int ordinal) =>
            WriteText(msg, reader.GetValue(ordinal));
    }
}
