namespace triaxis.DuckPg.TSql;

// The subset of T-SQL an application sends. Everything here is what a client actually puts on the
// wire: statements, queries, expressions. What it does not cover -- procedural batches, DDL,
// cursors -- is rejected by name rather than mistranslated.

abstract record Statement;

sealed record SelectStatement(Query Query) : Statement;

/// `EXPLAIN <statement>`, which is DuckDB's spelling and not T-SQL's -- SQL Server says
/// `SET SHOWPLAN_ALL ON`. It is here because a caller debugging a lake through the SQL Server door
/// otherwise has no way to ask what a statement will actually do, and what the gateway sends is not
/// what it was sent. The statement inside is parsed and rendered like any other; what the answer is
/// made of is the gateway's to decide.
sealed record ExplainStatement(Statement Inner, bool Analyze) : Statement;

/// `SELECT @a = x, @b = y` — T-SQL's assignment select, which returns no rows: the values go into
/// the variables the caller declared, and back to it as the call's return values. `Query` is the
/// same query with the assignments taken off its items, so what produces the values is one query
/// like any other; `Variables` names what each of its columns fills, in order.
sealed record AssignStatement(List<string> Variables, Query Query) : Statement;

/// `SELECT … INTO #t FROM …` — T-SQL's CTAS, and a statement rather than a query: what it returns
/// is a table. Only a temporary one, since a lake's tables are its files.
sealed record SelectIntoStatement(TableName Target, Query Query) : Statement;

sealed record DropTableStatement(TableName Target, bool IfExists) : Statement;

/// `Output` is the `OUTPUT INSERTED.…` clause: what the insert made of each row, which is how a
/// caller asks for a key it did not send. Empty for an insert that asks for nothing back.
sealed record InsertStatement(TableName Target, List<Name> Columns, InsertSource Source,
                              List<OutputItem> Output) : Statement;

/// `INSERT BULK t (col type, …) WITH (…)` -- not a statement an application writes: SqlBulkCopy
/// sends it to declare where the bulk load stream that follows lands. Only the names and their
/// order are kept, since the stream re-declares the types in its own metadata.
sealed record InsertBulkStatement(TableName Target, List<Name> Columns) : Statement;

/// One item of an OUTPUT clause: a column of the rows being written, or a constant -- EF Core sends
/// `OUTPUT 1` to count the rows a statement touched. Whether a column was qualified by `INSERTED` or
/// by the source's own alias is settled while parsing; either way it names one of those rows.
sealed record OutputItem(Expr Value, Name? Alias);

/// `MERGE ... WHEN MATCHED THEN UPDATE` desugars to this too, which is why the target carries an
/// alias: the assignments and the join condition both name it.
sealed record UpdateStatement(TableName Target, Name? Alias, List<Assignment> Assignments, TableSource? From,
                              Expr? Where, List<OutputItem> Output, RowLimit? Top = null) : Statement;

/// `Alias` is the target's own, which `DELETE FROM [s] FROM [t] AS [s]` names instead of the table.
sealed record DeleteStatement(TableName Target, Name? Alias, TableSource? From, Expr? Where,
                              List<OutputItem> Output, RowLimit? Top = null) : Statement;

/// `DELETE TOP (n) [PERCENT]` and its UPDATE twin: at most that many of the rows the predicate
/// matches, and which of them is left unsaid -- there is no ORDER BY in this form and SQL Server
/// says outright that the set is unordered. Kept as its own record rather than as a query's
/// `Top`/`TopPercent` pair, since a write's limit is answered somewhere else entirely: what a row
/// is, and so what "one of them" counts, is the lake's key rather than the statement's.
sealed record RowLimit(Expr Count, bool Percent);

/// `SET NOCOUNT ON`, `SET TRANSACTION ISOLATION LEVEL …` — session options a client sets and a
/// lake has no opinion about.
sealed record SetOptionStatement(string Option) : Statement;

/// `EXEC procedure [@name = value, …]`. A lake has no procedures of its own, so which ones are
/// answered is the writer's; this is only the shape a client calls one with.
sealed record ExecuteStatement(TableName Procedure, List<ExecuteArgument> Arguments) : Statement;

/// Named (`@Resource = 'x'`) or positional, as the call was written.
sealed record ExecuteArgument(Name? Name, Expr Value);

sealed record TransactionStatement(TransactionAction Action, string? Name) : Statement;

enum TransactionAction { Begin, Commit, Rollback, Save }

sealed record Assignment(Name Column, Expr Value);

abstract record InsertSource;

sealed record InsertValues(List<List<Expr>> Rows) : InsertSource;

sealed record InsertQuery(Query Query) : InsertSource;

// ---- queries -------------------------------------------------------------------------------------

/// A query with everything that wraps its body: common table expressions, ordering and paging.
sealed record Query(
    List<CommonTableExpression> With,
    QueryBody Body,
    List<OrderTerm> OrderBy,
    Expr? Offset,
    Expr? Fetch);

sealed record CommonTableExpression(Name Name, List<Name> Columns, Query Query);

sealed record OrderTerm(Expr Expr, bool Descending);

abstract record QueryBody;

sealed record SelectBody(
    bool Distinct,
    Expr? Top,
    bool TopPercent,
    List<SelectItem> Items,
    /// The `INTO #t` of a `SELECT … INTO`, lifted out of the body by the statement that owns it —
    /// a query nested anywhere else has nowhere to put a table.
    TableName? Into,
    TableSource? From,
    Expr? Where,
    List<Expr> GroupBy,
    Expr? Having) : QueryBody;

sealed record SetOperationBody(string Operator, bool All, QueryBody Left, QueryBody Right) : QueryBody;

sealed record ValuesBody(List<List<Expr>> Rows) : QueryBody;

sealed record SelectItem(Expr Expr, Name? Alias);

// ---- table sources -------------------------------------------------------------------------------

abstract record TableSource;

/// A name of one to four parts; only the last two matter to a lake.
sealed record TableName(List<Name> Parts)
{
    public Name Table => Parts[^1];

    public Name? Schema => Parts.Count >= 2 ? Parts[^2] : null;
}

sealed record NamedTableSource(TableName Name, Name? Alias) : TableSource;

sealed record DerivedTableSource(Query Query, Name? Alias, List<Name> Columns) : TableSource;

/// `read_parquet('…')` and friends: a function standing where a table does.
sealed record FunctionTableSource(FunctionCall Call, Name? Alias, List<Name> Columns) : TableSource;

sealed record JoinSource(JoinKind Kind, TableSource Left, TableSource Right, Expr? On) : TableSource;

/// `OPENJSON(json [, path]) WITH (col type '$.path', ...)`, which is how a collection reaches the
/// server as one parameter -- what EF Core sends for `WHERE x IN (list)`. Without the WITH it is
/// SQL Server's own key/value/type shape.
sealed record OpenJsonSource(Expr Json, Expr? Path, List<OpenJsonColumn> Schema, Name? Alias) : TableSource;

/// A column the WITH clause declares. `Path` is null when it was left out, which means `$.` and the
/// column's own name.
sealed record OpenJsonColumn(Name Name, TypeRef Type, string? Path);

enum JoinKind { Inner, Left, Right, Full, Cross }

// ---- expressions ---------------------------------------------------------------------------------

abstract record Expr;

sealed record Literal(LiteralKind Kind, string Text) : Expr;

enum LiteralKind { Number, String, Binary, Null, True, False }

/// An identifier as written, remembering whether it was quoted -- an unquoted name is matched
/// without case, a quoted one is not.
sealed record Name(string Text, bool Quoted);

sealed record ColumnRef(List<Name> Parts) : Expr;

sealed record StarRef(List<Name> Qualifier) : Expr;

sealed record VariableRef(string Name, bool System) : Expr;

sealed record FunctionCall(List<Name> Name, List<Expr> Arguments, bool Distinct, WindowSpec? Over) : Expr;

sealed record WindowSpec(List<Expr> PartitionBy, List<OrderTerm> OrderBy);

sealed record UnaryExpr(string Operator, Expr Operand) : Expr;

sealed record BinaryExpr(string Operator, Expr Left, Expr Right) : Expr;

sealed record CaseExpr(Expr? Operand, List<(Expr When, Expr Then)> Branches, Expr? Else) : Expr;

sealed record CastExpr(Expr Value, TypeRef Type) : Expr;

/// `CONVERT(type, value, style)` — the style is parsed so it can be rejected rather than ignored.
sealed record ConvertExpr(TypeRef Type, Expr Value, Expr? Style) : Expr;

sealed record InExpr(Expr Value, List<Expr> Items, Query? Subquery, bool Negated) : Expr;

sealed record BetweenExpr(Expr Value, Expr Low, Expr High, bool Negated) : Expr;

sealed record LikeExpr(Expr Value, Expr Pattern, Expr? Escape, bool Negated) : Expr;

sealed record IsNullExpr(Expr Value, bool Negated) : Expr;

sealed record ExistsExpr(Query Query) : Expr;

sealed record SubqueryExpr(Query Query) : Expr;

sealed record ParenExpr(Expr Inner) : Expr;

sealed record TypeRef(string Name, List<string> Arguments);
