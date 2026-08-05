using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace triaxis.DuckPg;

public enum LayerFormat { Parquet, Json, Yaml }

/// What one layer directory contributes to one table: the file or glob holding its rows, and how
/// to read it. `Seq` is the layer's height in the stack -- higher shadows lower. `Partitions` names
/// the columns the `k=v` directories in the path contribute, which is what tells a partition the
/// lake owns from one it merely happens to sit under.
sealed record LayerSource(int Seq, LayerFormat Format, string Path, string[] Partitions)
{
    public bool Hive => Partitions.Length > 0;

    /// YAML is read through a converted copy, so its file names cannot be published as provenance.
    public bool HasFileName => Format != LayerFormat.Yaml;
}

/// Reads layer directories: what tables they hold, and their rows into DuckDB.
static class Layer
{
    public static string Extension(this LayerFormat format) => format switch
    {
        LayerFormat.Parquet => ".parquet",
        LayerFormat.Json => ".json",
        _ => ".yaml",
    };

    static readonly (string Pattern, LayerFormat Format)[] Formats =
    [
        ("*.parquet", LayerFormat.Parquet), ("*.json", LayerFormat.Json),
        ("*.yaml", LayerFormat.Yaml), ("*.yml", LayerFormat.Yaml),
    ];

    /// A file directly in the directory is one table; a subdirectory is one table covering every
    /// parquet below it. A `k=v` directory is neither -- it is a partition *above* the tables, so
    /// `db=one/orders.parquet` and `db=two/orders.parquet` are one `orders` with a `db` column,
    /// which is how one view spans many databases.
    public static IEnumerable<(string Table, LayerSource Source)> Scan(string directory, int seq, ILogger logger)
    {
        var found = new Dictionary<string, LayerSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var partition in Partitions(directory))
            foreach (var (name, source) in Entries(directory, partition, seq))
            {
                if (!found.TryGetValue(name, out var kept)) found[name] = source;
                // Every partition holding the table produces the same glob; anything else is two
                // files claiming one table, which is a mistake worth hearing about.
                else if (!string.Equals(kept.Path, source.Path, StringComparison.Ordinal))
                    logger.LogWarning("{Path} ignored: {Table} already comes from {Kept}",
                        source.Path, name, kept.Path);
            }

        foreach (var (name, source) in found) yield return (name, source);
    }

    /// Every path of `k=v` directories below the layer, deepest included, plus the layer itself.
    static IEnumerable<string> Partitions(string directory)
    {
        yield return "";

        if (!Directory.Exists(directory)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(directory).OrderBy(d => d)
                     .Where(d => Path.GetFileName(d).Contains('=')))
            foreach (var nested in Partitions(dir))
                yield return Path.Combine(Path.GetFileName(dir), nested);
    }

    /// The tables one partition of a layer holds. The glob keeps the partition's *shape* rather
    /// than its value, so every partition contributes to the same source.
    static IEnumerable<(string Table, LayerSource Source)> Entries(string directory, string partition, int seq)
    {
        var here = Path.Combine(directory, partition);
        if (!Directory.Exists(here)) yield break;

        var keys = partition.Length == 0 ? []
            : partition.Split(Path.DirectorySeparatorChar).Select(p => p[..p.IndexOf('=')]).ToArray();
        var shape = partition.Length == 0 ? ""
            : string.Join(Path.DirectorySeparatorChar, partition.Split(Path.DirectorySeparatorChar).Select(_ => "*"));

        foreach (var (pattern, format) in Formats)
            foreach (var file in Directory.EnumerateFiles(here, pattern).OrderBy(f => f))
                yield return (Path.GetFileNameWithoutExtension(file),
                              new LayerSource(seq, format, Path.Combine(directory, shape, Path.GetFileName(file)), keys));

        // Dot-directories are ours -- the write layer keeps its tombstones in one.
        foreach (var dir in Directory.EnumerateDirectories(here).OrderBy(d => d)
                     .Where(d => Path.GetFileName(d) is var name && !name.StartsWith('.') && !name.Contains('=')))
        {
            var files = Directory.EnumerateFiles(dir, "*.parquet", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) continue;

            var below = files
                .SelectMany(f => Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar)[..^1])
                .Where(part => part.Contains('='))
                .Select(part => part[..part.IndexOf('=')]);

            yield return (Path.GetFileName(dir),
                          new LayerSource(seq, LayerFormat.Parquet,
                              Path.Combine(directory, shape, Path.GetFileName(dir), "**", "*.parquet"),
                              [.. keys.Concat(below).Distinct(StringComparer.OrdinalIgnoreCase)]));
        }
    }

    /// A single .dacpac sitting among the layers is the schema without having to be named.
    public static string? FindDacpac(IEnumerable<string> directories, ILogger logger)
    {
        var found = directories.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dacpac", SearchOption.AllDirectories))
            .Distinct()
            .ToList();

        switch (found.Count)
        {
            case 0: return null;
            case 1:
                logger.LogInformation("schema from {Dacpac}", found[0]);
                return found[0];
            default:
                logger.LogWarning("several dacpacs found ({Dacpacs}); name one with --dacpac to use it",
                    string.Join(", ", found));
                return null;
        }
    }

    // ---- reading ---------------------------------------------------------------------------------

    /// `hive_partitioning` is always stated: left to itself DuckDB turns it on, and then any `k=v`
    /// directory anywhere above the files -- including ones the lake merely happens to live under --
    /// silently becomes a column.
    public static string Query(LayerSource source, string glob, bool hive) =>
        $"{(source.Format == LayerFormat.Parquet ? "read_parquet" : "read_json_auto")}" +
        $"({SqlText.Literal(glob.Replace('\\', '/'))}, union_by_name=true, filename=true, " +
        $"hive_partitioning={(hive ? "true" : "false")})";

    /// Runs <paramref name="body"/> against the glob DuckDB can actually read the source from, and
    /// cleans up whatever conversion getting there needed.
    public static void Read(LayerSource source, Action<string> body)
    {
        if (source.Format != LayerFormat.Yaml)
        {
            body(source.Path);
            return;
        }

        var converted = Yaml.ToJsonTree(source.Path);
        try
        {
            body(converted);
        }
        finally
        {
            Yaml.Discard(converted);
        }
    }

    /// Non-parquet layers are materialised once, so a query neither re-parses the file nor pays
    /// inference again. Parquet is scanned in place -- that is what the format is for.
    public static List<Column> Materialise(DuckDBConnection conn, string target, LayerSource source)
    {
        List<Column> columns = [];
        Read(source, glob =>
        {
            columns = Columns(conn, source, glob);
            var projection = columns.Select(c => SqlText.Quote(c.Name))
                .Concat(source.HasFileName ? ["\"filename\""] : []);
            Exec(conn, $"CREATE OR REPLACE TABLE {target} AS SELECT {string.Join(", ", projection)} " +
                       $"FROM {Query(source, glob, source.Hive)}");
        });
        return columns;
    }

    public static List<Column> Columns(DuckDBConnection conn, LayerSource source)
    {
        List<Column> columns = [];
        Read(source, glob => columns = Columns(conn, source, glob));
        return columns;
    }

    /// The columns a source really has: its files' own, plus the partitions the layer declares.
    /// A partition key taken from the path *above* the layer is an artifact of where the lake
    /// sits, so a scan with hive off is what says which columns are the files' own.
    static List<Column> Columns(DuckDBConnection conn, LayerSource source, string glob)
    {
        var own = Describe(conn, Query(source, glob, hive: false));
        if (!source.Hive) return own;

        return [.. own, .. Describe(conn, Query(source, glob, hive: true))
            .Where(c => source.Partitions.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                        && !own.Any(o => o.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))];
    }

    public static List<Column> Describe(DuckDBConnection conn, string scan)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT * FROM {scan}";
        using var reader = cmd.ExecuteReader();

        var columns = new List<Column>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            // `filename` is injected by the scan itself and re-exposed as the internal `_file`.
            if (name != "filename") columns.Add(new Column(name, reader.GetString(1)));
        }
        return columns;
    }

    // ---- writing ---------------------------------------------------------------------------------

    /// Replaces a layer file with the contents of a table. The write is staged next to the target
    /// and moved into place, so a reader never sees a half-written file.
    public static void Write(DuckDBConnection conn, string select, string path, LayerFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");

        if (format == LayerFormat.Yaml) Yaml.Write(conn, select, staged);
        else
            Exec(conn, $"COPY ({select}) TO {SqlText.Literal(staged.Replace('\\', '/'))} " +
                       $"(FORMAT {(format == LayerFormat.Parquet ? "PARQUET" : "JSON, ARRAY true")})");

        File.Move(staged, path, overwrite: true);
    }

    static void Exec(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// YAML in and out. DuckDB understands neither direction, and both conversions are one document
/// walk, so they live together.
static class Yaml
{
    static string Scratch => Path.Combine(Path.GetTempPath(), "duckpg");

    /// The source as JSON DuckDB can read. The whole tree is mirrored rather than one file,
    /// because the `k=v` directories above the files are part of what is being read -- the copy
    /// has to keep them for the partitions to survive the conversion.
    public static string ToJsonTree(string glob)
    {
        var root = Root(glob);
        var scratch = Path.Combine(Scratch, $"{Path.GetFileName(root)}-{glob.GetHashCode():x8}");

        foreach (var file in Expand(glob))
        {
            var target = Path.Combine(scratch, Path.GetRelativePath(root, file) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Convert(file, target);
        }

        return Path.Combine(scratch, Path.GetRelativePath(root, glob) + ".json");
    }

    public static void Discard(string converted)
    {
        var scratch = Root(converted);
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
    }

    /// The fixed part of a glob: everything above the first wildcard.
    static string Root(string glob)
    {
        var star = glob.IndexOf('*');
        return Path.GetDirectoryName(star < 0 ? glob : glob[..star])!;
    }

    /// The files a glob of whole-segment wildcards matches. Only partition globs get here, so a
    /// segment is either a literal or a single `*`.
    static IEnumerable<string> Expand(string glob)
    {
        var segments = glob.Split(Path.DirectorySeparatorChar);
        IEnumerable<string> paths = [segments[0].Length == 0 ? $"{Path.DirectorySeparatorChar}" : segments[0]];

        for (var i = 1; i < segments.Length; i++)
        {
            var (segment, last) = (segments[i], i == segments.Length - 1);
            paths = paths.SelectMany(path => segment != "*" ? [Path.Combine(path, segment)]
                : last ? Directory.EnumerateFiles(path)
                : Directory.EnumerateDirectories(path));
        }

        return paths.Where(File.Exists);
    }

    /// YamlDotNet's own JSON emitter leaves control characters unescaped -- real exports carry tabs
    /// inside plain scalars -- so the document is walked and written with a real JSON writer. Going
    /// through the node model rather than a typed deserialiser also keeps this reflection-free.
    static void Convert(string source, string target)
    {
        var yaml = new YamlStream();
        using (var text = File.OpenText(source)) yaml.Load(text);

        using var stream = File.Create(target);
        using var writer = new Utf8JsonWriter(stream);
        if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlSequenceNode rows)
        {
            Write(writer, rows);
        }
        else
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
        writer.Flush();
    }

    static void Write(Utf8JsonWriter json, YamlNode node)
    {
        switch (node)
        {
            case YamlSequenceNode sequence:
                json.WriteStartArray();
                foreach (var item in sequence.Children) Write(json, item);
                json.WriteEndArray();
                break;

            case YamlMappingNode mapping:
                json.WriteStartObject();
                foreach (var (key, value) in mapping.Children)
                {
                    json.WritePropertyName((key as YamlScalarNode)?.Value ?? key.ToString());
                    Write(json, value);
                }
                json.WriteEndObject();
                break;

            case YamlScalarNode scalar:
                WriteScalar(json, scalar);
                break;

            default:
                json.WriteNullValue();
                break;
        }
    }

    /// Only unquoted scalars carry a type in YAML; a quoted one is always a string, which is what
    /// keeps an id like `007` from turning into a number.
    static void WriteScalar(Utf8JsonWriter json, YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style is not ScalarStyle.Plain)
        {
            json.WriteStringValue(text);
            return;
        }

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": json.WriteNullValue(); return;
            case "true" or "True" or "TRUE": json.WriteBooleanValue(true); return;
            case "false" or "False" or "FALSE": json.WriteBooleanValue(false); return;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            json.WriteNumberValue(integer);
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            json.WriteNumberValue(real);
        else
            json.WriteStringValue(text);
    }

    /// Rows as a YAML sequence of mappings. DuckDB renders each row as JSON, which is legal YAML
    /// for every scalar and for a nested value in flow style -- so the emitter only has to lay out
    /// the block structure around values it never has to escape itself.
    public static void Write(DuckDBConnection conn, string select, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT to_json(r) FROM ({select}) r";
        using var reader = cmd.ExecuteReader();
        using var writer = new StreamWriter(File.Create(path));

        while (reader.Read())
        {
            using var row = JsonDocument.Parse(reader.GetString(0));
            var first = true;
            foreach (var property in row.RootElement.EnumerateObject())
            {
                writer.WriteLine($"{(first ? "-" : " ")} {property.Name}: {property.Value.GetRawText()}");
                first = false;
            }
            if (first) writer.WriteLine("- {}");
        }
    }
}
