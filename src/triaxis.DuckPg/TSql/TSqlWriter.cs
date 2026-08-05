using System.Text;

namespace triaxis.DuckPg.TSql;

/// What the statement is being translated for: which schema `dbo` means, which `@parameters` were
/// declared, what the `@@variables` currently are, and who is asking -- a session's login name, or
/// the account duckpg itself runs as when nothing is connected.
sealed record TSqlContext(
    string Schema,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlySet<string> Parameters,
    string User);

/// Renders the parsed statement as DuckDB SQL. Every difference between the dialects is decided
/// here, on the tree, where the shape of the statement is known -- not on its text, where it is not.
sealed class TSqlWriter(TSqlContext context)
{
    readonly StringBuilder sql = new();

    /// Names a query defines for itself. A common table expression is a table only inside the
    /// query that declares it, so it must not be resolved against the lake's schema.
    readonly HashSet<string> defined = new(StringComparer.OrdinalIgnoreCase);

    public static string Write(Statement statement, TSqlContext context)
    {
        var writer = new TSqlWriter(context);
        writer.Statement(statement);
        return writer.sql.ToString();
    }

    public static string Write(Expr expr, TSqlContext context)
    {
        var writer = new TSqlWriter(context);
        writer.Expression(expr);
        return writer.sql.ToString();
    }

    TSqlWriter Put(string text)
    {
        sql.Append(text);
        return this;
    }

    void Join<T>(IEnumerable<T> items, Action<T> write, string separator = ", ")
    {
        var first = true;
        foreach (var item in items)
        {
            if (!first) Put(separator);
            first = false;
            write(item);
        }
    }

    // ---- statements ------------------------------------------------------------------------------

    /// Procedures answered by doing nothing, because what they arrange a lake has already arranged.
    static readonly HashSet<string> Granted =
        new(StringComparer.OrdinalIgnoreCase) { "sp_getapplock", "sp_releaseapplock" };

    void Statement(Statement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                Query(select.Query);
                return;

            case SelectIntoStatement into:
                Put("CREATE TEMP TABLE ");
                TempTable(into.Target);
                Put(" AS ");
                Query(into.Query);
                return;

            case DropTableStatement drop:
                Put("DROP TABLE ");
                if (drop.IfExists) Put("IF EXISTS ");
                TempTable(drop.Target);
                return;

            case InsertStatement insert:
                Put("INSERT INTO ");
                Table(insert.Target);
                if (insert.Columns.Count > 0)
                {
                    Put(" (");
                    Join(insert.Columns, c => Put(Quote(c)));
                    Put(")");
                }
                switch (insert.Source)
                {
                    case InsertValues values:
                        Put(" VALUES ");
                        Join(values.Rows, row => { Put("("); Join(row, Expression); Put(")"); });
                        return;
                    case InsertQuery query:
                        Put(" ");
                        Query(query.Query);
                        return;
                }
                return;

            case UpdateStatement update:
                Put("UPDATE ");
                Table(update.Target);
                if (update.Alias is not null) Put(" AS ").Put(Quote(update.Alias));
                Put(" SET ");
                Join(update.Assignments, a => { Put(Quote(a.Column)); Put(" = "); Expression(a.Value); });
                if (update.From is not null) { Put(" FROM "); Source(update.From); }
                if (update.Where is not null) { Put(" WHERE "); Expression(update.Where); }
                return;

            case DeleteStatement delete:
                Put("DELETE FROM ");
                Table(delete.Target);
                if (delete.From is not null) { Put(" USING "); Source(delete.From); }
                if (delete.Where is not null) { Put(" WHERE "); Expression(delete.Where); }
                return;

            // The gateway already treats these as no-ops; rendering them keeps one path for
            // everything a client sends.
            case SetOptionStatement option:
                Put("SET ").Put(option.Option);
                return;

            // An application lock serialises a caller against the other connections of a shared
            // database, and a lake is not one: it serves the application that owns its files. The
            // exclusion asked for is already there, so granting it is rendering nothing -- the
            // empty statement is the gateway's no-op, and a batched EXEC carries no result anyway.
            case ExecuteStatement execute when Granted.Contains(execute.Procedure.Table.Text):
                return;

            case ExecuteStatement execute:
                throw new TSqlException($"stored procedure {execute.Procedure.Table.Text} is not supported", 0);

            case TransactionStatement transaction:
                Put(transaction.Action switch
                {
                    TransactionAction.Begin => "BEGIN TRANSACTION",
                    TransactionAction.Commit => "COMMIT",

                    // Rolling back to a savepoint keeps what the transaction did before it and
                    // discards the rest. DuckDB has no savepoints, and half a transaction cannot be
                    // made out of the two things it does have -- so the caller is told, rather than
                    // silently handed the half it did not ask for.
                    TransactionAction.Rollback when transaction.Name is not null =>
                        throw new TSqlException(
                            $"ROLLBACK TRANSACTION {transaction.Name} cannot be honoured: there is no savepoint to " +
                            "roll back to, and keeping the rest of the transaction would keep writes meant to go", 0),

                    TransactionAction.Rollback => "ROLLBACK",

                    // Marking a point to return to costs nothing while returning to it is refused:
                    // a transaction that reaches its COMMIT passed the savepoint without needing it.
                    _ => "",
                });
                return;

            default:
                throw new TSqlException($"cannot render {statement.GetType().Name}", 0);
        }
    }

    // ---- queries ---------------------------------------------------------------------------------

    void Query(Query query)
    {
        var scope = query.With.Select(cte => cte.Name.Text).Where(defined.Add).ToList();

        if (query.With.Count > 0)
        {
            Put("WITH ");
            Join(query.With, cte =>
            {
                Put(Quote(cte.Name));
                if (cte.Columns.Count > 0) { Put(" ("); Join(cte.Columns, c => Put(Quote(c))); Put(")"); }
                Put(" AS (");
                Query(cte.Query);
                Put(")");
            });
            Put(" ");
        }

        Body(query.Body);

        if (query.OrderBy.Count > 0)
        {
            Put(" ORDER BY ");
            Join(query.OrderBy, Order);
        }

        // TOP and OFFSET/FETCH are the same idea said twice; DuckDB says it once.
        var top = (query.Body as SelectBody)?.Top;
        if (top is not null && query.Fetch is not null)
            throw new TSqlException("TOP and FETCH cannot both limit one query", 0);

        if (query.Fetch is not null) { Put(" LIMIT "); Expression(query.Fetch); }
        else if (top is not null) { Put(" LIMIT "); Expression(top is ParenExpr paren ? paren.Inner : top); }

        if (query.Offset is not null) { Put(" OFFSET "); Expression(query.Offset); }

        foreach (var name in scope) defined.Remove(name);
    }

    void Order(OrderTerm term)
    {
        Expression(term.Expr);
        if (term.Descending) Put(" DESC");
    }

    void Body(QueryBody body)
    {
        switch (body)
        {
            case SelectBody select:
                if (select.TopPercent) throw new TSqlException("TOP PERCENT is not supported", 0);
                // Lifted off by the statement that owns it, so one here is an INTO in a subquery,
                // a set operation or a CTE -- places that have nowhere to put a table.
                if (select.Into is { } into)
                    throw new TSqlException($"SELECT ... INTO {into.Table.Text} is a statement of its own", 0);
                Put("SELECT ");
                if (select.Distinct) Put("DISTINCT ");
                Join(select.Items, item =>
                {
                    Expression(item.Expr);
                    if (item.Alias is not null) Put(" AS ").Put(Quote(item.Alias));
                });
                if (select.From is not null) { Put(" FROM "); Source(select.From); }
                if (select.Where is not null) { Put(" WHERE "); Expression(select.Where); }
                if (select.GroupBy.Count > 0) { Put(" GROUP BY "); Join(select.GroupBy, Expression); }
                if (select.Having is not null) { Put(" HAVING "); Expression(select.Having); }
                return;

            case SetOperationBody set:
                Body(set.Left);
                Put($" {set.Operator}{(set.All ? " ALL" : "")} ");
                Body(set.Right);
                return;

            case ValuesBody values:
                Put("VALUES ");
                Join(values.Rows, row => { Put("("); Join(row, Expression); Put(")"); });
                return;
        }
    }

    // ---- table sources ---------------------------------------------------------------------------

    void Source(TableSource source)
    {
        switch (source)
        {
            case NamedTableSource named:
                Table(named.Name);
                if (named.Alias is not null) Put(" AS ").Put(Quote(named.Alias));
                return;

            case DerivedTableSource derived:
                Put("(");
                Query(derived.Query);
                Put(")");
                if (derived.Alias is not null) Put(" AS ").Put(Quote(derived.Alias));
                if (derived.Columns.Count > 0) { Put(" ("); Join(derived.Columns, c => Put(Quote(c))); Put(")"); }
                return;

            case OpenJsonSource openJson:
                OpenJson(openJson);
                return;

            case FunctionTableSource function:
                Expression(function.Call);
                if (function.Alias is not null) Put(" AS ").Put(Quote(function.Alias));
                if (function.Columns.Count > 0) { Put(" ("); Join(function.Columns, c => Put(Quote(c))); Put(")"); }
                return;

            case JoinSource join:
                Source(join.Left);
                Put(join.Kind switch
                {
                    JoinKind.Inner => " INNER JOIN ",
                    JoinKind.Left => " LEFT JOIN ",
                    JoinKind.Right => " RIGHT JOIN ",
                    JoinKind.Full => " FULL JOIN ",
                    _ => " CROSS JOIN ",
                });
                // A join under a join keeps its own ON with it, which parentheses say plainly --
                // written flat, the conditions would close in an order the reader has to work out.
                if (join.Right is JoinSource) { Put("("); Source(join.Right); Put(")"); }
                else Source(join.Right);
                if (join.On is not null) { Put(" ON "); Expression(join.On); }
                return;
        }
    }

    /// A column can carry the same qualification a table does -- LLBLGen writes
    /// `[dbo].[Orders].[OrderID]` -- and the qualifier has to follow the table to the lake's
    /// schema, or it names a table that is not the one in the FROM clause.
    void Column(List<Name> parts)
    {
        if (parts.Count < 3)
        {
            Join(parts, part => Put(Quote(part)), ".");
            return;
        }

        Qualifier(parts[..^1]);
        Put(".").Put(Quote(parts[^1]));
    }

    void Qualifier(List<Name> parts)
    {
        if (parts.Count < 2) Join(parts, part => Put(Quote(part)), ".");
        else Table(new TableName(parts));
    }

    /// A client says `dbo.orders`, or `[app].[dbo].[orders]`, and means the one table the lake
    /// publishes. The database part names the server it came from, which is this one.
    void Table(TableName name)
    {
        if (name.Parts.Count == 1 && (defined.Contains(name.Table.Text) || Temporary(name)))
        {
            Put(Quote(name.Table));
            return;
        }

        var schema = name.Schema;
        var resolved = schema is null || schema.Text.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            ? context.Schema
            : schema.Text;

        Put(SqlText.Quote(resolved)).Put(".").Put(Quote(name.Table));
    }

    /// `#t` is a temporary table, and DuckDB's belong to a connection exactly as SQL Server's
    /// belong to a session -- so it needs no schema and must not be given the lake's. `##t` is a
    /// different promise: a global temporary table is one another connection can see, and there is
    /// nothing here to share it with.
    static bool Temporary(TableName name)
    {
        if (name.Parts.Count != 1 || !name.Table.Text.StartsWith('#')) return false;
        if (name.Table.Text.StartsWith("##"))
            throw new TSqlException($"global temporary table {name.Table.Text} is not supported", 0);
        return true;
    }

    /// Where a statement makes or unmakes a table, rather than reads one. The lake's own tables are
    /// files: what is in them is a layer's business, and that they exist at all is the config's.
    void TempTable(TableName name)
    {
        if (!Temporary(name))
            throw new TSqlException($"{name.Table.Text} is not a temporary table, " +
                                    "and a lake's tables are the files under it", 0);
        Put(Quote(name.Table));
    }

    /// OPENJSON is a derived table here, and its columns are projected rather than resolved: a
    /// reference to `f.value` is then an ordinary column of a subquery, with nothing left to
    /// rewrite where it is used. Each element of the array comes out of `unnest`, and each declared
    /// column is the path it names, cast to the type it declares -- `$` being the element itself,
    /// and a column with no path meaning its own name.
    void OpenJson(OpenJsonSource source)
    {
        Put("(SELECT ");

        if (source.Schema.Count == 0)
        {
            // SQL Server's own shape: the index, the element as text, and a code for what it is.
            Put("\"key\", \"value\", CASE \"type\" WHEN 'NULL' THEN 0 WHEN 'VARCHAR' THEN 1 " +
                "WHEN 'BOOLEAN' THEN 3 WHEN 'ARRAY' THEN 4 WHEN 'OBJECT' THEN 5 ELSE 2 END AS \"type\" " +
                "FROM json_each(");
            Document(source);
            Put(")");
        }
        else
        {
            Join(source.Schema, column =>
            {
                Put("CAST(\"__element\" ->> ").Put(SqlText.Literal(column.Path ?? "$." + column.Name.Text));
                Put(" AS ").Put(TypeName(column.Type)).Put(") AS ").Put(Quote(column.Name));
            });

            Put(" FROM (SELECT unnest(from_json(");
            Document(source);
            Put(", '[\"JSON\"]')) AS \"__element\")");
        }

        Put(")");
        if (source.Alias is not null) Put(" AS ").Put(Quote(source.Alias));
    }

    /// The document itself, or the part of it a second argument points at.
    void Document(OpenJsonSource source)
    {
        Put("CAST(");
        Expression(source.Json);
        Put(" AS JSON)");

        if (source.Path is null) return;
        Put(" -> ");
        Expression(source.Path);
    }

    // ---- expressions -----------------------------------------------------------------------------

    void Expression(Expr expr)
    {
        switch (expr)
        {
            case Literal literal:
                Put(literal.Kind switch
                {
                    LiteralKind.Number => literal.Text,
                    LiteralKind.String => SqlText.Literal(literal.Text),
                    LiteralKind.Binary => $"from_hex({SqlText.Literal(literal.Text)})",
                    LiteralKind.Null => "NULL",
                    LiteralKind.True => "TRUE",
                    _ => "FALSE",
                });
                return;

            case ColumnRef column:
                Column(column.Parts);
                return;

            case StarRef star:
                if (star.Qualifier.Count > 0) { Qualifier(star.Qualifier); Put("."); }
                Put("*");
                return;

            case VariableRef variable:
                Put(Variable(variable));
                return;

            case ParenExpr paren:
                Put("(");
                Expression(paren.Inner);
                Put(")");
                return;

            case UnaryExpr unary:
                Put(unary.Operator == "NOT" ? "NOT " : unary.Operator);
                Expression(unary.Operand);
                return;

            case BinaryExpr binary:
                Binary(binary);
                return;

            case CaseExpr caseExpr:
                Put("CASE");
                if (caseExpr.Operand is not null) { Put(" "); Expression(caseExpr.Operand); }
                foreach (var (when, then) in caseExpr.Branches)
                {
                    Put(" WHEN ");
                    Expression(when);
                    Put(" THEN ");
                    Expression(then);
                }
                if (caseExpr.Else is not null) { Put(" ELSE "); Expression(caseExpr.Else); }
                Put(" END");
                return;

            case CastExpr cast:
                Put("CAST(");
                Expression(cast.Value);
                Put(" AS ").Put(TypeName(cast.Type)).Put(")");
                return;

            case ConvertExpr convert:
                if (convert.Style is not null)
                    throw new TSqlException("CONVERT with a style is not supported; use FORMAT or CAST", 0);
                Put("CAST(");
                Expression(convert.Value);
                Put(" AS ").Put(TypeName(convert.Type)).Put(")");
                return;

            case InExpr inExpr:
                Expression(inExpr.Value);
                Put(inExpr.Negated ? " NOT IN (" : " IN (");
                if (inExpr.Subquery is not null) Query(inExpr.Subquery);
                else Join(inExpr.Items, Expression);
                Put(")");
                return;

            case BetweenExpr between:
                Expression(between.Value);
                Put(between.Negated ? " NOT BETWEEN " : " BETWEEN ");
                Expression(between.Low);
                Put(" AND ");
                Expression(between.High);
                return;

            case LikeExpr like:
                Expression(like.Value);
                Put(like.Negated ? " NOT LIKE " : " LIKE ");
                Expression(like.Pattern);
                if (like.Escape is not null) { Put(" ESCAPE "); Expression(like.Escape); }
                return;

            case IsNullExpr isNull:
                Expression(isNull.Value);
                Put(isNull.Negated ? " IS NOT NULL" : " IS NULL");
                return;

            case ExistsExpr exists:
                Put("EXISTS (");
                Query(exists.Query);
                Put(")");
                return;

            case SubqueryExpr subquery:
                Put("(");
                Query(subquery.Query);
                Put(")");
                return;

            case FunctionCall call:
                Function(call);
                return;

            default:
                throw new TSqlException($"cannot render {expr.GetType().Name}", 0);
        }
    }

    /// T-SQL spells string concatenation `+`, which in DuckDB is arithmetic. Only where one side is
    /// provably text does this become `||` -- guessing on the rest would turn `1 + 2` into `'12'`.
    void Binary(BinaryExpr binary)
    {
        var op = binary.Operator == "+" && (Textual(binary.Left) || Textual(binary.Right)) ? "||" : binary.Operator;
        Expression(binary.Left);
        Put($" {op} ");
        Expression(binary.Right);
    }

    static readonly HashSet<string> TextFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "substring", "upper", "lower", "ltrim", "rtrim", "trim", "concat", "replace", "left", "right",
        "str", "format", "stuff", "reverse", "space",
    };

    static bool Textual(Expr expr) => expr switch
    {
        Literal { Kind: LiteralKind.String } => true,
        ParenExpr paren => Textual(paren.Inner),
        BinaryExpr { Operator: "+" } binary => Textual(binary.Left) || Textual(binary.Right),
        CastExpr cast => cast.Type.Name.Contains("char", StringComparison.OrdinalIgnoreCase)
                         || cast.Type.Name.Contains("text", StringComparison.OrdinalIgnoreCase),
        FunctionCall { Name.Count: 1 } call => TextFunctions.Contains(call.Name[0].Text),
        _ => false,
    };

    string Variable(VariableRef variable)
    {
        if (!variable.System)
            return context.Parameters.Contains(variable.Name)
                ? "$" + variable.Name
                : throw new TSqlException($"undeclared variable @{variable.Name}", 0);

        return context.Variables.TryGetValue(variable.Name, out var value)
            ? value
            : throw new TSqlException($"unsupported session value @@{variable.Name}", 0);
    }

    void Function(FunctionCall call)
    {
        if (call.Name.Count == 1 && Rewritten(call)) return;

        // DuckDB has one count and it is a BIGINT. SQL Server has two: COUNT, which is an `int`,
        // and COUNT_BIG, which is not -- so an application that casts what COUNT returns to `int`
        // throws on a lake where it would not on a database. The cast goes around the window
        // clause too, since a counted window is just as much an `int`.
        var counting = call.Name is [{ Quoted: false } only] ? only.Text.ToLowerInvariant() : "";
        var narrow = counting == "count";

        if (narrow) Put("CAST(");

        // A function name is not an identifier to be quoted: `"COUNT"` is a column called COUNT.
        if (counting == "count_big") Put("count");
        else Join(call.Name, part => Put(part.Quoted ? Quote(part) : part.Text), ".");

        Put("(");
        if (call.Distinct) Put("DISTINCT ");
        Join(call.Arguments, Expression);
        Put(")");

        if (call.Over is null)
        {
            if (narrow) Put(" AS INTEGER)");
            return;
        }

        Put(" OVER (");
        if (call.Over.PartitionBy.Count > 0)
        {
            Put("PARTITION BY ");
            Join(call.Over.PartitionBy, Expression);
        }
        if (call.Over.OrderBy.Count > 0)
        {
            if (call.Over.PartitionBy.Count > 0) Put(" ");
            Put("ORDER BY ");
            Join(call.Over.OrderBy, Order);
        }
        Put(")");

        if (narrow) Put(" AS INTEGER)");
    }

    /// The functions whose DuckDB equivalent is spelled differently, takes its arguments in another
    /// order, or is not a function at all.
    bool Rewritten(FunctionCall call)
    {
        var name = call.Name[0].Text.ToLowerInvariant();
        var args = call.Arguments;

        switch (name)
        {
            case "getdate" or "sysdatetime":
                Put("current_localtimestamp()");
                return true;

            case "getutcdate" or "sysutcdatetime":
                Put("timezone('UTC', now())");
                return true;

            case "newid":
                Put("uuid()");
                return true;

            // Who is asking is a fixed string here, not a lookup: there is no server-wide principal
            // behind a lake of files, only the session that asked or the account serving it.
            case "suser_sname" or "suser_name" or "user_name" or "original_login":
                Put(SqlText.Literal(context.User));
                return true;

            case "isnull" when args.Count == 2:
                Call("coalesce", args);
                return true;

            // LEN ignores trailing spaces, which is a rule of its own rather than a spelling.
            case "len" when args.Count == 1:
                Put("length(rtrim(");
                Expression(args[0]);
                Put("))");
                return true;

            case "iif" when args.Count == 3:
                Put("CASE WHEN ");
                Expression(args[0]);
                Put(" THEN ");
                Expression(args[1]);
                Put(" ELSE ");
                Expression(args[2]);
                Put(" END");
                return true;

            // CHARINDEX takes the needle first; instr takes the haystack first.
            case "charindex" when args.Count >= 2:
                Call("instr", [args[1], args[0]]);
                return true;

            case "datepart" or "datename" when args.Count == 2:
                Put($"date_part({SqlText.Literal(DatePart(args[0]))}, ");
                Expression(args[1]);
                Put(")");
                return true;

            case "datediff" when args.Count == 3:
                Put($"date_diff({SqlText.Literal(DatePart(args[0]))}, ");
                Expression(args[1]);
                Put(", ");
                Expression(args[2]);
                Put(")");
                return true;

            case "dateadd" when args.Count == 3:
                Put("(");
                Expression(args[2]);
                Put(" + (");
                Expression(args[1]);
                Put($") * INTERVAL '1 {DatePart(args[0])}')");
                return true;

            case "ceiling" when args.Count == 1:
                Call("ceil", args);
                return true;

            case "square" when args.Count == 1:
                Put("(");
                Expression(args[0]);
                Put(") ^ 2");
                return true;

            case "scope_identity" or "ident_current":
                throw new TSqlException($"{name}() has no meaning over files", 0);

            default:
                return false;
        }
    }

    void Call(string name, List<Expr> arguments)
    {
        Put(name).Put("(");
        Join(arguments, Expression);
        Put(")");
    }

    /// The date part is written as a bare word in T-SQL and as a string everywhere else.
    static string DatePart(Expr expr) => expr switch
    {
        ColumnRef { Parts.Count: 1 } part => Part(part.Parts[0].Text),
        Literal { Kind: LiteralKind.String } literal => Part(literal.Text),
        _ => throw new TSqlException("the date part has to be a name such as day or month", 0),
    };

    static string Part(string part) => part.ToLowerInvariant() switch
    {
        "yy" or "yyyy" => "year",
        "qq" or "q" => "quarter",
        "mm" or "m" => "month",
        "dy" or "y" => "dayofyear",
        "dd" or "d" => "day",
        "wk" or "ww" => "week",
        "dw" or "w" => "dayofweek",
        "hh" => "hour",
        "mi" or "n" => "minute",
        "ss" or "s" => "second",
        "ms" => "millisecond",
        "mcs" => "microsecond",
        "ns" => "nanosecond",
        var other => other,
    };

    // ---- names and types -------------------------------------------------------------------------

    /// Everything is quoted on the way out: DuckDB matches quoted identifiers without case anyway,
    /// so quoting costs nothing and keeps a column called `order` from becoming a keyword.
    static string Quote(Name name) => SqlText.Quote(name.Text);

    string TypeName(TypeRef type)
    {
        var arguments = type.Arguments.Count > 0 && !type.Arguments[0].Equals("max", StringComparison.OrdinalIgnoreCase)
            ? $"({string.Join(", ", type.Arguments)})"
            : "";

        return type.Name.ToLowerInvariant() switch
        {
            "bit" => "BOOLEAN",
            "tinyint" => "UTINYINT",
            "smallint" => "SMALLINT",
            "int" or "integer" => "INTEGER",
            "bigint" => "BIGINT",
            "real" => "FLOAT",
            "float" or "double precision" => "DOUBLE",
            "money" => "DECIMAL(19,4)",
            "smallmoney" => "DECIMAL(10,4)",
            "decimal" or "numeric" => $"DECIMAL{(arguments.Length > 0 ? arguments : "(18,0)")}",
            "date" => "DATE",
            "time" => "TIME",
            "datetime" or "datetime2" or "smalldatetime" => "TIMESTAMP",
            "datetimeoffset" => "TIMESTAMPTZ",
            "uniqueidentifier" => "UUID",
            "binary" or "varbinary" or "image" => "BLOB",
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml" or "sql_variant"
                or "character" or "character varying" => "VARCHAR",
            // Not a SQL Server type, so it is one the caller means literally -- DuckDB has many.
            _ => type.Name.ToUpperInvariant() + arguments,
        };
    }
}
