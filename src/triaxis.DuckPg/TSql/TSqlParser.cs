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
        var parser = new TSqlParser(new TSqlLexer(sql).Tokenize());
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
        var parser = new TSqlParser(new TSqlLexer(sql).Tokenize());
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
        return Assigning(query)
            ?? (query.Body is SelectBody { Into: { } target } body
                ? new SelectIntoStatement(target, query with { Body = body with { Into = null } })
                : new SelectStatement(query));
    }

    /// `SELECT @a = x` assigns rather than returns, and there is nothing it could be mistaken for:
    /// a comparison against a variable is projected by parenthesising it. All of the list or none of
    /// it, which is where SQL Server draws the line too -- a statement that both assigned and
    /// returned would be neither.
    static Statement? Assigning(Query query)
    {
        if (query.Body is not SelectBody body) return null;

        var targets = body.Items
            .Select(item => item.Expr is BinaryExpr { Operator: "=", Left: VariableRef { System: false } target }
                ? target.Name : null)
            .ToList();

        if (targets.All(target => target is null)) return null;
        if (targets.Any(target => target is null))
            throw new TSqlException("a SELECT either assigns to variables or returns rows, not both", 0);

        return new AssignStatement([.. targets.Select(target => target!)], query with
        {
            Body = body with
            {
                Items = [.. body.Items.Select(item => new SelectItem(((BinaryExpr)item.Expr).Right, null))],
            },
        });
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

        // T-SQL puts OUTPUT between the column list and the rows; it belongs to the statement
        // rather than to either.
        var output = Output();

        if (Accept("values"))
        {
            var rows = new List<List<Expr>>();
            do rows.Add(ParenthesisedList()); while (AcceptOperator(","));
            return new InsertStatement(target, columns, new InsertValues(rows), output);
        }

        if (Accept("default"))
        {
            Expect("values");
            return new InsertStatement(target, columns, new InsertValues([[]]), output);
        }

        return new InsertStatement(target, columns, new InsertQuery(Query()), output);
    }

    /// `UPDATE [o] SET … FROM [t] AS [o] WHERE …` names its target the same way a DELETE can, and
    /// for the same reason: it is what EF Core's ExecuteUpdate writes.
    Statement Update()
    {
        Expect("update");
        var at = Peek.Position;
        var target = TableName();
        Expect("set");

        var assignments = Assignments();
        // T-SQL puts the OUTPUT clause between SET and WHERE, which is why the statement cannot end
        // at its assignments.
        var output = Output();
        var from = Accept("from") ? TableSource() : null;
        var where = Accept("where") ? Expression() : null;

        if (from is null || Bound(from, target) is not { } source)
            return new UpdateStatement(target, null, assignments, from, where, output);

        var (rest, filtered) = Selecting(from, source, where, at,
            [.. assignments.Select(a => a.Value), .. output.Select(o => o.Value)]);
        return new UpdateStatement(source.Name, source.Alias, assignments, rest, filtered, output);
    }

    /// A write whose FROM clause joins its target to something else: the join is what picks the rows,
    /// so the other tables become the write's own FROM and the conditions holding them together join
    /// the WHERE. Only an inner join says that -- an outer one keeps rows matching nothing, and those
    /// are rows the write would still touch, which a condition cannot say afterwards. What is left
    /// after the joins that decide nothing have been dropped, that is.
    (TableSource? From, Expr? Where) Selecting(TableSource from, NamedTableSource target, Expr? where, int at,
                                               List<Expr>? read = null)
    {
        from = Pruned(from, target, [.. read ?? [], .. where is null ? (Expr[])[] : [where]]);

        if (ReferenceEquals(from, target)) return (null, where);
        if (!Inner(from))
            throw new TSqlException("an outer join cannot pick the rows a write touches: it keeps the ones " +
                                    "matching nothing, which is not something a condition says afterwards", at);

        List<TableSource> sources = [];
        List<Expr> conditions = [];
        Flatten(from, sources, conditions);
        sources.RemoveAll(source => ReferenceEquals(source, target));

        foreach (var condition in conditions)
            where = where is null ? condition : new BinaryExpr("AND", new ParenExpr(where), new ParenExpr(condition));

        return (sources.Count == 0 ? null : sources.Aggregate((a, b) => new JoinSource(JoinKind.Cross, a, b, null)),
                where);
    }

    /// Whether every join in the tree keeps only what matches, which is what makes its conditions
    /// ordinary predicates.
    static bool Inner(TableSource source) => source is not JoinSource join ||
        (join.Kind is JoinKind.Inner or JoinKind.Cross && Inner(join.Left) && Inner(join.Right));

    /// The tables a join tree stands on, and the conditions holding them together.
    static void Flatten(TableSource source, List<TableSource> sources, List<Expr> conditions)
    {
        if (source is not JoinSource join) { sources.Add(source); return; }

        Flatten(join.Left, sources, conditions);
        Flatten(join.Right, sources, conditions);
        if (join.On is not null) conditions.Add(join.On);
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

        var at = Peek.Position;
        var output = Output();
        if (output.Count == 0) return statement;

        return statement is InsertStatement insert
            ? insert with { Output = output }
            : throw new TSqlException("OUTPUT is supported on the insert a MERGE stands for, not the update", at);
    }

    /// `OUTPUT INSERTED.[id], i._Position` -- what the write made of each row, and which of the
    /// rows sent it was. Both qualifiers name a column of the row being written, so which one it was
    /// is settled here; `DELETED` names the row that was there before, and an insert replaces none.
    List<OutputItem> Output()
    {
        var items = new List<OutputItem>();
        if (!Accept("output")) return items;

        do
        {
            if (Peek.Is("deleted"))
                throw new TSqlException("OUTPUT DELETED names the row as it was before, which is not " +
                                        "kept: an insert replaces none, and a write keeps what it wrote",
                                        Peek.Position);

            // A constant -- `OUTPUT 1`, which is how EF Core counts the rows a statement touched.
            if (Peek.Kind is not (TokenKind.Word or TokenKind.QuotedName))
            {
                items.Add(new OutputItem(Primary(), Accept("as") ? Identifier() : null));
                continue;
            }

            var column = Identifier();
            while (AcceptOperator(".")) column = Identifier();

            // Only the spelled-out alias: the word after an OUTPUT item is as likely to be the
            // `VALUES` the rows follow, and taking that for a name loses the rest of the statement.
            items.Add(new OutputItem(new ColumnRef([column]), Accept("as") ? Identifier() : null));
        } while (AcceptOperator(","));

        if (Peek.Is("into"))
            throw new TSqlException("OUTPUT ... INTO puts the rows somewhere else, which needs a " +
                                    "table variable this does not have", Peek.Position);

        return items;
    }

    Statement MergeUpdate(TableName target, Name? alias, TableSource source, Expr on)
    {
        if (!Peek.Is("update")) throw Unexpected("only MERGE ... WHEN MATCHED THEN UPDATE is supported");
        Expect("update");
        Expect("set");
        return new UpdateStatement(target, alias, Assignments(), source, on, []);
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
        return new InsertStatement(target, columns, new InsertQuery(new Query([], body, [], null, null)), []);
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

    /// `DELETE FROM [s] FROM [t] AS [s] WHERE …` -- the target names an alias that the FROM clause
    /// binds, which is what EF Core's ExecuteDelete writes. It is resolved here, where the clause
    /// declaring it is in hand; a name the FROM clause does not bind is an ordinary table.
    Statement Delete()
    {
        Expect("delete");
        Accept("from");
        var at = Peek.Position;
        var target = TableName();
        var from = Accept("from") ? TableSource() : null;
        var output = Output();
        var where = Accept("where") ? Expression() : null;

        if (from is null || Bound(from, target) is not { } source)
            return new DeleteStatement(target, null, from, where, output);

        var (rest, filtered) = Selecting(from, source, where, at, [.. output.Select(o => o.Value)]);
        return new DeleteStatement(source.Name, source.Alias, rest, filtered, output);
    }

    /// The source a write's target names, or null when the FROM clause holds no such thing -- and
    /// then the target is a table of its own and the FROM clause is something it reads, which is the
    /// plain `UPDATE t SET … FROM s WHERE …`.
    ///
    /// An alias the clause bound, which is what EF Core writes; or the table spelled out, which is
    /// what LLBLGen writes -- it names every table in full and puts the target itself inside the
    /// join tree. A name matching more than one source is ambiguous, and left alone rather than
    /// guessed at: SQL Server refuses it too.
    static NamedTableSource? Bound(TableSource from, TableName target)
    {
        List<TableSource> sources = [];
        Flatten(from, sources, []);
        var named = sources.OfType<NamedTableSource>().ToList();

        if (target.Parts is [var alias] &&
            named.Where(source => source.Alias is { } bound && Same(bound, alias)).ToList() is [var only])
            return only;

        return named.Where(source => source.Alias is null && Same(source.Name.Table, target.Table)).ToList()
            is [var single] ? single : null;
    }

    static bool Same(Name a, Name b) => string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);

    /// An outer join whose nullable side nothing else names does not pick which rows a write touches:
    /// every row of the preserved side comes through it, matched or not, so taking it away leaves the
    /// same rows behind -- and what is left is an ordinary predicate the write can fold in. This is
    /// what an ORM rendering an entity's whole relation graph produces: LLBLGen writes out every
    /// relation the entity has, whether or not the statement reads any of it.
    ///
    /// Conservative on every axis. Only a single named table is dropped, never a join tree; never the
    /// target, since the rows it matched are exactly the ones the write meant; never a FULL join,
    /// which preserves both its sides. And anything that might name it and cannot be read -- an
    /// unqualified column, a subquery that may be correlated -- counts as naming it.
    static TableSource Pruned(TableSource from, NamedTableSource target, List<Expr> read)
    {
        while (Prune(from, target, read) is { } smaller) from = smaller;
        return from;
    }

    static TableSource? Prune(TableSource from, NamedTableSource target, List<Expr> read)
    {
        foreach (var join in Joins(from))
        {
            var nullable = join.Kind switch
            {
                JoinKind.Left => join.Right,
                JoinKind.Right => join.Left,
                _ => null,
            };

            if (nullable is not NamedTableSource dropped || ReferenceEquals(dropped, target)) continue;

            // What stays is the tree without that join, and what could still name the table is the
            // rest of the statement plus the conditions of the joins that stay -- its own goes with
            // it.
            var kept = Replace(from, join, ReferenceEquals(nullable, join.Right) ? join.Left : join.Right);
            List<TableSource> _ = [];
            List<Expr> conditions = [];
            Flatten(kept, _, conditions);

            if (!read.Concat(conditions).Any(expr => Names(expr, dropped))) return kept;
        }
        return null;
    }

    static IEnumerable<JoinSource> Joins(TableSource source) => source is not JoinSource join
        ? []
        : [join, .. Joins(join.Left), .. Joins(join.Right)];

    /// The tree with one join replaced by what is left of it, rebuilt down the path that reaches it.
    /// The leaves are the same objects either way, which is what lets the target keep its identity.
    static TableSource Replace(TableSource source, JoinSource join, TableSource with) =>
        ReferenceEquals(source, join) ? with
        : source is JoinSource other
            ? other with { Left = Replace(other.Left, join, with), Right = Replace(other.Right, join, with) }
            : source;

    /// Whether an expression may be reading a source's columns. A qualified reference says outright
    /// which one it means; an unqualified one and a subquery say nothing that can be read here, so
    /// both count as reading everything.
    static bool Names(Expr expr, NamedTableSource source) => Walk(expr).Any(part => part switch
    {
        ColumnRef column => column.Parts.Count < 2 || Same(column.Parts[^2], source.Alias ?? source.Name.Table),
        StarRef star => star.Qualifier.Count == 0 || Same(star.Qualifier[^1], source.Alias ?? source.Name.Table),
        ExistsExpr or SubqueryExpr => true,
        _ => false,
    });

    static IEnumerable<Expr> Walk(Expr expr) => [expr, .. Children(expr).SelectMany(Walk)];

    /// What one expression is made of. A subquery's own body is not walked into, because `Names`
    /// already answers for anything holding one.
    static IEnumerable<Expr> Children(Expr expr) => expr switch
    {
        ParenExpr paren => [paren.Inner],
        UnaryExpr unary => [unary.Operand],
        BinaryExpr binary => [binary.Left, binary.Right],
        CastExpr cast => [cast.Value],
        ConvertExpr convert => convert.Style is null ? [convert.Value] : [convert.Value, convert.Style],
        IsNullExpr isNull => [isNull.Value],
        BetweenExpr between => [between.Value, between.Low, between.High],
        LikeExpr like => like.Escape is null ? [like.Value, like.Pattern] : [like.Value, like.Pattern, like.Escape],
        InExpr inExpr => [inExpr.Value, .. inExpr.Items],
        FunctionCall call => [.. call.Arguments, .. call.Over is null ? (Expr[])[]
            : [.. call.Over.PartitionBy, .. call.Over.OrderBy.Select(o => o.Expr)]],
        CaseExpr branch =>
        [
            .. branch.Operand is null ? (Expr[])[] : [branch.Operand],
            .. branch.Branches.SelectMany(b => (Expr[])[b.When, b.Then]),
            .. branch.Else is null ? (Expr[])[] : [branch.Else],
        ],
        _ => [],
    };

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
            // `INNER LOOP JOIN`, `LEFT MERGE JOIN`: a hint telling SQL Server's optimiser which
            // algorithm to use. It says nothing about which rows come back, and DuckDB picks its own
            // way regardless, so it is read and dropped.
            foreach (var hint in JoinHints) if (Accept(hint)) break;
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

    static readonly string[] JoinHints = ["loop", "hash", "merge", "remote"];

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
