# What things cost, and what was measured

Every number here was measured on this code. The user-facing summary is [performance.md](../performance.md); this is the working out, including the experiments that did not survive.

## The merge, and paying it once

- **The merge is what costs, and materializing it is the whole lever.** The same statement --
  60 columns, filtered to one row, 60000 rows either way -- against a plain table and against the
  three-layer merge view a layered lake publishes. Medians over 100 calls, warmed:

  | | table | merge view |
  |---|---|---|
  | prepare a handle, nothing else | 0.79 | 3.77 |
  | handle reused, no parameters | 0.88 | 1.84 |
  | handle reused, a value bound each call | 1.54 | 6.10 |
  | fresh handle: prepare + bind + execute | 1.94 | 7.53 |
  | ADO, new command, parameter bound | **2.25** | **8.25** |
  | ADO, new command, value as a literal | 1.76 | 6.24 |

  So `Config.Materialize` is worth 3.7x on the shape a small ORM query takes, and nothing else here
  comes close: planning falls from 3.77 ms to 0.79 because almost all of it *was* the merge. Where
  performance matters, that is the answer, and the rest of this bullet is about the ~2.25 ms left.
- **What is left after materializing depends entirely on how wide the statement is, and the
  ~20% below was measured on a narrow one.** On the 68-column statement a real client sends, a held
  `duckdb_prepare` handle is worth far more: measured against a 12-row table, ADO costs 1.77 ms a
  call and a reused handle 0.69 (2.6x); against the merge view, 6.86 against 1.63 (**4.2x**). The
  planning is per *column*, so the wider the statement the more a cached plan saves -- a statement of
  68 constants with no table in it at all still costs 0.87 ms, against 0.33 for `SELECT 1`. Reusing
  the `DuckDBCommand` object saves nothing (6.77 against 6.38): its source extracts and prepares on
  every execute. The driver looks like what blocks a cache and is not -- `DuckDBDataReader`'s only
  constructor is `internal`, and `[UnsafeAccessor(UnsafeAccessorKind.Constructor)]` reaches it
  without reflection and stays AOT-clean. What blocks it is DuckDB, below.
- **On the narrow shape below, the same cache is worth ~20%.** Of that 2.25 ms, roughly 0.8 is
  planning, 0.9 is execution and ~0.5 is the parameter. A plan cache saves 1.94 to 1.54, about
  0.4 ms; and rendering the value as a literal instead of binding it saves 2.25 to 1.76, about the
  same. They overlap. DuckDB re-plans a parameterized
  statement on every execute however it is prepared: 1.54 ms bound against 0.88 ms with no
  parameters on the same reused handle, and binding the *same* value every call wins nothing back.
  That is DuckDB, not the driver -- ADO and native agree to within a few percent on the shape they
  share. `DuckDBCommand.Prepare()` is not the way in either: it costs 0.000 ms, leaves
  `Parameters.Count` at zero and will "prepare" `SELECT 1 FROM nosuchtable`, never reaching
  `duckdb_prepare`. Correctness would be free -- a prepared statement picks up a `CREATE OR REPLACE
  VIEW` underneath it, and prepared statements belong to a connection, so any cache is a session's.
  The C API cannot be asked for a statement's result columns before execution, only its parameters;
  `DESCRIBE <query>` answers for those and costs what the `LIMIT 0` already used costs.
- **A view is bound on every execution, not once when the lake is built.** Everything above is why:
  every expression in a view definition is paid for by every query touching it. Hence no cast to the
  type a layer already has, no merge wrapper around a table only one layer carries, `--cache` writing
  a merged table out once as parquet -- and, further along the same line, `--materialize` not
  publishing a view at all.

## Planning, and why there is no plan cache

- **Planning can be moved out of the turn, and it buys less than it looks like.** The idea: render
  the statement with its values as literals, `duckdb_prepare` it *before* taking the turn, then hold
  the turn only for `BEGIN`/execute/`COMMIT`. Measured, median over 100, against a table:

  | | outside the turn | **holding the turn** |
  |---|---|---|
  | SELECT, prepared inside (today) | 0.00 | 2.01 |
  | SELECT, prepared outside, literal | 0.65 | **1.12** |
  | SELECT, prepared outside, value bound | 0.33 | 1.65 |
  | UPDATE, prepared inside (today) | 0.00 | 0.96 |
  | UPDATE, prepared outside, literal | 0.24 | **0.85** |
  | `BEGIN` + `COMMIT` with nothing between | | 0.33 |

  The mechanism works and the literal is essential -- bound, the planning follows the execute back
  inside (1.65 against 1.12). But what the turn serializes is *writes*, and a one-row UPDATE plans
  cheaply: 0.96 to 0.85, about 12%, of which 0.33 is the transaction bracket that cannot move. Reads
  outside a transaction never take the turn at all, so their 2.01 to 1.12 buys no contention back --
  only the ~12% of latency that rendering a literal was already worth. And splitting the work costs
  more of it in total (0.24 + 0.85 against 0.96): it trades throughput for a shorter critical
  section, which is the right trade only when something is actually queued behind it.
  It was built anyway, measured on a real workload, and was slower everywhere -- 8.65 ms a write
  against 8.49 without it. **It was built on SQL-level `PREPARE`/`EXECUTE`/`DEALLOCATE`, and that is
  why.** SQL `PREPARE` is a statement of its own: each `EXECUTE` is parsed and looked up in the
  catalog before the plan it names is reached, so the wrapper was three parsed statements where
  there had been one -- 1.19 ms against 0.82 for the same step run directly. It also has its own
  grammar, which takes SELECT, INSERT, UPDATE and DELETE and refuses DDL with "syntax error at or
  near CREATE" -- and since every rewritten UPDATE and DELETE is *built* out of
  `CREATE OR REPLACE TEMP TABLE`, that read as a fatal blocker.

  **Neither is true of `duckdb_prepare`.** The C API prepares `CREATE OR REPLACE TEMP TABLE … AS
  SELECT`, `CREATE TABLE IF NOT EXISTS` and `CREATE OR REPLACE VIEW` alike -- it must, since that is
  the only thing `DuckDBCommand` ever does with any statement. So both reasons this was abandoned
  were artifacts of the mechanism it was written on, not facts about DuckDB, and the idea is open
  again for anyone who wants to measure it properly.
- **A held plan is bound against *statistics*, and DuckDB invalidates one on a catalog change and
  never on a data change -- which is what makes a plan cache unsafe here.** A plan made while a
  write branch is empty has that branch optimized out of it, and every row written afterwards is
  invisible to that plan for as long as it is held: the merge answers `base` where it should answer
  what was just written, silently and forever. Measured on duckpg's own merge shape with as little as
  one row underneath, so it is not a large-table effect. `SET disabled_optimizers='statistics_propagation'`
  is what fixes it -- `empty_result_pullup` alone does not -- and that is too high a price and too
  narrow a guarantee, since it names the one optimizer known to bake data in today rather than any
  that might tomorrow. What *would* be sound is invalidating every session's plans whenever anything
  commits, which is a generation counter on the gateway rather than an optimizer flag; it costs one
  re-plan per session per write, so it is worth it for a read-heavy lake and worth nothing for a
  write-heavy one. Whatever does it must not clear on a connection reset: SqlClient announces one
  before the first statement of every pooled checkout, so an ORM that opens and closes around each
  statement resets constantly -- measured at seven plans for seven checkouts of one statement, which
  is a cache paying to do nothing. Until that exists there is no plan cache. The machinery -- `PlanCache`, reaching
  `DuckDBDataReader`'s internal constructor through `UnsafeAccessor` to stay AOT-clean, and the
  measurements showing 2.5x against a table and 2.8x against a merge view -- was built and taken back
  out; it is not in the tree. What it cannot do is be correct.

## Compressing what is held

- **A checkpoint is the only thing that compresses an in-memory DuckDB, and nothing drives one.**
  Compression is applied when a row group is written out at checkpoint; a file database is driven
  there by its WAL, and an in-memory one is never driven anywhere. So a materialized lake holds every
  column as it was built until asked otherwise -- `pragma_storage_info` says `Uncompressed` for every
  segment of a freshly built table, and `DICT_FSST`, `BitPacking` and `Constant` for the same table
  after `CHECKPOINT`. That is the whole of `Config.Compress`: one statement at the end of
  `Catalog.Build`. Measured over 5M rows of a five-column table, medians through the wire:

  | | as built | compressed |
  |---|---|---|
  | held in memory | 279.7 MB | **64.0 MB** |
  | build | 0.7 s | 1.1 s |
  | `count(*) WHERE note = 'order-42'` | 14.6 ms | **4.3 ms** |
  | `sum(amount) WHERE bucket = 3` | **3.5 ms** | 6.5 ms |
  | a row by its key | 0.63 ms | 0.60 ms |
  | a one-row `UPDATE` | 9.9 ms | 10.0 ms |

  Which is why it is off: a dictionary-encoded string is filtered by comparing against the dictionary
  rather than against every row, and a bit-packed number is unpacked a vector at a time on the way
  into an aggregate. The two move in opposite directions by about the same factor, so nothing but the
  memory is true for every lake.
- **Once, at the end of the build, rather than per table.** A checkpoint is the whole database's, so
  a second one buys nothing and the one there is also covers the `layer` tables a YAML or JSON layer
  was read into -- which is why this is not gated on `Materialize`, though that is the mode it exists
  for. It runs on the lake's own connection under `Rebuild` too, and it does not need the lake to be
  quiet: `CHECKPOINT` with another connection's transaction open neither blocks nor errors on 1.5.5,
  it simply leaves that transaction's blocks alone. Size is no bound either -- a three-row table
  comes back `Constant` and `DICT_FSST`, which is what lets `CompressTests` assert on a fixture
  rather than on a million rows.
- **What is written afterwards is not compressed, and nothing checkpoints again.** The segments an
  insert appends read `Uncompressed` for the life of the process. Checkpointing on a timer or after a
  write was not built: it would decide, on behalf of a lake nobody is watching, to spend CPU on rows
  that may be read once -- and the interesting case, a lake loaded once and then read, is exactly the
  one the build-time checkpoint already covers.

## Sorting a small table here rather than in DuckDB

- **A sort costs what a row is wide, not what a table is long, and that is what `SortSmallTables`
  takes back.** Measured: adding `ORDER BY` to a 68-column statement costs ~3.9 ms at twelve rows and
  ~3.6 ms at twelve hundred -- flat in rows, ~1.3 ms fixed plus ~50 µs a column -- because DuckDB's
  sort operator carries the whole payload. On the table an ORM keeps asking about, that is most of
  the query. `FastOrder.Of` takes the `ORDER BY` and the `TOP` off the *tree*, `SortedRows` applies
  them to what came back, and what DuckDB is asked for is the filtered scan.
- **What `SortedRows` holds is an `int[]` of positions and the sort keys, and nothing else.** A
  materialized DuckDB result is already columnar and already in memory, so the rows do not have to be
  taken out of it to be reordered -- `Chunk` reads a value straight out of the vector by position when
  the row is written, which is why the *width* of a table costs nothing: a hundred columns nobody
  sorts by are never touched until the rows that survived the limit go out. Holding them instead was
  measured at 27% of everything this allocated and turned `get the last few` -- rows arriving in the
  reverse of the asked-for order, so every one displaces the last -- into reading the whole table:
  1.6x at a thousand rows and a hundred columns against 3.1x now. Sort keys *are* held, in an array of
  their own type, because reading a value out of a vector costs ~30 ns whichever way it is asked for
  and a sort asks for each one about `log n` times; ~30 ns is the driver's decode, not the access, so
  a boxed read costs the same as a typed one and only the caching matters. Measured against DuckDB
  doing the whole statement:

  | | 1 column | 10 columns | 100 columns |
  |---|---|---|---|
  | `TOP 1`, 10 rows | 3.6x | 3.8x | 3.5x |
  | `TOP 1`, 1000 rows | 3.3x | 3.5x | 3.1x |
  | `TOP 1`, 2048 rows | 3.2x | 2.9x | 2.7x |
  | `ORDER BY` alone, 1000 rows | 1.6x | 1.7x | 1.6x |
  | `ORDER BY` alone, 2048 rows | 1.5x | 1.5x | 1.3x |

  What is left is rented: the positions and the sort keys come from `ArrayPool` and go back on
  dispose, which is why `IRows` is disposable at all. A pooled array is longer than it was asked for
  -- hence `count` rather than `order.Length`, and a span sorted over the part that holds the result
  -- and it is not cleared, which is safe only because every slot up to that length is written before
  it is read. Keys are returned cleared, since one may be a reference and the pool outlives the
  result. Profiled over the matrix, this path allocated ~410 MB of 1396 before and ~12 MB of 963
  after, all of the remainder being metadata a statement needs once.
- **`FastOrder.Small` is 2048 because a data chunk is**, and one chunk is what can be addressed in
  place. Past it the reader keeps only the chunk it is on, the rows have to be copied out, and it is a
  cliff rather than a slope: 2.9x at 2048 rows and 0.3x at 4096, both at a hundred columns. So the
  count is asked of the *result* before a row is read -- `Values.Of` picks `Chunk` or `Copy` while
  nothing has been consumed, which is the only moment the choice can still be made. `Copy` exists for
  a result that is split for some other reason; a scan wide enough to run in parallel returns 41
  chunks for a thousand rows, and no table this path is taken for is anywhere near that.
- **The vectors are reached by reflection, and that is not the AOT problem it sounds like.** The
  field is `VectorDataReaderBase[]` -- an internal element type, which `UnsafeAccessor` cannot name
  and .NET 10's `UnsafeAccessorType` refuses for an array. A literal `typeof` with a literal field
  name is a shape ILLink resolves and keeps the field for, so it builds clean under `IsAotCompatible`
  with warnings as errors; the repo's rule is against reflection-based *serialization*, which this is
  not. What it has to survive is the driver renaming the field, so a miss falls back to `Copy` instead
  of throwing, and `ChunkTests` pins both branches -- a rename would otherwise cost the whole point of
  the path and pass every other test. The rest is public: `DuckDBResultChunkCount` and `DuckDBResult`
  are, and `IDuckDBDataReader` -- with `IsValid(offset)` and `GetValue<T>(offset)` -- is the interface
  those internal readers implement.
- **It is on, and the opt-out is `--no-sort-small-tables`.** What it can get wrong is how fast the
  answer comes rather than what the answer is: every way the bounds can be wrong degrades to a whole
  scan answered out of `Copy`, which is slower and still right. The one thing it decides differently
  from DuckDB is which of two rows tied on the sort key comes first -- both sorts are unstable and
  SQL leaves it unspecified either way. That is also why the suite is worth more than it looks: with
  the default on, every materialized-lake test in it runs through this path rather than around it.
- **Three things bound the path.** The table has to be **materialized and counted small at build**,
  since the statement goes out without its `TOP` and there is no falling back once it has: guessing a
  size and retrying would fetch the whole table on `get the last few`, which is the shape that matters
  most. The count is taken once when the table is built and grown by what an insert says it wrote --
  and *only* by an insert: a materialized table's UPDATE is an evict and a re-insert of the same rows
  and its DELETE only removes, so counting either would push a table nothing grew past the threshold
  and cost it the fast path for the life of the process. `Gateway.Grew` is where both sessions say so,
  and a count that is never told is the worst failure this has: the table still qualifies, the whole
  scan still goes out without its limit, and past 2048 rows `Copy` answers -- measured by someone
  else as 3x *slower* than leaving it off, which is exactly what the number says it should be.
  And the sort key has to be a **number or an instant**: text is a collation DuckDB owns and
  `string.CompareTo` is not it, so text is left where it works. A null sorts below every value --
  first ascending and last descending, which is SQL Server's order and neither of the ones
  `default_null_order` gives: the writer says `NULLS FIRST`/`NULLS LAST` on every term it renders,
  so the statement DuckDB is asked and the sort done here are the same question.
- **A number is not always ordered the same in both, and NaN is where they part.** DuckDB sorts it
  as the largest value there is -- ahead of infinity, behind only a null, and flipping with the
  direction the way any other value does. .NET's `CompareTo` puts it *below* negative infinity. So
  `Real<T>` orders the two float types itself and `Sorted<T>.Order` is virtual for it. What makes
  this the kind of thing `OrderingTests` cannot catch is that no layer file can hold a NaN, so
  `SortKeyTests` asks DuckDB for the order of a list holding one and compares the key against it.
  `-0.0` is the case that looked the same and is not a problem: DuckDB reads it equal to `0.0` and
  `CompareTo` answers 0 as well, so nothing has to be done about it.
  `OrderingTests` is the whole guarantee: every shape asked of both paths and compared, nulls and ties
  included. `OrderingUseTests` reads what was actually sent, since that comparison passes just as well
  when the fast path never fires.

## The row path

- **A value never crosses into `object` on the row path, and what makes that possible is that the
  decision is per column rather than per value.** Both writers used to switch on the runtime type of
  a boxed value -- `TdsTypes.WriteValue(object)` and `PgTypes.WriteText`/`WriteBinary` -- which is a
  box a row a column, and 24 bytes each. But the TDS token, the PostgreSQL OID *and* the CLR type the
  reader hands a column back in are all fixed by one thing, the column's DuckDB type, so the pair is
  known before the first row: `TdsField.For` and `PgField.For` choose once at COLMETADATA and
  RowDescription time and the row loop calls what they chose. `Ints<T>` covers every integer in one
  class because `IBinaryInteger` makes the widening to the declared length a typed conversion rather
  than `Convert.ToInt64`, and `Written<T>` covers everything PostgreSQL renders as text because
  `IUtf8SpanFormattable` writes it straight into the message. Measured over the suite, boxed value
  types fell from ~30 MB to 4.9 MB and nothing is left under either row writer -- what remains is
  Npgsql and SqlClient boxing on the *client* side of the tests. A reference does not box, which is
  why a string and a blob are typed only where it saves a type test, and `Objects` keeps the old
  behaviour for anything with no pair: read as it comes, converted from whatever it turns out to be.

## What a start asks the catalog

- **A question per table is a catalog scan per table, and a start that asks two of them spends most
  of itself asking.** `information_schema` and `duckdb_constraints` are table functions like
  `duckdb_tables()` below: every table in every attached database is materialized before the `WHERE`
  chooses any of them, so what one costs is the catalog and not the answer. `Catalog.Materialize`
  used to ask each table for its columns (was this one already in the store?) and each table for its
  key (is DuckDB holding it already?). Measured on a 300-table store of 50-row tables:

  | | per table | in one question |
  |---|---|---|
  | the columns of a stored table | 5666 ms | **23 ms** |
  | whether DuckDB holds its key | 1002 ms | **6 ms** |
  | `count(*)` and `max(<identity>)` | 137 / 180 ms | left alone |

  Which is 73% of what a restart over that store cost. `Catalog.Standing` takes both in two
  questions naming no table -- the same move `Shapes` makes for a baked database -- and the start
  falls from **9143 ms to 2150**, the first one that builds the store from **13764 to 4472**.
- **It is a snapshot of what the run before left, not a reading of what is there**, which is the only
  reason one question can answer for a build that is creating tables while it runs. That also
  settles the key: `Keyed` is *told* whether DuckDB already holds one rather than asking, because
  the two callers know without looking. A table the store carried has whatever it was given; a table
  this build just made with `CREATE OR REPLACE TABLE … AS` has none, since CTAS carries no
  constraint over -- which is why a spilled store, rebuilt from the layers every start, has to make
  the key again with the table. `SpillTests.TheKeyIsMadeAgainWithTheTable` and
  `StoreTests.AReloadKeepsWhatTheStoreHolds` are the two halves, and each fails if the other's
  answer is given.
- **What a bind costs is flat, so a start that binds a statement a table cannot be made cheaper --
  only made to bind nothing.** A lake serving views over parquet spent 36% of a host's whole CPU
  starting, and 89.7% of that was DuckDB's parser and binder: `Catalog.Materialize` learned each
  layer's columns with a `DESCRIBE SELECT * FROM read_parquet(…)`, which is one bind a source. The
  bind does not vary with the column count, the catalog size, the query shape, or whether the file
  is warm -- measured against a view over read_parquet at 0.379 ms, of which 0.124 is binding
  anything at all, 0.090 more is re-binding a view body from its text and 0.165 is the table
  function opening the file. So there is no cheaper statement to issue:

  | 152 parquet sources, 148 tables | |
  |---|---|
  | a `DESCRIBE` each | 210–250 ms |
  | `Layer.Footers`, one question naming every glob | 24–43 ms |
  | the five it declines to answer for, described | 11–14 ms |

  Which took a start of that lake from **676 ms of CPU to 414**, and its wall clock from 475 to 266.
  The column names, their order and their types are in the footers, and `parquet_schema` is DuckDB
  reading exactly those, for every file at once -- `duckdb_type` is its own mapping of them, so the
  answer is DuckDB's and not a second reading of the format.
- **The reading is declined rather than approximated, because the bar is the same catalog and not a
  close one.** A source of one file, or of files that all say the same thing, is the whole of what a
  footer can answer for. Where files disagree, `union_by_name` widens each column to a type holding
  what every file put in it and appends what a later file adds; that is the binder's arithmetic, not
  a fact in any footer, and reimplementing it would be a second answer to drift from the first. A
  nested column is the same problem in the small -- a footer spreads one over a row a level, and
  `STRUCT(a INTEGER, b VARCHAR)` is something DuckDB puts together as it binds. Both are left to be
  described. So is a partitioned source's `k=v` columns: what type a partition value has is DuckDB's
  own reading of the *path*, so a hive source still binds -- one statement rather than the two it
  used to. `LayerTests.ReadingAFooterSaysWhatDescribingWouldHave` holds every shape up against what
  describing it says, and counts the ones answered, since an equality that quietly stopped covering
  the ordinary shape would still pass.
- **The declared defaults are one question too.** A real schema declares a default on most columns
  of a few hundred tables, which collapse to a few dozen distinct expressions, and each of those
  used to be a statement to evaluate it and one or two more to ask `typeof` of what it came to:
  42 ms of a start, nearly all of it the per-statement floor. `Catalog.Evaluate` now casts every
  distinct default in one `SELECT` (3.6 ms for a few dozen against 17 one at a time), falling back to one
  at a time only when the batch fails, since one statement cannot say which default is the broken
  one. What `typeof` says of a literal is DuckDB's type system and nothing the lake decides, so
  `Catalog.Literal` keeps the answer for the process and a fleet of lakes over one schema asks each
  question once.
- **What is left after that is the views, and the only way not to pay for one is not to have it.**
  `CREATE VIEW` is ~0.8 ms on a lake of flat parquet and does not batch: 149 of them cost 235 ms as
  separate commands, 232 inside one `BEGIN`/`COMMIT` and 237 as one 149-statement command, so there
  is no per-statement overhead to amortise. Nor is it the catalog filling up -- 40 lakes' worth of
  views into one instance drifts from ~1.65 to ~2.4 ms a view over 6000 of them, which is real and
  is not the story. Serving from a baked database, the one existing path that publishes no views at
  all, is *worse*: 419 ms of start against 1608, because `BakedBase` copies the file every run.
  `Config.Inline` is what is left -- no relation is created, and `Catalog.Scan` puts the merge where
  the name would have been. 294 ms of start against 151, and ~3% on every statement.
- **The build runs on one thread, and serving runs on what `Config.Threads` says.** DuckDB's floor
  per statement is scheduling as much as parsing: `SELECT 1` costs 0.21 ms on this machine's four
  threads and 0.10 on one, and a trivial `CREATE VIEW` 0.28 against 0.10. A start is hundreds of
  such statements and no scan -- measured on a lake of a few hundred tables, `SET threads=1` around the build took
  the `CREATE VIEW`s from 188 ms to 138, the defaults from 42 to 26, the shims from 13 to 11, and
  the whole build from 693 to 552. `threads` is a global setting, so `Lake.StartAsync` sets it back
  -- to the configured count, or `RESET` -- before a door opens. A materialized lake builds with what
  it serves with, because its build is the collapse and a collapse is a scan: one 5M-row table
  collapses in 177 ms on four threads and 671 on one. The same few hundred small tables collapse faster on
  one thread (2.1 s against 3.1), since there the build is statements again, but a lake cannot tell
  which it is going to be before it has read the files -- so `Threads` is what says so, and a fleet
  of small materialized lakes sets it to 1 and gets both.
  `Threads` itself is a knob for a fleet: a process running many lakes at once is running that many
  DuckDBs, each with a pool sized for the whole machine.
- **It is the SQL Server door's alone, because that door is the only one with a parser.**
  `Gateway.Translate`'s default arm is `Plan.Rows(sql)`: a PostgreSQL-door read goes to DuckDB
  exactly as the client wrote it, so there is no tree to put the merge into and text substitution is
  the thing this codebase does not do -- a CTE named `orders`, a column named `orders`, the name
  inside a literal. `Config.Validate` refuses the pair rather than letting it surface as a missing
  table. What the flag costs even on its own door is everything that reads DuckDB's catalog instead
  of the parser: the `pg_constraint` shim joins `duckpg_constraints` to `pg_class` and
  `pg_attribute` *by relation and column name*, so with no relation there are no keys and no
  references to be read back. `sp_tablecollations_100` is the one such reader the TDS door itself
  needs -- SqlBulkCopy asks it before sending anything and refuses an empty answer -- and it is
  answered from `DESCRIBE` over the merge, which is the same list in the same order.
- **Two places had to learn there is no name, and the suite is what found them.** A declared
  identity's sequence starts past `max(<column>)` of what the table publishes, which was read by
  name; and `Embedded` recognises a joined write whose clause already carries its own target by
  reading that target's name out of the translated text, which an inlined target does not have --
  so it compares the subquery's alias instead, and without that the target went in twice under one
  alias. Both were found by running the whole TDS suite against an inlined lake rather than by
  reading the code: 643 tests, and those two shapes were the only ones that broke.
- **`parquet_metadata_cache` is worth ~15% of every bind that remains**, and every one does remain:
  a view is bound on every execution, so the footer a start read is read again to create the view
  and again by every statement through it. `SET GLOBAL` rather than a plain `SET`, which DuckDB
  scopes to the connection issuing it -- every session borrows a connection of its own onto this
  database, and a cache none of them can see is one they all pay around. A file that changes is read
  again, which is what keeps a rewritten write layer honest.
- **A declared view is made once the views it reads are, and one that reads a refused view is
  refused by that name rather than by DuckDB.** The model lists views in no useful order, and
  `Catalog.Declared` used to make each round whatever it could and retry the rest -- which rebound
  every refused view once per round, and a bind fails no cheaper than it succeeds: on a schema of a
  few hundred tables and two dozen views, half of them refused (functions with a body that is not an
  expression, and the views calling them), the ones that publish cost 58 ms and the retries 115.
  `TSqlContext.Reaches`
  is what the writer resolved onto the lake, collected off the tree as the query is rendered, so the
  order is known before anything is asked of DuckDB and a view naming a refused one is never sent.
- **The constraint rows go in through the appender, not as one `VALUES` list.** `Catalog.Constraints`
  fills the `pg_constraint` shim's table with a row per column of every key and reference, which on
  a real schema is several hundred rows and over 100 KB of SQL -- a statement DuckDB
  parses and binds a literal at a time, 41 ms on every start. The same rows through
  `DuckDBConnection.CreateAppender` cost 6 ms, and there is no statement to bind at all.

## What a pooled checkout costs

- **`duckdb_tables()` costs the whole catalog, not the rows it is filtered to.** It is a table
  function: every table in every attached database is materialized -- with its estimated size, its
  column and constraint counts and its rendered `CREATE` text -- and only then does the `WHERE`
  choose any of them. Measured on DuckDB 1.5.5 with a lake-shaped catalog, ~17 µs a table and flat
  in what the filter keeps: 250 tables (50 published over three read layers, plus the write and
  tombstone tables) answer `WHERE temporary` in 4--9 ms, and a thousand tables in 17 ms, against
  ~0.3 ms for a trivial statement. Every other spelling costs the same, since they are the same
  function underneath -- `information_schema.tables`, `SHOW ALL TABLES`, `SHOW TABLES FROM temp`,
  and `duckdb_tables()` narrowed to `database_name = 'temp'` were all measured and none is cheaper.
- **Which is why `TdsSession.Reset` asks only when the session made one.** SqlClient announces a
  reset before the first statement of every pooled checkout, so an ORM that opens and closes around
  each statement was paying a full catalog enumeration per statement -- growing with the lake and
  unrelated to anything the client did. A `bool` set in `TdsSession.Command`, where every statement
  this connection runs goes past, makes the answer free for the sessions that never make a `#table`:
  `SqlText.MakesTemporary` is asked of SQL duckpg rendered itself, so a false yes only costs the
  scan that used to be unconditional and a false no is not reachable. The catalog stays the
  authority on *what* to drop -- the flag decides only whether to look.
