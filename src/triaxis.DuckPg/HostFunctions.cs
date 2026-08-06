using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;

namespace triaxis.DuckPg;

/// The SQL Server functions whose meaning is .NET's rather than SQL's, answered in .NET rather than
/// rewritten into DuckDB expressions. `pwdencrypt` hashes UTF-16 text the way SQL Server does, and
/// `CONVERT`'s styles are the date formats `DateTime.ToString` already knows -- written as SQL, each
/// would be an approximation of a thing this process can simply do.
///
/// Registration belongs to the database rather than to the connection that made it, so one call at
/// startup answers for every session. Which is also the constraint: DuckDB calls these from whatever
/// thread is running the scan, so nothing here may hold session state, and none of them belongs in a
/// published view -- a view is bound and evaluated on every execution, and this is a managed call
/// per row.
static class HostFunctions
{
    public static void Register(DuckDBConnection conn)
    {
        // The store's own hash, in the format SQL Server writes: a two-byte version, a four-byte
        // salt, and SHA-512 over the password as UTF-16 followed by that salt. Same shape means a
        // hash a real SQL Server wrote verifies here, and one written here verifies there.
        //
        // Handed back as hex rather than as the blob it is: these bindings read a blob argument but
        // cannot write a blob result, so the macro below turns it into one where DuckDB can.
        conn.RegisterScalarFunction<string, string>("duckpg_pwdencrypt", (readers, writer, count) =>
        {
            for (ulong row = 0; row < count; row++)
            {
                var salt = RandomNumberGenerator.GetBytes(4);
                writer.WriteValue(Convert.ToHexString(Hashed(readers[0].GetValue<string>(row), salt)), row);
            }
        });

        // The salt is in the hash being compared against, which is what makes a comparison possible
        // at all: the same password hashed twice is two different hashes.
        conn.RegisterScalarFunction<string, byte[], bool>("pwdcompare", (readers, writer, count) =>
        {
            for (ulong row = 0; row < count; row++)
                writer.WriteValue(Matches(readers[0].GetValue<string>(row), Bytes(readers[1].GetValue<Stream>(row))), row);
        });

        // What `CONVERT(varchar, d, 120)` means: a style number naming a format, and the formats are
        // the ones .NET spells out.
        conn.RegisterScalarFunction<DateTime, int, string>("duckpg_convert_to_text", (readers, writer, count) =>
        {
            for (ulong row = 0; row < count; row++)
                writer.WriteValue(
                    readers[0].GetValue<DateTime>(row).ToString(Format(readers[1].GetValue<int>(row)),
                                                                CultureInfo.InvariantCulture), row);
        });

        conn.RegisterScalarFunction<string, int, DateTime>("duckpg_convert_from_text", (readers, writer, count) =>
        {
            for (ulong row = 0; row < count; row++)
            {
                var text = readers[0].GetValue<string>(row);
                var style = readers[1].GetValue<int>(row);
                writer.WriteValue(
                    DateTime.TryParseExact(text, Format(style), CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out var parsed)
                        ? parsed
                        : throw new FormatException($"`{text}` is not a date in style {style}"), row);
            }
        });

        using var command = conn.CreateCommand();
        command.CommandText = "CREATE OR REPLACE MACRO pwdencrypt(password) AS " +
                              "from_hex(duckpg_pwdencrypt(password))";
        command.ExecuteNonQuery();
    }

    /// A blob argument arrives as a stream over DuckDB's own memory, which is all these bindings
    /// offer for one -- and a hash is a few dozen bytes, so reading it out costs nothing worth
    /// arranging around.
    static byte[] Bytes(Stream blob)
    {
        using var memory = new MemoryStream();
        blob.CopyTo(memory);
        return memory.ToArray();
    }

    // ---- password hashes ---------------------------------------------------------------------

    /// `0x0200`, the salt, and SHA-512 of the password's UTF-16 bytes followed by the salt.
    static byte[] Hashed(string password, byte[] salt) =>
        [0x02, 0x00, .. salt, .. SHA512.HashData([.. Encoding.Unicode.GetBytes(password), .. salt])];

    /// SQL Server 2000 hashed with SHA-1 and kept a second, case-insensitive hash beside the first;
    /// anything since hashes with SHA-512 and keeps one. A hash of neither shape is not a hash this
    /// can answer for, and says so rather than answering `false` -- which would read as a wrong
    /// password rather than as a hash nobody here understands.
    static bool Matches(string password, byte[] hash) => hash switch
    {
        [0x02, 0x00, ..] when hash.Length == 6 + 64 =>
            CryptographicOperations.FixedTimeEquals(hash, Hashed(password, hash[2..6])),

        [0x01, 0x00, ..] when hash.Length >= 6 + 20 =>
            CryptographicOperations.FixedTimeEquals(
                hash[6..26], SHA1.HashData([.. Encoding.Unicode.GetBytes(password), .. hash[2..6]])),

        _ => throw new FormatException("this is not a password hash duckpg wrote or SQL Server did"),
    };

    // ---- CONVERT styles ----------------------------------------------------------------------

    /// The styles an application actually writes. A style is a promise about the text on both
    /// sides of the conversion, so one that is not here is refused by number rather than guessed
    /// at -- the wrong format silently applied is a date read as another date.
    static string Format(int style) => style switch
    {
        1 => "MM/dd/yy",
        2 => "yy.MM.dd",
        3 => "dd/MM/yy",
        4 => "dd.MM.yy",
        5 => "dd-MM-yy",
        6 => "dd MMM yy",
        7 => "MMM dd, yy",
        8 or 24 or 108 => "HH:mm:ss",
        10 => "MM-dd-yy",
        11 => "yy/MM/dd",
        12 => "yyMMdd",
        14 or 114 => "HH:mm:ss:fff",
        20 or 120 => "yyyy-MM-dd HH:mm:ss",
        21 or 25 or 121 => "yyyy-MM-dd HH:mm:ss.fff",
        23 => "yyyy-MM-dd",
        101 => "MM/dd/yyyy",
        102 => "yyyy.MM.dd",
        103 => "dd/MM/yyyy",
        104 => "dd.MM.yyyy",
        105 => "dd-MM-yyyy",
        106 => "dd MMM yyyy",
        107 => "MMM dd, yyyy",
        110 => "MM-dd-yyyy",
        111 => "yyyy/MM/dd",
        112 => "yyyyMMdd",
        113 => "dd MMM yyyy HH:mm:ss:fff",
        126 => "yyyy-MM-ddTHH:mm:ss.fff",
        127 => "yyyy-MM-ddTHH:mm:ss.fffZ",
        _ => throw new NotSupportedException($"CONVERT style {style} is not one duckpg knows"),
    };
}
