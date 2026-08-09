using System.Text;

namespace triaxis.DuckPg;

/// Minimal SQL text utilities: enough to find top-level keywords and identifiers without a parser.
/// DuckDB's json_serialize_sql() only handles SELECT, so DML rewriting has to do its own scanning.
static class SqlText
{
    /// PostgreSQL's `$1` written as DuckDB's named `$p1`. A numbered parameter must be bound by the
    /// number it was written with, and DuckDB counts only the ones a statement actually mentions --
    /// so `$3` on its own is "parameter number 3" in a statement that "only has 1", and no value can
    /// reach it by any spelling. A rewritten DML statement is several steps and none of them has to
    /// use every parameter, which makes that unreachable case the ordinary one. A name is not counted
    /// that way: DuckDB ignores one it was handed and does not want, so every step can take the same
    /// arguments. It is also what the TDS door has always sent, `@p0` rendering as `$p0`.
    public static string Rename(string sql)
    {
        StringBuilder? renamed = null;
        var copied = 0;
        for (var i = 0; i < sql.Length;)
        {
            var skip = SkipNonCode(sql, i);
            if (skip >= 0) { i = skip; continue; }

            if (sql[i] == '$' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]) && IsBoundary(sql, i - 1))
            {
                renamed ??= new StringBuilder(sql.Length + 8);
                renamed.Append(sql, copied, i + 1 - copied).Append('p');
                copied = i + 1;
                for (i += 2; i < sql.Length && char.IsAsciiDigit(sql[i]); i++) { }
                continue;
            }
            i++;
        }
        return renamed is null ? sql : renamed.Append(sql, copied, sql.Length - copied).ToString();
    }

    public static IEnumerable<string> SplitStatements(string sql)
    {
        var start = 0;
        foreach (var i in TopLevel(sql, ';'))
        {
            var piece = sql[start..i];
            if (!string.IsNullOrWhiteSpace(piece)) yield return piece.Trim();
            start = i + 1;
        }
        var tail = sql[start..];
        if (!string.IsNullOrWhiteSpace(tail)) yield return tail.Trim();
    }

    public static List<string> SplitList(string sql, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        foreach (var i in TopLevel(sql, separator))
        {
            parts.Add(sql[start..i].Trim());
            start = i + 1;
        }
        parts.Add(sql[start..].Trim());
        return parts;
    }

    public static int FindKeyword(string sql, string keyword, int from = 0)
    {
        var depth = 0;
        for (var i = from; i < sql.Length;)
        {
            var skip = SkipNonCode(sql, i);
            if (skip >= 0) { i = skip; continue; }

            switch (sql[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                default:
                    if (depth == 0
                        && string.Compare(sql, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                        && IsBoundary(sql, i - 1) && IsBoundary(sql, i + keyword.Length))
                        return i;
                    break;
            }
            i++;
        }
        return -1;
    }

    public static string FirstWord(string sql)
    {
        var i = SkipWhitespaceAndComments(sql, 0);
        var start = i;
        while (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_')) i++;
        return sql[start..i].ToUpperInvariant();
    }

    /// Reads a possibly quoted, possibly schema-qualified table reference starting at <paramref name="from"/>.
    public static (string? Schema, string Name, int Start, int End) ReadTableRef(string sql, int from)
    {
        var i = SkipWhitespaceAndComments(sql, from);
        var start = i;
        var parts = new List<string>();
        while (i < sql.Length)
        {
            if (sql[i] == '"')
            {
                var close = i + 1;
                while (close < sql.Length && !(sql[close] == '"' && (close + 1 >= sql.Length || sql[close + 1] != '"')))
                    close += sql[close] == '"' ? 2 : 1;
                parts.Add(sql[(i + 1)..close].Replace("\"\"", "\""));
                i = close + 1;
            }
            else
            {
                var wordStart = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                if (i == wordStart) break;
                parts.Add(sql[wordStart..i]);
            }
            if (i < sql.Length && sql[i] == '.') i++;
            else break;
        }
        return parts.Count switch
        {
            0 => (null, "", start, start),
            1 => (null, parts[0], start, i),
            _ => (parts[^2], parts[^1], start, i),
        };
    }

    /// Whether a statement makes a table belonging to the connection rather than to the lake. Only
    /// ever asked of SQL duckpg rendered itself -- a client's `#t` and the gateway's own scratch are
    /// both written here -- so the keyword is known, and a false yes costs only a catalog scan.
    public static bool MakesTemporary(string sql) => FirstWord(sql) == "CREATE" && FindKeyword(sql, "TEMP") >= 0;

    public static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    public static string Literal(string value) => '\'' + value.Replace("'", "''") + '\'';

    static IEnumerable<int> TopLevel(string sql, char target)
    {
        var depth = 0;
        for (var i = 0; i < sql.Length;)
        {
            var skip = SkipNonCode(sql, i);
            if (skip >= 0) { i = skip; continue; }

            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
            else if (sql[i] == target && depth == 0) yield return i;
            i++;
        }
    }

    /// Index just past a string literal, quoted identifier or comment starting at i; -1 if code starts there.
    static int SkipNonCode(string sql, int i)
    {
        switch (sql[i])
        {
            case '\'':
            case '"':
                var quote = sql[i];
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == quote)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == quote) { j += 2; continue; }
                        return j + 1;
                    }
                    j++;
                }
                return sql.Length;

            case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                var eol = sql.IndexOf('\n', i);
                return eol < 0 ? sql.Length : eol + 1;

            case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return end < 0 ? sql.Length : end + 2;

            default:
                return -1;
        }
    }

    static int SkipWhitespaceAndComments(string sql, int i)
    {
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (sql[i] is '-' or '/')
            {
                var skip = SkipNonCode(sql, i);
                if (skip >= 0 && sql[i] != '\'' && sql[i] != '"') { i = skip; continue; }
            }
            break;
        }
        return i;
    }

    static bool IsBoundary(string sql, int i) =>
        i < 0 || i >= sql.Length || !(char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$');
}
