using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg;

/// A declared default, in both of the forms it is needed in: `Expr` is what a row written now gets,
/// evaluated as it is written, and `Value` is what that expression was worth when the lake was
/// built -- which is the only honest stamp for a row in a file that predates the question.
sealed record ColumnDefault(string Expr, string Value);

/// `Identity` is a column the declaring schema says the store fills in: the caller does not send it
/// and asks for it back. Nothing else in a lake generates a value a file does not hold.
sealed record Column(string Name, string Type, ColumnDefault? Default = null, bool Identity = false);

sealed record VirtualColumn(string Name, string Expr);

/// One layer's rows for one table, as something the view can select from. `Rows` is what it turned
/// out to hold, where that was free to find out -- a materialized layer, which is everything but
/// parquet.
sealed record TableLayer(LayerSource Source, string Scan, List<Column> Columns, long? Rows = null)
{
    /// What this layer publishes, for the line a lake prints as it starts. A table nobody meant to
    /// write is visible as its shape long before anyone queries it -- a file whose rows were meant
    /// to be rows publishes one of them -- and unlike every check that could be made this guesses
    /// nothing.
    ///
    /// The columns are named only where there are few enough for the name to be the point: a shape
    /// narrow enough to be a mistake is narrow enough to name.
    public string Shape =>
        $"({(Rows is { } rows ? $"{Count(rows, "row")}, " : "")}{Count(Columns.Count, "column")}" +
        $"{(Columns.Count is > 0 and <= 3 ? ": " + string.Join(", ", Columns.Select(c => c.Name)) : "")})";

    static string Count(long n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}

/// A table as published: the layers it stacks, the shape it shows, and how a row is identified.
sealed record Table(
    string Schema,
    string Name,
    List<TableLayer> Layers,
    List<Column> Columns,
    List<VirtualColumn> Virtuals,
    string[] Key,
    bool Writable,
    LayerSource? WriteSource,
    string? Filter,
    bool Materialized = false)
{
    public string QualifiedName => $"{SqlText.Quote(Schema)}.{SqlText.Quote(Name)}";

    /// A materialized table is written to where it is read from -- there is no branch above it,
    /// because there is nothing below it either.
    public string WriteName => Materialized
        ? QualifiedName
        : $"{SqlText.Quote(WriteLayer.Schema)}.{SqlText.Quote(Name)}";
    public string TombstoneName => $"{SqlText.Quote(WriteLayer.Schema)}.{SqlText.Quote(Name + "__del")}";

    public bool Has(string column) => Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
}

/// Publishes each table as one view over its layers: the lowest layer at the bottom, the write
/// layer on top, and -- where a key is declared -- the topmost row for a key winning.
internal sealed class Catalog(Config config, WriteLayer write, DacpacSchema schema, ILogger<Catalog> logger)
{
    public Dictionary<string, Table> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// The same tables as types alone, which is all a statement being translated needs of them: a
    /// snapshot rather than a view of the live catalog, so a rebuild cannot change what a statement
    /// half-translated is being translated against.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Types { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// Which writable tables actually carry a write branch. A view is bound on every execution, so
    /// the branch and the tombstone check are paid for by every read of a table nobody has written
    /// to -- which, on a lake serving mostly reads, is most of them.
    readonly HashSet<string> promoted = new(StringComparer.OrdinalIgnoreCase);

    /// Which of those have ever hidden a row below. The tombstone check costs the same whatever the
    /// table looks like -- one subquery over one key column -- so it is worth not binding until a
    /// row has actually been hidden.
    readonly HashSet<string> tombstoned = new(StringComparer.OrdinalIgnoreCase);

    /// Where each cached table's copy landed. A write does not invalidate it -- the copy is the read
    /// layers, which no write touches -- so it stays underneath the write branch.
    readonly Dictionary<string, string> copies = new(StringComparer.OrdinalIgnoreCase);

    /// How many rows each materialized table holds, as an upper bound. Taken once when the table is
    /// built and grown by what an insert says it added -- nothing else makes a table bigger, so the
    /// bound stays true for the life of the process without ever costing a query. Only materialized
    /// tables are counted: on a merge view `count(*)` is the whole merge, which is more than the
    /// question is worth.
    readonly Dictionary<string, long> rows = new(StringComparer.OrdinalIgnoreCase);

    bool flushed;

    /// Schema holding the materialized YAML and JSON layers.
    public const string LayerSchema = "layer";

    /// Where a materialized lake keeps the merge it was cut from -- unread while it serves, and the
    /// baseline the shutdown delta is measured against.
    public const string BaseSchema = "base";

    public void Build(DuckDBConnection conn)
    {
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {SqlText.Quote(config.Schema)}");
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {LayerSchema}");
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {WriteLayer.Schema}");
        if (config.Materialize) Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {BaseSchema}");

        if (config.Base is { Length: > 0 }) Adopt(conn); else FromLayers(conn);

        Types = Tables.ToDictionary(
            table => table.Key,
            table => (IReadOnlyDictionary<string, string>)table.Value.Columns.ToDictionary(
                column => column.Name, column => column.Type, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        // A baked database already holds the views and the macros a dacpac declared -- they were
        // created when it was baked, and a copy of the file carries them. What has to be found again
        // is which macros those are, since that is what decides whether a call resolves onto the
        // lake or is left as the caller wrote it.
        if (config.Base is { Length: > 0 }) Adopted(conn);
        else { Declared(); Macros(conn); Declared(conn); }

        Empty();

        // DuckDB writes table data uncompressed and compresses it at a checkpoint, which no WAL
        // drives an in-memory database to -- so what a materialized lake collapsed its layers into
        // stays in the widest form of every column until something asks. Asked once here rather
        // than per table: a checkpoint is the whole database's, so it also covers the tables a YAML
        // or JSON layer was read into.
        if (config.Compress) Exec(conn, "CHECKPOINT");
    }

    void FromLayers(DuckDBConnection conn)
    {
        foreach (var (name, sources, declaredWrite) in Sources())
        {
            var settings = config.Table(name);

            // Whether a file's rows are mapping entries is a property of the file, but which column
            // the mapping keys fill is the table's -- so it is decided here, where the key is known,
            // and carried on the source rather than worked out again wherever one is read. The same
            // reading of the file says what is worth warning about, which is why it is read once
            // here and handed to both.
            var keyed = MappingKey(name, settings);
            var shapes = sources.Select(Layer.Shapes).ToList();
            var writeShapes = Layer.Shapes(declaredWrite);
            foreach (var files in shapes.Append(writeShapes)) Diagnose(name, keyed, files);

            var layers = Materialize(conn, name,
                [.. sources.Zip(shapes, (source, files) => Layer.Keyed(source, keyed, files)!)]);
            var writeSource = Layer.Keyed(declaredWrite, keyed, writeShapes);
            var describedWrite = writeSource is null ? null
                : new TableLayer(writeSource, "", Layer.Columns(conn, writeSource));

            List<Column> columns = schema.Columns(name) is { } declared
                ? [.. declared.Select(c => Defaulted(conn, name, c))]
                : Published(describedWrite is null ? layers : [.. layers, describedWrite]);
            var writable = settings.Writable ?? write.Enabled;
            var partitions = layers.SelectMany(l => l.Source.Partitions)
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var table = new Table(config.Schema, name, layers, columns,
                                  Virtuals(name, settings, columns),
                                  KeyFor(name, settings, columns, partitions),
                                  writable || config.Collapsed, writeSource, settings.Filter,
                                  config.Collapsed);
            Diagnose(table);

            if (table.Materialized)
            {
                Materialize(conn, table);
                Tables[name] = table;
                continue;
            }

            // A writable table the directory holds nothing for is published as though it were not
            // writable, and grows its write branch when something first writes to it.
            var carries = table.Writable && write.Carries(table);
            if (carries) { write.Prepare(conn, table); promoted.Add(name); }
            if (carries && write.HasTombstones(table)) tombstoned.Add(name);

            if (!carries && Cached(conn, table) is { } file) copies[name] = file;
            Exec(conn, ViewDefinition(table, carries, tombstoned.Contains(name)));
            if (carries) foreach (var sequence in Sequences(conn, table)) Exec(conn, sequence);
            Tables[name] = table;
        }
    }

    // ---- a baked database ------------------------------------------------------------------------

    /// A lake taken from a database somebody baked rather than built from layers. Everything a start
    /// otherwise pays for -- describing every file, parsing a dacpac, evaluating the defaults,
    /// collapsing the stack, building the keys -- has already been paid, once, and what is left is
    /// to read back what it came to. The copy this is reading is the run's own, so it is served and
    /// written to directly, exactly as a materialized lake's tables are.
    ///
    /// DuckDB's own catalog answers for the shape, the keys and the indexes. What it has nowhere to
    /// hold, `Bake` wrote down beside them.
    void Adopt(DuckDBConnection conn)
    {
        Exec(conn, $"ATTACH {SqlText.Literal(config.Base!)} AS {BaseCatalog} (READ_ONLY)");

        // Any DuckDB file will attach; only one this wrote can be served, and the difference is
        // everything DuckDB has nowhere to hold. Said by name rather than surfacing later as a
        // missing table nobody named.
        if (Scalar(conn, $"SELECT count(*) FROM duckdb_tables() WHERE database_name = " +
                         $"{SqlText.Literal(BaseCatalog)} AND schema_name = {SqlText.Literal(Bake.Schema)} " +
                         $"AND table_name = 'meta'") is not { } found || Convert.ToInt64(found) == 0)
            throw new DuckPgConfigurationException(
                $"{config.Base} is a DuckDB database but not a baked one -- it carries no " +
                $"`{Bake.Meta}`, so nothing in it says what a lake is to publish. `duckpg bake " +
                "--out <file>.duckdb` writes one that does");

        if (Stamp(conn, "version") is { } version && version != "1")
            throw new DuckPgConfigurationException(
                $"{config.Base} was baked as version {version} and this duckpg speaks 1 -- bake it again");

        if (Stamp(conn, "schema") is { } baked && !Same(baked, config.Schema))
            throw new DuckPgConfigurationException(
                $"the base publishes into `{baked}` and this lake publishes into `{config.Schema}` -- " +
                "a baked database holds its tables in the schema it was baked with, so serve it as " +
                $"that one or bake it again as `{config.Schema}`");

        var identities = Identities(conn);
        var keys = Keys(conn);
        var shapes = Shapes(conn, identities);
        var counted = Counted(conn);
        var writes = Written();

        foreach (var name in Adopting(conn))
        {
            var settings = config.Table(name);
            var columns = shapes.GetValueOrDefault(name) ?? [];
            var key = settings.Key is { Length: > 0 } configured ? configured
                    : keys.GetValueOrDefault(name) ?? [];

            // The one layer a base still has under -- over, rather -- is the write layer, which is
            // where the run before this one left what it did. Without its source a table carries
            // only the keys that were deleted, since those are a sidecar the scan finds anyway.
            var written = writes.GetValueOrDefault(name);
            var writeSource = Layer.Keyed(written, MappingKey(name, settings), Layer.Shapes(written));

            var table = new Table(config.Schema, name, [], columns, Virtuals(name, settings, columns),
                                  key, settings.Writable ?? true, writeSource, settings.Filter,
                                  Materialized: true);

            // Nothing is earned: the branch a write would make is the table itself, which is there.
            promoted.Add(name);
            // Counted before the write layer goes in and grown by what it adds, since asking a
            // table how many rows it has is a scan and this is 300 of them: 126 ms against the 2 of
            // reading what the storage already knows. It is a bound rather than a tally anyway --
            // one that is too low costs a sort in DuckDB rather than an answer that is wrong.
            rows[name] = counted.GetValueOrDefault(name);
            // Not into a store that is the state: its delta was applied the run it was written, and
            // applying it again would put those rows back over whatever has been written since.
            if (!Keeping) Apply(conn, table);
            Tables[name] = table;
        }
    }

    /// How many rows each table holds, out of what DuckDB already recorded when the bake wrote it.
    Dictionary<string, long> Counted(DuckDBConnection conn)
    {
        var counted = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using var command = conn.CreateCommand();
        command.CommandText = "SELECT table_name, estimated_size FROM duckdb_tables() " +
                              "WHERE database_name = current_database() AND schema_name = ?";
        command.Parameters.Add(new DuckDBParameter(config.Schema));

        using var reader = command.ExecuteReader();
        while (reader.Read()) counted[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1));
        return counted;
    }

    /// What the write directory holds, by table. A write layer is scanned like any other -- it is
    /// one -- and it is the only directory a base is served with.
    Dictionary<string, LayerSource> Written()
    {
        var written = new Dictionary<string, LayerSource>(StringComparer.OrdinalIgnoreCase);
        if (write.Directory is not { Length: > 0 } directory) return written;

        System.IO.Directory.CreateDirectory(directory);
        foreach (var (name, source) in Layer.Scan(directory, write.Seq, logger)) written[name] = source;
        return written;
    }

    /// What a run's own copy owes the write layer before it serves: the rows a previous run left in
    /// it, and the keys it hid. A baked database is the state as it was baked, so a delta beside it
    /// is a delta that has not been applied yet -- and applying it is the whole of what makes a
    /// restart mean anything.
    void Apply(DuckDBConnection conn, Table table)
    {
        var stacked = table with { Materialized = false };
        if (!table.Writable || !write.Carries(stacked)) return;

        write.Prepare(conn, stacked);
        var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

        if (table.Key.Length > 0)
        {
            var match = string.Join(" AND ", table.Key.Select(k =>
                $"t.{SqlText.Quote(k)} IS NOT DISTINCT FROM d.{SqlText.Quote(k)}"));

            // A tombstone hides a row of the base, which here means the row goes.
            Exec(conn, $"DELETE FROM {table.QualifiedName} t WHERE EXISTS " +
                       $"(SELECT 1 FROM {stacked.TombstoneName} d WHERE {match})");
            // And a written row shadows one of the base, which here means it replaces it.
            Exec(conn, $"DELETE FROM {table.QualifiedName} t WHERE EXISTS " +
                       $"(SELECT 1 FROM {stacked.WriteName} d WHERE {match})");
        }

        Exec(conn, $"INSERT INTO {table.QualifiedName} ({columns}) SELECT {columns} FROM {stacked.WriteName}");
        rows[table.Name] = Count(conn, table);

        // Past whatever the base handed out, since the write layer may have taken keys of its own --
        // and replaced rather than left alone, which `IF NOT EXISTS` would do to one already there.
        foreach (var column in table.Columns.Where(c => c.Identity))
            Exec(conn, $"CREATE OR REPLACE SEQUENCE {Sequence(table, column)} START " +
                       $"{Convert.ToInt64(Scalar(conn, $"SELECT coalesce(max({SqlText.Quote(column.Name)}), 0) + 1 FROM {table.QualifiedName}"))}");
    }

    /// The references the bake wrote down, and the macros the copy already carries. Both are what
    /// `Declared` works out from a dacpac for a lake built from layers; here the answer is in the
    /// file, which is the point of the file.
    void Adopted(DuckDBConnection conn)
    {
        pointing.Clear();
        using (var command = conn.CreateCommand())
        {
            command.CommandText = $"SELECT name, \"table\", columns, parent, parent_columns, on_delete " +
                                  $"FROM {BaseCatalog}.{Bake.Referenced}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var reference = new Reference(reader.GetString(0), reader.GetString(1), Strings(reader, 2),
                                              reader.GetString(3), Strings(reader, 4), reader.GetString(5));
                if (Tables.ContainsKey(reference.Table) && Tables.ContainsKey(reference.Parent))
                    Pointing(reference.Parent).Add(reference);
            }
        }

        macros.Clear();
        foreach (var name in Query(conn,
            "SELECT function_name FROM duckdb_functions() WHERE database_name = current_database() " +
            "AND schema_name = " + SqlText.Literal(config.Schema) + " AND function_type = 'macro'"))
            macros.Add(name);
    }

    /// Tables rather than views: a declared view the bake published is served by the copy as it is,
    /// and is nothing this has to publish again.
    ///
    /// Scoped to the database being served, like everything else that reads the catalog here. The
    /// base is attached under the same schema name holding the same tables, and DuckDB's catalog
    /// views span every attached database -- so without this each table is found twice and each of
    /// its columns arrives twice with it.
    IEnumerable<string> Adopting(DuckDBConnection conn) =>
        Query(conn, "SELECT table_name FROM information_schema.tables WHERE table_catalog = current_database() " +
                    "AND table_schema = " + SqlText.Literal(config.Schema) +
                    " AND table_type = 'BASE TABLE' ORDER BY table_name");

    /// Every table's columns, in one question. Asked per table instead it is the slowest thing a
    /// baked lake does by an order of magnitude: DuckDB answers `information_schema.columns` by
    /// scanning the whole catalog, so 300 tables asking about themselves cost 4.3 s against the
    /// 17 ms of one query naming none of them.
    Dictionary<string, List<Column>> Shapes(DuckDBConnection conn, Dictionary<string, string[]> identities)
    {
        var shapes = new Dictionary<string, List<Column>>(StringComparer.OrdinalIgnoreCase);

        using var command = conn.CreateCommand();
        command.CommandText =
            "SELECT table_name, column_name, data_type FROM information_schema.columns " +
            "WHERE table_catalog = current_database() AND table_schema = ? ORDER BY table_name, ordinal_position";
        command.Parameters.Add(new DuckDBParameter(config.Schema));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (table, column) = (reader.GetString(0), reader.GetString(1));
            if (!shapes.TryGetValue(table, out var columns)) shapes[table] = columns = [];
            columns.Add(new Column(column, reader.GetString(2), Identity:
                (identities.GetValueOrDefault(table) ?? []).Contains(column, StringComparer.OrdinalIgnoreCase)));
        }
        return shapes;
    }

    /// The key DuckDB is already holding, which is the one the bake put there.
    Dictionary<string, string[]> Keys(DuckDBConnection conn)
    {
        var keys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT table_name, constraint_column_names FROM duckdb_constraints() " +
                              "WHERE database_name = current_database() AND schema_name = ? " +
                              "AND constraint_type = 'PRIMARY KEY'";
        command.Parameters.Add(new DuckDBParameter(config.Schema));

        using var reader = command.ExecuteReader();
        while (reader.Read()) keys[reader.GetString(0)] = Strings(reader, 1);
        return keys;
    }

    Dictionary<string, string[]> Identities(DuckDBConnection conn)
    {
        var identities = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var command = conn.CreateCommand();
        command.CommandText = $"SELECT \"table\", \"column\" FROM {BaseCatalog}.{Bake.Identified}";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!identities.TryGetValue(reader.GetString(0), out var columns))
                identities[reader.GetString(0)] = columns = [];
            columns.Add(reader.GetString(1));
        }
        return identities.ToDictionary(i => i.Key, i => i.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    string? Stamp(DuckDBConnection conn, string key)
    {
        using var command = conn.CreateCommand();
        command.CommandText = $"SELECT value FROM {BaseCatalog}.{Bake.Meta} WHERE key = ?";
        command.Parameters.Add(new DuckDBParameter(key));
        return command.ExecuteScalar() as string;
    }

    static string[] Strings(DbDataReader reader, int column) =>
        reader.GetFieldValue<List<string>>(column).ToArray();

    static List<string> Query(DuckDBConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var answers = new List<string>();
        while (reader.Read()) answers.Add(reader.GetString(0));
        return answers;
    }

    static object? Scalar(DuckDBConnection conn, string sql)
    {
        using var command = conn.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// A lake holding nothing answers every query with "no such table", and nothing in that answer
    /// says the directory was what went wrong. It is not an error -- `CALL duckpg_reload` reads the
    /// layers again, so a lake started before its files arrive is something someone may mean -- but
    /// it is never what a bare `duckpg` meant, and that is the shape this is usually reached in.
    void Empty()
    {
        if (Tables.Count > 0) return;

        string?[] searched = [.. config.Layers, write.Directory];
        logger.LogWarning("this lake publishes no tables: {Reason}",
            searched.Where(d => d is { Length: > 0 }).ToList() is { Count: > 0 } directories
                ? $"nothing was found in {string.Join(", ", directories)}"
                : "no layer directory was given -- set `layers`, or name one on the command line");
    }

    public void Rebuild(DuckDBConnection conn)
    {
        Tables.Clear();
        rows.Clear();
        promoted.Clear();
        tombstoned.Clear();
        copies.Clear();
        Build(conn);
    }

    /// The upper bound on a materialized table's rows, or null for anything not counted.
    public long? Rows(string name) => rows.TryGetValue(name, out var bound) ? bound : null;

    /// What an insert said it wrote. Only ever grows the bound, so it stays an upper bound even
    /// where the count is not exact.
    public void Grew(string name, int added)
    {
        if (added > 0 && rows.TryGetValue(name, out var bound)) rows[name] = bound + added;
    }

    public Table? Resolve(string? schema, string name) =>
        (schema is null || schema.Equals(config.Schema, StringComparison.OrdinalIgnoreCase))
        && Tables.TryGetValue(name, out var table) ? table : null;

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---- discovery -------------------------------------------------------------------------------

    /// Every table any layer carries, keyed case-insensitively -- an export that disagrees with
    /// itself about capitalization (`ORDER_Lines` vs `Order_Lines`) still lands on one table.
    IEnumerable<(string Name, List<LayerSource> Layers, LayerSource? Write)> Sources()
    {
        var sources = new Dictionary<string, (List<LayerSource> Layers, LayerSource? Write)>(
            StringComparer.OrdinalIgnoreCase);

        (List<LayerSource> Layers, LayerSource? Write) Entry(string name) =>
            sources.TryGetValue(name, out var found) ? found : ([], null);

        foreach (var (index, directory) in config.Layers.Index())
            foreach (var (name, source) in Layer.Scan(directory, index, logger))
            {
                var entry = Entry(name);
                entry.Layers.Add(source);
                sources[name] = entry;
            }

        if (write.Directory is { } writeDirectory)
        {
            // A read layer must already be there; the write layer is where rows are going, so it is
            // made here rather than demanded of whoever configured it.
            Directory.CreateDirectory(writeDirectory);

            foreach (var (name, source) in Layer.Scan(writeDirectory, write.Seq, logger))
                sources[name] = Entry(name) with { Write = source };
        }

        // A table the schema declares but no layer carries is still part of the shape: publish it
        // empty, so the catalog is the schema rather than a list of which files turned up.
        foreach (var declared in schema.Tables)
            if (!sources.ContainsKey(declared)) sources[declared] = ([], null);

        return sources.Select(s => (s.Key, s.Value.Layers, s.Value.Write))
                      .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// Parquet is scanned where it lies -- that is what the format is for. Everything else is
    /// materialized once, so a query neither re-reads the file nor pays type inference again.
    List<TableLayer> Materialize(DuckDBConnection conn, string name, List<LayerSource> sources)
    {
        var layers = new List<TableLayer>();
        foreach (var source in sources.OrderBy(s => s.Seq))
        {
            if (source.Format == LayerFormat.Parquet)
            {
                layers.Add(new TableLayer(source, Layer.Query(source, source.Path, source.Hive),
                                          Layer.Columns(conn, source)));
                continue;
            }

            var materialized = $"{LayerSchema}.{SqlText.Quote($"{name}#{source.Seq}")}";
            var (columns, rows) = Layer.Materialize(conn, materialized, source);
            if (columns.Count > 0) layers.Add(new TableLayer(source, materialized, columns, rows));
        }
        return layers;
    }

    /// What a layer file's shape is worth saying out loud. Neither of these is an error: both files
    /// publish exactly what the documented rule says they publish, and a lake that unwrapped one
    /// instead would be guessing -- right most of the time and, the rest of the time, wrong with no
    /// more signal than there is now. Both are what a file looks like when its author meant the
    /// other thing, so what is left is to say so before anyone reads the answer.
    void Diagnose(string table, string? key, List<(string File, YamlShape Shape)> shapes)
    {
        foreach (var (file, shape) in shapes)
            if (shape.Wraps is { } wrapper)
                logger.LogWarning("{File} publishes one row whose only column is '{Column}', a list of " +
                                  "{Objects}: remove that top-level key if those were meant to be the " +
                                  "rows", file, wrapper,
                                  shape.Wrapped == 1 ? "1 object" : $"{shape.Wrapped} objects");
            else if (shape.Keyed && key is null)
                logger.LogWarning("{File} names its rows, and {Table} has no single key column for the " +
                                  "entry keys to fill: it publishes one row whose columns are the entry " +
                                  "names -- name a key with --key, `tables:` or a dacpac, or write the " +
                                  "entries as a sequence", file, table);
    }

    // ---- declared views --------------------------------------------------------------------------

    /// What points at each published table, by the table pointed at, each carrying the action a
    /// delete from that table performs for it. The action is the *resolved* one rather than the
    /// declared one: what this lake cannot perform is demoted to the refusal a plain reference gets,
    /// and that is decided once here rather than at every delete. Worked out as soon as the tables
    /// are known, since a reference this cannot enforce is worth saying so about at startup rather
    /// than at the first delete -- and a lake made of some of a database's tables has plenty.
    readonly Dictionary<string, List<Reference>> pointing = new(StringComparer.OrdinalIgnoreCase);

    /// The declared scalar functions that were actually published as macros. A call to one of these
    /// is resolved onto the lake; a call to anything else is left as it was written.
    readonly HashSet<string> macros = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Functions => macros;

    /// Every reference this lake actually holds, with the action already resolved to one it can
    /// perform. What a bake writes down, since a database carries a lake's tables and DuckDB has
    /// nowhere to put a rule duckpg enforces itself.
    public IEnumerable<Reference> References => pointing.Values.SelectMany(references => references);

    public const string NoAction = "NoAction";
    public const string Cascade = "Cascade";
    public const string SetNull = "SetNull";
    public const string SetDefault = "SetDefault";

    /// What a delete can actually do about a reference. Anything else the schema names is carried
    /// as itself so it can be warned about by the name it was written with.
    static readonly HashSet<string> Performed =
        new(StringComparer.OrdinalIgnoreCase) { NoAction, Cascade, SetNull, SetDefault };

    static bool Does(Reference reference, string action) =>
        reference.OnDelete.Equals(action, StringComparison.OrdinalIgnoreCase);

    List<Reference> Pointing(Table table) => pointing.GetValueOrDefault(table.Name) ?? [];

    /// The references a delete from this table has to answer for.
    public IEnumerable<Reference> Referencing(Table table) => Pointing(table).Where(r => Does(r, NoAction));

    /// The references a delete from this table performs by deleting one table down.
    public IEnumerable<Reference> Cascading(Table table) => Pointing(table).Where(r => Does(r, Cascade));

    /// And the ones it performs by leaving the rows where they are and emptying what pointed.
    public IEnumerable<Reference> Clearing(Table table) =>
        Pointing(table).Where(r => Does(r, SetNull) || Does(r, SetDefault));

    void Declared()
    {
        pointing.Clear();

        foreach (var declared in schema.References)
        {
            // A lake is usually some of a database's tables, so a reference to or from one it does
            // not publish is ordinary rather than wrong.
            if (!Tables.TryGetValue(declared.Table, out var child) ||
                !Tables.TryGetValue(declared.Parent, out var parent)) continue;

            if (!Performed.Contains(declared.OnDelete))
            {
                logger.LogWarning("{Reference} declares ON DELETE {Action}, which duckpg does not do: " +
                                  "a delete from {Parent} is left to say what it says",
                                  declared.Name, declared.OnDelete, declared.Parent);
                continue;
            }

            // What a delete collects before the rows go is the table's key, so that is the only
            // thing a reference can be checked against here.
            if (!parent.Key.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                    declared.ParentColumns.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
            {
                logger.LogWarning("{Reference} points at {Columns} of {Parent}, which is not its key: " +
                                  "duckpg checks a reference against the key a delete collects",
                                  declared.Name, string.Join(", ", declared.ParentColumns), declared.Parent);
                continue;
            }

            if (declared.Columns.Any(column => !child.Has(column))) continue;

            // Performing anything at all is a write to the child, and a write needs what any write
            // needs. Where it cannot be made the reference is kept as one that refuses instead:
            // orphaning the rows is the one answer that is wrong whichever way it is reached.
            var reference = declared;
            if (!Does(reference, NoAction) && Unperformable(reference, child) is { } why)
            {
                logger.LogWarning("{Reference} declares ON DELETE {Action}, which duckpg cannot perform: " +
                                  "{Child} {Why}, so a delete from {Parent} is refused instead",
                                  reference.Name, reference.OnDelete, child.Name, why, parent.Name);
                reference = reference with { OnDelete = NoAction };
            }

            Pointing(parent.Name).Add(reference);
        }

        Acyclic();

        foreach (var group in pointing.Values.SelectMany(references => references)
                     .GroupBy(reference => reference.OnDelete, StringComparer.OrdinalIgnoreCase))
            logger.LogDebug("{Count} declared references answer ON DELETE {Action}", group.Count(), group.Key);
    }

    List<Reference> Pointing(string table) =>
        pointing.TryGetValue(table, out var references) ? references : pointing[table] = [];

    /// Why a reference's declared action cannot be carried out, or null when it can. A cascade
    /// deletes the child rows and a clear rewrites them; either way something has to be written to
    /// the child, and shadowing a row in a layer below it takes a key.
    static string? Unperformable(Reference reference, Table child) =>
        !child.Writable ? "is not writable"
        : child.Key.Length == 0 ? "has no key, and the rows have to shadow what is beneath them"
        : Does(reference, Cascade) ? null
        // Emptying a key column would collapse every cleared row onto one key and leave the rows
        // they used to shadow uncovered -- a move, not a rewrite, and not one a caller asked for.
        : reference.Columns.Intersect(child.Key, StringComparer.OrdinalIgnoreCase).Any()
            ? $"points with {string.Join(", ", reference.Columns.Intersect(child.Key, StringComparer.OrdinalIgnoreCase))}, which is part of its own key"
        : null;

    /// A cascade that could reach the table it started from would not end. SQL Server refuses to
    /// declare one at all; this demotes it to the refusal a plain reference gets, which is the same
    /// answer arrived at later. A clear cannot loop: the rows it touches stay where they are.
    void Acyclic()
    {
        foreach (var (parent, references) in pointing)
            for (var i = 0; i < references.Count; i++)
                if (Does(references[i], Cascade) && Reaches(references[i].Table, parent))
                {
                    logger.LogWarning("{Reference} cascades from {Parent} back to itself, which cannot " +
                                      "terminate: a delete from {Parent} is refused instead",
                                      references[i].Name, parent, parent);
                    references[i] = references[i] with { OnDelete = NoAction };
                }
    }

    bool Reaches(string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([from]);
        while (pending.TryPop(out var table))
        {
            if (table.Equals(to, StringComparison.OrdinalIgnoreCase)) return true;
            if (!seen.Add(table)) continue;
            foreach (var reference in pointing.GetValueOrDefault(table) ?? [])
                if (Does(reference, Cascade)) pending.Push(reference.Table);
        }
        return false;
    }

    /// The dacpac's own views, published beside the tables they read: the query is T-SQL, so it is
    /// translated on the tree like any statement a client sends, and `dbo` lands on the lake.
    ///
    /// A view may select from another view the model happens to list later, and the model says
    /// nothing about which. Rather than order them, each round creates what it can and the next
    /// round tries the rest -- a round that adds nothing is one where what is left is broken rather
    /// than merely early, and those are named and skipped instead of stopping the lake.
    /// A declared scalar function, published as a macro beside the tables it reads. A macro is an
    /// expression, so only a body that answers with one can become one: anything with a variable, a
    /// branch or a second statement is a procedure, and is left undeclared and said so at startup
    /// rather than at the first call. The body is translated on the tree like any other T-SQL, with
    /// `@parameter` rendering as the macro's own parameter rather than as a value the caller bound.
    ///
    /// DuckDB binds a macro when it is created and not when it is called, so one that calls another
    /// has to be made second -- which is what a pass that stops making progress settles, exactly as
    /// it does for the views below.
    void Macros(DuckDBConnection conn)
    {
        macros.Clear();
        var declared = new HashSet<string>(schema.Functions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var function in schema.Functions)
            try
            {
                if (Returned(function.Body) is not { } answer)
                    throw new InvalidOperationException(
                        "its body is more than one RETURN, and a macro is an expression");

                var context = new TSqlContext(
                    config.Schema, new Dictionary<string, string>(),
                    new HashSet<string>(function.Parameters, StringComparer.OrdinalIgnoreCase),
                    Environment.UserName, Types, declared, Macro: true);

                var body = TSqlWriter.Write(TSqlParser.ParseExpression(answer), context);
                var parameters = string.Join(", ", function.Parameters.Select(SqlText.Quote));

                // Cast to what it was declared to return: a function returning `int` whose body adds
                // two of them is an int in SQL Server and a BIGINT here, and a caller reading the
                // scalar as an int throws on the difference.
                pending[function.Name] =
                    $"CREATE OR REPLACE MACRO {SqlText.Quote(config.Schema)}.{SqlText.Quote(function.Name)}" +
                    $"({parameters}) AS (CAST({body} AS {function.ReturnType}))";
            }
            catch (Exception e)
            {
                refused[function.Name] = e.Message.ReplaceLineEndings(" ");
            }

        while (pending.Count > 0)
        {
            var published = 0;
            foreach (var (name, statement) in pending.ToList())
                try
                {
                    Exec(conn, statement);
                    pending.Remove(name);
                    macros.Add(name);
                    published++;
                }
                catch (Exception e)
                {
                    refused[name] = e.Message.ReplaceLineEndings(" ");
                }

            if (published == 0) break;
        }

        foreach (var name in declared.Where(n => !macros.Contains(n)))
            logger.LogWarning("function {Function} is not published: {Reason}",
                              name, refused.GetValueOrDefault(name, "unknown"));
    }

    /// The one body a macro can carry: an answer, and nothing else. DacFx stores every function's
    /// body wrapped in `BEGIN … END`, so that is peeled rather than required -- and the trailing
    /// `END` of a `CASE` survives it, since only the outermost pair goes.
    static string? Returned(string body)
    {
        var text = body.Trim();
        if (text.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith("END", StringComparison.OrdinalIgnoreCase))
            text = text[5..^3].Trim();

        if (!text.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase)) return null;

        // `RETURNS`, or a column called `RETURNED`, is not the keyword.
        var rest = text[6..];
        if (rest.Length > 0 && (char.IsLetterOrDigit(rest[0]) || rest[0] == '_')) return null;

        return rest.Trim().TrimEnd(';').Trim() is { Length: > 0 } answer ? answer : null;
    }

    void Declared(DuckDBConnection conn)
    {
        var context = new TSqlContext(config.Schema, new Dictionary<string, string>(),
                                      new HashSet<string>(), Environment.UserName, Types, macros);
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, query) in schema.Views)
            if (Tables.ContainsKey(name)) logger.LogWarning("{View} is declared as a view and carried as a table", name);
            else pending[name] = query;

        while (pending.Count > 0)
        {
            var published = 0;
            foreach (var (name, query) in pending.ToList())
                try
                {
                    var translated = TSqlTranslator.Translate(query, context);
                    if (translated.Count != 1)
                        throw new InvalidOperationException($"a view is one query, not {translated.Count}");

                    Exec(conn, $"CREATE OR REPLACE VIEW {SqlText.Quote(config.Schema)}.{SqlText.Quote(name)} " +
                                  $"AS {translated[0].Sql}");
                    pending.Remove(name);
                    published++;
                }
                catch (Exception e)
                {
                    refused[name] = e.Message.ReplaceLineEndings(" ");
                }

            if (published == 0) break;
        }

        foreach (var name in pending.Keys)
            logger.LogWarning("view {View} skipped: {Reason}", name, refused.GetValueOrDefault(name, "unknown"));
    }

    // ---- declared defaults -----------------------------------------------------------------------

    /// What each declared default came to, kept for the life of the process: a rebuild republishes
    /// the view with the value this instance started with rather than stamping a new one.
    readonly Dictionary<(string Expression, string Type), ColumnDefault?> evaluated = new();

    /// A table asked for per-row ids that has no key to derive one from. Warned rather than refused:
    /// one `--derive-ids` across a mixed lake should leave the odd table out, exactly as one `--key`
    /// does, and what it leaves is the frozen value the lake had before it was asked.
    void Diagnose(Table table)
    {
        if (!config.DeriveIds || table.Key.Length > 0) return;

        foreach (var column in table.Columns.Where(c => c.Default is { } d && Invents(d.Expr)))
            logger.LogWarning("{Table}.{Column} keeps one id for the whole run: {Table} declares no key to derive " +
                              "one from", table.Name, column.Name, table.Name);
    }

    /// What fills a read layer's gap for a column: what the declared default was worth when the lake
    /// was built, or -- where the lake was asked to make one up per row and can -- an id derived from
    /// the row's key.
    string? Filled(Table table, Column column) =>
        column.Default is not { } declared ? null : Derived(table, column) ?? declared.Value;

    /// A `NEWID()` default answered per row rather than per run: the row's key written into the low
    /// half of a uuid whose high half is fixed for the column. The high half is a hash of the table
    /// and column names taken here, once, so no row pays for it and two columns cannot answer alike.
    ///
    /// Derived and not generated. A view is bound on every execution, so `uuid()` in one would answer
    /// differently every scan -- which is why a declared default is frozen at build in the first
    /// place. Determinism is what buys back everything freezing was protecting: the same id on every
    /// scan, the same id after a restart, and an id `Baseline` agrees with, so the shutdown delta
    /// stays a delta rather than becoming every row of the table.
    ///
    /// A key of one narrow integer goes in as itself, so consecutive rows get consecutive ids and an
    /// index over them stays dense -- which is what `NEWSEQUENTIALID()` is for. Anything else is
    /// hashed to 64 bits: `hex` of a wider integer is truncated by the padding, and a composite or a
    /// string key has no ordering worth keeping anyway.
    ///
    /// Never for a key column itself -- the key decides which row shadows which, and one derived from
    /// itself would be circular -- and never without a key, since a row with no identity has nothing
    /// to derive from.
    string? Derived(Table table, Column column)
    {
        if (!config.DeriveIds || column.Default is not { } declared || !Invents(declared.Expr)) return null;
        if (table.Key.Length == 0 || table.Key.Any(k => Same(k, column.Name))) return null;

        var key = table.Key is [var only] && table.Columns.Any(c => Same(c.Name, only) && Narrow(c.Type))
            ? $"{SqlText.Quote(only)}::BIGINT"
            : $"hash({string.Join(", ", table.Key.Select(SqlText.Quote))})";
        var low = $"lpad(hex({key}), 16, '0')";

        return $"CAST('{Prefix(table.Name, column.Name)}' || substr({low}, 1, 4) || '-' || " +
               $"substr({low}, 5, 12) AS UUID)";
    }

    /// The half of the uuid that belongs to the column rather than to the row, as the first three
    /// groups of one. SHA-256 rather than a counter: it has to be the same on the next run and in
    /// the next process, and a name is the only thing that is.
    static string Prefix(string table, string column)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{table}.{column}")));
        return $"{hash[..8]}-{hash[8..12]}-{hash[12..16]}-";
    }

    /// Whether a declared default is the one thing a lake can honestly make up per row. `NEWID()`
    /// stands for a value nobody else holds, and only the row's own identity can decide that the
    /// same way twice; `(getdate())` asks when the row was written, which a file cannot say.
    static bool Invents(string expr) =>
        string.Concat(expr.Where(ch => ch is not ('(' or ')' or ' '))).Equals("uuid", StringComparison.OrdinalIgnoreCase);

    /// An integer whose hex fits the 16 characters the low half of a uuid has room for. A wider one
    /// is silently cut short by the padding, which would give two keys one id.
    static bool Narrow(string type) =>
        type is "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT"
             or "UTINYINT" or "USMALLINT" or "UINTEGER" or "UBIGINT";

    Column Defaulted(DuckDBConnection conn, string table, Column column) =>
        schema.Default(table, column.Name) is { } expression
            ? column with { Default = Evaluate(conn, expression, column.Type) }
            : column;

    /// The declared default, translated on the tree like any other T-SQL, and then also run once to
    /// see what it says. A default DuckDB cannot answer at build time it could not answer at write
    /// time either, so one that throws is dropped with a warning and the column keeps its NULL.
    ColumnDefault? Evaluate(DuckDBConnection conn, string expression, string type)
    {
        if (evaluated.TryGetValue((expression, type), out var declared)) return declared;

        try
        {
            // Nobody is connected when the lake is built, so `SUSER_SNAME()` in a default is the
            // account duckpg runs as -- the only user there is at that point.
            var context = new TSqlContext(config.Schema, new Dictionary<string, string>(),
                                          new HashSet<string>(), Environment.UserName);
            var expr = TSqlWriter.Write(TSqlParser.ParseExpression(expression), context);

            using var command = conn.CreateCommand();
            command.CommandText = $"SELECT CAST({expr} AS {type})::VARCHAR";
            declared = command.ExecuteScalar() is string value
                ? new ColumnDefault(expr, Literal(conn, value, type))
                : null;
            logger.LogDebug("default {Expression} is {Expr}, worth {Value}",
                expression, expr, declared?.Value ?? "NULL");
        }
        catch (Exception e)
        {
            logger.LogWarning("default {Expression} ignored: {Reason}", expression, e.Message.ReplaceLineEndings(" "));
            declared = null;
        }

        evaluated[(expression, type)] = declared;
        return declared;
    }

    /// A default is written into every read layer's branch, so it is worth one expression rather
    /// than two -- but only where the bare literal already is the column's type. DuckDB is asked
    /// rather than guessed at: a literal of any other type would let the `COALESCE` around it widen
    /// the branch past what the catalog publishes, and `1.0` is a `DECIMAL` where the column is a
    /// `DOUBLE`.
    static string Literal(DuckDBConnection conn, string value, string type)
    {
        foreach (var candidate in (string[])[value, SqlText.Literal(value)])
            try
            {
                using var command = conn.CreateCommand();
                command.CommandText = $"SELECT typeof({candidate})";
                if (command.ExecuteScalar() is string actual && Same(actual, type)) return candidate;
            }
            catch (DuckDBException)
            {
                // Not an expression at all -- an unquoted string, say. The cast covers it.
            }

        return $"CAST({SqlText.Literal(value)} AS {type})";
    }

    // ---- the published shape ---------------------------------------------------------------------

    /// Columns in the order the topmost layer holding them puts them, so the shape follows the
    /// most recent layer rather than an accident of which file was read first.
    static List<Column> Published(List<TableLayer> layers)
    {
        var order = new List<string>();
        foreach (var layer in layers.OrderByDescending(l => l.Source.Seq))
            foreach (var column in layer.Columns)
                if (!order.Any(n => Same(n, column.Name))) order.Add(column.Name);

        return [.. order.Select(name => new Column(name, TypeOf(layers, name)))];
    }

    /// A parquet file carries a real schema; YAML and JSON types are inferred from the values, and
    /// inference reads every integer as BIGINT. So a layer that knows beats a layer that guessed,
    /// and among equals the higher one wins.
    static string TypeOf(List<TableLayer> layers, string name) =>
        Declared(layers.Where(l => l.Source.Format == LayerFormat.Parquet), name)
        ?? Declared(layers, name)!;

    static string? Declared(IEnumerable<TableLayer> layers, string name) =>
        layers.OrderByDescending(l => l.Source.Seq)
              .SelectMany(l => l.Columns)
              .FirstOrDefault(c => Same(c.Name, name))?.Type;

    /// The table's own virtual columns, then any default the table has neither as a real column nor
    /// as one of its own -- so faked columns land last, where an export that kept them puts them.
    List<VirtualColumn> Virtuals(string name, TableConfig table, List<Column> columns)
    {
        var virtuals = table.Columns.Select(c => new VirtualColumn(c.Name, ColumnExpression(c))).ToList();

        foreach (var fallback in config.Columns)
            if (!fallback.Except.Any(e => Same(e, name))
                && (fallback.Only.Length == 0 || fallback.Only.Any(o => Same(o, name)))
                && !columns.Any(c => Same(c.Name, fallback.Name))
                && !virtuals.Any(v => Same(v.Name, fallback.Name)))
                virtuals.Add(new VirtualColumn(fallback.Name, ColumnExpression(fallback)));

        return virtuals;
    }

    /// A table naming its own key gets it. The fallback only applies where the table actually has
    /// those columns -- one `--key` across a mixed lake should leave the odd table out, not break it.
    ///
    /// A partition column joins whatever key is found, because rows are only unique *within* a
    /// partition: every database in a `db=…` lake has its own row 1, and without the partition in
    /// the key one would shadow the other.
    /// The column a keyed file's mapping keys would fill, or null when the table has no single one.
    /// Read the same way `KeyFor` reads it but without its guard, since the column a keyed file is
    /// missing is exactly the one being asked about -- and only when the key is one column, because
    /// a mapping key is one value and cannot be two.
    string? MappingKey(string name, TableConfig table) =>
        (table.Key is { Length: > 0 } configured ? configured
         : config.DefaultKey is { Length: > 0 } fallback ? fallback
         : schema.Key(name) ?? []) is [var only] ? only : null;

    string[] KeyFor(string name, TableConfig table, List<Column> columns, string[] partitions)
    {
        var key =
            table.Key.Length > 0 ? table.Key
            : config.DefaultKey.Length > 0 && config.DefaultKey.All(k => columns.Any(c => Same(c.Name, k)))
                ? config.DefaultKey
            : schema.Key(name) is { Length: > 0 } declared && declared.All(k => columns.Any(c => Same(c.Name, k)))
                ? declared
            : [];

        return key.Length == 0 ? key
            : [.. key, .. partitions.Where(p => columns.Any(c => Same(c.Name, p)) && !key.Any(k => Same(k, p)))];
    }

    static string ColumnExpression(ColumnConfig column)
    {
        if (string.IsNullOrEmpty(column.Name))
            throw new InvalidOperationException("virtual column needs a `name`");
        var expr = column.Const is not null ? SqlText.Literal(column.Const)
                 : column.Expr ?? throw new InvalidOperationException($"virtual column `{column.Name}` needs `const` or `expr`");
        return column.Type is null ? expr : $"CAST({expr} AS {column.Type})";
    }

    // ---- the view --------------------------------------------------------------------------------

    /// A table published as one scan of its own merged rows, written out once. Only where there is
    /// something to merge and nothing to write: one layer is already a single scan, and a writable
    /// table's rows change under any copy of them. Null when no cache is configured or the table
    /// does not qualify.
    string? Cached(DuckDBConnection conn, Table table)
    {
        if (config.Cache is not { Length: > 0 } cache || table.Layers.Count < 2) return null;

        var merged = Merged(table, writable: false, tombstones: false, defaults: !Deferrable(table));
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, $"{table.Name}-{Fingerprint(Signature(table), table.Layers)}.parquet").Replace('\\', '/');

        // Keyed by what produced it, so a restart over files that have not changed reuses the copy
        // rather than deriving it again -- and one that has changed lands on a different name
        // instead of quietly answering with the old rows.
        if (File.Exists(file))
        {
            logger.LogDebug("reusing {File} for {Table}", file, table.Name);
            return file;
        }

        // ZSTD rather than none or snappy: a third smaller than snappy for the same read, and a
        // compressed scan beats an uncompressed one outright -- there is simply less to move.
        Exec(conn, $"COPY ({merged}) TO {SqlText.Literal(file)} (FORMAT PARQUET, COMPRESSION ZSTD)");
        logger.LogDebug("materialized {Table} into {File}", table.Name, file);

        // Whatever this table used to be keyed by is now answering nothing.
        foreach (var stale in Directory.EnumerateFiles(cache, $"{table.Name}-*.parquet"))
            if (!Same(stale.Replace('\\', '/'), file)) File.Delete(stale);

        return file;
    }

    /// Everything about a table that decides its merged rows, in a form that says the same thing
    /// every time it is asked. A declared default appears as the expression it was declared as
    /// rather than as what that expression was worth at startup: `(getdate())` answers differently
    /// every run, and keying on the answer would rebuild every stamped table for no reason -- which
    /// is most of them in a real schema.
    static string Signature(Table table) =>
        string.Join("\n", table.Columns.Select(c => $"{c.Name}:{c.Type}:{c.Default?.Expr}")
            .Concat(table.Virtuals.Select(v => $"+{v.Name}={v.Expr}"))
            .Concat(table.Layers.Select(l => $"@{l.Source.Seq}:{l.Scan}"))
            .Append($"key={string.Join(",", table.Key)}")
            .Append($"filter={table.Filter}"));

    /// What a materialized copy is keyed by: everything about the table that decides its rows, and
    /// the bytes of every file it reads. Same fingerprint, same rows.
    static string Fingerprint(string signature, IEnumerable<TableLayer> layers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(signature));

        foreach (var file in layers.SelectMany(l => Files(l.Source)).OrderBy(f => f, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            using var stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// The files behind a source, which is one file or everything a `**/*.ext` glob reaches.
    static IEnumerable<string> Files(LayerSource source)
    {
        var star = source.Path.IndexOf('*');
        if (star < 0) return File.Exists(source.Path) ? [source.Path] : [];

        var root = source.Path[..star].TrimEnd('/', '\\');
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*" + Path.GetExtension(source.Path), SearchOption.AllDirectories)
            : [];
    }

    static string Scan(string file) =>
        $"read_parquet({SqlText.Literal(file)}, hive_partitioning=false)";

    /// Whether a declared default decides which row shadows which. One on a key column does: the
    /// merge partitions by that key, so a row whose file left the column empty is only under the
    /// default's value once the default has been applied -- and a copy written without it would be
    /// merged differently by whoever read it back.
    bool KeyedByDefault(Table table) =>
        table.Key.Any(k => table.Columns.Any(c => Same(c.Name, k) && c.Default is not null));

    /// Whether a declared default can be left out of the copy and applied by the view reading it.
    /// It can where nothing in the merge depends on it: a filter or a virtual column reads the
    /// merged row, defaults and all. A copy is a file that outlives the process that wrote it, so a
    /// default it does not carry is stamped by whoever reads it -- which is what `(getdate())` meant
    /// before there was a copy.
    bool Deferrable(Table table) =>
        table.Filter is null && table.Virtuals.Count == 0 && !KeyedByDefault(table);

    /// What a bake writes for a table: the merge exactly as it is published, with the declared
    /// defaults left to whoever reads the file back. A default is a *value* in a read layer because
    /// a row in a file that predates the question has to be stamped with something -- but a baked
    /// file outlives the run that wrote it, so freezing `(getdate())` into it would answer every
    /// later run with the moment this one started. Left empty, the next run stamps it, which is what
    /// the default meant before there was a bake.
    ///
    /// Except where the default decides the merge rather than only filling a gap in it: a row is
    /// identified by its key, and deferring a default on one would shadow the wrong row here instead
    /// of later. That one is written out. A virtual column is not the same question it is for a
    /// copy -- a bake does not write one, and the run that reads the file computes it from columns
    /// that default in front of it, exactly as this one did.
    public string Baked(Table table) =>
        Merged(table, Promoted(table), Tombstoned(table), defaults: KeyedByDefault(table));

    /// The view over a copy: the stored column, with a deferred default over the top.
    string Over(Table table, string file) =>
        Deferrable(table) && table.Columns.Any(c => c.Default is not null)
            ? "SELECT " + string.Join(", ", table.Columns.Select(c =>
                  (Filled(table, c) is { } fill ? $"COALESCE({SqlText.Quote(c.Name)}, {fill})" : SqlText.Quote(c.Name))
                  + $" AS {SqlText.Quote(c.Name)}")) + $" FROM {Scan(file)}"
            : $"SELECT * FROM {Scan(file)}";

    /// The whole stack, evaluated once into a table the sessions then read and write directly. The
    /// merge it came from stays behind as a view nothing reads while the lake serves: it costs no
    /// memory, and it is the only honest baseline for the delta written out at shutdown.
    ///
    /// The write directory is a layer like any other here -- a delta a previous run left behind is
    /// read back in and collapsed with the rest, which is what makes a restart mean anything.
    void Materialize(DuckDBConnection conn, Table table)
    {
        // Merged against the stacked form of the table: a materialized one writes where it reads,
        // so asking it for its write branch by name would point the merge at itself.
        var stacked = table with { Materialized = false };
        var carries = table.Writable && write.Carries(stacked);
        if (carries) write.Prepare(conn, stacked);

        // A store already carrying this table is the state, and the layers are not consulted for it
        // again: rebuilding would throw away everything written since it was made. What the file
        // holds has to be what the catalog says it publishes, though -- a store made before a column
        // was declared would fail on every query naming that column, and nowhere near here.
        if (Keeping && Stored(conn, table) is { Count: > 0 } stored)
        {
            var declared = table.Columns.Select(c => c.Name);
            if (!stored.SequenceEqual(declared, StringComparer.OrdinalIgnoreCase))
                throw new DuckPgConfigurationException(
                    $"the store holds {table.Name} as ({string.Join(", ", stored)}), and this lake " +
                    $"publishes it as ({string.Join(", ", declared)}) -- the store is of another " +
                    "schema, and rebuilding it here would discard what has been written to it");

            promoted.Add(table.Name);
            rows[table.Name] = Count(conn, table);
            Keyed(conn, table);
            foreach (var sequence in Sequences(conn, table)) Exec(conn, sequence);
            return;
        }

        // The baseline is the read layers and nothing else. Measured against a stack that already
        // carried the last delta, the next one comes out empty -- and since the write layer is
        // rewritten whole, that empties it of everything the run before did. A row hidden by a
        // tombstone goes the same way: absent from a baseline that applied it, it is not hidden
        // again, and comes back on the run after.
        Exec(conn, $"CREATE OR REPLACE VIEW {Baseline(table)} AS " +
                   Merged(stacked, writable: false, tombstones: false));

        // What it serves is that baseline with the previous run's delta on top of it: the whole
        // stack, evaluated once.
        Exec(conn, $"CREATE OR REPLACE TABLE {table.QualifiedName} AS " +
                   Merged(stacked, carries, carries && write.HasTombstones(stacked)));

        // Nothing is earned here: the branch a write would have to make is the table itself.
        promoted.Add(table.Name);
        rows[table.Name] = Count(conn, table);
        Keyed(conn, table);
        foreach (var sequence in Sequences(conn, table)) Exec(conn, sequence);
    }

    /// The declared key, held by DuckDB itself. A materialized table is the whole of what the lake
    /// publishes, so unlike a write branch's `PRIMARY KEY` there is no layer below it for a
    /// duplicate to hide in -- and the ART it builds is what turns a lookup on the key from a scan
    /// into a lookup: measured over 10M rows in no particular order, 4.2 ms a point query against
    /// 0.47 with the index. Where the rows happen to arrive in key order the zone maps had already
    /// done most of it, and it is worth about a fifth.
    ///
    /// The key itself and not a unique index over the same columns, which builds the same ART and
    /// would differ only in letting a key column be NULL: a row whose key is not there has no
    /// identity, and the write branch of a layered lake has refused it all along. So this is what
    /// that table already says, said once the layers are collapsed.
    ///
    /// It refuses to build over a stack that publishes a key twice -- which a single-layer table can,
    /// being published without the `QUALIFY` that dedupes, there being nothing to shadow -- or one
    /// that leaves the key empty. Both are the lake saying the layers are wrong at startup rather
    /// than serving a row nobody can name.
    void Keyed(DuckDBConnection conn, Table table)
    {
        if (table.Key.Length > 0 && !Holds(conn, table))
            Exec(conn, $"ALTER TABLE {table.QualifiedName} ADD PRIMARY KEY " +
                       $"({string.Join(", ", table.Key.Select(SqlText.Quote))})");

        foreach (var (name, columns) in Uniques(table))
            Exec(conn, $"CREATE UNIQUE INDEX IF NOT EXISTS {SqlText.Quote($"{table.Name}_{name}")} " +
                       $"ON {table.QualifiedName} ({string.Join(", ", columns.Select(SqlText.Quote))})");
    }

    /// The uniqueness the dacpac declares past the key -- a `UNIQUE` constraint or a unique index,
    /// which say the same thing about the rows and are held the same way. Unlike the key this is an
    /// index and not a constraint, because that is what it is: the columns may be NULL, and DuckDB
    /// counts two NULLs as different where SQL Server counts them as one, so what a lake refuses
    /// here is a shade narrower than what SQL Server would.
    ///
    /// Only where the table publishes every column the rule is over -- a lake showing a subset of a
    /// declared table should lose the rule rather than fail on it -- and never the key again under
    /// another name. A partition column joins these for the same reason it joins the key: rows are
    /// only unique *within* a partition, and a rule that forgot that would refuse a lake for holding
    /// the row it was partitioned to hold.
    IEnumerable<(string Name, string[] Columns)> Uniques(Table table)
    {
        var partitions = table.Layers.SelectMany(l => l.Source.Partitions)
                              .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var unique in schema.Uniques.Where(u => Same(u.Table, table.Name)))
        {
            if (!unique.Columns.All(c => table.Has(c) && Carried(table, c))) continue;

            string[] columns = [.. unique.Columns,
                                .. partitions.Where(p => table.Has(p) && !unique.Columns.Any(c => Same(c, p)))];
            if (!Covers(table.Key, columns)) yield return (unique.Name, columns);
        }
    }

    /// Whether the lake holds values for a column, as against the schema merely declaring it. A
    /// column no read layer carries is the same in every row those layers produce -- a declared
    /// default, frozen at build, or a typed NULL -- so it tells one row from another only by
    /// accident of there being one row. `(newid())` is the case that makes this plain: one id for
    /// the run, stamped onto every row a file did not carry, which a rule over that column would
    /// then refuse the whole lake for. If the values were wanted they would be in the input.
    ///
    /// A lake with no read layers has nothing frozen: its rows arrive as writes, and the write
    /// table's own default stamps each as it is written.
    bool Carried(Table table, string column) =>
        table.Layers.Count == 0 || table.Layers.Any(l => l.Columns.Any(c => Same(c.Name, column)))
        || (table.Columns.FirstOrDefault(c => Same(c.Name, column)) is { } declared
            && Derived(table, declared) is not null);

    /// Whether two column lists say the same thing about a row, which order does not change.
    static bool Covers(string[] key, string[] columns) =>
        key.Length == columns.Length && key.All(k => columns.Any(c => Same(k, c)));

    /// Whether DuckDB is already holding this table's key, which a store carrying the table from a
    /// previous run is -- and asking for it twice is an error rather than a no-op.
    static bool Holds(DuckDBConnection conn, Table table)
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT 1 FROM duckdb_constraints() WHERE schema_name = ? AND table_name = ? " +
                              "AND constraint_type = 'PRIMARY KEY'";
        command.Parameters.Add(new DuckDBParameter(table.Schema));
        command.Parameters.Add(new DuckDBParameter(table.Name));

        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    /// A store the lake's state lives in, rather than one that is only somewhere for its tables to
    /// live. It is what decides both halves of the bargain -- whether the layers are read for a table
    /// the file already carries, and whether a delta is written at shutdown -- and they have to be
    /// the same answer, or the run either loses its writes or writes them down twice.
    bool Keeping => config.Store is { Length: > 0 } && config.StoreMode == StoreMode.Keep;

    static long Count(DuckDBConnection conn, Table table)
    {
        using var command = conn.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table.QualifiedName}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// The columns a store already holds for a table, in order, or nothing when it holds no such
    /// table. A view is not a table: a store written by a build that is no longer this one may carry
    /// either, and only a table is state worth keeping.
    static List<string> Stored(DuckDBConnection conn, Table table)
    {
        using var command = conn.CreateCommand();
        command.CommandText =
            "SELECT column_name FROM information_schema.columns c WHERE c.table_schema = ? AND c.table_name = ? " +
            "AND EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = c.table_schema " +
            "AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE') ORDER BY ordinal_position";
        command.Parameters.Add(new DuckDBParameter(table.Schema));
        command.Parameters.Add(new DuckDBParameter(table.Name));

        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    /// What the layers said before anything was written to them, for the shutdown delta.
    string Baseline(Table table) =>
        config.Base is { Length: > 0 }
            ? $"{SqlText.Quote(BaseCatalog)}.{SqlText.Quote(config.Schema)}.{SqlText.Quote(table.Name)}"
            : $"{SqlText.Quote(BaseSchema)}.{SqlText.Quote(table.Name)}";

    /// What the baked database is attached as while a run serves its copy. Read-only, and read only
    /// at shutdown: it is the state this run started from, which is exactly what the delta is
    /// measured against -- the same thing `base` holds for a lake cut from layers, except that here
    /// somebody else already wrote it down.
    public const string BaseCatalog = "duckpg_base";

    /// What a materialized lake leaves behind: the rows that are not what the layers said, and the
    /// keys that were there and are not. Written in the write layer's own format, so the next run --
    /// materialized or not -- reads it as the layer it is. Nothing is kept without a directory to
    /// keep it in, which is the ephemeral bargain the mode makes.
    public void Flush(DuckDBConnection conn)
    {
        // Once only: the baseline reads the very tables this replaces, so a second pass would
        // measure the delta against the delta.
        // A store that is the state keeps what it was given by keeping it, so there is nothing to
        // work out at shutdown -- and a delta beside it would be a second answer to the same
        // question. One that is only where the tables live answers like any other materialized lake.
        if (!config.Collapsed || write.Directory is null || Keeping || flushed) return;
        flushed = true;

        foreach (var table in Tables.Values.Where(t => t.Writable))
        {
            var stacked = table with { Materialized = false };
            var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

            // Computed before either target is touched: the baseline view reads the very tables the
            // next two statements replace.
            Exec(conn, $"CREATE OR REPLACE TEMP TABLE duckpg_delta AS SELECT {columns} " +
                       $"FROM {table.QualifiedName} EXCEPT SELECT {columns} FROM {Baseline(table)}");

            var keys = string.Join(", ", table.Key.Select(SqlText.Quote));
            if (table.Key.Length > 0)
                Exec(conn, $"CREATE OR REPLACE TEMP TABLE duckpg_gone AS SELECT {keys} " +
                           $"FROM {Baseline(table)} EXCEPT SELECT {keys} FROM {table.QualifiedName}");

            // The branch may already be there, holding the delta a previous run left and this build
            // read back in -- what goes out is the whole delta, not another one on top of it.
            foreach (var statement in write.Definition(stacked, ifNotExists: true)) Exec(conn, statement);
            Exec(conn, $"DELETE FROM {stacked.WriteName}");
            if (table.Key.Length > 0) Exec(conn, $"DELETE FROM {stacked.TombstoneName}");
            Exec(conn, $"INSERT INTO {stacked.WriteName} ({columns}) SELECT {columns} FROM duckpg_delta");


            if (table.Key.Length > 0)
                Exec(conn, $"INSERT INTO {stacked.TombstoneName} SELECT * FROM duckpg_gone");

            write.Persist(conn, stacked);
        }
    }

    string ViewDefinition(Table table, bool writable, bool tombstones) =>
        $"CREATE OR REPLACE VIEW {table.QualifiedName} AS {Merged(table, writable, tombstones)}";

    /// The copy standing in for the read layers it was made from. A write adds a branch above it
    /// rather than replacing it: the copy *is* the read layers, and a write does not touch those.
    /// Only a copy of a plain merge can serve -- one made under a filter or a virtual column has
    /// those baked into it, and the wrapper would apply them twice.
    string? Underlay(Table table) =>
        Deferrable(table) && copies.TryGetValue(table.Name, out var file) ? file : null;

    /// What a first write to a table costs: the tables the write layer keeps for it, and the view
    /// rewritten to read them. Repeatable on purpose -- a statement that rolled back leaves the
    /// catalog as it was, and the next write says the same thing again.
    public string[] Promotion(DuckDBConnection conn, Table table, bool tombstones) =>
        [.. Sequences(conn, table),
         .. write.Definition(table, ifNotExists: true),
         ViewDefinition(table, writable: true, tombstones || Tombstoned(table))];

    /// Where a store-generated column draws its values from, one sequence per column and both named
    /// after it, in the write layer's own schema.
    public static string Sequence(Table table, Column column) =>
        $"{SqlText.Quote(WriteLayer.Schema)}.{SqlText.Quote($"{table.Name}__{column.Name}__seq")}";

    /// A declared identity starts after what the lake already holds: the files are the state, so the
    /// first key handed out is the one past the highest of them. Asked once, when the table grows
    /// its write branch -- a lake that reloads asks the files again, which is what makes a key it
    /// generated last time not one it generates twice.
    public string[] Sequences(DuckDBConnection conn, Table table)
    {
        var sequences = new List<string>();
        foreach (var column in table.Columns.Where(c => c.Identity))
        {
            using var command = conn.CreateCommand();
            command.CommandText =
                $"SELECT coalesce(max({SqlText.Quote(column.Name)}), 0) + 1 FROM {table.QualifiedName}";
            sequences.Add($"CREATE SEQUENCE IF NOT EXISTS {Sequence(table, column)} " +
                          $"START {Convert.ToInt64(command.ExecuteScalar())}");
        }
        return [.. sequences];
    }

    public bool Promoted(Table table) => promoted.Contains(table.Name);

    public bool Tombstoned(Table table) => tombstoned.Contains(table.Name);

    /// Recorded only once a write has actually committed, so a rolled-back promotion is simply said
    /// again rather than assumed.
    public void Promote(string name) => promoted.Add(name);

    public void Tombstone(string name) => tombstoned.Add(name);

    string Merged(Table table, bool writable, bool tombstones, bool defaults = true)
    {
        // Everything the read layers say, already merged and written down once. Below the write
        // branch, so a tombstone hides it and a written row shadows it, exactly as a layer would.
        if (Underlay(table) is { } copy)
            return Wrap(table, writable, tombstones, [
                "SELECT " + string.Join(", ", table.Columns.Select(c =>
                     (defaults && Filled(table, c) is { } fill ? $"COALESCE({SqlText.Quote(c.Name)}, {fill})" : SqlText.Quote(c.Name))
                     + $" AS {SqlText.Quote(c.Name)}")) +
                 $", NULL::VARCHAR AS \"_file\", 0::BIGINT AS \"_seq\" FROM {Scan(copy)}",
                .. writable
                    ? (string[])[Branch(table, table.Columns, table.WriteName, write.Seq, named: false, defaults: false)]
                    : []]);

        // One layer and nothing to merge it with: the table *is* that layer, and everything the
        // merge needs around it -- the union, the sequence numbers, the row numbering that picks a
        // winner, the projection renaming each column to itself -- is an expression DuckDB binds
        // for every statement and answers the same way every time. A view is bound per execution,
        // not per lake, so that is the whole cost of a query on a small table.
        //
        // A filter or a virtual column reads the merged row rather than the file's, so those keep
        // the wrapper they are written against.
        if (!writable && table.Layers is [var only] && table.Virtuals.Count == 0 && table.Filter is null)
            return "SELECT " +
                   string.Join(", ", table.Columns.Select(c =>
                       (Value(table, c, only.Columns.FirstOrDefault(a => Same(a.Name, c.Name)), defaults)
                        ?? $"CAST(NULL AS {c.Type})") + $" AS {SqlText.Quote(c.Name)}")) +
                   $" FROM {only.Scan}";

        // Each branch names its own columns and casts them to the published type; UNION ALL BY NAME
        // fills a layer's gaps with NULL. Only a read layer fills its gaps with the declared default
        // instead: a written row was there to be stamped as it was written, a row in a file below
        // was not.
        var layers = table.Layers.Select(layer => Branch(table, layer.Columns, layer.Scan, layer.Source.Seq,
                                                         layer.Source.HasFileName, defaults)).ToList();
        if (writable)
            layers.Add(Branch(table, table.Columns, table.WriteName, write.Seq, named: false, defaults: false));
        if (layers.Count == 0)
            layers.Add("SELECT NULL::VARCHAR AS \"_file\", 0::BIGINT AS \"_seq\" WHERE false");

        // A column nothing below produces -- one the schema declares that no layer carries, or a
        // virtual one's base -- still has to be projected, as a typed NULL. The write layer always
        // has every column, and a read layer produces a defaulted one whether it carries it or not.
        return Wrap(table, writable, tombstones, layers, column =>
            writable
            || table.Layers.Any(l => l.Columns.Any(c => Same(c.Name, column.Name)))
            || (defaults && column.Default is not null && table.Layers.Count > 0));
    }

    /// The merge around a set of branches: what it publishes, what a tombstone hides, and which row
    /// wins where several carry the same key.
    string Wrap(Table table, bool writable, bool tombstones, List<string> layers, Func<Column, bool>? produced = null)
    {
        var projection = string.Join(", ", table.Columns
            .Select(c => (produced?.Invoke(c) ?? true ? $"r.{SqlText.Quote(c.Name)}" : $"CAST(NULL AS {c.Type})")
                         + $" AS {SqlText.Quote(c.Name)}")
            .Concat(table.Virtuals.Select(v => $"({v.Expr}) AS {SqlText.Quote(v.Name)}")));

        var where = new List<string>();
        if (tombstones && table.Key.Length > 0)
        {
            var match = string.Join(" AND ", table.Key.Select(k =>
                $"d.{SqlText.Quote(k)} IS NOT DISTINCT FROM r.{SqlText.Quote(k)}"));
            // A tombstone hides the row it names in every layer below the write layer; the write
            // layer's own rows are what deleting removes outright.
            where.Add($"NOT EXISTS (SELECT 1 FROM {table.TombstoneName} d WHERE r.\"_seq\" < {write.Seq} AND {match})");
        }
        if (table.Filter is not null) where.Add($"({table.Filter})");

        // With a key, the topmost layer holding a row is the one that counts; without one there is
        // no way to tell rows apart, so the layers simply stack.
        var shadowing = table.Key.Length > 0 && layers.Count > 1
            ? $" QUALIFY row_number() OVER (PARTITION BY {string.Join(", ", table.Key.Select(k => "r." + SqlText.Quote(k)))}" +
              " ORDER BY r.\"_seq\" DESC) = 1"
            : "";

        return $"SELECT {projection} " +
               $"FROM ({string.Join(" UNION ALL BY NAME ", layers)}) r" +
               (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + shadowing;
    }

    /// What one layer offers for a column: its own value, cast to the published type only where it
    /// is not already that type, with a declared default over the top. Null where the layer has
    /// neither -- a gap for `UNION ALL BY NAME` to fill.
    ///
    /// A layer that carries the column still has rows leaving it empty, so the default goes over
    /// the value rather than only where the column is missing from the file altogether.
    string? Value(Table table, Column column, Column? source, bool defaults)
    {
        // A cast to the type the layer already has is an expression DuckDB binds on every
        // statement and nothing else -- and a view is bound per execution, not per lake.
        var value = source is null ? null
            : Same(source.Type, column.Type) ? SqlText.Quote(column.Name)
            : $"CAST({SqlText.Quote(column.Name)} AS {column.Type})";
        var fill = defaults ? Filled(table, column) : null;
        return (value, fill) switch
        {
            (null, null) => null,
            (null, _) => fill,
            (_, null) => value,
            _ => $"COALESCE({value}, {fill})",
        };
    }

    string Branch(Table table, List<Column> available, string scan, int seq, bool named, bool defaults)
    {
        var columns = string.Join(", ", table.Columns
            .Select(c => (Column: c, Value: Value(table, c, available.FirstOrDefault(a => Same(a.Name, c.Name)), defaults)))
            .Where(c => c.Value is not null)
            .Select(c => $"{c.Value} AS {SqlText.Quote(c.Column.Name)}"));

        return $"SELECT {(columns.Length > 0 ? columns + ", " : "")}" +
               $"{(named ? "\"filename\"" : "NULL::VARCHAR")} AS \"_file\", {seq}::BIGINT AS \"_seq\" FROM {scan}";
    }

    static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
