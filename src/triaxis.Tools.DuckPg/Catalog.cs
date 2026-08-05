using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

sealed record Column(string Name, string Type);

sealed record VirtualColumn(string Name, string Expr);

/// One layer's rows for one table, as something the view can select from.
sealed record TableLayer(LayerSource Source, string Scan, List<Column> Columns);

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
    string? Filter)
{
    public string QualifiedName => $"{SqlText.Quote(Schema)}.{SqlText.Quote(Name)}";
    public string WriteName => $"{SqlText.Quote(WriteLayer.Schema)}.{SqlText.Quote(Name)}";
    public string TombstoneName => $"{SqlText.Quote(WriteLayer.Schema)}.{SqlText.Quote(Name + "__del")}";

    public bool Has(string column) => Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
}

/// Publishes each table as one view over its layers: the lowest layer at the bottom, the write
/// layer on top, and -- where a key is declared -- the topmost row for a key winning.
sealed class Catalog(Config config, WriteLayer write, DacpacSchema schema, ILogger<Catalog> logger)
{
    public Dictionary<string, Table> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// Schema holding the materialised YAML and JSON layers.
    const string LayerSchema = "layer";

    public void Build(DuckDBConnection conn)
    {
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {SqlText.Quote(config.Schema)}");
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {LayerSchema}");
        Exec(conn, $"CREATE SCHEMA IF NOT EXISTS {WriteLayer.Schema}");

        foreach (var (name, sources, writeSource) in Sources())
        {
            var settings = config.Table(name);
            var layers = Materialise(conn, name, sources);
            var describedWrite = writeSource is null ? null
                : new TableLayer(writeSource, "", Layer.Columns(conn, writeSource));

            var columns = schema.Columns(name) ?? Published(describedWrite is null ? layers : [.. layers, describedWrite]);
            var writable = settings.Writable ?? write.Enabled;
            var partitions = layers.SelectMany(l => l.Source.Partitions)
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var table = new Table(config.Schema, name, layers, columns,
                                  Virtuals(name, settings, columns),
                                  KeyFor(name, settings, columns, partitions),
                                  writable, writeSource, settings.Filter);

            if (table.Writable) write.Prepare(conn, table);
            Exec(conn, ViewDefinition(table));
            Tables[name] = table;
        }
    }

    public void Rebuild(DuckDBConnection conn)
    {
        Tables.Clear();
        Build(conn);
    }

    public Table? Resolve(string? schema, string name) =>
        (schema is null || schema.Equals(config.Schema, StringComparison.OrdinalIgnoreCase))
        && Tables.TryGetValue(name, out var table) ? table : null;

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---- discovery -------------------------------------------------------------------------------

    /// Every table any layer carries, keyed case-insensitively -- an export that disagrees with
    /// itself about capitalisation (`ORDER_Lines` vs `Order_Lines`) still lands on one table.
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
            foreach (var (name, source) in Layer.Scan(writeDirectory, write.Seq, logger))
                sources[name] = Entry(name) with { Write = source };

        // A table the schema declares but no layer carries is still part of the shape: publish it
        // empty, so the catalog is the schema rather than a list of which files turned up.
        foreach (var declared in schema.Tables)
            if (!sources.ContainsKey(declared)) sources[declared] = ([], null);

        return sources.Select(s => (s.Key, s.Value.Layers, s.Value.Write))
                      .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// Parquet is scanned where it lies -- that is what the format is for. Everything else is
    /// materialised once, so a query neither re-reads the file nor pays type inference again.
    List<TableLayer> Materialise(DuckDBConnection conn, string name, List<LayerSource> sources)
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

            var materialised = $"{LayerSchema}.{SqlText.Quote($"{name}#{source.Seq}")}";
            var columns = Layer.Materialise(conn, materialised, source);
            if (columns.Count > 0) layers.Add(new TableLayer(source, materialised, columns));
        }
        return layers;
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

    string ViewDefinition(Table table)
    {
        var projection = string.Join(", ", table.Columns.Select(Source)
            .Concat(table.Virtuals.Select(v => $"({v.Expr}) AS {SqlText.Quote(v.Name)}")));

        // A column no layer carries -- one the schema declares, or a virtual one's base -- still
        // has to be projected, as a typed NULL. The write layer always has every column.
        string Source(Column column) =>
            table.Writable || table.Layers.Any(l => l.Columns.Any(c => Same(c.Name, column.Name)))
                ? $"r.{SqlText.Quote(column.Name)}"
                : $"CAST(NULL AS {column.Type}) AS {SqlText.Quote(column.Name)}";

        // Each branch names its own columns and casts them to the published type; UNION ALL BY NAME
        // fills a layer's gaps with NULL.
        var layers = table.Layers.Select(layer => Branch(table, layer.Columns, layer.Scan, layer.Source.Seq,
                                                         layer.Source.HasFileName)).ToList();
        if (table.Writable)
            layers.Add(Branch(table, table.Columns, table.WriteName, write.Seq, named: false));
        if (layers.Count == 0)
            layers.Add("SELECT NULL::VARCHAR AS \"_file\", 0::BIGINT AS \"_seq\" WHERE false");

        var where = new List<string>();
        if (table.Writable && table.Key.Length > 0)
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

        return $"CREATE OR REPLACE VIEW {table.QualifiedName} AS SELECT {projection} " +
               $"FROM ({string.Join(" UNION ALL BY NAME ", layers)}) r" +
               (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + shadowing;
    }

    string Branch(Table table, List<Column> available, string scan, int seq, bool named)
    {
        var columns = string.Join(", ", table.Columns
            .Where(c => available.Any(a => Same(a.Name, c.Name)))
            .Select(c => $"CAST({SqlText.Quote(c.Name)} AS {c.Type}) AS {SqlText.Quote(c.Name)}"));

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
