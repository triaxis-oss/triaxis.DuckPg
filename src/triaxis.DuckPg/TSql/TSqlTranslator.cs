namespace triaxis.DuckPg.TSql;

/// One translated statement: the DuckDB SQL to send, whatever has to be done to its rows after
/// DuckDB has produced them, and -- for an assignment select -- the variables its columns fill
/// instead of going back to the caller as a result set.
sealed record Translated(string Sql, Reorder? Reorder, IReadOnlyList<string>? Assigns = null);

/// T-SQL in, DuckDB SQL out, one statement in the batch at a time.
static class TSqlTranslator
{
    /// Rendered against the context as it stands, which is why a batch is translated a statement at
    /// a time rather than all at once: `@@ROWCOUNT` and `SCOPE_IDENTITY()` are written into the
    /// statement as the values they hold now, and what the statement before it did is part of that.
    public static Translated Translate(Statement statement, TSqlContext context)
    {
        var (sent, reorder) = FastOrder.Of(statement, context);
        return new Translated(TSqlWriter.Write(sent, context), reorder,
                              statement is AssignStatement assign ? assign.Variables : null);
    }

    public static List<Translated> Translate(string sql, TSqlContext context) =>
        [.. TSqlParser.Parse(sql).Select(statement => Translate(statement, context))];
}
