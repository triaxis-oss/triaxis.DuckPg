namespace triaxis.DuckPg.TSql;

/// T-SQL in, DuckDB SQL out, one string per statement in the batch.
static class TSqlTranslator
{
    public static List<string> Translate(string sql, TSqlContext context) =>
        [.. TSqlParser.Parse(sql).Select(statement => TSqlWriter.Write(statement, context))];
}
