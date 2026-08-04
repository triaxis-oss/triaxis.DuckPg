namespace triaxis.Tools.DuckPg;

sealed class Config
{
    public string Listen { get; set; } = "127.0.0.1:55432";

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

    /// Key used by any table that neither names its own nor takes one from the schema.
    public string[] DefaultKey { get; set; } = [];

    /// A .dacpac whose model.xml declares the real shape: column names, order and types, and the
    /// primary key. Where it knows a table, it decides what the view publishes. When unset, a
    /// single .dacpac sitting in a layer directory is picked up on its own.
    public string? Dacpac { get; set; }

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
        if (Dacpac is not null) Dacpac = Path.GetFullPath(Dacpac, baseDirectory);
    }

    public TableConfig Table(string name) => Tables.GetValueOrDefault(name) ?? new TableConfig();
}

sealed class TableConfig
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

sealed class ColumnConfig
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
