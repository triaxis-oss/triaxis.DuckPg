namespace triaxis.DuckPg.TSql;

/// What to do to a statement's rows once DuckDB has handed them over: sort them by these result
/// columns, then keep the first `Limit`.
///
/// DuckDB's sort costs what a row is *wide* rather than what a table is long -- measured at ~1.3 ms
/// plus ~50 µs a column, unchanged between twelve rows and twelve hundred -- so on the small table an
/// ORM keeps asking about, the sort is most of what the query costs. Doing it here instead is only
/// sound where the table is known to be small *before* anything is sent, since the statement goes out
/// without its `TOP`: guessing and falling back would fetch the whole table on the shape that matters
/// most, which is `get the last few`.
///
/// `Rows` is what the catalog counted the table at, carried so the sort keys can be sized once
/// rather than grown into.
sealed record Reorder(int[] Keys, bool[] Descending, int? Limit, int Rows);

/// Deciding whether a statement qualifies, and rewriting it into the one to send. On the tree, like
/// every other dialect decision here -- the statement that goes out is rendered from an AST with its
/// ordering and paging removed, not from text with clauses cut off it.
static class FastOrder
{
    /// A table bigger than this is left to DuckDB, and the number is DuckDB's rather than a measured
    /// crossover: 2048 rows is a data chunk, and one chunk is what `Chunk` can address in place. Past
    /// it the rows have to be copied out instead, which is a cliff and not a slope -- measured at 100
    /// columns, 2.9x at 2048 rows and 0.3x at 4096. Below it the cost is scanning the whole table
    /// rather than the rows asked for, which is what makes the win narrow rather than negative: 2.7x
    /// to 4.3x for a `TOP n` at every width and length measured, and 1.1x to 2.6x for an `ORDER BY`
    /// with no `TOP`, which has to hand back every row either way.
    public const long Small = 2048;

    /// The statement to send, and what to do to its rows afterwards -- or the statement unchanged
    /// when it does not qualify.
    public static (Statement Statement, Reorder? Reorder) Of(Statement statement, TSqlContext context)
    {
        if (context.Small is null || statement is not SelectStatement { Query: var query }) return (statement, null);

        // Anything wrapping the body is something the ordering could belong to rather than to the
        // one table underneath it.
        if (query.With.Count > 0 || query.OrderBy.Count == 0 || query.Offset is not null || query.Fetch is not null)
            return (statement, null);

        if (query.Body is not SelectBody body) return (statement, null);
        if (body.Distinct || body.GroupBy.Count > 0 || body.Having is not null || body.Into is not null)
            return (statement, null);

        // One table, named outright, and one this lake holds as a table small enough to sort here.
        if (body.From is not NamedTableSource { Name: var name }) return (statement, null);
        if (context.Small(name.Table.Text) is not { } rows || rows > Small) return (statement, null);
        if (context.Tables?.TryGetValue(name.Table.Text, out var types) is not true) return (statement, null);

        // `TOP n` for a constant n, or no TOP at all. A percentage is counted against the body,
        // which is the thing being taken apart.
        int? limit = null;
        if (body.Top is not null)
        {
            if (body.TopPercent) return (statement, null);
            var top = body.Top is ParenExpr paren ? paren.Inner : body.Top;
            if (top is not Literal { Kind: LiteralKind.Number, Text: var text }
                || !int.TryParse(text, out var value) || value < 0)
                return (statement, null);
            limit = value;
        }

        // Every item has to be a plain column, so a result column can be named and found again --
        // and so that nothing in the projection is an aggregate or a window, which would make the
        // ordering mean something other than ordering rows.
        var projected = new List<string>();
        foreach (var item in body.Items)
        {
            if (item.Expr is not ColumnRef column) return (statement, null);
            projected.Add((item.Alias ?? column.Parts[^1]).Text);
        }

        // And every sort key has to be one of those columns, in a type .NET and DuckDB agree about.
        // Text is left to DuckDB: a collation this does not know is a wrong answer nobody would see.
        var keys = new int[query.OrderBy.Count];
        var descending = new bool[query.OrderBy.Count];
        for (var i = 0; i < query.OrderBy.Count; i++)
        {
            if (query.OrderBy[i].Expr is not ColumnRef key) return (statement, null);
            var at = projected.FindIndex(p => p.Equals(key.Parts[^1].Text, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return (statement, null);
            if (!types.TryGetValue(key.Parts[^1].Text, out var type) || !Sortable(type)) return (statement, null);
            keys[i] = at;
            descending[i] = query.OrderBy[i].Descending;
        }

        var sent = new SelectStatement(query with
        {
            OrderBy = [],
            Body = body with { Top = null },
        });
        return (sent, new Reorder(keys, descending, limit, (int)rows));
    }

    /// Types whose order is the same here as in DuckDB. Numbers and instants compare by value in
    /// both; text does not, since a collation is DuckDB's and a `string.CompareTo` is not it.
    static bool Sortable(string type) => type.ToUpperInvariant() switch
    {
        "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "HUGEINT" => true,
        "UTINYINT" or "USMALLINT" or "UINTEGER" or "UBIGINT" => true,
        "FLOAT" or "DOUBLE" or "REAL" => true,
        "DATE" or "TIME" => true,
        var t when t.StartsWith("DECIMAL", StringComparison.Ordinal) => true,
        var t when t.StartsWith("TIMESTAMP", StringComparison.Ordinal) => true,
        _ => false,
    };
}
