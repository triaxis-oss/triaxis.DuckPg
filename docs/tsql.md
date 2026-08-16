# The T-SQL duckpg accepts

A client on the [TDS front door](protocols.md#the-tds-door) sends T-SQL; DuckDB does not speak
it. duckpg **parses** it — lexer, recursive-descent parser, and a renderer that emits DuckDB SQL from
the tree. Nothing in the dialect is rewritten by pattern matching on text, which is why `'a' + b` and
`1 + 2` can be told apart at all. The same translator reads a dacpac's views, scalar functions and
default expressions, so everything below is what those may contain too.

## Names

| Written | Becomes |
|---|---|
| `[bracketed]`, `"quoted"` names | quoted identifiers |
| `dbo.orders`, `app.dbo.orders`, bare `orders` | the lake's schema |
| `[dbo].[orders].[id]`, `[app].[dbo].[orders].[id]` | the same schema, so a qualified column still finds its table |

## Selecting rows

| Written | Becomes |
|---|---|
| `SELECT TOP 5`, `OFFSET … FETCH NEXT` | `LIMIT` / `OFFSET` |
| `SELECT TOP 50 PERCENT … ORDER BY …` | `LIMIT` the counted share, rounded up as SQL Server rounds it |
| `a LEFT JOIN b JOIN c ON … ON …` — a join nested in a join | the same tree, parenthesized |
| `INNER LOOP JOIN`, `HASH`, `MERGE`, `REMOTE` join hints | dropped |
| `WITH (NOLOCK)` and other table hints | dropped |

A hint is dropped rather than translated because it steers an optimiser this does not have, and says
nothing about which rows come back.

## Literals, types and operators

| Written | Becomes |
|---|---|
| `N'text'`, `0xDEAD` | `'text'`, `from_hex('DEAD')` |
| `CAST(x AS NVARCHAR(MAX))`, `INT`, `BIT`, `DATETIME2`, `UNIQUEIDENTIFIER`, `MONEY` | `VARCHAR`, `INTEGER`, `BOOLEAN`, `TIMESTAMP`, `UUID`, `DECIMAL(19,4)` |
| `CONVERT(INT, x)` | `CAST(x AS INTEGER)` |
| `[flag] * [n]` where `flag` is a `bit` | `CAST(flag AS INTEGER) * n`, as T-SQL converts it |

`+` becomes `||` only where one side is provably text — a string literal, a `CAST` to a character
type, or a function that returns one. Everywhere else it stays arithmetic, because guessing would
turn `1 + 2` into `'12'`.

A `bit` is converted for arithmetic only where the column resolves to one, since DuckDB refuses
`BOOLEAN * INTEGER` outright. A reference into a derived table resolves to nothing and is left
alone — DuckDB's error is better than a cast nobody can justify.

## Functions

| Written | Becomes |
|---|---|
| `ISNULL`, `LEN`, `IIF`, `CHARINDEX`, `NEWID`, `GETDATE`, `GETUTCDATE`, `CEILING` | their DuckDB equivalents, argument order and all |
| `DATEPART(day, d)`, `DATEDIFF`, `DATEADD` | `date_part('day', d)`, `date_diff`, interval arithmetic |
| `CONVERT(varchar, d, 120)` and the other styles | the date format the style names, applied in .NET |
| `pwdencrypt`, `pwdcompare` | SQL Server's own hash: version, salt, SHA-512 over UTF-16 |

The last two are answered in .NET rather than rewritten into DuckDB expressions, because their
meaning is .NET's: the styles are the date formats `DateTime.ToString` already knows, and the hash is
SHA-512 over UTF-16 text the way SQL Server writes it — so a hash your real database wrote verifies
here, and one written here verifies there. They are registered on the database at startup, so both
front doors find them, and they are not for a view, since these are a managed call per row.

A declared scalar function from a dacpac is published as a macro and resolves at its call site the
same way; see [the schema, from a dacpac](schema.md).

## Writes

| Written | Becomes |
|---|---|
| `MERGE t a USING s ON … WHEN MATCHED THEN UPDATE SET …` | `UPDATE t AS a SET … FROM s WHERE …` |
| `MERGE t USING (VALUES …) i (…) ON 1=0 WHEN NOT MATCHED THEN INSERT …` — EF Core's batch insert | one multi-row `INSERT` |
| `DELETE FROM [s] FROM [t] AS [s] WHERE …` — EF Core's `ExecuteDelete` | a delete against the table the alias binds |
| `UPDATE [o] SET … FROM [t] AS [o] WHERE …` — its `ExecuteUpdate` | the same, on the other write |
| either of those joined to another table by an inner join | the other tables become the write's own `FROM`, their conditions its `WHERE` |
| either of those joined by an outer join something reads | the tree is carried whole and the write is keyed on the rows it selected |
| `DELETE FROM [db].[dbo].[t] FROM ((… JOIN [db].[dbo].[t] ON …) LEFT JOIN …)` — LLBLGen | the target is found by name inside the tree; the joins nothing reads are dropped |
| `OUTPUT INSERTED.[id], i._Position` | the rows are written down first, then answered from |
| `UPDATE … OUTPUT 1 WHERE …`, `DELETE … OUTPUT 1` | one row per row the statement touched |
| `SCOPE_IDENTITY()`, `@@IDENTITY` | the last key this connection generated, as `numeric(38,0)` |
| `IDENT_CURRENT('t')` | the last key generated for that table, by any connection |
| `SELECT @id = SCOPE_IDENTITY()` | nothing goes back as rows; the value fills the OUTPUT parameter |

Any join shape picks the rows a write touches, outer joins included. A join tree around a write only
*selects* — it says which rows of the target are affected and nothing more — so a write is keyed on
what its tree selected. Only an inner join folds into the write's own `FROM` and `WHERE`, since that
shape is an inner join itself; anything else is carried whole and the rows are collected through it.
A target row the tree matches more than once is affected once, not once per match, and a target row
whose outer join matched nothing is still selected — which is what a predicate like
`WHERE [child].[col] IS NULL` means to say.

An outer join *nothing else reads* is dropped before either path, which keeps the everyday two-table
write the two-table write it always was. An ORM writes out the entity's whole relation graph whether
the statement reads it or not, and such a join cannot change which rows are written: every row of the
preserved side comes through it, matched or not, so taking it away leaves the same rows behind. Only
a single named table is dropped, never a join tree, never a FULL join, and never the write's own
target — `[a] LEFT JOIN [target]` matched exactly the rows the statement meant, and dropping `a`
would widen it to every row of the target. An unqualified column or a subquery in the predicate
counts as reading everything, so nothing is dropped on their account.

The write's target is resolved to an alias the FROM clause bound, or — when the statement spells the
table out and puts it inside the join tree, which is what LLBLGen does — to the one source of that
name. A name matching two sources is ambiguous and the statement is refused, as SQL Server refuses
it: nothing in it says which of the two the rows come from.

One gap: an `UPDATE` carrying such a tree needs the lake's declared key, because its assignments read
the tree and so cannot be moved aside the way a `DELETE`'s rows can. Against a table the lake
publishes that is always there; against a session's own `#temp` table it is not, and the statement is
refused by name. A `DELETE` has no such limit — a temporary table's own `rowid` stands in for a key.

`OUTPUT INSERTED.[key], i._Position` is answered by materializing the rows, writing from there and
reading back off the same copy, so each key comes back beside the position of the row that got it. A
column the rows do not carry and nothing generates is refused by name rather than answered with a
null — but a column with a declared default *is* generated, stamped into those same rows as they are
written, which is what keeps a `getdate()` default from being one thing in the file and another in
the caller's hand.

The `MERGE` branches that add or remove rows are refused: what "already there" means when the row a
statement would shadow lives in a layer below is the lake's question, not one statement's.

A client that cannot use `OUTPUT` reads its key back the older way, and gets the same answer:

```sql
INSERT INTO [orders] ([amount]) VALUES (@p1) ;SELECT  @id = SCOPE_IDENTITY()
```

sent as one batch with `@id` declared OUTPUT. The insert hands its generated key back as it writes,
so what `SCOPE_IDENTITY()` answers is the row that was actually stored rather than wherever the
sequence has since got to. It is the connection's own — another session never sees it, and it is
null until that connection has generated one. `@@IDENTITY` is the same value, since there are no
triggers here for a scope to tell apart, and `IDENT_CURRENT('t')` is the table's rather than the
session's. All three answer `numeric(38,0)`, whatever the column was declared as, and none of them
survives a restart: which row was written last is not something a layer file records.

`SELECT @a = x, @b = y` assigns and returns nothing, which is what makes the batch above work
through `ExecuteNonQuery`; the values go back as the call's return values, in the types the caller
declared, and a statement that both assigned and returned is refused the way SQL Server refuses it.
A query that found no rows leaves the parameters as they went in.

An ORM that qualifies everything it writes — LLBLGen Pro among them — is what all of this is for:
table references, column references and `TOP(@p)` paging over a row-numbered derived table all land
on the lake without the application knowing what it is talking to.

## Session state, transactions and locks

| Written | Becomes |
|---|---|
| `SUSER_SNAME()`, `SUSER_NAME()`, `USER_NAME()`, `ORIGINAL_LOGIN()` | the session's login name, as a literal |
| `@@VERSION`, `@@ROWCOUNT`, `@@TRANCOUNT`, `@@SPID` | the session's own values |
| `SET NOCOUNT ON`, isolation levels | no-ops |
| `SAVE TRANSACTION x` | nothing; `ROLLBACK TRANSACTION x` is refused rather than faked |
| `EXEC sp_getapplock @Resource = …`, `sp_releaseapplock` | granted |
| `EXEC sp_tablecollations_100 N'[t]'` — SqlBulkCopy's question | the destination's columns, every collation the one the login advertised; every other `EXEC` is refused by name |

A savepoint is refused rather than approximated. `SAVE TRANSACTION` renders to nothing, since marking
a point costs nothing, but DuckDB has no savepoint to return to — so `ROLLBACK TRANSACTION x` fails
loudly instead of quietly keeping the writes it was asked to discard. EF Core marks one whenever it
saves inside a transaction the caller opened.

An application lock is granted by doing nothing. `sp_getapplock` serializes a caller against the
other connections of a shared database, and a lake is not one — it serves the application that owns
its files, so the exclusion is already there. `EXEC` of anything else is refused by name, which is a
better answer for an ORM calling a stored procedure than a syntax error about `EXEC`.

## Temporary tables

`SELECT … INTO #t FROM …` becomes `CREATE TEMP TABLE #t AS …`, and `DROP TABLE [IF EXISTS] #t` the
plain drop. A `#table` belongs to a DuckDB connection exactly as SQL Server's belongs to a session,
including going away when a pooled connection is handed out again. `##global` ones are refused, since
another connection cannot see them here, and `SELECT … INTO` and `DROP TABLE` accept nothing else: a
lake's tables are the files under it.

## Asking what a statement does

`EXPLAIN <statement>` and `EXPLAIN ANALYZE <statement>` are DuckDB's spelling rather than SQL
Server's `SET SHOWPLAN_ALL ON`, and are accepted here because what duckpg sends is not always what it
was sent: a write against a layered lake becomes several statements. Where the answer is a single
query, EXPLAIN is DuckDB's own plan; where it is several, it lists them in order under `step` and
`statement`, along with any check that runs before them.

## What is refused

A style or a hash format that is not covered is named rather than approximated, and so is a statement
the parser does not cover at all: DDL, procedural batches, cursors, `DECLARE` and the `MERGE`
branches above are refused with a syntax error naming them and its position, rather than passed
through to fail somewhere less obvious. `LIKE` patterns use `%` and `_`; SQL Server's `[a-z]` ranges
have no DuckDB equivalent.
