namespace triaxis.DuckPg.TSql;

/// One translated statement: the DuckDB SQL to send, and whatever has to be done to its rows after
/// DuckDB has produced them.
sealed record Translated(string Sql, Reorder? Reorder);

/// T-SQL in, DuckDB SQL out, one statement in the batch at a time.
static class TSqlTranslator
{
    public static List<Translated> Translate(string sql, TSqlContext context) =>
    [
        .. TSqlParser.Parse(sql).Select(statement =>
        {
            var (sent, reorder) = FastOrder.Of(statement, context);
            return new Translated(TSqlWriter.Write(sent, context), reorder);
        })
    ];
}
