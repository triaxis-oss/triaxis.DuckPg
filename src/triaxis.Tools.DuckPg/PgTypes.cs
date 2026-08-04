using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;

namespace triaxis.Tools.DuckPg;

/// Mapping between DuckDB logical types and PostgreSQL type OIDs, plus text-format encoding.
static class PgTypes
{
    public const int Bool = 16, Bytea = 17, Int8 = 20, Int2 = 21, Int4 = 23, Text = 25, Json = 114,
                     Float4 = 700, Float8 = 701, Unknown = 705, Varchar = 1043, Date = 1082, Time = 1083,
                     Timestamp = 1114, TimestampTz = 1184, Interval = 1186, Numeric = 1700, Uuid = 2950;

    /// DuckDB's type name as reported by the reader -> PG OID. Types PG has no equivalent for
    /// (LIST, STRUCT, MAP, ...) are surfaced as text holding their JSON rendering.
    public static int Oid(string duckType) => duckType.ToLowerInvariant() switch
    {
        "boolean" => Bool,
        "tinyint" or "smallint" or "utinyint" => Int2,
        "integer" or "usmallint" => Int4,
        "bigint" or "uinteger" => Int8,
        "hugeint" or "uhugeint" or "ubigint" or "decimal" => Numeric,
        "float" => Float4,
        "double" => Float8,
        "blob" or "bit" => Bytea,
        "date" => Date,
        "time" or "timetz" => Time,
        "timestamp" or "timestamp_s" or "timestamp_ms" or "timestamp_ns" => Timestamp,
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

    public static byte[] Encode(object? value) => Utf8(Render(value));

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
        DateTime dt => FormatTimestamp(dt),
        DateTimeOffset dto => FormatTimestamp(dto.UtcDateTime) + "+00",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => FormatInterval(ts),
        float f => FormatFloat(f, f.ToString("R", CultureInfo.InvariantCulture)),
        double d => FormatFloat(d, d.ToString("R", CultureInfo.InvariantCulture)),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IDictionary or IEnumerable => ToJson(value),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// Binary results, which typed clients (Npgsql, the JDBC driver) ask for and cannot do without:
    /// in text format they hand every column back as a string.
    public static byte[] EncodeBinary(int oid, object value)
    {
        switch (oid)
        {
            case Bool: return [(byte)(Convert.ToBoolean(value) ? 1 : 0)];
            case Int2: return BigEndian(2, b => BinaryPrimitives.WriteInt16BigEndian(b, Convert.ToInt16(value)));
            case Int4: return BigEndian(4, b => BinaryPrimitives.WriteInt32BigEndian(b, Convert.ToInt32(value)));
            case Int8: return BigEndian(8, b => BinaryPrimitives.WriteInt64BigEndian(b, Convert.ToInt64(value)));
            case Float4: return BigEndian(4, b => BinaryPrimitives.WriteSingleBigEndian(b, Convert.ToSingle(value)));
            case Float8: return BigEndian(8, b => BinaryPrimitives.WriteDoubleBigEndian(b, Convert.ToDouble(value)));
            case Uuid: return ((Guid)value).ToByteArray(bigEndian: true);
            case Bytea: return value switch { byte[] b => b, Stream s => ReadAll(s), _ => Encode(value) };
            case Numeric: return EncodeNumeric(Convert.ToDecimal(value));

            case Date:
                return BigEndian(4, b => BinaryPrimitives.WriteInt32BigEndian(b, (int)(AsDateTime(value) - PgEpoch).TotalDays));
            case Timestamp:
            case TimestampTz:
                return BigEndian(8, b => BinaryPrimitives.WriteInt64BigEndian(b, (AsDateTime(value) - PgEpoch).Ticks / 10));
            case Time:
                return BigEndian(8, b => BinaryPrimitives.WriteInt64BigEndian(b, AsTimeSpan(value).Ticks / 10));
            case Interval:
                var interval = AsTimeSpan(value);
                var binary = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(binary, (interval.Ticks - interval.Days * TimeSpan.TicksPerDay) / 10);
                BinaryPrimitives.WriteInt32BigEndian(binary.AsSpan(8), interval.Days);
                return binary;

            default: return Encode(value); // text, json and the JSON-rendered nested types
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

    static byte[] BigEndian(int size, Action<byte[]> write)
    {
        var buffer = new byte[size];
        write(buffer);
        return buffer;
    }

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

    /// Mirror of DecodeNumeric: split the decimal string into base-10000 groups either side of the point.
    static byte[] EncodeNumeric(decimal value)
    {
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
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

        var buffer = new byte[8 + groups.Count * 2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, (short)groups.Count);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(2), (short)weight);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), value < 0 ? (ushort)0x4000 : (ushort)0);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(6), (short)scale);
        for (var i = 0; i < groups.Count; i++)
            BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(8 + i * 2), groups[i]);
        return buffer;
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

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static string FormatTimestamp(DateTime dt) =>
        dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture);

    static string FormatInterval(TimeSpan ts) =>
        $"{(ts < TimeSpan.Zero ? "-" : "")}{Math.Abs(ts.Days) * 24 + Math.Abs(ts.Hours):00}:{Math.Abs(ts.Minutes):00}:{Math.Abs(ts.Seconds):00}" +
        (ts.Ticks % TimeSpan.TicksPerSecond != 0 ? $".{Math.Abs(ts.Ticks % TimeSpan.TicksPerSecond) / 10:000000}" : "");

    static string FormatFloat(double v, string fallback) =>
        double.IsPositiveInfinity(v) ? "Infinity"
        : double.IsNegativeInfinity(v) ? "-Infinity"
        : double.IsNaN(v) ? "NaN"
        : fallback;

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
