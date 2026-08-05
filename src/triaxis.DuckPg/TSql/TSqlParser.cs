namespace triaxis.DuckPg.TSql;

/// Recursive-descent parser for the T-SQL an application sends. Anything outside that subset is
/// refused by name -- a query that cannot be understood is not a query that should be guessed at.
sealed class TSqlParser
{
    readonly List<Token> tokens;
    int pos;

    TSqlParser(List<Token> tokens) => this.tokens = tokens;

    public static List<Statement> Parse(string sql)
    {
        var parser = new TSqlParser(new TSqlLexer(sql).Tokenise());
        var statements = new List<Statement>();

        while (true)
        {
            while (parser.AcceptOperator(";")) { }
            if (parser.Peek.Kind == TokenKind.End) return statements;
            statements.Add(parser.Statement());
        }
    }

    /// One expression standing on its own, as a dacpac's declared default is. Anything left over is
    /// refused rather than ignored -- a default that only half parsed is not one to apply.
    public static Expr ParseExpression(string sql)
    {
        var parser = new TSqlParser(new TSqlLexer(sql).Tokenise());
        var expr = parser.Expression();
        return parser.Peek.Kind == TokenKind.End
            ? expr
            : throw new TSqlException($"unexpected `{parser.Peek.Text}`", parser.Peek.Position);
    }

    // ---- token plumbing --------------------------------------------------------------------------

    Token Peek => tokens[pos];

    Token Ahead(int n) => tokens[Math.Min(pos + n, tokens.Count - 1)];

    Token Take() => tokens[pos++];

    bool Accept(string word)
    {
        if (!Peek.Is(word)) return false;
        pos++;
        return true;
    }

    bool AcceptOperator(string op)
    {
        if (!Peek.Is(TokenKind.Operator, op)) return false;
        pos++;
        return true;
    }

    void Expect(string word)
    {
        if (!Accept(word)) throw Unexpected($"expected {word}");
    }

    void ExpectOperator(string op)
    {
        if (!AcceptOperator(op)) throw Unexpected($"expected {op}");
    }

    TSqlException Unexpected(string what) => new($"{what}, found {Peek}", Peek.Position);

    /// A name is a bare word or a quoted one; a quoted name keeps its case, a bare one does not.
    Name Identifier()
    {
        var token = Take();
        return token.Kind switch
        {
            TokenKind.Word => new Name(token.Text, false),
            TokenKind.QuotedName => new Name(token.Text, true),
            _ => throw new TSqlException($"expected a name, found {token}", token.Position),
        };
    }

    bool AtName => Peek.Kind is TokenKind.Word or TokenKind.QuotedName;

    // ---- statements ------------------------------------------------------------------------------

    Statement Statement()
    {
        if (Peek.Is("select") || Peek.Is("with") || Peek.Is("values")) return SelectOrInto();
        if (Peek.Is("insert")) return Insert();
        if (Peek.Is("drop")) return Drop();
        if (Peek.Is("update")) return Update();
        if (Peek.Is("delete")) return Delete();
        if (Peek.Is("merge")) return Merge();
        if (Peek.Is("set")) return SetOption();
        if (Peek.Is("exec") || Peek.Is("execute")) return Execute();
        if (Peek.Is("begin") || Peek.Is("commit") || Peek.Is("rollback") || Peek.Is("save")) return Transaction();

        throw new TSqlException($"unsupported statement `{Peek.Text}`", Peek.Position);
    }

    /// A query, unless it names somewhere to put the rows: `SELECT … INTO` creates a table, which
    /// is a statement's business and not a query's. The INTO is lifted off the body here so that
    /// nothing below the statement has to know it was ever there.
    Statement SelectOrInto()
    {
        var query = Query();
        return query.Body is SelectBody { Into: { } target } body
            ? new SelectIntoStatement(target, query with { Body = body with { Into = null } })
            : new SelectStatement(query);
    }

    /// `DROP TABLE [IF EXISTS] #t`, which is how a client clears the scratch table it made. What a
    /// lake publishes is a layer file, so dropping one is not a statement it takes.
    Statement Drop()
    {
        Expect("drop");
        if (!Peek.Is("table")) throw Unexpected("only DROP TABLE is supported");
        Expect("table");

        var ifExists = false;
        if (Accept("if")) { Expect("exists"); ifExists = true; }
        return new DropTableStatement(TableName(), ifExists);
    }

    Statement Insert()
    {
        Expect("insert");
        Accept("into");
        var target = TableName();

        var columns = new List<Name>();
        // A parenthesis here is the column list unless it opens the source query.
        if (Peek.Is(TokenKind.Operator, "(") && !Ahead(1).Is("select") && !Ahead(1).Is("with") && !Ahead(1).Is("values"))
        {
            ExpectOperator("(");
            do columns.Add(Identifier()); while (AcceptOperator(","));
            ExpectOperator(")");
        }

        if (Accept("values"))
        {
            var rows = new List<List<Expr>>();
            do rows.Add(ParenthesisedList()); while (AcceptOperator(","));
            return new InsertStatement(target, columns, new InsertValues(rows));
        }

        if (Accept("default"))
        {
            Expect("values");
            return new InsertStatement(target, columns, new InsertValues([[]]));
        }

        return new InsertStatement(target, columns, new InsertQuery(Query()));
    }

    Statement Update()
    {
        Expect("update");
        var target = TableName();
        Expect("set");

        var assignments = Assignments();
        var from = Accept("from") ? TableSource() : null;
        var where = Accept("where") ? Expression() : null;
        return new UpdateStatement(target, null, assignments, from, where);
    }

    /// `MERGE` is another statement by another spelling, and which one depends on the branch. An
    /// update joined to its source is what `WHEN MATCHED THEN UPDATE` means. `WHEN NOT MATCHED THEN
    /// INSERT` over a condition that cannot match is a multi-row insert, which is what EF Core sends
    /// a batch of rows as. What is not covered is the conditional form, where the branch taken
    /// depends on whether a row is already there: what a row's existence means across layers is the
    /// layer machinery's, not one statement's.
    Statement Merge()
    {
        Expect("merge");
        Accept("into");
        var target = TableName();
        var (alias, _) = SourceAlias();

        Expect("using");
        var source = PrimaryTableSource();
        Expect("on");
        var on = Expression();

        Expect("when");
        var matched = !Accept("not");
        Expect("matched");
        if (!matched && Accept("by") && !Accept("target"))
            throw Unexpected("MERGE ... WHEN NOT MATCHED BY SOURCE is not supported");
        if (Peek.Is("and")) throw Unexpected("MERGE WHEN MATCHED AND is not supported");
        Expect("then");

        var statement = matched
            ? MergeUpdate(target, alias, source, on)
            : MergeInsert(target, source, on);

        if (Peek.Is("when")) throw Unexpected("only one MERGE branch at a time is supported");

        // `OUTPUT INSERTED.<key>` asks for what the write made of each row, which is how a caller
        // gets a key it did not send back. A lake writes down what it is given and makes up nothing,
        // so there is nothing to hand back -- and saying so beats answering with the rows as sent.
        if (Peek.Is("output"))
            throw new TSqlException(
                "MERGE ... OUTPUT is not supported: a lake stores the row it is given, so a column " +
                "the statement does not insert has no value to read back", Peek.Position);

        return statement;
    }

    Statement MergeUpdate(TableName target, Name? alias, TableSource source, Expr on)
    {
        if (!Peek.Is("update")) throw Unexpected("only MERGE ... WHEN MATCHED THEN UPDATE is supported");
        Expect("update");
        Expect("set");
        return new UpdateStatement(target, alias, Assignments(), source, on);
    }

    /// The rows are the `USING` clause and the condition says nothing can match, so the branch is
    /// the only one there is: every source row is inserted. A condition that *can* match would make
    /// this an insert of what is not already there, which is a different statement -- one that has
    /// to decide what "already there" means when the row it would shadow is in a layer below.
    Statement MergeInsert(TableName target, TableSource source, Expr on)
    {
        if (!Never(on))
            throw Unexpected("MERGE ... WHEN NOT MATCHED THEN INSERT is supported only over a condition " +
                             "that cannot match, such as ON 1 = 0");

        Expect("insert");
        var columns = new List<Name>();
        ExpectOperator("(");
        do columns.Add(Identifier()); while (AcceptOperator(","));
        ExpectOperator(")");

        Expect("values");
        var values = ParenthesisedList();

        var body = new SelectBody(false, null, false, [.. values.Select(v => new SelectItem(v, null))],
                                  null, source, null, [], null);
        return new InsertStatement(target, columns, new InsertQuery(new Query([], body, [], null, null)));
    }

    /// `ON 1 = 0` and its spellings: what EF Core writes to say that this MERGE is an insert.
    static bool Never(Expr on) => on switch
    {
        ParenExpr paren => Never(paren.Inner),
        BinaryExpr { Operator: "=" or "<>", Left: Literal { Kind: LiteralKind.Number } left,
                     Right: Literal { Kind: LiteralKind.Number } right } binary =>
            (left.Text == right.Text) != (binary.Operator == "="),
        Literal { Kind: LiteralKind.False } => true,
        _ => false,
    };

    List<Assignment> Assignments()
    {
        var assignments = new List<Assignment>();
        do
        {
            var column = Identifier();
            // A qualified target (`t.amount = …`) names the same column; the table part is noise.
            while (AcceptOperator(".")) column = Identifier();
            ExpectOperator("=");
            assignments.Add(new Assignment(column, Expression()));
        } while (AcceptOperator(","));
        return assignments;
    }

    Statement Delete()
    {
        Expect("delete");
        Accept("from");
        var target = TableName();
        var from = Accept("from") ? TableSource() : null;
        var where = Accept("where") ? Expression() : null;
        return new DeleteStatement(target, from, where);
    }

    /// `SET` here is only the session-option form; `SET @x = …` needs variables, which a lake has
    /// no place to keep.
    Statement SetOption()
    {
        Expect("set");
        if (Peek.Kind == TokenKind.Variable)
            throw new TSqlException("SET of a variable is not supported", Peek.Position);

        var words = new List<string>();
        while (Peek.Kind is TokenKind.Word or TokenKind.Number or TokenKind.String
               && !Peek.Is(TokenKind.Operator, ";"))
            words.Add(Take().Text);

        if (words.Count == 0) throw Unexpected("expected an option name");
        return new SetOptionStatement(string.Join(' ', words));
    }

    /// A procedure call, arguments and all. `EXEC ('…')` and `EXEC @rc = …` are refused here rather
    /// than parsed: one is a batch a lake cannot see into, the other wants a variable to put a
    /// result in, and nothing here has one.
    Statement Execute()
    {
        if (!Accept("exec")) Expect("execute");

        if (Peek.Kind == TokenKind.Variable)
            throw new TSqlException("EXEC into a variable is not supported", Peek.Position);
        if (Peek.Is(TokenKind.Operator, "("))
            throw new TSqlException("EXEC of a string is not supported", Peek.Position);

        var procedure = TableName();
        var arguments = new List<ExecuteArgument>();

        if (Peek.Kind != TokenKind.End && !Peek.Is(TokenKind.Operator, ";"))
            do
            {
                Name? name = null;
                if (Peek.Kind == TokenKind.Variable && Ahead(1).Is(TokenKind.Operator, "="))
                {
                    name = new Name(Take().Text, false);
                    ExpectOperator("=");
                }

                var value = Expression();
                if (Peek.Is("output") || Peek.Is("out"))
                    throw new TSqlException("an OUTPUT argument is not supported", Peek.Position);
                arguments.Add(new ExecuteArgument(name, value));
            } while (AcceptOperator(","));

        return new ExecuteStatement(procedure, arguments);
    }

    Statement Transaction()
    {
        var verb = Take().Text.ToLowerInvariant();
        var action = verb switch
        {
            "begin" => TransactionAction.Begin,
            "commit" => TransactionAction.Commit,
            "rollback" => TransactionAction.Rollback,
            _ => TransactionAction.Save,
        };

        if (!Accept("transaction") && !Accept("tran") && action is TransactionAction.Begin or TransactionAction.Save)
            throw Unexpected("expected TRANSACTION");
        Accept("work");

        var name = AtName && !Peek.Is(TokenKind.Operator, ";") ? Identifier().Text : null;
        return new TransactionStatement(action, name);
    }

    // ---- queries ---------------------------------------------------------------------------------

    Query Query()
    {
        var with = new List<CommonTableExpression>();
        if (Accept("with"))
            do
            {
                var name = Identifier();
                var columns = new List<Name>();
                if (Peek.Is(TokenKind.Operator, "(") && !Ahead(1).Is("select"))
                {
                    ExpectOperator("(");
                    do columns.Add(Identifier()); while (AcceptOperator(","));
                    ExpectOperator(")");
                }
                Expect("as");
                ExpectOperator("(");
                var inner = Query();
                ExpectOperator(")");
                with.Add(new CommonTableExpression(name, columns, inner));
            } while (AcceptOperator(","));

        var body = QueryBody();

        var orderBy = new List<OrderTerm>();
        if (Accept("order"))
        {
            Expect("by");
            do
            {
                var expr = Expression();
                var descending = Accept("desc");
                if (!descending) Accept("asc");
                orderBy.Add(new OrderTerm(expr, descending));
            } while (AcceptOperator(","));
        }

        Expr? offset = null, fetch = null;
        if (Accept("offset"))
        {
            offset = Expression();
            if (!Accept("rows")) Accept("row");
            if (Accept("fetch"))
            {
                if (!Accept("next")) Accept("first");
                fetch = Expression();
                if (!Accept("rows")) Accept("row");
                Expect("only");
            }
        }

        return new Query(with, body, orderBy, offset, fetch);
    }

    QueryBody QueryBody()
    {
        var left = QueryTerm();

        while (true)
        {
            string? op = Peek.Is("union") ? "UNION" : Peek.Is("except") ? "EXCEPT" : Peek.Is("intersect") ? "INTERSECT" : null;
            if (op is null) return left;
            pos++;
            var all = Accept("all");
            left = new SetOperationBody(op, all, left, QueryTerm());
        }
    }

    QueryBody QueryTerm()
    {
        if (Peek.Is(TokenKind.Operator, "(") && (Ahead(1).Is("select") || Ahead(1).Is("with") || Ahead(1).Is("values")))
        {
            ExpectOperator("(");
            var inner = QueryBody();
            ExpectOperator(")");
            return inner;
        }

        if (Accept("values"))
        {
            var rows = new List<List<Expr>>();
            do rows.Add(ParenthesisedList()); while (AcceptOperator(","));
            return new ValuesBody(rows);
        }

        return Select();
    }

    SelectBody Select()
    {
        Expect("select");
        Accept("all");
        var distinct = Accept("distinct");

        Expr? top = null;
        var percent = false;
        if (Accept("top"))
        {
            top = Peek.Is(TokenKind.Operator, "(") ? Primary() : Primary();
            percent = Accept("percent");
            if (Accept("with")) Expect("ties");
        }

        var items = new List<SelectItem>();
        do items.Add(SelectItem()); while (AcceptOperator(","));

        var into = Accept("into") ? TableName() : null;
        var from = Accept("from") ? TableSource() : null;
        var where = Accept("where") ? Expression() : null;

        var groupBy = new List<Expr>();
        if (Accept("group"))
        {
            Expect("by");
            do groupBy.Add(Expression()); while (AcceptOperator(","));
        }

        var having = Accept("having") ? Expression() : null;
        return new SelectBody(distinct, top, percent, items, into, from, where, groupBy, having);
    }

    SelectItem SelectItem()
    {
        // T-SQL's own aliasing form, which reads backwards from everyone else's: `alias = expr`.
        if (AtName && Ahead(1).Is(TokenKind.Operator, "="))
        {
            var alias = Identifier();
            ExpectOperator("=");
            return new SelectItem(Expression(), alias);
        }

        var expr = Expression();

        if (Accept("as")) return new SelectItem(expr, Identifier());
        if (AtName && !EndsSelectItem(Peek)) return new SelectItem(expr, Identifier());
        if (Peek.Kind == TokenKind.String) return new SelectItem(expr, new Name(Take().Text, true));
        return new SelectItem(expr, null);
    }

    static readonly HashSet<string> ItemEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "where", "group", "having", "order", "union", "except", "intersect", "offset", "for", "option", "into",
    };

    static bool EndsSelectItem(Token token) => token.Kind == TokenKind.Word && ItemEnders.Contains(token.Text);

    // ---- table sources ---------------------------------------------------------------------------

    TableSource TableSource()
    {
        var left = PrimaryTableSource();

        while (true)
        {
            if (AcceptOperator(","))
            {
                left = new JoinSource(JoinKind.Cross, left, PrimaryTableSource(), null);
                continue;
            }

            if (Accept("cross"))
            {
                Expect("join");
                left = new JoinSource(JoinKind.Cross, left, PrimaryTableSource(), null);
                continue;
            }

            var kind = Peek.Is("inner") ? JoinKind.Inner
                : Peek.Is("left") ? JoinKind.Left
                : Peek.Is("right") ? JoinKind.Right
                : Peek.Is("full") ? JoinKind.Full
                : Peek.Is("join") ? JoinKind.Inner
                : (JoinKind?)null;

            if (kind is null) return left;

            if (!Peek.Is("join")) pos++;
            Accept("outer");
            Expect("join");

            // The operand is a join tree of its own, not just a table: T-SQL lets a join nest inside
            // another and defers the conditions -- `a JOIN b JOIN c ON … ON …`, closing in reverse.
            // A join keyword arriving before this join's ON is the nested one; an ON ends the
            // operand, which is what keeps the ordinary left-deep chain left-deep.
            var right = TableSource();
            Expr? on = Accept("on") ? Expression() : null;
            left = new JoinSource(kind.Value, left, right, on);
        }
    }

    TableSource PrimaryTableSource()
    {
        if (Peek.Is(TokenKind.Operator, "("))
        {
            ExpectOperator("(");

            if (Peek.Is("select") || Peek.Is("with") || Peek.Is("values"))
            {
                var query = Query();
                ExpectOperator(")");
                var (alias, columns) = SourceAlias();
                return new DerivedTableSource(query, alias, columns);
            }

            var nested = TableSource();
            ExpectOperator(")");
            return nested;
        }

        var name = TableName();

        // A table-valued function: `read_parquet('…')` stands exactly where a table does.
        if (Peek.Is(TokenKind.Operator, "("))
        {
            if (name.Parts is [{ Quoted: false, Text: var function }] &&
                function.Equals("openjson", StringComparison.OrdinalIgnoreCase))
                return OpenJson();

            var call = new FunctionCall(name.Parts, ParenthesisedList(), false, null);
            var (fnAlias, fnColumns) = SourceAlias();
            return new FunctionTableSource(call, fnAlias, fnColumns);
        }

        var (tableAlias, _) = SourceAlias();
        SkipHints();
        return new NamedTableSource(name, tableAlias);
    }

    /// `OPENJSON(json [, path]) [WITH (name type ['$.path'] [, ...])] [AS] alias`.
    TableSource OpenJson()
    {
        ExpectOperator("(");
        var json = Expression();
        var path = AcceptOperator(",") ? Expression() : null;
        ExpectOperator(")");

        var schema = new List<OpenJsonColumn>();
        if (Peek.Is("with") && Ahead(1).Is(TokenKind.Operator, "("))
        {
            Expect("with");
            ExpectOperator("(");
            do
            {
                var column = Identifier();
                var type = Type();
                var columnPath = Peek.Kind == TokenKind.String ? Take().Text : null;

                // `AS JSON` keeps the element as a document rather than a value, which is a shape
                // this does not publish.
                if (Peek.Is("as")) throw Unexpected("OPENJSON ... AS JSON is not supported");

                schema.Add(new OpenJsonColumn(column, type, columnPath));
            }
            while (AcceptOperator(","));
            ExpectOperator(")");
        }

        var (alias, _) = SourceAlias();
        return new OpenJsonSource(json, path, schema, alias);
    }

    (Name? Alias, List<Name> Columns) SourceAlias()
    {
        Name? alias = null;
        if (Accept("as")) alias = Identifier();
        else if (AtName && !EndsTableSource(Peek)) alias = Identifier();

        var columns = new List<Name>();
        if (alias is not null && Peek.Is(TokenKind.Operator, "("))
        {
            ExpectOperator("(");
            do columns.Add(Identifier()); while (AcceptOperator(","));
            ExpectOperator(")");
        }

        return (alias, columns);
    }

    static readonly HashSet<string> SourceEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "group", "having", "order", "union", "except", "intersect", "inner", "left", "right", "full",
        "cross", "join", "on", "offset", "for", "option", "set", "values", "with", "outer", "into", "using",
    };

    static bool EndsTableSource(Token token) => token.Kind == TokenKind.Word && SourceEnders.Contains(token.Text);

    /// `WITH (NOLOCK)` and its relatives say how SQL Server should lock, which a read of files does
    /// not have to answer for.
    void SkipHints()
    {
        if (!Peek.Is("with") || !Ahead(1).Is(TokenKind.Operator, "(")) return;
        pos++;
        var depth = 0;
        do
        {
            if (Peek.Is(TokenKind.Operator, "(")) depth++;
            else if (Peek.Is(TokenKind.Operator, ")")) depth--;
            else if (Peek.Kind == TokenKind.End) throw Unexpected("unterminated table hint");
            pos++;
        } while (depth > 0);
    }

    TableName TableName()
    {
        var parts = new List<Name> { Identifier() };
        while (AcceptOperator("."))
        {
            // `server..table` leaves an empty part behind, which names nothing.
            if (Peek.Is(TokenKind.Operator, ".")) continue;
            parts.Add(Identifier());
        }
        return new TableName(parts);
    }

    // ---- expressions -----------------------------------------------------------------------------

    Expr Expression() => Or();

    Expr Or()
    {
        var left = And();
        while (Accept("or")) left = new BinaryExpr("OR", left, And());
        return left;
    }

    Expr And()
    {
        var left = Not();
        while (Accept("and")) left = new BinaryExpr("AND", left, Not());
        return left;
    }

    Expr Not() => Accept("not") ? new UnaryExpr("NOT", Not()) : Comparison();

    static readonly string[] Comparators = ["=", "<>", "!=", "<=", ">=", "<", ">"];

    Expr Comparison()
    {
        var left = Additive();

        while (true)
        {
            var negated = Accept("not");

            if (Accept("is"))
            {
                var isNot = Accept("not");
                Expect("null");
                left = new IsNullExpr(left, isNot);
                continue;
            }

            if (Accept("in"))
            {
                ExpectOperator("(");
                if (Peek.Is("select") || Peek.Is("with"))
                {
                    var query = Query();
                    ExpectOperator(")");
                    left = new InExpr(left, [], query, negated);
                }
                else
                {
                    var items = new List<Expr>();
                    do items.Add(Expression()); while (AcceptOperator(","));
                    ExpectOperator(")");
                    left = new InExpr(left, items, null, negated);
                }
                continue;
            }

            if (Accept("between"))
            {
                var low = Additive();
                Expect("and");
                left = new BetweenExpr(left, low, Additive(), negated);
                continue;
            }

            if (Accept("like"))
            {
                var pattern = Additive();
                var escape = Accept("escape") ? Additive() : null;
                left = new LikeExpr(left, pattern, escape, negated);
                continue;
            }

            if (negated) throw Unexpected("expected IN, BETWEEN, LIKE or NULL after NOT");

            var comparator = Comparators.FirstOrDefault(op => Peek.Is(TokenKind.Operator, op));
            if (comparator is null) return left;
            pos++;
            left = new BinaryExpr(comparator == "!=" ? "<>" : comparator, left, Additive());
        }
    }

    static readonly string[] Additives = ["+", "-", "&", "|", "^", "||"];

    Expr Additive()
    {
        var left = Multiplicative();
        while (Additives.FirstOrDefault(op => Peek.Is(TokenKind.Operator, op)) is { } op)
        {
            pos++;
            left = new BinaryExpr(op, left, Multiplicative());
        }
        return left;
    }

    static readonly string[] Multiplicatives = ["*", "/", "%"];

    Expr Multiplicative()
    {
        var left = Unary();
        while (Multiplicatives.FirstOrDefault(op => Peek.Is(TokenKind.Operator, op)) is { } op)
        {
            pos++;
            left = new BinaryExpr(op, left, Unary());
        }
        return left;
    }

    Expr Unary()
    {
        foreach (var op in (string[])["-", "+", "~"])
            if (AcceptOperator(op))
                return new UnaryExpr(op, Unary());
        return Primary();
    }

    Expr Primary()
    {
        var token = Peek;

        switch (token.Kind)
        {
            case TokenKind.Number:
                pos++;
                return new Literal(LiteralKind.Number, token.Text);

            case TokenKind.String:
                pos++;
                return new Literal(LiteralKind.String, token.Text);

            case TokenKind.Binary:
                pos++;
                return new Literal(LiteralKind.Binary, token.Text);

            case TokenKind.Variable:
                pos++;
                return new VariableRef(token.Text, false);

            case TokenKind.SystemVariable:
                pos++;
                return new VariableRef(token.Text, true);

            case TokenKind.Operator when token.Text == "(":
                pos++;
                if (Peek.Is("select") || Peek.Is("with") || Peek.Is("values"))
                {
                    var query = Query();
                    ExpectOperator(")");
                    return new SubqueryExpr(query);
                }
                var inner = Expression();
                ExpectOperator(")");
                return new ParenExpr(inner);

            case TokenKind.Operator when token.Text == "*":
                pos++;
                return new StarRef([]);
        }

        if (token.Is("null")) { pos++; return new Literal(LiteralKind.Null, "NULL"); }
        if (token.Is("true")) { pos++; return new Literal(LiteralKind.True, "TRUE"); }
        if (token.Is("false")) { pos++; return new Literal(LiteralKind.False, "FALSE"); }
        if (token.Is("case")) return Case();
        if (token.Is("cast")) return Cast();
        if (token.Is("convert")) return Convert();
        if (token.Is("exists"))
        {
            pos++;
            ExpectOperator("(");
            var query = Query();
            ExpectOperator(")");
            return new ExistsExpr(query);
        }

        if (AtName) return NameOrCall();

        throw Unexpected("expected an expression");
    }

    Expr NameOrCall()
    {
        var parts = new List<Name> { Identifier() };

        while (Peek.Is(TokenKind.Operator, "."))
        {
            pos++;
            if (AcceptOperator("*")) return new StarRef(parts);
            parts.Add(Identifier());
        }

        if (!Peek.Is(TokenKind.Operator, "(")) return new ColumnRef(parts);

        ExpectOperator("(");
        var distinct = Accept("distinct");
        var arguments = new List<Expr>();

        if (!Peek.Is(TokenKind.Operator, ")"))
            do arguments.Add(AcceptOperator("*") ? new StarRef([]) : Expression()); while (AcceptOperator(","));
        ExpectOperator(")");

        WindowSpec? over = null;
        if (Accept("over"))
        {
            ExpectOperator("(");
            var partition = new List<Expr>();
            if (Accept("partition"))
            {
                Expect("by");
                do partition.Add(Expression()); while (AcceptOperator(","));
            }

            var order = new List<OrderTerm>();
            if (Accept("order"))
            {
                Expect("by");
                do
                {
                    var expr = Expression();
                    var descending = Accept("desc");
                    if (!descending) Accept("asc");
                    order.Add(new OrderTerm(expr, descending));
                } while (AcceptOperator(","));
            }
            ExpectOperator(")");
            over = new WindowSpec(partition, order);
        }

        return new FunctionCall(parts, arguments, distinct, over);
    }

    Expr Case()
    {
        Expect("case");
        Expr? operand = Peek.Is("when") ? null : Expression();

        var branches = new List<(Expr, Expr)>();
        while (Accept("when"))
        {
            var when = Expression();
            Expect("then");
            branches.Add((when, Expression()));
        }
        if (branches.Count == 0) throw Unexpected("expected WHEN");

        var otherwise = Accept("else") ? Expression() : null;
        Expect("end");
        return new CaseExpr(operand, branches, otherwise);
    }

    Expr Cast()
    {
        Expect("cast");
        ExpectOperator("(");
        var value = Expression();
        Expect("as");
        var type = Type();
        ExpectOperator(")");
        return new CastExpr(value, type);
    }

    Expr Convert()
    {
        Expect("convert");
        ExpectOperator("(");
        var type = Type();
        ExpectOperator(",");
        var value = Expression();
        var style = AcceptOperator(",") ? Expression() : null;
        ExpectOperator(")");
        return new ConvertExpr(type, value, style);
    }

    TypeRef Type()
    {
        var name = Identifier().Text;

        // `DOUBLE PRECISION`, `CHARACTER VARYING` and friends are two words for one type.
        if (Peek.Kind == TokenKind.Word && (Peek.Is("precision") || Peek.Is("varying")))
            name += " " + Take().Text;

        var arguments = new List<string>();
        if (Peek.Is(TokenKind.Operator, "("))
        {
            pos++;
            do arguments.Add(Take().Text); while (AcceptOperator(","));
            ExpectOperator(")");
        }

        return new TypeRef(name, arguments);
    }

    List<Expr> ParenthesisedList()
    {
        ExpectOperator("(");
        var items = new List<Expr>();
        if (!Peek.Is(TokenKind.Operator, ")"))
            do items.Add(Expression()); while (AcceptOperator(","));
        ExpectOperator(")");
        return items;
    }
}
