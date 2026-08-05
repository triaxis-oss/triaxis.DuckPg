using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

/// The topmost layer, and the only one that accepts writes. Its rows live in DuckDB while the
/// gateway runs and in ordinary layer files between runs -- so what a client wrote is a file
/// another instance can read, not a private journal.
sealed class WriteLayer(Config config, ILogger<WriteLayer> logger)
{
    /// Schema holding the write layer's tables and their tombstones.
    public const string Schema = "wr";

    public bool Enabled => config.Write is not null || config.Writable;

    /// Where writes are persisted; null keeps them in memory for the life of the process.
    public string? Directory => config.Write;

    /// Above every read layer, which is what makes a written row shadow the file it came from.
    public int Seq => config.Layers.Length;

    /// A table is persisted in the format it already has a file in, so a hand-written YAML table
    /// stays YAML rather than turning into parquet the first time someone writes to it.
    LayerFormat FormatOf(Table table) => table.WriteSource?.Format ?? config.WriteFormat;

    string PathOf(Table table) => Path.Combine(Directory!, table.Name + FormatOf(table).Extension());

    string TombstonePathOf(Table table) =>
        Path.Combine(Directory!, ".deleted", table.Name + FormatOf(table).Extension());

    /// The table and its tombstone list, loaded from whatever the directory already holds. Both are
    /// rebuilt from the files, so a reload picks up an edit made outside the gateway.
    public void Prepare(DuckDBConnection conn, Table table)
    {
        Exec(conn, $"DROP TABLE IF EXISTS {table.WriteName}");
        Exec(conn, $"DROP TABLE IF EXISTS {table.TombstoneName}");
        foreach (var statement in Definition(table)) Exec(conn, statement);

        if (table.WriteSource is { } source)
            Layer.Read(source, glob => Fill(conn, table.WriteName, table.Columns, source, glob));

        if (table.Key.Length > 0 && Directory is not null && Tombstones(table) is { } tombstones)
            Layer.Read(tombstones, glob => Fill(conn, table.TombstoneName, KeyColumns(table), tombstones, glob));
    }

    /// Whether the directory already holds something for this table -- rows, or keys it hides below.
    /// One that does has to be loaded when the lake is built; one that does not has nothing to say
    /// until something writes to it.
    public bool Carries(Table table) =>
        Directory is not null && (table.WriteSource is not null || HasTombstones(table));

    /// Whether anything has ever been hidden below. Until something has, the view's tombstone check
    /// is a subquery over an empty table that every read of it binds and no read of it needs.
    public bool HasTombstones(Table table) => Directory is not null && Tombstones(table) is not null;

    /// The tables the write layer keeps for one lake table, as statements. `IF NOT EXISTS` makes the
    /// on-demand path safe to repeat: a statement that rolled back leaves nothing behind, and one
    /// that did not is left alone rather than emptied.
    public string[] Definition(Table table, bool ifNotExists = false)
    {
        var maybe = ifNotExists ? "IF NOT EXISTS " : "";

        // A declared default belongs on the table, where an INSERT that omits the column picks it up
        // as it is written -- the expression itself, not the value the view fills a file's gaps
        // with, because a row being written now can be stamped with now.
        var declared = string.Join(", ", table.Columns.Select(c =>
            $"{SqlText.Quote(c.Name)} {c.Type}" + (c.Default is null ? "" : $" DEFAULT {c.Default.Expr}")));
        var key = table.Key.Length > 0
            ? $", PRIMARY KEY ({string.Join(", ", table.Key.Select(SqlText.Quote))})"
            : "";

        if (table.Key.Length == 0)
            return [$"CREATE TABLE {maybe}{table.WriteName} ({declared})"];

        return [
            $"CREATE TABLE {maybe}{table.WriteName} ({declared}{key})",
            $"CREATE TABLE {maybe}{table.TombstoneName} " +
            $"({string.Join(", ", KeyColumns(table).Select(c => $"{SqlText.Quote(c.Name)} {c.Type}"))}, " +
            $"PRIMARY KEY ({string.Join(", ", table.Key.Select(SqlText.Quote))}))",
        ];
    }

    static List<Column> KeyColumns(Table table) =>
        [.. table.Columns.Where(c => table.Key.Any(k => k.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))];

    /// Writes the table back to its file, and its tombstones to a sidecar the layer scan skips.
    /// An emptied table takes its file with it, so the directory says what the layer holds.
    public void Persist(DuckDBConnection conn, Table table)
    {
        if (Directory is null) return;

        Save(conn, PathOf(table), FormatOf(table), table.Columns.Select(c => c.Name), table.WriteName);
        if (table.Key.Length > 0)
            Save(conn, TombstonePathOf(table), FormatOf(table), table.Key, table.TombstoneName);
    }

    void Save(DuckDBConnection conn, string path, LayerFormat format, IEnumerable<string> columns, string source)
    {
        var select = $"SELECT {string.Join(", ", columns.Select(SqlText.Quote))} FROM {source}";
        if (Scalar(conn, $"SELECT count(*) FROM {source}") == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Layer.Write(conn, select, path, format);
        logger.LogDebug("persisted {Path}", path);
    }

    /// The sidecar holding this table's deleted keys, in whichever format it was written.
    LayerSource? Tombstones(Table table)
    {
        var directory = Path.Combine(Directory!, ".deleted");
        foreach (var format in new[] { LayerFormat.Parquet, LayerFormat.Json, LayerFormat.Yaml })
        {
            var path = Path.Combine(directory, table.Name + format.Extension());
            if (File.Exists(path)) return new LayerSource(Seq, format, path, []);
        }
        return null;
    }

    /// Loading rather than CREATE TABLE AS: the target already declares the published types, and
    /// INSERT casts a layer's own guesses onto them.
    static void Fill(DuckDBConnection conn, string target, List<Column> columns, LayerSource source, string glob)
    {
        var scan = Layer.Query(source, glob, source.Hive);
        var present = Layer.Describe(conn, scan);
        var shared = columns.Where(c => present.Any(p => p.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                            .Select(c => SqlText.Quote(c.Name))
                            .ToList();
        if (shared.Count == 0) return;

        Exec(conn, $"INSERT INTO {target} ({string.Join(", ", shared)}) " +
                   $"SELECT {string.Join(", ", shared)} FROM {scan}");
    }

    static long Scalar(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static void Exec(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
