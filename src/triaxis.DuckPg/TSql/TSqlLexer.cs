using System.Text;

namespace triaxis.DuckPg.TSql;

enum TokenKind
{
    End,
    /// A bare word. Keywords are not reserved here -- the parser decides what a word means.
    Word,
    /// `[bracketed]` or `"quoted"`: always a name, never a keyword.
    QuotedName,
    Number,
    String,
    /// `0xDEADBEEF`
    Binary,
    /// `@p` -- a parameter.
    Variable,
    /// `@@version` -- a session value.
    SystemVariable,
    Operator,
}

readonly record struct Token(TokenKind Kind, string Text, int Position)
{
    public bool Is(TokenKind kind) => Kind == kind;

    /// Words are compared without case, which is what makes `select` and `SELECT` one keyword.
    public bool Is(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);

    public bool Is(TokenKind kind, string text) =>
        Kind == kind && string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Kind switch
    {
        TokenKind.End => "end of statement",
        TokenKind.String => $"'{Text}'",
        _ => Text,
    };
}

sealed class TSqlException(string message, int position)
    : Exception($"{message} at position {position}")
{
    public int Position { get; } = position;
}

/// Turns T-SQL text into tokens. It knows the lexical rules only -- what a word means is the
/// parser's business, which is why nothing here is a keyword.
sealed class TSqlLexer(string sql)
{
    int pos;

    public List<Token> Tokenise()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Kind == TokenKind.End) return tokens;
        }
    }

    Token Next()
    {
        SkipTrivia();
        if (pos >= sql.Length) return new Token(TokenKind.End, "", pos);

        var start = pos;
        var c = sql[pos];

        if (c == '[') return new Token(TokenKind.QuotedName, Delimited('[', ']'), start);
        if (c == '"') return new Token(TokenKind.QuotedName, Delimited('"', '"'), start);
        if (c == '\'') return new Token(TokenKind.String, Delimited('\'', '\''), start);

        // `N'...'` is the same string; the prefix only says the literal is Unicode, which every
        // string is once it reaches DuckDB.
        if ((c is 'N' or 'n') && pos + 1 < sql.Length && sql[pos + 1] == '\'')
        {
            pos++;
            return new Token(TokenKind.String, Delimited('\'', '\''), start);
        }

        if (c == '@')
        {
            pos++;
            var system = pos < sql.Length && sql[pos] == '@';
            if (system) pos++;
            var name = Word();
            if (name.Length == 0) throw new TSqlException("expected a variable name after '@'", start);
            return new Token(system ? TokenKind.SystemVariable : TokenKind.Variable, name, start);
        }

        if (char.IsDigit(c) || (c == '.' && pos + 1 < sql.Length && char.IsDigit(sql[pos + 1])))
            return Number(start);

        if (char.IsLetter(c) || c is '_' or '#')
            return new Token(TokenKind.Word, Word(), start);

        return new Token(TokenKind.Operator, Operator(), start);
    }

    void SkipTrivia()
    {
        while (pos < sql.Length)
        {
            if (char.IsWhiteSpace(sql[pos])) { pos++; continue; }

            if (sql[pos] == '-' && pos + 1 < sql.Length && sql[pos + 1] == '-')
            {
                while (pos < sql.Length && sql[pos] != '\n') pos++;
                continue;
            }

            // Block comments nest in T-SQL, unlike most dialects.
            if (sql[pos] == '/' && pos + 1 < sql.Length && sql[pos + 1] == '*')
            {
                var depth = 0;
                while (pos < sql.Length)
                {
                    if (sql[pos] == '/' && pos + 1 < sql.Length && sql[pos + 1] == '*') { depth++; pos += 2; continue; }
                    if (sql[pos] == '*' && pos + 1 < sql.Length && sql[pos + 1] == '/')
                    {
                        pos += 2;
                        if (--depth == 0) break;
                        continue;
                    }
                    pos++;
                }
                continue;
            }

            return;
        }
    }

    /// A delimited run in which the closing character is escaped by doubling it.
    string Delimited(char open, char close)
    {
        var value = new StringBuilder();
        var start = pos;
        pos++;

        while (true)
        {
            if (pos >= sql.Length) throw new TSqlException($"unterminated {open}{close}", start);

            if (sql[pos] == close)
            {
                if (pos + 1 < sql.Length && sql[pos + 1] == close) { value.Append(close); pos += 2; continue; }
                pos++;
                return value.ToString();
            }

            value.Append(sql[pos++]);
        }
    }

    string Word()
    {
        var start = pos;
        while (pos < sql.Length && (char.IsLetterOrDigit(sql[pos]) || sql[pos] is '_' or '#' or '$' or '@')) pos++;
        return sql[start..pos];
    }

    Token Number(int start)
    {
        if (sql[pos] == '0' && pos + 1 < sql.Length && sql[pos + 1] is 'x' or 'X')
        {
            pos += 2;
            var digits = pos;
            while (pos < sql.Length && Uri.IsHexDigit(sql[pos])) pos++;
            return new Token(TokenKind.Binary, sql[digits..pos], start);
        }

        while (pos < sql.Length && char.IsDigit(sql[pos])) pos++;
        if (pos < sql.Length && sql[pos] == '.')
        {
            pos++;
            while (pos < sql.Length && char.IsDigit(sql[pos])) pos++;
        }
        if (pos < sql.Length && sql[pos] is 'e' or 'E')
        {
            var mark = pos;
            pos++;
            if (pos < sql.Length && sql[pos] is '+' or '-') pos++;
            if (pos < sql.Length && char.IsDigit(sql[pos]))
                while (pos < sql.Length && char.IsDigit(sql[pos])) pos++;
            else
                pos = mark;
        }

        return new Token(TokenKind.Number, sql[start..pos], start);
    }

    static readonly string[] Compound = ["<>", "!=", "!<", "!>", "<=", ">=", "::", "+=", "-=", "*=", "/=", "%=", "||"];

    string Operator()
    {
        foreach (var candidate in Compound)
            if (pos + candidate.Length <= sql.Length
                && sql.AsSpan(pos, candidate.Length).SequenceEqual(candidate))
            {
                pos += candidate.Length;
                return candidate;
            }

        return sql[pos++].ToString();
    }
}
