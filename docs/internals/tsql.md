# Translating T-SQL, and rewriting the writes

The dialect as a client meets it is [tsql.md](../tsql.md); this is how the parser, the renderer and the gateway are built, and which of an ORM's shapes each rule exists for.

## The tree, never the text

- **The dialect is translated on the tree, never on the text.** `TSql/` parses T-SQL into an AST and
  renders DuckDB SQL from it. A regex "fix" for a dialect difference belongs in the renderer as a
  case, not in a string replacement — this is why `'a' + b` concatenates and `1 + 2` adds.
- **A statement the parser does not cover is refused**, with the position. Passing unknown text
  through to DuckDB moves the failure somewhere harder to read.
- **A join's right operand is a join tree, not a table.** `a LEFT JOIN b JOIN c ON … ON …` nests, and
  the conditions close in reverse; parsing it left-deep leaves the last `ON` with nothing to attach
  to, which is what made a dacpac's view unpublishable. A join keyword arriving before this join's
  `ON` belongs to the operand, an `ON` ends it -- which is what keeps an ordinary chain a chain.
- **`TOP n PERCENT` is counted, not handed to DuckDB's `LIMIT n%`.** SQL Server rounds the share up
  and always answers at least one row; `LIMIT n%` rounds down and answers none for a small enough
  share. `TSqlWriter.Percent` limits by `CEIL(count(*) * n / 100)` over the body it just rendered --
  reusing the text rather than rendering it twice, so a CTE the body reads is still in scope.
- **A `bit` is converted for arithmetic, and only where the column resolves to one.** T-SQL makes a
  `bit` an integer to multiply it; DuckDB refuses `BOOLEAN * INTEGER` outright. The cast is written
  by `TSqlWriter.Operand`, which asks `TypeOf` what the column actually is -- `TSqlContext.Tables`
  is the catalog's types, and `Scope` binds each FROM clause before the items that read it, since
  the items are written first. Guessing instead is not available: `CASE WHEN 5` is *true* to DuckDB,
  so a coercion applied to a number would answer 1 rather than fail, and casting a DECIMAL that was
  taken for a `bit` would truncate it. A reference into a derived table resolves to nothing and is
  left alone -- DuckDB's error is better than a cast nobody can justify.
- **`COUNT` is an `int` in SQL Server and a BIGINT in DuckDB**, and an application casting the
  scalar to `int` throws on the difference. `TSqlWriter.Function` casts a `COUNT` back, around the
  window clause as well, and renders `COUNT_BIG` as the plain count -- which is how a caller asks
  for the wide one on the database this stands in for. `SUM` is not narrowed the same way: what
  SQL Server returns depends on the argument's type, which is not on the tree. It does arrive as a
  number rather than as text, which is a different bug -- see [the reader's type
  names](wire.md#types-on-the-wire).

## Statements a client sends

- **`OPENJSON` is a derived table, not a function call.** EF Core passes a collection as one JSON
  parameter and unpacks it with `OPENJSON(@p) WITH ([value] int '$')`, so the columns the WITH
  clause declares are projected in the subquery `TSqlWriter.OpenJson` renders. Resolving them where
  they are *used* instead would mean rewriting column references against an alias, which is the
  thing the tree is meant to avoid.
- **A batch is translated a statement at a time, and that is what makes any of it work.** Every
  session value a statement mentions -- `@@ROWCOUNT`, `@@TRANCOUNT`, `SCOPE_IDENTITY()` -- is written
  into it as a literal, so rendering the whole batch up front renders the second statement against
  the state the first has not yet changed. `INSERT …; SELECT @id = SCOPE_IDENTITY()` is exactly that
  shape, and it is what a client sends. `TSqlParser.Parse` still reads the batch once; only the
  rendering moved into the loop, which is why `TSqlTranslator.Translate` now takes a statement.
- **`SELECT @a = x` assigns and `SELECT a = x` aliases, and the only difference is that one names a
  variable.** `TSqlParser.Assigning` reads the whole select list or none of it -- SQL Server refuses
  the mixture and so does this -- and hands back an `AssignStatement` whose `Query` is the same query
  with the assignments taken off its items, so what produces the values is a query like any other.
  What it is not is a comparison: projecting one means parenthesising it. The rows never reach the
  client; `TdsSession` puts the last row into `assigned` and `Outputs` sends them back as RETURNVALUE
  tokens for the parameters the RPC marked BY_REF_VALUE. The DONE token carries no count, because
  SQL Server does not report one for an assignment select -- `INSERT …; SELECT @id = …` through
  `ExecuteNonQuery` has to answer 1 rather than 2.
- **DuckDB has no savepoints, and half a transaction cannot be made out of what it does have.**
  `SAVE TRANSACTION` renders to nothing -- marking a point costs nothing -- but
  `ROLLBACK TRANSACTION <name>` throws instead of rendering a plain `ROLLBACK` or nothing at all:
  one discards more than was asked, the other keeps writes that were asked to go, and EF Core marks
  a savepoint on every `SaveChangesAsync` inside a caller's transaction. The refusal is the feature.
- **A `#name` is a temporary table, and nothing else is one.** DuckDB's belong to a connection, which
  is what SQL Server means by a session, so `TSqlWriter.Table` renders them unqualified and never
  into the lake's schema. `SELECT … INTO` and `DROP TABLE` take nothing else -- a lake's tables are
  the files under it -- and `##name` is refused, because a global temporary table is one another
  connection can see. They also have to disappear when a pooled connection is handed out again.
- **An application lock is granted by doing nothing.** `EXEC sp_getapplock` asks to be serialized
  against the other connections of a shared database; a lake serves the application that owns its
  files, so the exclusion is already there and `TSqlWriter` renders the statement as nothing --
  which `Gateway.Translate` already answers as `Plan.Empty`. Making it a real lock would promise
  more than a lake can keep, since the files under it may be served by another process. `EXEC` of
  anything else is refused by name: the parser covers the call so an ORM reaching for a procedure
  is told which one is missing, not that `EXEC` is unparseable.
- **`SCOPE_IDENTITY()` is per connection and `IDENT_CURRENT` is per table, which is why they are kept
  in two different places.** `TdsSession.identity` is the session's and `Gateway.identities` is the
  process's; `@@IDENTITY` is `SCOPE_IDENTITY()` without the scope, and without triggers there is no
  scope to tell them apart, so one value backs both -- answered in `TSqlWriter.Variable` rather than
  through the `Variables` dictionary, or the same number would have two sources. All three render as
  `numeric(38,0)`, which is what SQL Server answers whatever the column was declared as.

## Finding what a write is against

- **A write's target can be an alias its own FROM clause binds.** `DELETE FROM [s] FROM [t] AS [s]`
  is what EF Core's `ExecuteDelete` writes, and `UPDATE [o] SET … FROM [t] AS [o]` is its
  `ExecuteUpdate`; both resolve through `TSqlParser.Aliased`, and taking `s` for a table name pushed
  the real one into `USING` and left the write against nothing. The alias then has to survive into
  the gateway, since the predicate names it too -- which is what `Gateway.DeleteAlias` keeps, and why
  the scan carries an `AS`.
- **A write's target is named as often as it is aliased, and an ORM names it.** EF Core writes
  `DELETE FROM [s] FROM [t] AS [s]`; LLBLGen spells every table out in full and puts the target
  *inside* its own join tree -- `DELETE FROM [db].[dbo].[t] FROM ((… INNER JOIN [db].[dbo].[t] ON …)
  LEFT JOIN …)`. So `TSqlParser.Bound` resolves either way: an alias the clause bound, or, failing
  that, the one unaliased source carrying that table's name. *One* -- a name matching two sources is
  ambiguous and left alone, which is what SQL Server does with it, and a target that resolves to
  nothing is the plain `UPDATE t SET … FROM s` where the FROM clause is something the target reads
  rather than something it sits in. The old guard was `target.Parts is not [var named]`, so a
  three-part target never even reached the alias lookup.
- **A join around that target folds into the write's own clauses.** `TSqlParser.Selecting` makes the
  other tables the write's `FROM` and their `ON` conditions part of its `WHERE`, which is the shape
  `Gateway.RewriteUpdate` already ran for a `MERGE`. Only an inner join folds: an outer one keeps the
  rows matching nothing, and those are rows the write would still touch, which a condition cannot say
  once the join is gone. `Gateway.RewriteDelete` had to learn the same clause and to qualify the keys
  it collects, since both tables may carry a column of that name. A join hint -- `INNER LOOP JOIN`,
  `HASH`, `MERGE`, `REMOTE` -- is read and dropped: it steers an optimiser this does not have, and
  says nothing about which rows come back.
- **An outer join that decides nothing is dropped, and that is not the same as allowing one.**
  An ORM renders the entity's whole relation graph whether the statement reads it or not, so the
  refusal above fires on joins that could not change a single row: every row of the preserved side
  comes through a LEFT JOIN, matched or not, so removing one nothing else names leaves exactly the
  rows behind that were there. `TSqlParser.Pruned` takes those away until none is left and `Inner`
  then decides the rest, which is why the refusal still stands for the join somebody reads.
  Conservative on every axis, because each axis is a way to delete the wrong rows: only a single
  named table, never a join tree; never the target, since `[a] LEFT JOIN [target]` matched exactly
  the rows the write meant and dropping `a` would widen it to all of them; never a FULL join, which
  preserves both sides. And `Names` counts an unqualified column and a subquery as reading
  everything -- one could be anyone's and the other may be correlated, and neither says anything
  that can be read off the tree. What makes the difference visible is a row pointing at a parent that
  is not there: it is in the result of the LEFT JOIN and not of an INNER one, so a rewrite that
  confused the two would leave it behind and report a smaller count. That row is in
  `TdsTests.Related` for exactly that reason.

## Rewriting a write

- **A materialized table needs none of this, and saying so is worth 7 ms a write.** The plan exists
  to stand a write branch over the layers below: rows are collected, tombstoned where a key moved,
  evicted from the branch and re-inserted. A materialized table *is* the whole of what the lake
  publishes -- which the same method already relies on when it turns tombstoning off -- so there is
  nothing below for an old row to show through and nothing for a written row to shadow. What is left
  is DuckDB's own UPDATE, keyed by the table's own index rather than by a temp table standing in for
  one. `Gateway.RewriteUpdate` sends the statement as it arrived, with the target renamed to the
  physical table, when the target is materialized and the statement has no `FROM`, no row limit and
  assigns no key column -- the three things the plan is still for. `RewriteDelete` does the same,
  with a cascade counting against it too, since a cascade reads the keys back one table down.
  Measured on a 414-table lake, one row by key: 8.0 ms as four statements, against 1.05 ms for
  DuckDB doing the same update directly on that lake spilled to a store. As one statement, on a
  one-table lake, it is 1.2 ms -- beside 0.85 for a `SELECT` by key on the same lake, which is the
  floor a round trip cannot go below. **~95% of the difference was statement preparation** -- a CPU sampling profile put two thirds in `PrepareMultiple` alone -- so
  what four statements cost is mostly that there are four of them: the cost was flat in table size
  (5.8 ms over 164k rows, 10.2 over 490) and most of it survived on a one-table lake with no dacpac.
  An INSERT was already one statement, and what it paid past the write was `Gateway.Duplicates`
  asking a question the table's own `PRIMARY KEY` answers -- 3.23 ms against 1.48 once it stopped
  being asked. That is [a key's to explain](schema.md#keys), not a plan's.
- **`MERGE ... WHEN MATCHED THEN UPDATE` is an update joined to its source**, and the parser
  desugars it to exactly that -- target, alias, `USING` as `From`, `ON` as `Where`. That is why
  `UpdateStatement` carries an alias at all, and why `Gateway.RewriteUpdate` had to learn the
  `FROM` clause it used to fold into the assignment list: with another table in scope, the columns
  the statement did not assign have to say which side they came from, in the projection and in
  `Keys` alike. The branches that add or remove rows are refused by name -- what a row's existence
  means is the layer machinery's, not one statement's.
- **`MERGE` is whichever statement its branch means.** `WHEN MATCHED THEN UPDATE` is an update
  joined to its source. `WHEN NOT MATCHED THEN INSERT` over a condition that cannot match --
  `TSqlParser.Never`, which is EF Core's `ON 1=0` -- is a multi-row insert, and that is how a batch
  of rows arrives. A condition that *can* match is refused: what "already there" means when the row
  it would shadow is in a layer below is the layer machinery's, not one statement's. `OUTPUT` is
  answered by writing the rows down first: `Gateway.RewriteReturning` materializes them into
  `duckpg_written`, inserts from that, and reads the answer off the same copy -- which is why the
  plan returns rows *and* writes, and why both sessions run every step but the last as a write.
  DuckDB's `RETURNING` cannot do it alone: it sees the target's columns and not the source's, so
  `i._Position` -- the row EF is asking about -- is not something it can hand back.
- **The OUTPUT clause sits between SET and WHERE**, so an UPDATE carrying one does not end at its
  assignments -- read that way, `OUTPUT` looks like the start of a statement nothing covers. What it
  asks for is answered off what the plan already built: `duckpg_updated` holds every row as the
  update left it, `duckpg_keys` holds what a delete collected before the rows went. A DELETE can
  therefore only answer for its key, and says so; `OUTPUT 1`, which is EF Core counting the rows it
  touched, needs neither.
- **A row limit on a write is performed on the keys, not on the rows.** `DELETE TOP (n)` is how a
  legacy ORM takes a long delete in bites, and neither dialect below has a place for it: SQL Server
  writes it before the target and DuckDB has no `DELETE … LIMIT` at all. So `TSqlWriter.Limit` puts
  it on the end as a `LIMIT` nobody else could have written, and `Gateway.Limited` takes it back off
  -- the limit then goes on `Keyed`, since what one row of a lake's table is is its key, and every
  step and every check reads back the key set that choice produced. `Keyed` orders by the key to
  make it, though SQL Server says outright that the set is unordered and any n rows would answer:
  the query is evaluated more than once -- the plan writes it down, and `Plan.Checks` re-asks it
  because a check runs before the first step -- and an arbitrary n taken twice is two different
  sets, which would leave the reference and duplicate checks answering for rows that stayed.
  An UPDATE needs one thing more, `Gateway.Within`, since its rows are projected by a query of their
  own: the key set stands as a derived table there rather than being joined to, because it is built
  over the same scan under the same aliases and only a subquery keeps that copy from swallowing the
  comparison's other side. The parentheses are required, which is what tells `TOP` from a table of
  that name -- SQL Server requires them on a write and makes them optional on a SELECT. A target the
  lake does not publish is refused rather than passed on: a `#temp` has no declared key to limit.
- **A numbered parameter cannot be bound to a statement that skips one, so `SqlText.Rename` makes
  every `$1` a named `$p1`.** DuckDB counts only the parameters a statement mentions and still
  demands the number each was written with: `$3` on its own is "parameter number 3" in a statement
  that "only has 1", and no spelling reaches it -- binding "1" answers "values were not provided
  for 3", binding "3" answers that there is no 3. A rewritten write is several steps and none of
  them has to use every parameter (the step collecting keys reads the predicate, never the
  assignments), so that unreachable case is the ordinary one rather than a corner. A *name* is not
  counted: DuckDB ignores one it was handed and does not want, which is what lets every step take
  the same arguments — and is what the TDS door was always doing, `@p0` rendering as `$p0`.

## Seeing what was sent

- **The log said what a client asked for and nothing said what was run.** `Gateway.Translate` logs
  the incoming statement, so at any verbosity a four-statement plan read as a plain UPDATE -- which
  is worse than logging nothing, since it reads as proof that the gateway passed the statement
  through. `Gateway.Logged` now logs each check, each step and the affected-count query, and only
  where the plan is more than the statement itself, so an ordinary query still costs one line.
- **`EXPLAIN` is answered here rather than handed to DuckDB, for the same reason.** Explaining what
  the client sent explains a statement that is not the one that runs -- and against a layered lake it
  explains an UPDATE of a view, which DuckDB refuses outright. `Gateway.PlanExplain` translates the
  inner statement and explains *that*: one query, and it is DuckDB's own plan with its costs;
  anything else, and it is the statements themselves in order. It has to be, since every step but the
  first reads temp tables the one before it makes and none of them exist until it runs. A check
  counts as a query for that test even though it is not a step: it runs, and hiding it would leave
  the same gap between what is said and what happens that the log had. `ExplainStatement` carries it
  through the T-SQL door, which refused the word outright -- it is not T-SQL, and a caller debugging
  a lake through the SQL Server door had no way to ask at all.
- **What persists is what `Dirty` says, never how many steps a plan has.** Both sessions used to
  persist a rows-returning plan only when it had more than one step, on the reasoning that one step
  is a SELECT. A keyed write against a materialized table is one step *and* returns rows, so that
  reasoning quietly stopped being true and what it wrote would not have reached the files.

## Answering in .NET

- **A function is answered in .NET when its meaning is .NET's.** `CONVERT`'s styles are
  `DateTime.ToString` formats and `pwdencrypt` is SHA-512 over UTF-16 text -- written as DuckDB SQL,
  each would be an approximation of something this process can just do, and the hash would not match
  the one a real SQL Server wrote. `HostFunctions.Register` runs once at startup: a registration
  belongs to the database, not to the connection that made it, so every session's own connection
  finds them. The constraints are the price: DuckDB calls them from whatever thread runs the scan,
  so nothing there may hold session state (which is why `SUSER_SNAME()` stays a rendered literal),
  and none of them belongs in a published view -- that is a managed call per row, on every read.
  These bindings read a blob argument as a stream but cannot write a blob result, which is why
  `pwdencrypt` hands back hex and a macro turns it into the blob the caller is owed.
