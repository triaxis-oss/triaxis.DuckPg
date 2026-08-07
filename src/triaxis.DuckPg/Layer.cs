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
sealed record LayerSource(int Seq, LayerFormat Format, string Path, string[] Partitions,
                          string? KeyedBy = null)
{
    public bool Hive => Partitions.Length > 0;

    /// Whether the files have to be rewritten before DuckDB can read them: always for YAML, which it
    /// does not speak, and for a keyed file of either format, whose rows are mapping entries rather
    /// than array items.
    public bool Converted => Format == LayerFormat.Yaml || KeyedBy is not null;

    /// A converted source is read out of a copy, so its file names cannot be published as provenance.
    public bool HasFileName => !Converted;
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

    /// The source as it should be read given the key the table would take its mapping keys into --
    /// unchanged where the files are ordinary sequences of rows, or where the table has no single
    /// column for a mapping key to fill. A mapping key is one value, so a composite key cannot come
    /// out of one and the question is not asked.
    ///
    /// Parquet carries its own shape and is never in question. Everything else is decided by looking
    /// at the files: a source whose files disagree is converted whole, and each file is then read as
    /// whatever it is.
    public static LayerSource? Keyed(LayerSource? source, string? key) =>
        source is null || key is null || source.Format == LayerFormat.Parquet
        || !Yaml.Expand(source.Path).Any(Yaml.IsKeyed)
            ? source
            : source with { KeyedBy = key };

    /// Runs <paramref name="body"/> against the glob DuckDB can actually read the source from, and
    /// cleans up whatever conversion getting there needed.
    public static void Read(LayerSource source, Action<string> body)
    {
        if (!source.Converted)
        {
            body(source.Path);
            return;
        }

        var converted = Yaml.ToJsonTree(source.Path, source.KeyedBy, source.Format);
        try
        {
            body(converted);
        }
        finally
        {
            Yaml.Discard(converted);
        }
    }

    /// Non-parquet layers are materialized once, so a query neither re-parses the file nor pays
    /// inference again. Parquet is scanned in place -- that is what the format is for.
    public static List<Column> Materialize(DuckDBConnection conn, string target, LayerSource source)
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
    /// and moved into place, so a reader never sees a half-written file. A file that was read as
    /// mapping entries is written back as mapping entries: the shape belongs to the file, and a lake
    /// that quietly rewrote one as a sequence would have changed something nobody asked it to.
    public static void Write(DuckDBConnection conn, string select, string path, LayerFormat format,
                             string? keyedBy = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");

        if (keyedBy is not null) Yaml.WriteKeyed(conn, select, staged, keyedBy, format);
        else if (format == LayerFormat.Yaml) Yaml.Write(conn, select, staged);
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
    public static string ToJsonTree(string glob, string? key, LayerFormat format)
    {
        var root = Root(glob);
        var scratch = Path.Combine(Scratch, $"{Path.GetFileName(root)}-{glob.GetHashCode():x8}");

        foreach (var file in Expand(glob))
        {
            var target = Path.Combine(scratch, Path.GetRelativePath(root, file) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Convert(file, target, key, format);
        }

        return Path.Combine(scratch, Path.GetRelativePath(root, glob) + ".json");
    }

    /// A file whose rows are the entries of one mapping -- the row's key as the entry's key, the rest
    /// of the row as its value -- rather than a sequence of rows. Every value has to be a mapping for
    /// that reading to hold: one that is not means the document is a single row whose columns happen
    /// to include a nested one, which is what an object-rooted JSON file has always been.
    ///
    /// A sequence root is the ordinary shape and is ruled out on the first character, so the parse is
    /// only paid by a file that might actually be keyed.
    public static bool IsKeyed(string path)
    {
        using (var peek = File.OpenText(path))
            for (int c; (c = peek.Read()) >= 0;)
            {
                if (char.IsWhiteSpace((char)c)) continue;
                // `-` opens a YAML sequence entry, but `---` opens a document -- and that document
                // may still be a mapping.
                if (c == '[' || (c == '-' && peek.Peek() is var next && (next < 0 || char.IsWhiteSpace((char)next))))
                    return false;
                break;
            }

        var yaml = new YamlStream();
        using (var text = File.OpenText(path)) yaml.Load(text);
        return yaml.Documents is [{ RootNode: YamlMappingNode root }, ..]
               && root.Children.Count > 0
               && root.Children.Values.All(value => value is YamlMappingNode);
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
    public static IEnumerable<string> Expand(string glob)
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
    static void Convert(string source, string target, string? key, LayerFormat format)
    {
        var yaml = new YamlStream();
        using (var text = File.OpenText(source)) yaml.Load(text);

        using var stream = File.Create(target);
        using var writer = new Utf8JsonWriter(stream);
        switch (yaml.Documents is [{ RootNode: var node }, ..] ? node : null)
        {
            case YamlSequenceNode rows:
                Write(writer, rows);
                break;

            // Keyed only where the table has a column for the mapping keys to fill; without one the
            // same document is the single row an object-rooted JSON file has always been, and saying
            // so beats publishing a table whose rows have lost their names.
            case YamlMappingNode rows when key is not null && rows.Children.Count > 0
                                           && rows.Children.Values.All(value => value is YamlMappingNode):
                WriteKeyed(writer, rows, key, format);
                break;

            case YamlMappingNode row:
                writer.WriteStartArray();
                Write(writer, row);
                writer.WriteEndArray();
                break;

            default:
                writer.WriteStartArray();
                writer.WriteEndArray();
                break;
        }
        writer.Flush();
    }

    /// One row per mapping entry, the entry's key written into the key column. A row that carries
    /// that column *inside* it as well is a second answer to a question the file already answered,
    /// and the entry's key is the one the file was organized by -- so the inner one is dropped.
    ///
    /// The key is typed as any other scalar for YAML, where quoting is the author's own choice and
    /// `"007"` is deliberately text. JSON has no way to write a mapping key that is not a string, so
    /// there the quoting says nothing and the text is what it is read from -- otherwise every key of
    /// a keyed JSON file would be text, and an `id` of 1 would not match the same row in a layer
    /// beneath it.
    static void WriteKeyed(Utf8JsonWriter json, YamlMappingNode rows, string key, LayerFormat format)
    {
        json.WriteStartArray();
        foreach (var (name, value) in rows.Children)
        {
            json.WriteStartObject();
            json.WritePropertyName(key);
            if (name is YamlScalarNode scalar && format != LayerFormat.Json) WriteScalar(json, scalar);
            else WriteInferred(json, (name as YamlScalarNode)?.Value ?? name.ToString());

            foreach (var (field, item) in ((YamlMappingNode)value).Children)
            {
                var column = (field as YamlScalarNode)?.Value ?? field.ToString();
                if (column.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                json.WritePropertyName(column);
                Write(json, item);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
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
        if (scalar.Style is ScalarStyle.Plain) WriteInferred(json, scalar.Value ?? "");
        else json.WriteStringValue(scalar.Value ?? "");
    }

    static void WriteInferred(Utf8JsonWriter json, string text)
    {
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

    /// Rows back out as the mapping entries they were read as. The key column becomes the entry's
    /// key and is not repeated inside it, which is what makes the file read back as what was written.
    /// A row whose key is null has no entry to be -- there is no such mapping key -- so it is written
    /// under the empty one, where it will read back as an empty string rather than silently vanish.
    public static void WriteKeyed(DuckDBConnection conn, string select, string path, string key,
                                  LayerFormat format)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT to_json(r) FROM ({select}) r";
        using var reader = cmd.ExecuteReader();
        using var stream = File.Create(path);

        if (format == LayerFormat.Json)
        {
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            json.WriteStartObject();
            while (reader.Read())
            {
                using var row = JsonDocument.Parse(reader.GetString(0));
                var (name, fields) = Split(row.RootElement, key);
                json.WritePropertyName(name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? "" : name.GetRawText());
                json.WriteStartObject();
                foreach (var field in fields) field.WriteTo(json);
                json.WriteEndObject();
            }
            json.WriteEndObject();
            return;
        }

        using var writer = new StreamWriter(stream);
        while (reader.Read())
        {
            using var row = JsonDocument.Parse(reader.GetString(0));
            var (name, fields) = Split(row.RootElement, key);

            // A string key goes out quoted whatever it looks like: quoted or plain it reads back as
            // the same string, and quoted it also survives a `007` or a `:` in the text. A number or
            // a boolean goes out as itself, which is the only way it reads back as one.
            writer.WriteLine(name.GetRawText() + ":");
            foreach (var field in fields) writer.WriteLine($"  {field.Name}: {field.Value.GetRawText()}");
            if (fields.Count == 0) writer.WriteLine("  {}");
        }
    }

    /// A row split into the value its key column holds and everything else, matched the way SQL
    /// matches a column name rather than the way JSON matches a property.
    static (JsonElement Key, List<JsonProperty> Fields) Split(JsonElement row, string key)
    {
        var name = row;
        List<JsonProperty> fields = [];
        foreach (var property in row.EnumerateObject())
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) name = property.Value;
            else fields.Add(property);
        return (name, fields);
    }
}
