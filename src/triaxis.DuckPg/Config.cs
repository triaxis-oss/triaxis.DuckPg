namespace triaxis.DuckPg;

public sealed class Config
{
    /// Where the PostgreSQL front door listens; null or empty opens no such door. Unset by
    /// default, the same as `Tds`: a library that opened a port nobody asked for would collide with
    /// the next lake in the same process. The `duckpg` command supplies its own default.
    public string? Listen { get; set; }

    /// Listen address for the TDS front door, which SqlClient speaks. Off unless it is set.
    public string? Tds { get; set; }

    /// Schema the generated views live in.
    public string Schema { get; set; } = "lake";

    /// Layer directories, lowest first. Each is scanned for `<table>.yaml`, `<table>.json`,
    /// `<table>.parquet` and `<table>/**/*.parquet` alike -- the format is a property of the file,
    /// not of the layer.
    public string[] Layers { get; set; } = [];

    /// Directory holding the topmost layer, which accepts writes. Created when missing.
    public string? Write { get; set; }

    /// Format the write layer persists a table it has no file for yet in.
    public LayerFormat WriteFormat { get; set; } = LayerFormat.Parquet;

    /// Accept writes without a write directory -- they live in memory and are lost on exit.
    public bool Writable { get; set; }

    /// Collapse every layer into a real DuckDB table at build and serve that, rather than a view
    /// over the layers. There is then no merge to bind on every read, no write branch to earn and no
    /// tombstone to hide anything: a write is a write, to the table the reads come from. Nothing is
    /// persisted -- what a materialized lake holds is lost on exit, save for the delta a write
    /// directory gets on a clean shutdown.
    public bool Materialize { get; set; }

    /// Where a materialized lake is kept, as a DuckDB database file. Without one the tables live in
    /// memory and the layers are collapsed into them on every start; with one they are collapsed
    /// once, into the file, and every start after that opens what is already there -- so a write
    /// survives by being written, rather than by being worked out again at shutdown. The layers are
    /// then only consulted for a table the file does not yet carry: the file is the state.
    /// Only meaningful with `Materialize`, which is what makes the tables tables.
    public string? Store { get; set; }

    /// Let one transaction run at a time, the next waiting for the one in front of it. A DuckDB
    /// transaction takes its catalog snapshot when it begins, so a write branch created by anybody
    /// after that is invisible to it for as long as it lives -- and it cannot create one itself
    /// either. Ordering writes alone does not close that, since the two writes need never overlap.
    /// Off by default: a lake serving readers pays nothing for DuckDB's own concurrency.
    public bool SerializeTransactions { get; set; }

    /// Key used by any table that neither names its own nor takes one from the schema.
    public string[] DefaultKey { get; set; } = [];

    /// A .dacpac whose model.xml declares the real shape: column names, order and types, and the
    /// primary key. Where it knows a table, it decides what the view publishes. When unset, a
    /// single .dacpac sitting in a layer directory is picked up on its own.
    public string? Dacpac { get; set; }

    /// Where the merged rows of a table more than one layer carries are written once, as a ZSTD
    /// parquet the view then scans. A view is bound on every execution, so a table that cannot
    /// change while the lake is up is worth binding as one file rather than as the merge that
    /// produced it. Unset means no copy is made and every query does the merge.
    public string? Cache { get; set; }

    /// Fetch the native DuckDB when the machine has none, rather than failing. Off by default:
    /// nothing is downloaded unless it was asked for. The copy lands in the local application data
    /// directory and is reused, so it costs one download per machine rather than one per run --
    /// which is the alternative to bringing `DuckDB.NET.Data.Full`, whose every-platform payload is
    /// 420 MB against the ~70 MB of the one library this actually needs.
    public bool InstallDuckDb { get; set; }

    /// DuckDB session variable -> startup parameter name ("user", "database", "application_name")
    /// or a "-c key=value" entry passed through libpq's `options`.
    public Dictionary<string, string> SessionVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// Columns added to every table that does not already have one of that name -- for the audit
    /// columns an export strips because they are bulky and say little.
    public List<ColumnConfig> Columns { get; set; } = [];

    public Dictionary<string, TableConfig> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// Paths in the file are relative to the file itself, not to the working directory.
    public void ResolvePaths(string baseDirectory)
    {
        Layers = [.. Layers.Select(l => Path.GetFullPath(l, baseDirectory))];
        if (Write is not null) Write = Path.GetFullPath(Write, baseDirectory);
        if (Store is not null) Store = Path.GetFullPath(Store, baseDirectory);
        if (Dacpac is not null) Dacpac = Path.GetFullPath(Dacpac, baseDirectory);
    }

    public TableConfig Table(string name) => Tables.GetValueOrDefault(name) ?? new TableConfig();

    /// What a lake cannot start without, checked before anything is built so the answer names the
    /// path that is wrong rather than surfacing later as an empty catalog or a binder error out of
    /// DuckDB. A layer directory that is not there is the one worth catching: nothing else notices.
    public void Validate()
    {
        if (Listen is not { Length: > 0 } && Tds is not { Length: > 0 })
            throw new DuckPgConfigurationException(
                "no front door to open: set `listen` for the PostgreSQL protocol, `tds` for SQL Server's, or both");

        foreach (var layer in Layers)
            if (!Directory.Exists(layer))
                throw new DuckPgConfigurationException($"layer directory not found: {layer}");

        if (Dacpac is { Length: > 0 } dacpac && !File.Exists(dacpac))
            throw new DuckPgConfigurationException($"dacpac not found: {dacpac}");

        // A store holds tables, and only a materialized lake has any: the views a layered one
        // publishes would be kept beside write-layer tables that the layer files also still hold,
        // and the next start would apply every write twice.
        if (Store is { Length: > 0 } && !Materialize)
            throw new DuckPgConfigurationException(
                "`store` keeps a materialized lake, so it needs `materialize`: without it a lake " +
                "publishes views over the layer files, and there is nothing in a database file to keep");

        // A filter and a `getvariable()` column are answered per session, and a table shared by
        // every session cannot carry either. Refused rather than dropped: a mode that silently
        // stopped filtering rows would be the worst way to find this out.
        if (Materialize)
        {
            foreach (var (name, table) in Tables)
                if (table.Filter is { Length: > 0 })
                    throw new DuckPgConfigurationException(
                        $"table {name} declares a `filter`, which materialize cannot bake into a " +
                        "table every session shares -- it is answered per session or not at all");

            foreach (var column in Columns.Concat(Tables.Values.SelectMany(t => t.Columns)))
                if (column.Expr is { } expr && expr.Contains("getvariable", StringComparison.OrdinalIgnoreCase))
                    throw new DuckPgConfigurationException(
                        $"column {column.Name} reads a session variable, which materialize cannot " +
                        "bake into a table every session shares");
        }

        // A cache inside a layer would be read back as part of the lake on the next build -- every
        // materialized table arriving a second time, as a layer of its own.
        if (Cache is { Length: > 0 } cache)
            foreach (var directory in (string?[])[.. Layers, Write])
                if (directory is not null && Under(cache, directory))
                    throw new DuckPgConfigurationException(
                        $"cache directory {cache} is inside the layer {directory}, " +
                        "where the lake would read its own copies back");
    }

    /// Compared as directories rather than as text, so `/lake-cache` is not a child of `/lake`.
    static bool Under(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class TableConfig
{
    /// Columns identifying a row. Required for UPDATE/DELETE, and for a row in one layer to
    /// shadow the same row in the layer below.
    public string[] Key { get; set; } = [];

    /// Opts a single table out of a writable lake, or into an otherwise read-only one.
    public bool? Writable { get; set; }

    /// Columns added on top of what the layers contain, projected in the order written.
    public List<ColumnConfig> Columns { get; set; } = [];

    /// Predicate ANDed into the view -- combined with a session variable this is row-level filtering.
    public string? Filter { get; set; }
}

public sealed class ColumnConfig
{
    public string Name { get; set; } = "";

    /// Constant value, emitted as a SQL literal.
    public string? Const { get; set; }

    /// Arbitrary SQL expression over the base columns, `_file`, or getvariable(...).
    public string? Expr { get; set; }

    /// Optional cast applied to Const/Expr.
    public string? Type { get; set; }

    /// Tables this column is *not* added to; empty means all of them. Only meaningful for the
    /// top-level defaults, where the real database has tables that never had the column.
    public string[] Except { get; set; } = [];

    /// Tables this column is added to, when naming them is shorter than naming the exceptions.
    public string[] Only { get; set; } = [];
}
