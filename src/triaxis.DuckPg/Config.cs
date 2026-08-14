namespace triaxis.DuckPg;

public sealed class Config
{
    /// Where the PostgreSQL front door listens; null or empty opens no such door. Unset by
    /// default, the same as `Tds`: a library that opened a port nobody asked for would collide with
    /// the next lake in the same process. The `duckpg` command supplies its own default.
    public string? Listen { get; set; }

    /// Listen address for the TDS front door, which SqlClient speaks. Off unless it is set.
    public string? Tds { get; set; }

    /// What a TDS packet carries, in bytes. A client asks for a size in LOGIN7 and the server's
    /// answer -- ENVCHANGE 4, in the login response -- is what both then use, so this raises a client
    /// that asked for less as much as it holds down one that asked for more. Bigger is fewer packet
    /// headers, fewer writes to the socket, and less of a packet given up by the row that ended it
    /// early to stay out of the seam; the default is the largest TDS allows, since a lake answers
    /// with rows far more often than it answers with one value. 512 to 32767. On macOS the door
    /// clips the answer to 16000 whatever is configured: the loopback MTU there is 16K, and a
    /// packet that always arrives split is what trips a fragile reassembly path in SqlClient.
    public int TdsPacketSize { get; set; } = 32767;

    /// Schema the generated views live in. Whatever it is called, it goes in front of every
    /// session's search path, so an unqualified name finds it without the caller knowing the name.
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

    /// A baked database served instead of layers: the tables a materialized lake would have built,
    /// already built, with their keys, indexes, defaults and declared views in them. It is copied on
    /// the way up and never written to -- what a run does to its copy goes out as a delta against it
    /// at shutdown, exactly as a materialized lake's does against the layers it was cut from. So
    /// nothing is scanned, described, parsed or merged to start one, which is what makes a fresh
    /// instance of the same state cheap enough to take a thousand of.
    public string? Base { get; set; }

    /// Whether the lake is served as real tables rather than as views over layers. A base is a
    /// materialized lake somebody else already collapsed, so everything true of one is true of it.
    internal bool Collapsed => Materialize || Base is { Length: > 0 };

    /// Where a materialized lake's tables live, as a DuckDB database file, rather than in memory.
    /// Only meaningful with `Materialize`, which is what makes the tables tables. What the file then
    /// means is `StoreMode`'s.
    public string? Store { get; set; }

    /// Whether the store is the lake's state or only somewhere for its tables to live. Keeping is
    /// the default, because a file the caller named is the one thing here that outlives the process
    /// and discarding it silently would be the worse surprise.
    public StoreMode StoreMode { get; set; } = StoreMode.Keep;

    /// Ask DuckDB to compress what it is holding once the lake is built. Table data is written
    /// uncompressed and stays that way until a checkpoint, which nothing drives an in-memory
    /// database to -- so a materialized lake holds every column in its widest form. One checkpoint
    /// at the end of the build is what buys that back: over 5M rows of a five-column table, 280 MB
    /// down to 64 MB for ~0.4 s of build.
    ///
    /// Off, because what it does to a read depends on the column. Filtering on a dictionary-encoded
    /// string gets faster, the comparison being against the dictionary rather than against every
    /// row -- 4.3 ms against 14.6 -- and aggregating over a bit-packed one gets slower, 6.5 ms
    /// against 3.5, since every vector is unpacked on the way out. Lookups by key and writes are
    /// unmoved. So this is memory, and a wager on which of those two a lake is read for.
    ///
    /// Only what is there when the build ends. A row written after that is held uncompressed, and
    /// nothing checkpoints a second time.
    public bool Compress { get; set; }

    /// Sort and limit a small materialized table's rows here rather than in DuckDB. DuckDB's sort
    /// costs what a row is *wide* rather than what a table is long -- ~1.3 ms plus ~50 µs a column,
    /// unchanged between twelve rows and twelve hundred -- so on the table an ORM keeps asking about
    /// it is most of the query. Only where the table is held as a table and was counted small when it
    /// was built, since the statement goes out without its `TOP` and there is no falling back once it
    /// has. Everything else is left exactly as it was.
    ///
    /// On, because what it can get wrong is how fast the answer comes rather than what the answer is:
    /// a bound that is too low sends the statement without its limit and a result too big to address
    /// in place is read out instead, which is slower and still right. The one thing it decides
    /// differently from DuckDB is which of two rows tied on the sort key comes first, and SQL leaves
    /// that unspecified in both.
    public bool SortSmallTables { get; set; } = true;

    /// Hold a declared key over what the table publishes, refusing a write that would put two rows
    /// under one key. DuckDB cannot be left to it on a layered lake: the write branch's own PRIMARY
    /// KEY sees this process's rows and nothing below them. A materialized table is keyed by DuckDB
    /// too, since it is a table and there is nothing below it.
    ///
    /// On, because the alternative is a lake that quietly answers with a row nobody wrote. It costs
    /// one scan of what the table publishes per insert and per key-moving update -- most of what a
    /// write costs on a layered lake, since there is no index over a merge -- so a lake whose writers
    /// are already known to be sending fresh keys, a bulk load out of a trusted source above all, can
    /// have that back by turning it off. What that gives back is the scan and not the rule:
    /// materialized, the table's own PRIMARY KEY refuses the row anyway, in DuckDB's words rather
    /// than the rule's. Nothing else changes: reads, deletes and ordinary updates never asked.
    public bool CheckKeys { get; set; } = true;

    /// Give every row its own value for a `NEWID()` default, instead of the one value the whole run
    /// shares. A declared default is evaluated once when the lake is built -- a view is bound on
    /// every execution, so a `uuid()` in one would answer differently every scan -- which leaves a
    /// column no file carries holding the same id in every row. That is honest for `(getdate())`,
    /// where a row in a file cannot say when it was written, and useless for an id, whose whole
    /// point is that no two rows share one.
    ///
    /// So the value is *derived* rather than generated: the row's key written into the low half of
    /// a uuid whose high half is fixed for the column. Consecutive keys give consecutive ids under a
    /// shared prefix, which is what `NEWSEQUENTIALID()` means and what an index likes. Being
    /// deterministic is what makes it usable at all -- the same id on every scan, across a restart,
    /// and equal to what the shutdown delta measures against, which a generated one would not be.
    ///
    /// Off, because it costs a scan of that column roughly 190 ms a million rows against 1 ms for
    /// the frozen value. Materialized that is paid once when the table is built and reads are free;
    /// layered it is paid by every read, and a lake serving analytics has nothing to spend it on.
    /// A table with no declared key is left alone with a warning: there is no identity to derive
    /// from, and one `--derive-ids` across a mixed lake should leave the odd table out rather than
    /// refuse to start.
    public bool DeriveIds { get; set; }

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

    /// A copy for a run that has to change what it was given: a bake into a database is a
    /// materialized lake in a file, and the configuration it was handed describes neither. Shallow,
    /// which is all it has to be -- what is overwritten is scalars, and the lists and dictionaries
    /// mean the same thing to both copies.
    internal Config Copy() => (Config)MemberwiseClone();

    /// Paths in the file are relative to the file itself, not to the working directory.
    public void ResolvePaths(string baseDirectory)
    {
        Layers = [.. Layers.Select(l => Path.GetFullPath(l, baseDirectory))];
        if (Write is not null) Write = Path.GetFullPath(Write, baseDirectory);
        if (Base is not null) Base = Path.GetFullPath(Base, baseDirectory);
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

        if (TdsPacketSize is < 512 or > 32767)
            throw new DuckPgConfigurationException(
                $"`tdsPacketSize: {TdsPacketSize}` is not a packet TDS can carry -- it is 512 to 32767 bytes");

        ValidateShape();
    }

    /// The same questions asked of a configuration nothing is served from: `duckpg bake` builds the
    /// catalog these layers describe and opens no door at all, so everything but the door is its
    /// question too.
    internal void ValidateShape()
    {
        foreach (var layer in Layers)
            if (!Directory.Exists(layer))
                throw new DuckPgConfigurationException($"layer directory not found: {layer}");

        if (Dacpac is { Length: > 0 } dacpac && !File.Exists(dacpac))
            throw new DuckPgConfigurationException($"dacpac not found: {dacpac}");

        if (Base is { Length: > 0 } baked)
        {
            if (!File.Exists(baked))
                throw new DuckPgConfigurationException($"baked database not found: {baked}");

            // A base is the whole of what the lake publishes, already merged. Layers under it would
            // have to be merged with it, which is the work it exists to have done already -- and the
            // rows it holds came from layers of its own, so this is nearly always the stack it was
            // baked from, left behind.
            // The copy keeps the base's file name, and DuckDB names the database after the file --
            // so `base: lake.duckdb` under the `lake` schema leaves every `lake.orders` ambiguous
            // between the two, exactly as a store named that way does.
            if (string.Equals(Path.GetFileNameWithoutExtension(baked), Schema, StringComparison.OrdinalIgnoreCase))
                throw new DuckPgConfigurationException(
                    $"the base names the database `{Path.GetFileNameWithoutExtension(baked)}`, which is " +
                    $"also the schema this lake publishes into -- DuckDB cannot tell the two apart, so " +
                    $"name the file something else or set `schema`");

            if (Layers.Length > 0)
                throw new DuckPgConfigurationException(
                    $"`base` serves {baked}, which is a whole lake already collapsed, and " +
                    $"{Layers.Length} layer {(Layers.Length == 1 ? "directory" : "directories")} " +
                    "were given as well -- bake them in, or serve the layers instead of the base");
        }

        // A store holds tables, and only a materialized lake has any: the views a layered one
        // publishes would be kept beside write-layer tables that the layer files also still hold,
        // and the next start would apply every write twice.
        if (Store is { Length: > 0 } && !Collapsed)
            throw new DuckPgConfigurationException(
                "`store` keeps a materialized lake, so it needs `materialize`: without it a lake " +
                "publishes views over the layer files, and there is nothing in a database file to keep");

        if (StoreMode != StoreMode.Keep && Store is not { Length: > 0 })
            throw new DuckPgConfigurationException(
                $"`storeMode: {StoreMode}` says what the store file is for, and no `store` was named");

        // DuckDB names the database after the store file, so `store: lake.duckdb` under the default
        // schema makes every `lake.orders` the catalog builds ambiguous between the two. What comes
        // out otherwise is a binder error from the first materialized table, naming neither the file
        // nor the schema -- and the obvious name for the file is the one that cannot be used.
        if (Store is { Length: > 0 } file &&
            string.Equals(Path.GetFileNameWithoutExtension(file), Schema, StringComparison.OrdinalIgnoreCase))
            throw new DuckPgConfigurationException(
                $"the store file names the database `{Path.GetFileNameWithoutExtension(file)}`, which is " +
                $"also the schema this lake publishes into -- DuckDB cannot tell the two apart, so " +
                $"name the file something else or set `schema`");

        if (Collapsed) ValidateSessionless(Base is { Length: > 0 } ? "a baked database" : "materialize");

        // A cache inside a layer would be read back as part of the lake on the next build -- every
        // materialized table arriving a second time, as a layer of its own.
        if (Cache is { Length: > 0 } cache && Inside(cache) is { } inside)
            throw new DuckPgConfigurationException(
                $"cache directory {cache} is inside the layer {inside}, " +
                "where the lake would read its own copies back");
    }

    /// A filter and a `getvariable()` column are answered per session, and rows every session shares
    /// cannot carry either -- a materialized table and a baked parquet alike. Refused rather than
    /// dropped: a mode that silently stopped filtering rows would be the worst way to find this out.
    internal void ValidateSessionless(string mode)
    {
        foreach (var (name, table) in Tables)
            if (table.Filter is { Length: > 0 })
                throw new DuckPgConfigurationException(
                    $"table {name} declares a `filter`, which {mode} cannot fold into rows every " +
                    "session shares -- it is answered per session or not at all");

        foreach (var column in Columns.Concat(Tables.Values.SelectMany(t => t.Columns)))
            if (column.Expr is { } expr && expr.Contains("getvariable", StringComparison.OrdinalIgnoreCase))
                throw new DuckPgConfigurationException(
                    $"column {column.Name} reads a session variable, which {mode} cannot fold into " +
                    "rows every session shares");
    }

    /// The layer directory a path lies in, itself included, or null when it lies outside all of
    /// them. A copy of what the lake publishes written there is read back as part of the lake on the
    /// next build.
    internal string? Inside(string path) =>
        ((string?[])[.. Layers, Write]).FirstOrDefault(d => d is { Length: > 0 } && Within(path, d));

    /// Compared as directories rather than as text, so `/lake-cache` is not a child of `/lake`.
    static bool Within(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public enum StoreMode
{
    /// The file is the state. The layers are collapsed into it once and every start after that opens
    /// what is already there, consulting them only for a table the file does not yet carry -- so a
    /// write survives by having been written rather than by being worked out again at shutdown, and
    /// no delta is left beside it.
    Keep,

    /// The file is only where the tables live. Every start collapses the layers into it afresh and
    /// every shutdown writes the delta, exactly as an in-memory materialized lake does -- what the
    /// file buys is the memory, and nothing else. A table this build no longer publishes is left in
    /// the file rather than dropped: reclaiming the space is not worth deleting something the caller
    /// may have meant to keep.
    Spill,
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
