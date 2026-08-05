namespace triaxis.Tools.DuckPg.TSql;

// The subset of T-SQL an application sends. Everything here is what a client actually puts on the
// wire: statements, queries, expressions. What it does not cover -- procedural batches, DDL,
// cursors -- is rejected by name rather than mistranslated.

abstract record Statement;

sealed record SelectStatement(Query Query) : Statement;

sealed record InsertStatement(TableName Target, List<Name> Columns, InsertSource Source) : Statement;

sealed record UpdateStatement(TableName Target, List<Assignment> Assignments, TableSource? From, Expr? Where) : Statement;

sealed record DeleteStatement(TableName Target, TableSource? From, Expr? Where) : Statement;

/// `SET NOCOUNT ON`, `SET TRANSACTION ISOLATION LEVEL …` — session options a client sets and a
/// lake has no opinion about.
sealed record SetOptionStatement(string Option) : Statement;

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
