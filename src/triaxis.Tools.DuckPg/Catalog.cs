using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using triaxis.Tools.DuckPg.TSql;

namespace triaxis.Tools.DuckPg;

/// A declared default, in both of the forms it is needed in: `Expr` is what a row written now gets,
/// evaluated as it is written, and `Value` is what that expression was worth when the lake was
/// built -- which is the only honest stamp for a row in a file that predates the question.
sealed record ColumnDefault(string Expr, string Value);

sealed record Column(string Name, string Type, ColumnDefault? Default = null);

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

            List<Column> columns = schema.Columns(name) is { } declared
                ? [.. declared.Select(c => Defaulted(conn, name, c))]
                : Published(describedWrite is null ? layers : [.. layers, describedWrite]);
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

        Declared(conn);
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

    // ---- declared views --------------------------------------------------------------------------

    /// The dacpac's own views, published beside the tables they read: the query is T-SQL, so it is
    /// translated on the tree like any statement a client sends, and `dbo` lands on the lake.
    ///
    /// A view may select from another view the model happens to list later, and the model says
    /// nothing about which. Rather than order them, each round creates what it can and the next
    /// round tries the rest -- a round that adds nothing is one where what is left is broken rather
    /// than merely early, and those are named and skipped instead of stopping the lake.
    void Declared(DuckDBConnection conn)
    {
        var context = new TSqlContext(config.Schema, new Dictionary<string, string>(),
                                      new HashSet<string>(), Environment.UserName);
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
                                  $"AS {translated[0]}");
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

        // A column nothing below produces -- one the schema declares that no layer carries, or a
        // virtual one's base -- still has to be projected, as a typed NULL. The write layer always
        // has every column, and a read layer produces a defaulted one whether it carries it or not.
        string Source(Column column) =>
            (table.Writable
             || table.Layers.Any(l => l.Columns.Any(c => Same(c.Name, column.Name)))
             || (column.Default is not null && table.Layers.Count > 0)
                ? $"r.{SqlText.Quote(column.Name)}"
                : $"CAST(NULL AS {column.Type})") + $" AS {SqlText.Quote(column.Name)}";

        // Each branch names its own columns and casts them to the published type; UNION ALL BY NAME
        // fills a layer's gaps with NULL. Only a read layer fills its gaps with the declared default
        // instead: a written row was there to be stamped as it was written, a row in a file below
        // was not.
        var layers = table.Layers.Select(layer => Branch(table, layer.Columns, layer.Scan, layer.Source.Seq,
                                                         layer.Source.HasFileName, defaults: true)).ToList();
        if (table.Writable)
            layers.Add(Branch(table, table.Columns, table.WriteName, write.Seq, named: false, defaults: false));
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

    string Branch(Table table, List<Column> available, string scan, int seq, bool named, bool defaults)
    {
        // A layer that carries the column still has rows leaving it empty, so the default goes over
        // the value rather than only where the column is missing from the file altogether.
        string? Value(Column column, Column? source)
        {
            // A cast to the type the layer already has is an expression DuckDB binds on every
            // statement and nothing else -- and a view is bound per execution, not per lake.
            var value = source is null ? null
                : Same(source.Type, column.Type) ? SqlText.Quote(column.Name)
                : $"CAST({SqlText.Quote(column.Name)} AS {column.Type})";
            var fill = defaults ? column.Default?.Value : null;
            return (value, fill) switch
            {
                (null, null) => null,
                (null, _) => fill,
                (_, null) => value,
                _ => $"COALESCE({value}, {fill})",
            };
        }

        var columns = string.Join(", ", table.Columns
            .Select(c => (Column: c, Value: Value(c, available.FirstOrDefault(a => Same(a.Name, c.Name)))))
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
