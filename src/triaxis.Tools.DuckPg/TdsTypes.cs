using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace triaxis.Tools.DuckPg;

/// How one column goes onto the wire: the TDS type token plus whatever that token's TYPE_INFO and
/// values need. Anything DuckDB has that SQL Server does not becomes NVARCHAR(MAX) holding the
/// same JSON rendering the PostgreSQL side publishes.
sealed record TdsColumn(byte Token, int Length = 0, int Precision = 0, int Scale = 0);

static class TdsTypes
{
    public const byte Guid = 0x24, IntN = 0x26, Date = 0x28, Time = 0x29, DateTime2 = 0x2A,
                      DateTimeOffset = 0x2B, DecimalN = 0x6A, NumericN = 0x6C, FloatN = 0x6D,
                      MoneyN = 0x6E, DateTimeN = 0x6F, BitN = 0x68, VarBinary = 0xA5, VarChar = 0xA7,
                      Char = 0xAF, Binary = 0xAD, NVarChar = 0xE7, NChar = 0xEF,
                      Text = 0x23, NText = 0x63, Image = 0x22;

    /// A variable-length type declared with this length is a MAX type, whose values are sent in
    /// chunks rather than with one leading length.
    const ushort Max = 0xFFFF;

    /// Microseconds, which is what DuckDB stores and what .NET can render without rounding.
    const int FractionScale = 6;

    /// 1033 / CI_AS, the collation every client understands. It has to be *a* collation: a zeroed
    /// one makes SqlClient treat the column as having no encoding at all.
    public static readonly byte[] Collation = [0x09, 0x04, 0x00, 0x00, 0x00];

    /// The names are the reader's own -- `UnsignedBigInt`, `TimestampMs` -- not the SQL spellings,
    /// and a name that matches nothing here goes out as text.
    public static TdsColumn Describe(string duckType)
    {
        var bare = duckType.ToLowerInvariant();
        var paren = bare.IndexOf('(');
        if (paren > 0) bare = bare[..paren];

        return bare switch
        {
            "boolean" => new TdsColumn(BitN, 1),
            "tinyint" or "unsignedtinyint" or "smallint" => new TdsColumn(IntN, 2),
            "integer" or "unsignedsmallint" => new TdsColumn(IntN, 4),
            "bigint" or "unsignedinteger" => new TdsColumn(IntN, 8),
            "float" => new TdsColumn(FloatN, 4),
            "double" => new TdsColumn(FloatN, 8),
            "decimal" => Decimal(duckType),
            // What summing anything integral gives, and what no SQL Server type is wide enough for.
            // A DECIMAL(38,0) carries sixteen bytes of magnitude, which is a HUGEINT exactly; a
            // client whose own decimal is narrower is the one that has to say so.
            "hugeint" or "unsignedhugeint" or "unsignedbigint" => Decimal(38, 0),
            "date" => new TdsColumn(Date),
            "time" or "timetz" => new TdsColumn(Time, Scale: FractionScale),
            "timestamp" or "timestamps" or "timestampms" or "timestampns" => new TdsColumn(DateTime2, Scale: FractionScale),
            "timestamptz" => new TdsColumn(DateTimeOffset, Scale: FractionScale),
            "uuid" => new TdsColumn(Guid, 16),
            "blob" or "bit" => new TdsColumn(VarBinary, Max),
            _ => new TdsColumn(NVarChar, Max),
        };
    }

    /// A decimal declares its precision and scale in the metadata, and the byte count follows.
    public static TdsColumn Decimal(int precision, int scale) =>
        new(DecimalN, DecimalLength(precision), precision, scale);

    /// `DECIMAL(18,3)` when the type name carries them; DuckDB's reader usually does not.
    static TdsColumn Decimal(string duckType)
    {
        var open = duckType.IndexOf('(');
        var precision = 18;
        var scale = 0;
        if (open > 0 && duckType.IndexOf(')', open) is var close && close > open)
        {
            var parts = duckType[(open + 1)..close].Split(',');
            precision = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            if (parts.Length > 1) scale = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
        }
        return Decimal(precision, scale);
    }

    static int DecimalLength(int precision) => precision switch
    {
        <= 9 => 5,
        <= 19 => 9,
        <= 28 => 13,
        _ => 17,
    };

    // ---- metadata --------------------------------------------------------------------------------

    public static void WriteTypeInfo(TdsMsg msg, TdsColumn column)
    {
        msg.U8(column.Token);
        switch (column.Token)
        {
            case IntN or BitN or FloatN or Guid:
                msg.U8(column.Length);
                break;

            case DecimalN or NumericN:
                msg.U8(column.Length).U8(column.Precision).U8(column.Scale);
                break;

            case Time or DateTime2 or DateTimeOffset:
                msg.U8(column.Scale);
                break;

            case Date:
                break;

            case NVarChar or Char or NChar or VarChar:
                msg.U16(column.Length).Raw(Collation);
                break;

            default:
                msg.U16(column.Length);
                break;
        }
    }

    // ---- values ----------------------------------------------------------------------------------

    /// <paramref name="payload"/> is what one packet holds: a MAX value is chunked to it, so no
    /// chunk of it crosses a packet boundary.
    public static void WriteValue(TdsMsg msg, TdsColumn column, object? value, int payload)
    {
        if (value is null or DBNull)
        {
            if (column.Token is NVarChar or VarBinary or Char or VarChar && column.Length == Max) msg.I64(-1);
            else if (column.Token is NVarChar or VarBinary or Char or VarChar) msg.U16(Max);
            else msg.U8(0);
            return;
        }

        switch (column.Token)
        {
            case BitN:
                msg.U8(1).U8(Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0);
                return;

            case IntN:
                WriteInteger(msg, column.Length, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;

            case FloatN when column.Length == 4:
                msg.U8(4).Raw(BitConverter.GetBytes(Convert.ToSingle(value, CultureInfo.InvariantCulture)));
                return;

            case FloatN:
                msg.U8(8).Raw(BitConverter.GetBytes(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                return;

            case DecimalN or NumericN:
                WriteDecimal(msg, column, value);
                return;

            case Guid:
                msg.U8(16).Raw(((System.Guid)value).ToByteArray());
                return;

            case Date:
                msg.U8(3).Raw(Days(AsDateTime(value)));
                return;

            case Time:
                WriteTime(msg, AsTimeSpan(value));
                return;

            case DateTime2:
                WriteDateTime2(msg, AsDateTime(value));
                return;

            case DateTimeOffset:
                WriteDateTimeOffset(msg, value);
                return;

            case VarBinary:
                WritePlp(msg, AsBytes(value), payload);
                return;

            default:
                WritePlp(msg, Encoding.Unicode.GetBytes(PgTypes.Render(value)), payload);
                return;
        }
    }

    static void WriteInteger(TdsMsg msg, int length, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        msg.U8(length).Raw(b[..length]);
    }

    /// PG's numeric is base-10000; TDS's is a sign byte and a little-endian magnitude, scaled by
    /// the precision the column declared -- so the value has to be shifted, not just copied.
    /// A HUGEINT arrives as a BigInteger and can be wider than a decimal, so it goes out as the
    /// integer it already is rather than through one it would not fit in.
    static void WriteDecimal(TdsMsg msg, TdsColumn column, object value)
    {
        var integer = value as BigInteger? ?? Scaled(Convert.ToDecimal(value, CultureInfo.InvariantCulture), column.Scale);

        var magnitude = BigInteger.Abs(integer).ToByteArray(isUnsigned: true, isBigEndian: false);
        var payload = new byte[column.Length - 1];
        magnitude.AsSpan(0, Math.Min(magnitude.Length, payload.Length)).CopyTo(payload);

        msg.U8(column.Length).U8(integer.Sign < 0 ? 0 : 1).Raw(payload);
    }

    static BigInteger Scaled(decimal value, int scale)
    {
        for (var i = 0; i < scale; i++) value *= 10;
        return new BigInteger(decimal.Truncate(value));
    }

    static void WriteTime(TdsMsg msg, TimeSpan time)
    {
        var length = TimeLength(FractionScale);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, time.Ticks / TicksPerUnit(FractionScale));
        msg.U8(length).Raw(b[..length]);
    }

    static void WriteDateTime2(TdsMsg msg, DateTime value)
    {
        var length = TimeLength(FractionScale);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value.TimeOfDay.Ticks / TicksPerUnit(FractionScale));
        msg.U8(length + 3).Raw(b[..length]).Raw(Days(value));
    }

    static void WriteDateTimeOffset(TdsMsg msg, object value)
    {
        var offset = value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            _ => new DateTimeOffset(AsDateTime(value), TimeSpan.Zero),
        };
        var utc = offset.UtcDateTime;
        var length = TimeLength(FractionScale);

        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, utc.TimeOfDay.Ticks / TicksPerUnit(FractionScale));
        Span<byte> minutes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(minutes, (short)offset.Offset.TotalMinutes);

        msg.U8(length + 5).Raw(b[..length]).Raw(Days(utc)).Raw(minutes);
    }

    /// A MAX value is sent as its total length, then chunks, then an empty chunk to end it. Each
    /// chunk stops at the packet boundary rather than running through it: SqlClient reassembles a
    /// read that ended mid-packet by replaying the value's framing, and a chunk spanning the
    /// boundary loses it its place there -- it then reads response bytes as the next length, which
    /// surfaces much later and looks like anything but this. SQL Server itself never emits one,
    /// because it fills a packet and starts a chunk in the next.
    static void WritePlp(TdsMsg msg, byte[] value, int payload)
    {
        msg.I64(value.Length);

        var at = 0;
        while (at < value.Length)
        {
            // The chunk header goes in first, so the room left is measured from after it.
            var take = Math.Min(value.Length - at, payload - (msg.Length + 4) % payload);
            msg.I32(take).Raw(value.AsSpan(at, take));
            at += take;
        }

        msg.I32(0);
    }

    static byte[] Days(DateTime value)
    {
        var days = (int)(value.Date - new DateTime(1, 1, 1)).TotalDays;
        return [(byte)days, (byte)(days >> 8), (byte)(days >> 16)];
    }

    static int TimeLength(int scale) => scale switch { <= 2 => 3, <= 4 => 4, _ => 5 };

    static long TicksPerUnit(int scale) => scale switch { 6 => 10, 7 => 1, _ => (long)Math.Pow(10, 7 - scale) };

    static DateTime AsDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
    };

    static TimeSpan AsTimeSpan(object value) => value switch
    {
        TimeSpan ts => ts,
        TimeOnly t => t.ToTimeSpan(),
        DateTime dt => dt.TimeOfDay,
        _ => TimeSpan.Parse(value.ToString()!, CultureInfo.InvariantCulture),
    };

    static byte[] AsBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        Stream stream => Read(stream),
        _ => Encoding.UTF8.GetBytes(value.ToString()!),
    };

    static byte[] Read(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ---- parameters ------------------------------------------------------------------------------

    /// One RPC parameter: its declared type, read straight off the wire, and then its value.
    public static object? ReadValue(ref TdsReader reader)
    {
        var token = reader.U8();
        switch (token)
        {
            case IntN or BitN or FloatN or MoneyN or DateTimeN or Guid:
            {
                var declared = reader.U8();
                var length = reader.U8();
                if (length == 0) return null;
                return Scalar(token, declared, reader.Bytes(length));
            }

            case DecimalN or NumericN:
            {
                reader.U8();
                var precision = reader.U8();
                var scale = reader.U8();
                var length = reader.U8();
                if (length == 0) return null;
                return ReadDecimal(reader.Bytes(length), scale);
            }

            case Date:
            {
                var length = reader.U8();
                return length == 0 ? null : FromDays(reader.Bytes(3));
            }

            case Time or DateTime2 or DateTimeOffset:
            {
                var scale = reader.U8();
                var length = reader.U8();
                return length == 0 ? null : ReadTemporal(token, scale, reader.Bytes(length));
            }

            case NVarChar or NChar or Char or VarChar:
            {
                var declared = reader.U16();
                reader.Skip(5); // collation
                var bytes = ReadVariable(ref reader, declared);
                return bytes is null ? null
                    : token is NVarChar or NChar ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            }

            case VarBinary or Binary:
            {
                var declared = reader.U16();
                return ReadVariable(ref reader, declared);
            }

            // The legacy LOB types, which an old client still sends: their declared maximum is four
            // bytes rather than two, and the value's own length is four bytes with -1 for null.
            case Text or NText or Image:
            {
                reader.I32();
                if (token is Text or NText) reader.Skip(5); // collation
                var length = reader.I32();
                if (length < 0) return null;

                var bytes = reader.Bytes(length);
                return token switch
                {
                    NText => Encoding.Unicode.GetString(bytes),
                    Text => Encoding.UTF8.GetString(bytes),
                    _ => bytes,
                };
            }

            case 0x1F: // NULLTYPE, which is what an untyped null parameter arrives as
                return null;

            default:
                throw new ProtocolException($"unsupported parameter type 0x{token:X2}");
        }
    }

    static object Scalar(byte token, byte declared, byte[] value) => token switch
    {
        BitN => value[0] != 0,
        Guid => new Guid(value),
        FloatN => declared == 4 ? BitConverter.ToSingle(value) : BitConverter.ToDouble(value),
        MoneyN => Money(value),
        DateTimeN => LegacyDateTime(value),
        _ => value.Length switch
        {
            1 => (long)value[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            _ => BinaryPrimitives.ReadInt64LittleEndian(value),
        },
    };

    static decimal Money(byte[] value) => value.Length == 4
        ? BinaryPrimitives.ReadInt32LittleEndian(value) / 10000m
        : ((long)BinaryPrimitives.ReadInt32LittleEndian(value) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4)))
          / 10000m;

    /// The pre-2008 DATETIME: days since 1900 and 1/300s ticks since midnight.
    static DateTime LegacyDateTime(byte[] value) => value.Length == 4
        ? new DateTime(1900, 1, 1).AddDays(BinaryPrimitives.ReadInt16LittleEndian(value))
                                  .AddMinutes(BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(2)))
        : new DateTime(1900, 1, 1).AddDays(BinaryPrimitives.ReadInt32LittleEndian(value))
                                  .AddMilliseconds(BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(4)) * 10.0 / 3.0);

    static decimal ReadDecimal(byte[] value, int scale)
    {
        var magnitude = new BigInteger(value.AsSpan(1), isUnsigned: true, isBigEndian: false);
        var result = (decimal)magnitude;
        for (var i = 0; i < scale; i++) result /= 10;
        return value[0] == 0 ? -result : result;
    }

    static object ReadTemporal(byte token, int scale, byte[] value)
    {
        var timeLength = TimeLength(scale);
        Span<byte> ticks = stackalloc byte[8];
        value.AsSpan(0, Math.Min(timeLength, value.Length)).CopyTo(ticks);
        var time = TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(ticks) * TicksPerUnit(scale));

        if (token == Time) return time;

        var date = FromDays(value.AsSpan(timeLength, 3).ToArray());
        var stamp = date.Add(time);
        return token == DateTime2 ? stamp
            : new DateTimeOffset(stamp, TimeSpan.Zero)
                .ToOffset(TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(value.AsSpan(timeLength + 3))));
    }

    static DateTime FromDays(byte[] value) =>
        new DateTime(1, 1, 1).AddDays(value[0] | value[1] << 8 | value[2] << 16);

    /// Non-MAX values carry one length; MAX values arrive in chunks and end with an empty one.
    static byte[]? ReadVariable(ref TdsReader reader, ushort declared)
    {
        if (declared != Max)
        {
            var length = reader.U16();
            return length == Max ? null : reader.Bytes(length);
        }

        var total = reader.U64();
        if (total == 0xFFFFFFFFFFFFFFFF) return null;

        using var value = new MemoryStream();
        while (true)
        {
            var chunk = reader.I32();
            if (chunk == 0) break;
            value.Write(reader.Bytes(chunk));
        }
        return value.ToArray();
    }
}
