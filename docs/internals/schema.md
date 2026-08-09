# The declared schema, and the rules that come out of it

What a dacpac buys a lake is [schema.md](../schema.md); this is how it is read and how each declaration is enforced over a stack of files.

## Defaults

- **A declared default is a value in the read layers and an expression in the write layer.**
  `Catalog.Evaluate` returns both: `ColumnDefault.Value`, answered once against DuckDB and kept for
  the life of the process, fills the gaps in a read layer's branch — a table scanned twice has to
  answer the same both times, and a row already in a file cannot say when it was written.
  `ColumnDefault.Expr` is what the write table declares, so a row being inserted is stamped as it is
  inserted. Filling the write branch in the view, or freezing the write table's default, each undoes
  half of that. A `--cache` copy carries no default at all: `Catalog.Over` applies it in the view
  reading the copy, so a stamp belongs to the process answering rather than to a file outliving it.
  Only a default the merge depends on -- on a key column, or under a filter or a virtual column --
  is written into the copy. `Catalog.Signature` keys on a default's declared expression and not on
  what it evaluated to, or every stamped table would rebuild on every restart.

## Keys

- **A declared key is a rule over the merged view, and only a materialized lake can hand any of it
  to DuckDB.** The write branch's `PRIMARY KEY` sees only the rows this process wrote, so an INSERT
  of a key a file below already holds is let through -- and the row then shadows that file's row,
  which is an UPDATE nobody asked for rather than the refusal a client expects. So for a layered lake
  `Gateway.Duplicates` is the whole of the rule. It asks both halves as one `Plan.Checks` query over
  the rows about to be written -- a key the table already publishes, and one the statement repeats --
  for the same reason a reference is asked there: a statement outside a transaction commits each step
  as it goes, and the RETURNING path writes the rows down before it can answer. It is asked only
  where the statement carries every key column, since a key the store generates cannot collide with
  one it generated before.
- **A materialized table is keyed by DuckDB as well, because it is a table and there is nothing
  below it.** `Catalog.Keyed` issues `ALTER TABLE ... ADD PRIMARY KEY` once the table is built --
  `Catalog.Holds` first, since a `--store` carrying the table from a previous run already has it and
  asking twice is an error rather than a no-op. Both halves of the rule are then held twice, which is
  the price of the index being worth having on its own: it is an ART, and it turns a lookup on the
  key from a scan into a lookup -- 4.2 ms a point query against 0.47 over 10M rows in no particular
  order, though only about a fifth where the rows arrive in key order and the zone maps had already
  done the work. The key itself rather than a unique index over the same columns: they build the same
  ART and differ only in whether a key column may be NULL, and a row whose key is not there has no
  identity -- which is what the write branch of a layered lake has said all along, its own
  `PRIMARY KEY` refusing exactly that. What it refuses at startup is a stack that publishes one key
  twice -- a single-layer table is published without the `QUALIFY` that dedupes, there being nothing
  to shadow -- or leaves the key empty. Both are the lake saying the layers are wrong rather than
  serving a row nobody can name. It also outlives `Config.CheckKeys`: turning the rule off drops the
  scan of the merge, and materialized the key still refuses the row, with DuckDB's own message
  instead of `23505`.
- **Uniqueness declared past the key is a rule too, and DuckDB holds that one alone.** A `UNIQUE`
  constraint and a unique index say the same thing about the rows, so `DacpacSchema.ReadUnique` reads
  both into one list: the two differ only in which relationship names the table -- `DefiningTable`
  for a constraint, `IndexedObject` for an index, which carries the table in its own name besides --
  and in that an index has to say it is unique with a property DacFx *omits* rather than writes false,
  so an absent one is a plain index and reading it the other way would turn every index in a real
  dacpac into a constraint. `Catalog.Uniques` then creates one unique index per rule on a
  materialized table. Unlike the key this is an index and not a constraint, because that is what it
  is: the columns may be NULL, and DuckDB counts two NULLs as different where SQL Server counts them
  as one, so a lake refuses a shade less here than SQL Server would. It is skipped where the table
  does not publish every column the rule is over -- a lake showing a subset of a declared table loses
  the rule rather than failing on it, the same bargain `KeyFor` strikes -- and where the rule is the
  key again under another name, or where a column it is over is one no read layer carries. That last
  is `Catalog.Carried`, and it exists because a declared default is *frozen at build*: a column no
  file produces is the same value in every row the read layers produce, so `(newid())` is one id for
  the whole run, and a rule over it would refuse the entire lake at startup for data it was never
  given. A lake with no read layers has nothing frozen -- every row arrives as a write, stamped as it
  is written -- so it keeps its rules. The whole rule goes rather than the column, since uniqueness
  over two columns is not uniqueness over whichever one the layers happen to carry. What this does
  *not* cover is a column some layer carries and another does not: those gaps are filled by the same
  frozen value, and two of them collide. That is the layers disagreeing about a column that is
  genuinely in the lake, and it stays a startup failure. A partition column joins it for the reason
  it joins the key: rows are
  only unique *within* a partition, and a rule that forgot that would refuse a lake for holding the
  row it was partitioned to hold. **A layered lake keeps none of this**: it publishes views, and
  `Gateway.Duplicates` asks about the key alone. Asking about a declared unique too would be another
  scan of the merge per rule per write, and the key's scan is already most of what a write costs.
  That divergence is a decision and not a gap left open -- see below.
- **The foreign keys DuckDB offers are not the ones a lake needs, materialized or not.**
  `ALTER TABLE ... ADD FOREIGN KEY` is unimplemented, so a constraint has to be declared in
  `CREATE TABLE` -- which costs `Materialize` its CTAS and demands the tables be built in dependency
  order and torn down in reverse, since `CREATE OR REPLACE TABLE` on a parent is refused while a
  child points at it. `ON DELETE CASCADE` is refused by the parser outright. Two of them
  are fatal rather than merely expensive: a self-referencing table cannot be loaded at all, because
  the constraint is checked per row against committed state and a hierarchy's parent is in the same
  statement as its child; and `Gateway.RewriteUpdate` evicts a row before re-inserting it, which a
  child pointing at it refuses outside a transaction -- which is where plans run.
- **An UPDATE writes rows too, and the only two ways it can write two under one key are worth
  exactly one query.** A moved key may land on one the lake already publishes; a join around the
  target may match a row twice and write it twice. Anything else reads the merged view, which is
  already keyed, so `Gateway.RewriteUpdate` asks only when `moves || from >= 0` and an ordinary
  update pays nothing. What makes it the same question `Duplicates` answers for an insert is
  `replaced`: the keys the statement is taking away as it writes, which an insert does not have. A
  row landing on one of those is landing on nothing -- without it `SET id = id + 1` over a whole
  table would read as every row colliding with itself, and the row that merely stayed where it was
  would read as colliding with the row it *is*. `Moved` is the key half of the projection the plan
  builds anyway, which is what keeps the check and the write agreeing about where a row lands.
- **What a key check costs is one scan of what the table publishes, and that is the floor.** There is
  no index over a view, so asking whether a layered lake already holds a key means evaluating the
  merge. Measured on a two-layer 25k-row lake: 6.95 ms for the insert form against 6.45 for a bare
  `count(*)` over the same view, and 2.19 against 0.88 materialized. That is most of what a write
  costs -- an insert with no check was 1.4 ms layered and 1.1 materialized -- so this is the one
  place where `--materialize` is worth 3x for the same reason everything else here is. Getting to
  the floor was worth having: a correlated EXISTS cost 7.62 rather than 6.95, and the update form
  15.89 rather than 10.87, because it scanned the merge once for the rows, once for the keys being
  replaced and once again per key. What it does *not* scale with is rows written -- one check a
  statement, so a thousand-row batch pays it once -- and it is not asked at all of a keyless table,
  a key the store generates, an ordinary UPDATE, a DELETE or any read. `Config.CheckKeys` is the
  opt-out for a lake whose writers are known to send fresh keys, and it is one condition in
  `Gateway.Duplicates` rather than a second path. What it gives back is the scan and not the rule:
  materialized, the index still holds the key, so only a layered lake is left with nothing.
- **A materialized table is not asked twice about a key it already holds.** Its `PRIMARY KEY` sees
  everything the lake publishes, so `Gateway.Duplicates` runs no query for one and keeps only the
  words: `Plan.Violation` carries the message and `23505` the check would have refused in, and a
  session reports them in place of DuckDB's when a step is refused for a key. Measured, one insert:
  3.23 ms asking first against 1.48 leaving it to the table. **Only where the statement writes
  without first taking anything away** -- `replacing`, which is the update form. A plan that replaces
  evicts before it re-inserts and its steps are not one transaction, so a key refused at the insert
  is refused after the eviction has committed and the rows are simply gone. That is what a check
  running *before* a plan is for, and a constraint underneath it does not change it. A declared
  unique that is not the key keeps DuckDB's own words, since the gateway never had any for it and
  `Violation.Caused` matches only a primary key: dressing one as the other would name the wrong
  constraint.
- **The two modes enforce differently because they are asked different things, and closing that is
  not the improvement it looks like.** Layered is how a lake is read: many layers, few or no writers,
  questions that scan. Materialized is how one is written against -- a test suite standing a lake up,
  writing to it, and expecting a store's answers -- so it is the mode where a refusal has anything to
  refuse. That is why every rule DuckDB can hold is handed to it there and only the key is paid for
  over the merge here, why a stack breaking a declared rule is a startup failure rather than a
  warning, and why the layered side is not made to answer for a declared unique. A scan per rule per
  write would tax the mode that has no writes to tax, to enforce a rule against nobody.

## References and cascades

- **A declared reference is a rule over the merged view, not a constraint on a table.** DuckDB
  enforces foreign keys but refuses to point one at a view, and a lake publishes views -- so a
  constraint on the write table would only see the rows this process wrote, while the row pointing
  at a parent may live in any layer. `Gateway.Referenced` builds the question as a query instead,
  and `Plan.Checks` is what a session runs *before* the plan's steps: a statement outside a
  transaction commits each step as it goes, so a rule enforced after the tombstone would be enforced
  on a row already gone. The keys are selected twice for that -- once by the check, once by the plan
  -- and only for a table something points at. The insert side is not checked at all: it would be
  more promise than a stack of files can keep, since what a read layer holds can change between runs.
- **A cascade is that same delete, one table down.** `Gateway.Cascading` walks the declared
  references from the table being deleted from, and each level collects its own keys into a
  `duckpg_cascade_n` temp table before hiding them -- read off the level above's temp table, since
  by then it is there. The checks are built from the *query* that produced those keys rather than
  from the table, because `Plan.Checks` runs before any step does; that is also why every level
  contributes its non-cascading references, and not only the table the statement named. A cascade
  writes to more than one table, which is what made `Plan.Dirty`, `Promoted` and `Tombstoned` lists
  and `Promoting` take a set: each table down the chain earns its write branch the same way.
  DacFx numbers the action -- `OnDeleteAction` `1` is CASCADE -- and leaves the property out
  altogether when it is NO ACTION, so reading it by any other name is indistinguishable from a
  schema where nothing cascades: no warning fires, because the fallback *is* the default. `Dacpac`
  in the suite has to write that same encoding, or every cascade test only checks its own spelling.
  `2` and `3` are SET NULL and SET DEFAULT, and `DacpacFormatTests.ReadsTheDeleteActionsDacFxEncodes`
  pins all three to the checked-in dacpac SqlPackage built. NO ACTION cannot be pinned at all, since
  an omitted property and one read by the wrong name look exactly alike.
  What a cascade cannot do is demoted at startup to the refusal a plain reference gets, in
  `Catalog.Acyclic` and `Unperformable` -- a child that is not writable or has no key, and a cycle,
  which SQL Server will not let you declare either. Orphaning the rows is the one answer that is
  wrong whichever way it is reached, so the honest fallback is the refusal rather than doing nothing.
- **What a reference *does* is decided once, at build, and written onto the reference.** There is one
  `Catalog.pointing` map from the table pointed at, each entry carrying the resolved action rather
  than the declared one -- `Referencing`, `Cascading` and `Clearing` are three readings of it. That
  is what makes a demotion a `reference with { OnDelete = NoAction }` instead of a move between
  parallel dictionaries, and it is why `Acyclic` rewrites in place: a cascade demoted to a refusal
  has to stop being reachable by `Reaches` at the same moment it starts being a check.
- **A clear is an UPDATE, and that is the whole of it.** `ON DELETE SET NULL` and `SET DEFAULT` leave
  the rows where they are with the pointing columns emptied, so `Gateway.Clearing` builds what
  `RewriteUpdate` builds -- the rows as the merged view has them, the pointing columns replaced,
  `Evict` and then an insert into the child's branch. No tombstone, because the key does not move and
  the rewritten row shadows what is beneath it on its own; no recursion and no checks, because the
  rows are still there afterwards and nothing below them is orphaned. `SET DEFAULT` is the same
  expression with `ColumnDefault.Expr` in place of NULL, and NULL again where the column declares no
  default -- which is what SQL Server does with it. The one thing a cascade may do and a clear may
  not is point with part of the child's own key: emptying a key column collapses every cleared row
  onto one key and uncovers the rows they were shadowing, so `Unperformable` demotes it. That case
  has no equivalent for a cascade, which is why the check is asked only of a clear.

## Generated keys

- **A key the store fills in comes from a declared identity and nowhere else.** A dacpac column
  marked `IsIdentity` gets a sequence in the write layer (`Catalog.Sequence`), seeded past the
  highest value the merged view holds when the table grows its write branch -- at build for a table
  that already carries rows, in the promotion otherwise, which is the only moment the count is both
  needed and cheap. A declared default is the other value a lake can answer for, and by the same
  move: `Gateway.RewriteReturning` stamps `ColumnDefault.Expr` into the rows being written and reads
  the answer off them, so a `getdate()` default cannot be one thing in the file and another in the
  caller's hand -- which evaluating it twice would make it. `Gateway.Generated` fills a key for any
  insert that leaves it out, so the plain
  statement and the answered one decide it the same way -- cast back to the declared type, since a
  sequence counts in BIGINT whatever the column is and the answer is read off the rows rather than
  off the table, which is how an `int` key reached SqlClient as a long. It is per process, so two serving the same
  write directory would collide; and an OUTPUT naming anything else a lake does not generate is
  refused, since the alternative is answering with a null the caller would store.
- **A generated key is remembered because the write handed it back, and for no other reason.** The
  sequence cannot be asked afterwards: DuckDB's `currval` is the *database's* last value and not the
  connection's -- measured, two connections read each other's -- so it answers a question about
  another session as readily as about this one. So the write says instead: `Gateway.Identifying` puts
  `RETURNING <key>` on the step that inserts, `Plan.IdentityStep` names which step that is, and
  `Identity.Read` reads the keys off it rather than counting rows -- `ExecuteNonQuery` reports nothing
  for a statement carrying `RETURNING`, which is what made a PostgreSQL-side insert report zero rows
  the first time this was built. The step is *derived* rather than stored, since `Promoting` prepends
  to `Steps` and an index would then point at the promotion. The last key of the several a batch
  writes is the answer, which is the row written last. Both doors read it, because
  `IDENT_CURRENT` is the process's and a lake serves two front doors; only the TDS one can ask.
  Nothing here survives a restart: which row was written last is not something a layer file records,
  so `SCOPE_IDENTITY()` before this process generated anything is a null rather than a zero.
- **What that costs is the driver's reader and not the `RETURNING`, which is free.** Measured against
  DuckDB directly, median over 500: 0.757 ms for the insert, 0.731 with `RETURNING` through
  `ExecuteNonQuery` -- the same within noise -- and 0.856 reading it back, so ~0.10 ms is
  `DuckDBDataReader` being materialized for one row of one column. End to end through the TDS door on
  a materialized lake that is 1.3 → 1.5 ms on the cheapest write there is, and on a layered lake it
  cannot be measured at all: persisting the parquet dominates, at ~4 ms either way. It is paid only
  by an insert into a table with a declared identity the statement left out -- the same insert that
  skips the duplicate-key check, since a key the store generates cannot collide -- and nothing else
  moved: the same statement against a table with no identity measured 3.3 ms before and 3.3 after,
  which is what says the parameter plumbing costs nothing.

## Reading the dacpac

- **A declared scalar function is a macro, and a macro is an expression.** `DacpacSchema` reads
  `SqlScalarFunction`: `BodyScript` holds the `BEGIN … END` *alone* -- the `CREATE FUNCTION` header
  is nowhere in the model but an annotation -- so the parameters come from the `Parameters`
  relationship in document order, and the return type from the function's own direct `Type` child,
  never a descendant, since every parameter has one of those too. `Catalog.Macros` translates the
  body on the tree with `TSqlContext.Macro` set, which is what makes `@value` the macro's parameter
  rather than a bound `$value`, and casts the result to the declared return type -- `Small` in the
  suite is a SMALLINT over arithmetic DuckDB answers as INTEGER, which is the same narrowing COUNT
  needs. `TSqlWriter.Declared` resolves the call site, and only for a function that was published:
  an unpublished one keeps the name it was written with, which is the better error. DuckDB binds a
  macro when it is *created*, not when it is called, so one calling another must be made second --
  hence the pass that stops when it makes no progress. A body that is not one `RETURN` is refused
  and logged, because half-translating a procedure is worse than not having it.
- **The suite's dacpac writer is not the format.** `Dacpac.cs` writes what the readers expect, so
  the two can agree and both be wrong -- which is exactly what happened to `OnDeleteAction`.
  `tests/.../Schema/sample.dacpac` is DacFx's own output, checked in and *not* built by the test run,
  with the `.sqlproj` and `.sql` beside it saying how to remake it. Its elements are listed
  alphabetically, which is why `Amplified` is named to arrive before the `Doubled` it calls: a
  fixture that happened to be in dependency order would prove nothing about the ordering.
