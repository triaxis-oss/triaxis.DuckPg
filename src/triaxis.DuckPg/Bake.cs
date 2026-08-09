using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// A stack of layers written out as the parquet layer it publishes. Reading YAML or JSON means
/// parsing every file and inferring its types again on every start -- the one cost a lake pays that
/// has nothing to do with what is asked of it -- so this pays it once and leaves an ordinary layer
/// directory behind, which the runs after it stack like any other.
///
/// **Using the baked layer is semantically identical to using the layers it was baked from.** That
/// is the whole contract, and everything below is only what it takes to keep it:
///
/// - It is told the key, and so the dacpac a key can come from. Without one the layers concatenate
///   rather than shadow, and a later run keying the single file it wrote would answer differently.
/// - It is told the write directory rather than being handed it as one more layer, because a write
///   layer is the only layer whose files do not say everything it holds: its deletes are keys in a
///   `.deleted/` sidecar that the layer scan skips on purpose. Listed as an ordinary layer it would
///   bake the rows it hides back into the lake, silently. Knowing, it folds that layer in like the
///   top of the stack it is -- which is also how a write layer that has outgrown the files below it
///   is flattened back into them.
/// - It writes the table's own columns and nothing the configuration adds on top: the virtual
///   columns and declared defaults that run projected are the ones the next run projects, rather
///   than one round of them frozen into the file and a second laid over that. `Catalog.Baked` is
///   where the defaults are argued.
/// - What it cannot keep identical it refuses rather than writes -- a `filter:`, which is answered
///   per session, and `materialize`, whose tables hold every default already stamped.
///
/// It is not a lake and it is not `Cache`: no door is opened, nothing is served, and what it writes
/// is named for the table rather than for a hash of what produced it, because the point is a
/// directory a later run can be pointed at.
sealed class Bake(Config config, Catalog catalog, DuckDBConnection duck, IDuckDbInstaller installer,
                  ILogger<Bake> logger)
{
    /// What a name says a bake is to write, for a caller that did not say. A layer's format is a
    /// property of the file, and this is the same reading applied to the output: `.duckdb` is one
    /// thing and nothing else is. Only ever a default -- `BakeFormat` is what decides.
    public const string DatabaseExtension = ".duckdb";

    public static BakeFormat Inferred(string target) =>
        Path.GetExtension(target).Equals(DatabaseExtension, StringComparison.OrdinalIgnoreCase)
            ? BakeFormat.Database : BakeFormat.Parquet;

    /// The same lake, collapsed into the file being written rather than served from views. A store
    /// that is the state is exactly what a baked database is, so `Keep` is what it is built as --
    /// and the file is deleted first, or that same rule would have this build open what an earlier
    /// bake left and keep it instead of replacing it.
    internal static Config Materialized(Config config, string target)
    {
        if (Directory.Exists(target))
            throw new DuckPgConfigurationException(
                $"{target} is a directory, and a bake into a database writes one file");

        var baked = config.Copy();
        baked.Materialize = true;
        baked.Store = target;
        baked.StoreMode = StoreMode.Keep;
        return baked;
    }

    /// The lake collapsed into one database: the tables a materialized lake would serve, their keys
    /// and indexes, the declared views and macros, and the rules DuckDB has nowhere to keep. A later
    /// run copies this and serves the copy, so nothing is scanned, described, parsed or merged on
    /// the way up -- which is the whole of what it buys over a directory of parquet.
    ///
    /// A materialized table holds every declared default already stamped, and there is no reader
    /// left to stamp one: `(getdate())` in a baked database is the moment the bake ran, exactly as
    /// it is in a `--store`. That is the one way this is not the layers it came from, and
    /// `--derive-ids` is the answer for the ids among them, being derived rather than generated.
    public async Task WriteDatabaseAsync(string target, CancellationToken cancellation)
    {
        config.ValidateShape();
        config.ValidateSessionless("a baked database");

        // Replaced rather than opened: `Materialized` builds this as a store that is the state, and
        // a store that is already there is kept rather than rebuilt.
        foreach (var stale in (string[])[target, target + ".wal"])
            if (File.Exists(stale)) File.Delete(stale);

        if (config.InstallDuckDb && !DuckDbLibrary.SearchPath.Any(DuckDbLibrary.Usable))
            await installer.InstallAsync(cancellation);

        duck.Open();
        using (var macros = duck.CreateCommand())
        {
            macros.CommandText = Shims.Macros;
            macros.ExecuteNonQuery();
        }

        HostFunctions.Register(duck);
        catalog.Build(duck);
        Declare();

        // What a lake would otherwise pay for on every start, paid once here: DuckDB writes table
        // data uncompressed until something checkpoints it, and a file nobody has checkpointed is a
        // file every later run copies in full.
        Exec("CHECKPOINT");

        logger.LogInformation("baked {Tables} into {Target}",
            catalog.Tables.Count == 1 ? "1 table" : $"{catalog.Tables.Count} tables", target);
    }

    /// The part of a lake DuckDB has nowhere to hold. Columns, keys, uniques, defaults, sequences,
    /// views and macros are all in its own catalog by the time the tables are built, and a copy of
    /// the file carries them. A declared reference and its ON DELETE are duckpg's own rules, checked
    /// in .NET over the merged view, and an identity is a column the declaring schema said the store
    /// fills in -- neither is anything DuckDB would recognise, so both are written down.
    void Declare()
    {
        Exec($"CREATE SCHEMA IF NOT EXISTS {Schema}");
        Exec($"CREATE OR REPLACE TABLE {Meta} (key VARCHAR PRIMARY KEY, value VARCHAR)");
        Exec($"INSERT INTO {Meta} VALUES ('version', '1'), ('schema', {SqlText.Literal(config.Schema)})");

        Exec($"CREATE OR REPLACE TABLE {Referenced} (name VARCHAR, \"table\" VARCHAR, columns VARCHAR[], " +
             "parent VARCHAR, parent_columns VARCHAR[], on_delete VARCHAR)");
        foreach (var reference in catalog.References)
            Exec($"INSERT INTO {Referenced} VALUES ({SqlText.Literal(reference.Name)}, " +
                 $"{SqlText.Literal(reference.Table)}, {List(reference.Columns)}, " +
                 $"{SqlText.Literal(reference.Parent)}, {List(reference.ParentColumns)}, " +
                 $"{SqlText.Literal(reference.OnDelete)})");

        Exec($"CREATE OR REPLACE TABLE {Identified} (\"table\" VARCHAR, \"column\" VARCHAR)");
        foreach (var table in catalog.Tables.Values)
            foreach (var column in table.Columns.Where(c => c.Identity))
                Exec($"INSERT INTO {Identified} VALUES ({SqlText.Literal(table.Name)}, " +
                     $"{SqlText.Literal(column.Name)})");
    }

    /// A schema of duckpg's own, like `wr`, `layer` and `base` -- not the unqualified `main` the
    /// shims live in. Those are unqualified because a client has to find them: `Shims.Apply` rewrites
    /// `pg_catalog.pg_class` to a bare `duckpg_pg_class`, so being in the search path is the point.
    /// This is the opposite -- nothing should ever name it from a session, and `main` is in every
    /// session's path. A schema is also the namespace the prefix was spelling out by hand, which is
    /// why the tables below can be called what they are.
    public const string Schema = "duckpg";
    public const string Meta = $"{Schema}.meta";
    public const string Referenced = $"{Schema}.reference";
    public const string Identified = $"{Schema}.identity";

    static string List(string[] values) =>
        $"[{string.Join(", ", values.Select(SqlText.Literal))}]";

    public async Task WriteAsync(string directory, CancellationToken cancellation)
    {
        // Before anything is built, so a path that is wrong is still the subject of the answer.
        config.ValidateShape();
        config.ValidateSessionless("`duckpg bake`");

        if (config.Inside(directory) is { } layer)
            throw new DuckPgConfigurationException(
                $"output directory {directory} is inside the layer {layer}, where the next run " +
                "would read back what this one wrote as a layer of its own");

        // Collapsing the layers is what a bake is, so doing it into memory first is the same work
        // twice -- and it costs the thing a bake is careful about: a materialized table holds every
        // declared default already stamped, and one written into a file outlives the run that
        // stamped it. `store` needs `materialize`, so this answers for both. `cache` is left alone:
        // its copy is the read layers merged, which is what a bake is reading anyway.
        if (config.Materialize)
            throw new DuckPgConfigurationException(
                "`materialize` collapses the layers into tables for a lake to serve, and a bake " +
                "serves nothing -- it writes that same collapse out to files and stops. Take it " +
                "off, and `store` with it: a store is a lake's state, and a bake keeps none");

        if (config.InstallDuckDb && !DuckDbLibrary.SearchPath.Any(DuckDbLibrary.Usable))
            await installer.InstallAsync(cancellation);

        duck.Open();
        using (var macros = duck.CreateCommand())
        {
            macros.CommandText = Shims.Macros;
            macros.ExecuteNonQuery();
        }

        HostFunctions.Register(duck);
        catalog.Build(duck);

        Directory.CreateDirectory(directory);
        var written = new List<string>();

        foreach (var table in catalog.Tables.Values)
        {
            cancellation.ThrowIfCancellationRequested();

            // A table the schema declares and no layer carries publishes nothing, and the schema
            // still declares it on the next run: an empty file would only be one more thing to read.
            if (table.Layers.Count == 0 && table.WriteSource is null)
            {
                logger.LogDebug("{Table} is declared and carried by no layer; nothing to write", table.Name);
                continue;
            }

            written.Add(Write(table, directory));
        }

        Strays(directory, written);
        logger.LogInformation("baked {Tables} into {Directory}",
            written.Count == 1 ? "1 table" : $"{written.Count} tables", directory);
    }

    string Write(Table table, string directory)
    {
        // A partition column joins the key, so a table read across `db=…` is written back across
        // `db=…`. Flattened into one file it would publish `db` as an ordinary column, and the run
        // after this one would let one database's row 1 shadow another's. DuckDB writes the value
        // into the directory name and leaves it out of the file, which is what a hive layer is.
        var partitions = Partitions(table);
        var target = Path.Combine(directory, table.Name + (partitions.Length > 0 ? "" : ".parquet"));

        // Whatever a previous bake left for this table, so a partition that no longer has rows stops
        // answering with the ones it used to have. A file is replaced by the COPY itself.
        if (partitions.Length > 0 && Directory.Exists(target)) Directory.Delete(target, recursive: true);

        // ZSTD rather than none or snappy, for the reason `Cache` picks it: a third smaller for the
        // same read, and a compressed scan beats an uncompressed one outright.
        var options = "FORMAT PARQUET, COMPRESSION ZSTD" + (partitions.Length > 0
            ? $", PARTITION_BY ({string.Join(", ", partitions.Select(SqlText.Quote))})" : "");

        var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

        using var command = duck.CreateCommand();
        command.CommandText = $"COPY (SELECT {columns} FROM ({catalog.Baked(table)}) t) " +
                              $"TO {SqlText.Literal(target.Replace('\\', '/'))} ({options})";
        // What a COPY answers with is how many rows it wrote, which is the only count here that
        // costs nothing: asking the view again would be the whole merge a second time.
        logger.LogInformation("{Table} -> {Target} ({Rows} rows)",
            table.Name, target, command.ExecuteNonQuery());

        return Path.GetFileName(target);
    }

    /// The partition columns the table's layers contribute and the table actually publishes.
    static string[] Partitions(Table table) =>
        [.. table.Layers.SelectMany(l => l.Source.Partitions)
                 .Distinct(StringComparer.OrdinalIgnoreCase).Where(table.Has)];

    /// What the output directory holds that this bake did not write. A bake into a directory an
    /// earlier one filled leaves that one's tables behind, and a layer directory is read for
    /// whatever is in it -- so a table this lake no longer publishes would go on being published by
    /// the file nobody replaced. Said rather than deleted: the directory is the caller's.
    void Exec(string sql)
    {
        using var command = duck.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    void Strays(string directory, List<string> written)
    {
        var mine = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase);

        foreach (var stray in Directory.EnumerateFiles(directory, "*.parquet")
                     .Concat(Directory.EnumerateDirectories(directory))
                     .Where(path => Path.GetFileName(path) is var name
                                    && !name.StartsWith('.') && !mine.Contains(name)))
            logger.LogWarning("{Stray} was not written by this bake and is left where it is: " +
                              "the next run reads it as a layer file like any other", stray);
    }
}
