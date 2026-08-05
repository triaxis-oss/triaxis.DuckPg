using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using System.Globalization;
using System.Text;

namespace triaxis.DuckPg;

/// Mapping between DuckDB logical types and PostgreSQL type OIDs, plus text-format encoding.
static class PgTypes
{
    public const int Bool = 16, Bytea = 17, Int8 = 20, Int2 = 21, Int4 = 23, Text = 25, Json = 114,
                     Float4 = 700, Float8 = 701, Unknown = 705, Varchar = 1043, Date = 1082, Time = 1083,
                     Timestamp = 1114, TimestampTz = 1184, Interval = 1186, Numeric = 1700, Uuid = 2950;

    /// DuckDB's type name as reported by the reader -> PG OID. The names are the reader's own --
    /// `UnsignedBigInt`, `TimestampMs` -- not the SQL spellings. Types PG has no equivalent for
    /// (LIST, STRUCT, MAP, ...) are surfaced as text holding their JSON rendering.
    public static int Oid(string duckType) => duckType.ToLowerInvariant() switch
    {
        "boolean" => Bool,
        "tinyint" or "smallint" or "unsignedtinyint" => Int2,
        "integer" or "unsignedsmallint" => Int4,
        "bigint" or "unsignedinteger" => Int8,
        "hugeint" or "unsignedhugeint" or "unsignedbigint" or "decimal" => Numeric,
        "float" => Float4,
        "double" => Float8,
        "blob" or "bit" => Bytea,
        "date" => Date,
        "time" or "timetz" => Time,
        "timestamp" or "timestamps" or "timestampms" or "timestampns" => Timestamp,
        "timestamptz" => TimestampTz,
        "interval" => Interval,
        "uuid" => Uuid,
        "json" => Json,
        _ => Text,
    };

    /// The DuckDB type to stand a parameter in as when describing a statement that has not run.
    public static string DuckDbType(int oid) => oid switch
    {
        Bool => "BOOLEAN",
        Int2 => "SMALLINT",
        Int4 => "INTEGER",
        Int8 => "BIGINT",
        Float4 => "FLOAT",
        Float8 => "DOUBLE",
        Numeric => "DECIMAL(38,9)",
        Bytea => "BLOB",
        Date => "DATE",
        Time => "TIME",
        Timestamp => "TIMESTAMP",
        TimestampTz => "TIMESTAMPTZ",
        Interval => "INTERVAL",
        Uuid => "UUID",
        Json => "JSON",
        _ => "VARCHAR",
    };

    /// Fixed-width types must advertise their real length; anything else is variable (-1).
    public static short TypeLength(int oid) => oid switch
    {
        Bool => 1,
        Int2 => 2,
        Int4 or Float4 or Date => 4,
        Int8 or Float8 or Timestamp or TimestampTz => 8,
        Uuid => 16,
        Interval => 16,
        _ => -1,
    };

    /// The text rendering of a value, including the JSON a LIST, STRUCT or MAP turns into. The TDS
    /// side publishes the same string, so a nested value reads identically through either protocol.
    public static string Render(object? value) => value switch
    {
        null or DBNull => "",
        bool b => b ? "t" : "f",
        string s => s,
        byte[] bytes => "\\x" + Convert.ToHexStringLower(bytes),
        Stream stream => "\\x" + Convert.ToHexStringLower(ReadAll(stream)),
        Guid g => g.ToString("D"),
        DateTime dt => dt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "+00",
        DateOnly d => d.ToString(DateFormat, CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString(TimeFormat, CultureInfo.InvariantCulture),
        TimeSpan ts => FormatInterval(ts),
        float f => FloatName(f) ?? f.ToString("R", CultureInfo.InvariantCulture),
        double d => FloatName(d) ?? d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IDictionary or IEnumerable => ToJson(value),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// The same rendering written straight into the outgoing message. The scalars a result set is
    /// mostly made of format into UTF-8 in place; the rest go through `Render`, which stays the one
    /// place a type's text is decided.
    public static void WriteText(Msg msg, object value)
    {
        switch (value)
        {
            case string s: msg.Utf8(s); break;
            case bool b: msg.U8(b ? (byte)'t' : (byte)'f'); break;
            case DateTime dt: msg.Format(dt, TimestampFormat); break;
            case DateOnly d: msg.Format(d, DateFormat); break;
            case float f when float.IsFinite(f): msg.Format(f, "R"); break;
            case double d when double.IsFinite(d): msg.Format(d, "R"); break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or Guid:
                msg.Format((IUtf8SpanFormattable)value);
                break;
            default: msg.Utf8(Render(value)); break;
        }
    }

    /// Binary results, which typed clients (Npgsql, the JDBC driver) ask for and cannot do without:
    /// in text format they hand every column back as a string.
    public static void WriteBinary(Msg msg, int oid, object value)
    {
        switch (oid)
        {
            case Bool: msg.U8((byte)(Convert.ToBoolean(value) ? 1 : 0)); break;
            case Int2: msg.I16(Convert.ToInt16(value)); break;
            case Int4: msg.I32(Convert.ToInt32(value)); break;
            case Int8: msg.I64(Convert.ToInt64(value)); break;
            case Float4: msg.F32(Convert.ToSingle(value)); break;
            case Float8: msg.F64(Convert.ToDouble(value)); break;
            case Uuid: msg.Raw(((Guid)value).ToByteArray(bigEndian: true)); break;
            case Numeric: WriteNumeric(msg, value); break;

            case Bytea:
                switch (value)
                {
                    case byte[] bytes: msg.Raw(bytes); break;
                    case Stream stream: msg.Raw(ReadAll(stream)); break;
                    default: WriteText(msg, value); break;
                }
                break;

            case Date: msg.I32((int)(AsDateTime(value) - PgEpoch).TotalDays); break;
            case Timestamp:
            case TimestampTz: msg.I64((AsDateTime(value) - PgEpoch).Ticks / 10); break;
            case Time: msg.I64(AsTimeSpan(value).Ticks / 10); break;

            case Interval:
                var interval = AsTimeSpan(value);
                msg.I64((interval.Ticks - interval.Days * TimeSpan.TicksPerDay) / 10)
                   .I32(interval.Days)
                   .I32(0); // months, which a TimeSpan cannot carry
                break;

            default: WriteText(msg, value); break; // text, json and the JSON-rendered nested types
        }
    }

    /// Parameters arriving in binary format (Npgsql's default for known types).
    public static object DecodeBinary(int oid, byte[] raw) => oid switch
    {
        Bool => raw[0] != 0,
        Int2 => BinaryPrimitives.ReadInt16BigEndian(raw),
        Int4 => BinaryPrimitives.ReadInt32BigEndian(raw),
        Int8 => BinaryPrimitives.ReadInt64BigEndian(raw),
        Float4 => BinaryPrimitives.ReadSingleBigEndian(raw),
        Float8 => BinaryPrimitives.ReadDoubleBigEndian(raw),
        Bytea => raw,
        Numeric => DecodeNumeric(raw),
        Uuid => new Guid(raw, bigEndian: true),
        Date => PgEpoch.AddDays(BinaryPrimitives.ReadInt32BigEndian(raw)),
        Timestamp or TimestampTz => PgEpoch.AddTicks(BinaryPrimitives.ReadInt64BigEndian(raw) * 10),
        Time => new TimeSpan(BinaryPrimitives.ReadInt64BigEndian(raw) * 10),
        _ => Encoding.UTF8.GetString(raw),
    };

    /// Text-format parameters are handed to DuckDB as strings unless the client declared a numeric
    /// type -- DuckDB's implicit casts cover the rest.
    public static object DecodeText(int oid, string raw) => oid switch
    {
        Bool => raw is "t" or "true" or "1" or "TRUE",
        Int2 => short.Parse(raw, CultureInfo.InvariantCulture),
        Int4 => int.Parse(raw, CultureInfo.InvariantCulture),
        Int8 => long.Parse(raw, CultureInfo.InvariantCulture),
        Float4 => float.Parse(raw, CultureInfo.InvariantCulture),
        Float8 => double.Parse(raw, CultureInfo.InvariantCulture),
        Numeric => decimal.Parse(raw, CultureInfo.InvariantCulture),
        _ => raw,
    };

    static DateTime AsDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => Convert.ToDateTime(value),
    };

    static TimeSpan AsTimeSpan(object value) => value switch
    {
        TimeSpan ts => ts,
        TimeOnly t => t.ToTimeSpan(),
        DateTime dt => dt.TimeOfDay,
        _ => TimeSpan.Parse(value.ToString() ?? "0", CultureInfo.InvariantCulture),
    };

    /// Mirror of DecodeNumeric: split the decimal string into base-10000 groups either side of the
    /// point. A HUGEINT arrives as a BigInteger, which no decimal is wide enough to hold and which
    /// has no point to split at -- both come down to digits and a sign.
    static void WriteNumeric(Msg msg, object value)
    {
        var negative = value is BigInteger big ? big.Sign < 0 : Convert.ToDecimal(value) < 0;
        var text = value is BigInteger integer
            ? BigInteger.Abs(integer).ToString(CultureInfo.InvariantCulture)
            : Math.Abs(Convert.ToDecimal(value)).ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? "" : text[(point + 1)..];
        var scale = fraction.Length;

        whole = whole.PadLeft((whole.Length + 3) / 4 * 4, '0');
        fraction = fraction.PadRight((fraction.Length + 3) / 4 * 4, '0');

        var groups = new List<short>();
        for (var i = 0; i < whole.Length; i += 4) groups.Add(short.Parse(whole.Substring(i, 4)));
        var weight = groups.Count - 1;
        for (var i = 0; i < fraction.Length; i += 4) groups.Add(short.Parse(fraction.Substring(i, 4)));

        while (groups.Count > 0 && groups[0] == 0) { groups.RemoveAt(0); weight--; }
        while (groups.Count > 0 && groups[^1] == 0) groups.RemoveAt(groups.Count - 1);
        if (groups.Count == 0) weight = 0;

        msg.I16(groups.Count).I16(weight).I16(negative ? 0x4000 : 0).I16(scale);
        foreach (var group in groups) msg.I16(group);
    }

    /// PostgreSQL sends numerics as base-10000 digit groups: ndigits, weight, sign, dscale, digits[].
    static decimal DecodeNumeric(byte[] raw)
    {
        var digits = BinaryPrimitives.ReadInt16BigEndian(raw);
        var weight = BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(2));
        var sign = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4));
        if (sign == 0xC000) throw new PgError("22P02", "NaN numeric parameters are not supported");

        decimal value = 0;
        for (var i = 0; i < digits; i++)
            value = value * 10000 + BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(8 + i * 2));

        for (var shift = (weight - (digits - 1)) * 4; shift != 0; shift += shift > 0 ? -1 : 1)
            value = shift > 0 ? value * 10 : value / 10;

        return sign == 0x4000 ? -value : value;
    }

    static readonly DateTime PgEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.FFFFFF", DateFormat = "yyyy-MM-dd",
                 TimeFormat = "HH:mm:ss.FFFFFF";

    static string FormatInterval(TimeSpan ts) =>
        $"{(ts < TimeSpan.Zero ? "-" : "")}{Math.Abs(ts.Days) * 24 + Math.Abs(ts.Hours):00}:{Math.Abs(ts.Minutes):00}:{Math.Abs(ts.Seconds):00}" +
        (ts.Ticks % TimeSpan.TicksPerSecond != 0 ? $".{Math.Abs(ts.Ticks % TimeSpan.TicksPerSecond) / 10:000000}" : "");

    /// PostgreSQL names the values a float cannot round-trip as digits; null means it can.
    static string? FloatName(double v) =>
        double.IsPositiveInfinity(v) ? "Infinity"
        : double.IsNegativeInfinity(v) ? "-Infinity"
        : double.IsNaN(v) ? "NaN"
        : null;

    /// DuckDB's LIST/STRUCT/MAP arrive as nested lists and dictionaries. Rendering them by hand
    /// rather than through a serializer keeps the scalars on the same text rules as every other
    /// column -- and keeps the whole encoder reflection-free, so it survives native AOT.
    static string ToJson(object value)
    {
        var json = new StringBuilder();
        WriteJson(json, value);
        return json.ToString();
    }

    static void WriteJson(StringBuilder json, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                json.Append("null");
                break;

            case bool b:
                json.Append(b ? "true" : "false");
                break;

            case sbyte or byte or short or ushort or int or uint or long or ulong
                 or float or double or decimal:
                json.Append(Render(value));
                break;

            case IDictionary map:
                json.Append('{');
                var firstEntry = true;
                foreach (DictionaryEntry entry in map)
                {
                    if (!firstEntry) json.Append(',');
                    firstEntry = false;
                    WriteJsonString(json, entry.Key.ToString() ?? "");
                    json.Append(':');
                    WriteJson(json, entry.Value);
                }
                json.Append('}');
                break;

            // A string is IEnumerable and a blob is better as text, so both are handled above this.
            case IEnumerable items when value is not string and not byte[]:
                json.Append('[');
                var firstItem = true;
                foreach (var item in items)
                {
                    if (!firstItem) json.Append(',');
                    firstItem = false;
                    WriteJson(json, item);
                }
                json.Append(']');
                break;

            default:
                WriteJsonString(json, Render(value));
                break;
        }
    }

    static void WriteJsonString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4"));
                    else json.Append(c);
                    break;
            }
        json.Append('"');
    }

    static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
