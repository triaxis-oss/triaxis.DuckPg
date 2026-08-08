# duckpg — working notes

A PostgreSQL wire-protocol frontend for a stack of YAML/JSON/parquet layers, executing against
DuckDB. Read `README.md` first: it is the user-facing contract, and this file only adds what
someone changing the code needs.

## Where things are

Two projects, and a package each: `src/triaxis.DuckPg` is the lake, and `src/triaxis.DuckPg.Cli` is
the `duckpg` command and nothing else. The tool packs its whole publish output, so it carries the
library rather than depending on it.

`TestLake` stays in the test project. What a fixture owes a caller is a directory of layers and a
connection string, and a caller embedding a lake writes its own layers anyway -- shipping one would
publish a temp-directory convention as API.

| File | What it owns |
|---|---|
| `ServeCommand.cs` | The CLI: arguments, the config file, argument-over-file precedence. |
| `DuckPgServiceCollectionExtensions.cs` | `AddDuckPg` and `AddDuckPgFactory`: what a lake is made of, as registrations. |
| `DuckPgLakeFactory.cs` | Lakes on demand, each owning the container it came out of. |
| `DuckDbInstaller.cs` | `IDuckDbInstaller`: fetching DuckDB, for a lake starting and for `--install-duckdb` alike. |
| `Config.cs` | The bound configuration. Every property here is part of the contract. |
| `Lake.cs` | The composition root: DuckDB connection, schema, catalog, gateway, listeners. Tests use it too. |
| `Layer.cs` | Scanning a layer directory, reading a source, writing one back. YAML ↔ JSON. |
| `Catalog.cs` | The published shape: which tables exist, their columns, keys, the view SQL, and the dacpac's own views. |
| `WriteLayer.cs` | The top layer: DuckDB tables loaded from files, and persisted back to them. |
| `DacpacSchema.cs` | The declared schema as a service: finds the dacpac, reads `model.xml` for columns, keys, defaults and views. No DacFx. |
| `Gateway.cs` | Statement translation: catalog shims, GUC no-ops, DML rewriting. `Shims` lives here. |
| `PgWire.cs`, `PgTypes.cs`, `PgServer.cs`, `PgSession.cs` | The PostgreSQL protocol. Rarely the thing that is wrong. |
| `TdsWire.cs`, `TdsTypes.cs`, `TdsServer.cs`, `TdsSession.cs` | The TDS protocol: packets, tokens, RPC, transactions. |
| `TSql/` | Lexer, parser, AST and DuckDB renderer for the T-SQL a client sends. |
| `HostFunctions.cs` | The SQL Server functions answered in .NET: `pwdencrypt`, `pwdcompare`, `CONVERT` styles. |
| `SqlText.cs` | Enough SQL scanning to find top-level keywords without a parser. |
| `SortedRows.cs` | `IRows`, and a result held and sorted here rather than by DuckDB. |
| `DuckDbLibrary.cs` | Finding the machine's DuckDB, and the AOT dependencies DuckDB.NET needs. |
| `DuckDbDownload.cs` | Fetching that library from DuckDB's releases, when asked and only then. |

## Invariants worth not breaking

- **A layer's format is a property of the file, not of the layer.** Anything that special-cases
  "the parquet layer" or "the seed layer" is a step backwards; the only layer with a role is the
  write layer.
- **A file's *shape* is the file's too, but which column a mapping key fills is the table's.** That
  split is why `LayerSource.KeyedBy` is set in `Catalog.Build` -- where `MappingKey` knows the key --
  rather than in `Layer.Scan`, which sees files and no configuration. `Yaml.Shape` answers only the
  half a file can: root is a mapping, every value is a mapping. The other half is that the key is
  exactly one column, since a mapping key is one value; a table with a composite key or none reads
  the same document as the single row an object-rooted JSON file has always been, and that fallback
  is what keeps this from breaking a lake that already had one. `Converted` -- YAML always, keyed
  either format -- is what `HasFileName` now answers to, because a copy cannot publish provenance.
  The shape survives a write: `WriteLayer.Persist` passes `KeyedBy` back, since rewriting someone's
  keyed file as a sequence would be a change nobody asked for. A mapping key is written plain for a
  number and quoted for text -- quoted reads back as the same string, and plain would turn `007`
  into seven. JSON cannot write a non-string key at all, which is why a keyed JSON key is read from
  its text rather than its quoting, and why that one format cannot hold `007` as a key.
- **The fallback is right and indistinguishable from the mistake, so the answer is signal and never
  a guess.** A root mapping publishes one row, which is also what a file looks like when its author
  wrapped the rows in a `widgets:` key or named them with no key column to put the names in. Neither
  is unwrapped: a heuristic would be right most of the time and, the rest of the time, wrong with no
  more signal than there was before it, and a documented rule would have become conditional on it.
  So `Yaml.Shape` reads both out of the parse `Keyed` already paid for -- `Layer.Shapes` is where
  that one reading is done, and `Catalog.Diagnose` is what says it, because the second half of the
  question (has this table a single key column?) is the catalog's. Above both is
  `TableLayer.Shape` on the startup line: rows and columns as they turned out, which guesses nothing
  and is the only one of the three that catches a surprise nobody anticipated. The rows are counted
  only for a materialized layer, where the count is a table already in memory -- reading a lake's
  parquet footers to answer a log line is not what starting up is for.
- **The YAML-to-JSON copy belongs to one conversion, and naming it after the layer is what made it
  a bug.** `Yaml.ToJsonTree` used to derive a scratch directory from the glob, so every lake over
  that layer was inside one copy: two started together called `File.Create` on the same file and
  whichever lost died, and either could delete the tree the other was still reading, since
  `Yaml.Discard` takes the whole directory. It reads like a cache and is not one -- `Layer.Read`
  throws it away as soon as it has been read, so nothing is ever there to reuse and all the sharing
  bought was the collision. `Directory.CreateTempSubdirectory` is the whole fix: a name nobody else
  can derive is unique across processes as readily as across tasks, needs no lock and no key, and
  leaves no directory anyone else has to be able to write into. Making it a real shared cache is
  the other coherent answer and a much larger one -- it means a lock file held shared while the
  tree is read and exclusively to build or delete it, a key over the files' identity *and* the
  mapping key, and keeping the tree rather than discarding it, since a cache read once pays for
  none of that. What settles it is that there is nothing to win: only YAML and keyed files are
  converted at all -- parquet is scanned where it lies -- so the input is a seed somebody typed,
  and a seed converts in 0.65 ms at ten rows, 7.9 at a hundred and 28 at five hundred. Sharing a
  few milliseconds per table is not worth a lock, which is the argument to make again if anyone
  proposes measuring this on a layer no one would write by hand.
  `SharedLayerTests` is where both halves are pinned, the collision and the two trees.
- **Layer sequence numbers decide everything.** Read layers are 0..n-1 in configured order, the
  write layer is n. `QUALIFY … ORDER BY _seq DESC` is what makes a higher layer shadow a lower one,
  and a tombstone only hides rows with `_seq < writeSeq`.
- **A write branch is earned, not assumed.** A writable table with no file in the write directory
  is published as though it were read-only; `Gateway.Promoting` prepends `Catalog.Promotion` to the
  first write's plan, so the branch appears on the writing session's connection inside its
  transaction. `Catalog.promoted` is only set once that write commits -- a rolled-back promotion is
  made again rather than assumed, which is why the DDL is `IF NOT EXISTS` and the view rewrite is
  `CREATE OR REPLACE`. A table whose directory already holds rows or tombstones is prepared at
  build, because those rows have to be loaded. The tombstone check is promoted separately and by
  the same rules: measured, it costs a flat ~1 ms regardless of the table's width, because it binds
  one subquery over one key column -- so it waits for a row to actually be hidden. An `UPDATE` only
  hides one when it moves a key; otherwise the rewritten row shadows what is beneath it on its own.
  A `--cache` copy survives the promotion: `Catalog.Underlay` puts it below the write branch, since
  the copy is the read layers and a write does not touch those.
- **A materialized lake is the same catalog with the merge paid once.** `Config.Materialize` cuts
  each table out of its own stack -- `Catalog.Materialize` keeps that stack behind as a view in the
  `base` schema, unread while the lake serves and the only honest baseline for the delta
  `Catalog.Flush` writes at shutdown. What makes it fit the existing machinery is `Table.WriteName`
  answering `QualifiedName`: `Evict` plus the insert of `duckpg_updated` *is* an UPDATE, and `Evict`
  alone *is* a DELETE, so only the tombstone steps had to go and promotion became a no-op. The merge
  itself has to be built against `table with { Materialized = false }`, or it would name itself as
  its own write branch. `Flush` runs once and computes both temp tables before touching either
  target, since the baseline reads what it is about to replace. What the baseline must *not* include
  is the previous run's delta: the write layer is rewritten whole, so a delta measured against a
  stack that already carried it comes out empty and takes the earlier run's writes with it -- and a
  tombstone, absent from a baseline that applied it, is not written again and the row returns on the
  run after. So the baseline is the read layers alone while the table is cut from the whole stack;
  the two views differ, and a lake restarted twice is what tells them apart -- `DeltaTests` runs every
  shape of write through three of them, in both modes, because two agreeing proves nothing. What a
  reconstructed delta cannot say is *how many*: `EXCEPT` is a set difference, so on a keyless table a
  row inserted identical to one the layers hold is not in it. A keyed table is safe, and a delete
  without a key is refused before it can be lost. `Config.Store` takes the
  question away entirely: the tables are in a DuckDB file, so a start that finds one keeps it and
  never reads the layers for it again -- `Catalog.Stored` is what asks, and a shape that disagrees
  with what the catalog publishes is refused rather than rebuilt, since rebuilding is the one thing
  a store exists to prevent. No delta is flushed beside one.
- **What a store *means* is one answer, and `Catalog.Keeping` is where it is given.** The file is
  either the lake's state or only somewhere for its tables to live, and the two halves -- whether the
  layers are read for a table the file already carries, and whether a delta is written at shutdown --
  have to agree, or the run either loses its writes or writes them down twice. `StoreMode.Keep` is
  both halves on and `StoreMode.Spill` is both off, which is why one property answers both gates
  rather than each asking `config.Store` for itself. `Spill` exists for memory and nothing else: it
  behaves in every other way like an in-memory materialized lake, so `SpillTests` restarts twice for
  the same reason `DeltaTests` does. Nothing in a DuckDB file says which mode wrote it, so the two
  cannot share a path -- and there is no check that could tell.
- **DuckDB.NET duplicates an in-memory connection and no other.** `Duplicate()` throws
  "Duplication of the connection is only supported for in-memory connections", which is why
  `DuckDbSession.Of` opens the file again for a stored lake -- the driver holds one instance per
  connection string, so both reach the same database. Nothing says this in a stack trace: the
  sessions simply never get a connection, and every client times out in the pre-login handshake
  while the listener sits there accepting.
- **The write layer holds effective state, not a log.** A deleted row leaves the write table and
  gains a tombstone; a re-inserted one comes back with no tombstone bookkeeping. There is no
  per-row sequence number, and adding one back means re-deriving what shadows what.
- **Persistence happens after DuckDB commits**, never before — see `PgSession.Persist`. A write
  inside a transaction is remembered and written at `COMMIT`, dropped at `ROLLBACK`.
- **`hive_partitioning` is always passed explicitly.** Left to itself DuckDB turns it on and
  invents a column from any `k=v` directory above the lake. `LayerSource.Partitions` records the
  keys the layer actually declares, and only those survive into the published columns — it is what
  separates a partition the lake owns from one it merely sits under.
- **A partition column is part of the key.** `db=one/orders.parquet` and `db=two/orders.parquet`
  both have a row 1; without `db` in the key the `QUALIFY` would drop one of them. `KeyFor`
  appends partition columns to whatever key was found, and to nothing when no key was found.
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
- **The type catalog describes the OIDs the gateway puts on the wire**, not DuckDB's own types.
  `Shims.Macros` replaces `pg_type` wholesale for that reason; DuckDB's has NULL oids and its own
  type names.
- **The dialect is translated on the tree, never on the text.** `TSql/` parses T-SQL into an AST and
  renders DuckDB SQL from it. A regex "fix" for a dialect difference belongs in the renderer as a
  case, not in a string replacement — this is why `'a' + b` concatenates and `1 + 2` adds.
- **A statement the parser does not cover is refused**, with the position. Passing unknown text
  through to DuckDB moves the failure somewhere harder to read.
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
  `string.CompareTo` is not it, so text is left where it works. Nulls sort last either direction,
  which is DuckDB's `default_null_order` rather than SQL Server's -- the lake renders the clause
  through today, so DuckDB is what this has to agree with.
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
- **A view is bound on every execution, not once when the lake is built.** Everything above is why:
  every expression in a view definition is paid for by every query touching it. Hence no cast to the
  type a layer already has, no merge wrapper around a table only one layer carries, `--cache` writing
  a merged table out once as parquet -- and, further along the same line, `--materialize` not
  publishing a view at all.
- **A shutdown that is not reached writes nothing.** A materialized lake's delta goes out at
  shutdown, so every way of shutting down has to reach it -- and the `duckpg` command reached none
  of them. `Lake.Completion` answers to the lake's own token, which no signal touches, so the await
  never returned; and SIGINT unhandled ends the process where it stands, so nothing unwound. The
  writes were simply gone, with no error anywhere. `ServeCommand` takes SIGINT, SIGTERM and SIGQUIT
  itself and sets `PosixSignalContext.Cancel` so the runtime does not end the process first, then
  stops the lake rather than only disposing it; a second signal is left to the default, which is the
  escape hatch if a stop hangs. `Lake.Dispose` flushes too, since a container teardown is
  synchronous and losing a lake's writes there would be just as quiet. `Gateway.Flush` is what both
  call, under `gate`, because it is the lake's own connection and a committing session is on it.
  What the unit tests could not have caught is that none of this is reached: they all called
  `StopAsync`, which worked the whole time.
- **A lake owns what it was built from, or nothing at all.** A factory-built lake holds its own
  container and releases it on disposal, which is what lets a caller hold one object instead of two
  with an ordering constraint; one resolved from someone else's container owns nothing of theirs.
  `DisposeAsync` waits for the listeners, `Dispose` only cancels them -- a synchronous dispose that
  blocked on a serving loop is the sync-over-async this exists to avoid.
- **The lake's schema goes in front of every session's search path, and `main` stays behind it.**
  DuckDB's own default is `main` and PostgreSQL's is `public`, so whatever a lake publishes into is a
  schema some caller does not expect -- and `SELECT * FROM orders` misses it. `DuckDbSession.SearchPath`
  is what makes the name stop mattering; it is set on the session's own connection, since DuckDB scopes
  `search_path` per connection. `main` has to stay in the path because `Shims.Apply` rewrites
  `pg_catalog.pg_class` to an unqualified `duckpg_pg_class`, and the shims are created unqualified on
  the lake's own connection, which is to say in `main`. Nothing else moves: an unqualified `CREATE`
  would follow the path, but every temp table duckpg builds says `TEMP`, which names the catalog
  outright -- and the TDS door never used the path at all, since `TSqlWriter.Table` writes the lake's
  schema onto `[orders]` itself.
- **Both front doors are opt-in, and a lake needs one.** `PgServer.Enabled` and `TdsServer.Enabled`
  read the same way, so a consumer speaking one protocol opens one listener. `Config.Validate` is
  what makes "neither" an error rather than a lake nothing can reach.
- **The public surface is Config, Lake, IDuckPgLakeFactory, IDuckDbInstaller, LayerFormat, StoreMode,
  DuckPgConfigurationException and DuckDbLibrary -- nothing else.**
  The catalog, the gateway, the two protocols and `TSql/` are internal, which is why `Lake`'s
  constructor is internal and `AddDuckPg` assembles it by hand: a public constructor would have to
  take public parameters, and that would make every part of a lake an API. `InternalsVisibleTo`
  covers the tool and the suite.
- **AOT-clean**: no reflection-based serialization, no `JsonSerializer.Serialize<object>`. The
  project ships portable but the analyzers stay on, so a regression shows up as a build warning
  (and warnings are errors here).

## Things that were learned the hard way

- Npgsql closes the connection on SQLSTATE `XX000`, so `PgError.SqlStateOf` mapping DuckDB's error
  text to real codes is what makes a failed query survivable. Cancellation must map to `57014`.
- Npgsql hands back every column as `String` regardless of OID until the data goes out in binary
  format — hence `PgTypes.WriteBinary`, including base-10000 `numeric`.
- **A numbered parameter cannot be bound to a statement that skips one, so `SqlText.Rename` makes
  every `$1` a named `$p1`.** DuckDB counts only the parameters a statement mentions and still
  demands the number each was written with: `$3` on its own is "parameter number 3" in a statement
  that "only has 1", and no spelling reaches it -- binding "1" answers "values were not provided
  for 3", binding "3" answers that there is no 3. A rewritten write is several steps and none of
  them has to use every parameter (the step collecting keys reads the predicate, never the
  assignments), so that unreachable case is the ordinary one rather than a corner. A *name* is not
  counted: DuckDB ignores one it was handed and does not want, which is what lets every step take
  the same arguments — and is what the TDS door was always doing, `@p0` rendering as `$p0`.
- A DataRow is a few bytes and a socket write is a syscall, which is what once held the PostgreSQL
  wire to ~250k rows/s. Responses go out through a `BufferedStream`; only the write side, because
  one cannot interleave reads and writes over a socket, and `PgWire` flushes before every read so
  nothing can sit in the buffer while the server waits on the client.
- With the syscalls gone, allocation is what is left: a row is formatted straight into a `Msg` that
  the loop reuses (`Utf8`, `Format`, `BeginField`), never into a `byte[]` or a `string` per value.
  Anything on the row path that returns a fresh array puts the ceiling back.
- `Describe('S')` must answer, or `cmd.Prepare()` fails. DuckDB cannot bind a statement with open
  parameters, so typed `NULL`s are substituted and the query run `LIMIT 0`.
- YamlDotNet's JSON emitter leaves control characters unescaped, and real exports carry tabs inside
  plain scalars. The conversion walks the node model and writes through `Utf8JsonWriter`.
- JSON type inference reads every integer as `BIGINT`; that is why a parquet layer's type wins, and
  why a dacpac is worth having.
- Loading a half-written native library aborts the process with SIGBUS rather than failing, so
  nothing is left to report it -- which is why `DuckDbLibrary` checks a candidate's size before
  trying it, and why a download is unpacked into a staging directory beside its target and renamed
  onto it -- beside it, so the rename stays on one filesystem and cannot half happen.
- A `CommandErrorException` whose template has more holes than arguments is logged as nothing at
  all -- the exit code arrives, the message does not. Count them, or build the text and pass it as
  one argument.

## What TDS demanded, and does not say out loud

- **The TDS version in LOGINACK is big-endian**, though LOGIN7 sends it little-endian. Get it wrong
  and SqlClient says "invalid or unsupported protocol version" with no further clue.
- **The login response must carry a collation (ENVCHANGE 7).** Without one SqlClient throws a
  `NullReferenceException` inside its own RPC writer the first time a string parameter is sent —
  the failure never reaches the wire, so the server looks innocent.
- **DuckDB reports a decimal column as plain `Decimal`.** Precision and scale come from
  `reader.GetSchemaTable()`, and TDS has to declare both in COLMETADATA or every value truncates
  to an integer.
- **Encryption is refused with `ENCRYPT_NOT_SUP`**, which is why `Encrypt=False` is part of the
  connection string. TDS otherwise encrypts the login packet even in a plaintext session.
- **A cancel arrives on the same connection as the query**, unlike PostgreSQL's second connection.
  It is only noticed between row packets; see `TdsSession.Canceled`.
- **Nothing may be cut across the seam between two packets.** SqlClient reassembles a read that
  ended mid-packet by replaying the framing it had begun, and framing split across the seam loses
  it its place -- it then reads response bytes as a length, and the failure surfaces much later,
  usually as an `ArgumentOutOfRangeException` while the reader is being closed. Two mechanisms keep
  it out of the seam, and they have to agree about where the seam is:
  - Each row is built on its own (`TdsSession.Rows`) as though it began a packet. One that does not
    fit in the packet being filled ends that packet where it is -- short -- and starts the next.
  - A MAX value's chunks stop at the packet boundary rather than running through it
    (`TdsTypes.WritePlp`), measured from the row's own start, which is why the row is built at
    offset zero: for a row too big for any packet, that is where the cuts really fall.

  Flushing after the row that overflows instead cuts inside it, which is a different bug that looks
  the same. This is invisible on a fast loopback and constant over a real network, because TCP
  decides how often a read ends mid-packet. `TdsTests.LongResultsSurviveASplitRead` forces the split
  so it is deterministic, and `PacketsEndWhereRowsDo` checks the framing itself rather than the
  client's tolerance of it -- SqlClient survives some violations and not others, which is how the
  first version of this fix passed while leaving wide rows broken.
- **The legacy LOB parameter types are still in use.** LLBLGen on the old `System.Data.SqlClient`
  types a string parameter as `NTEXT`, so `TdsTypes.ReadValue` has to know `TEXT`/`NTEXT`/`IMAGE`:
  four bytes of declared maximum instead of two, a collation on the text ones, and a four-byte
  value length where -1 is null. Nothing here ever sends them back.
- **`COUNT` is an `int` in SQL Server and a BIGINT in DuckDB**, and an application casting the
  scalar to `int` throws on the difference. `TSqlWriter.Function` casts a `COUNT` back, around the
  window clause as well, and renders `COUNT_BIG` as the plain count -- which is how a caller asks
  for the wide one on the database this stands in for. `SUM` is not narrowed the same way: what
  SQL Server returns depends on the argument's type, which is not on the tree. It does arrive as a
  number rather than as text, which is a different bug -- see the type names below.
- **`OPENJSON` is a derived table, not a function call.** EF Core passes a collection as one JSON
  parameter and unpacks it with `OPENJSON(@p) WITH ([value] int '$')`, so the columns the WITH
  clause declares are projected in the subquery `TSqlWriter.OpenJson` renders. Resolving them where
  they are *used* instead would mean rewriting column references against an alias, which is the
  thing the tree is meant to avoid.
- **`MERGE ... WHEN MATCHED THEN UPDATE` is an update joined to its source**, and the parser
  desugars it to exactly that -- target, alias, `USING` as `From`, `ON` as `Where`. That is why
  `UpdateStatement` carries an alias at all, and why `Gateway.RewriteUpdate` had to learn the
  `FROM` clause it used to fold into the assignment list: with another table in scope, the columns
  the statement did not assign have to say which side they came from, in the projection and in
  `Keys` alike. The branches that add or remove rows are refused by name -- what a row's existence
  means is the layer machinery's, not one statement's.
- **The OUTPUT clause sits between SET and WHERE**, so an UPDATE carrying one does not end at its
  assignments -- read that way, `OUTPUT` looks like the start of a statement nothing covers. What it
  asks for is answered off what the plan already built: `duckpg_updated` holds every row as the
  update left it, `duckpg_keys` holds what a delete collected before the rows went. A DELETE can
  therefore only answer for its key, and says so; `OUTPUT 1`, which is EF Core counting the rows it
  touched, needs neither.
- **A declared key is a rule over the merged view too, and neither constraint DuckDB offers keeps
  it.** The write branch's `PRIMARY KEY` sees only the rows this process wrote, so an INSERT of a key
  a file below already holds is let through -- and the row then shadows that file's row, which is an
  UPDATE nobody asked for rather than the refusal a client expects. A materialized table has no
  constraint at all: `Catalog.Materialize` builds it with `CREATE TABLE AS`, and CTAS keeps no key,
  so the same key goes in as many times as it is sent. `Gateway.Duplicates` asks both halves as one
  `Plan.Checks` query over the rows about to be written -- a key the table already publishes, and one
  the statement repeats -- for the same reason a reference is asked there: a statement outside a
  transaction commits each step as it goes, and the RETURNING path writes the rows down before it can
  answer. It is asked only where the statement carries every key column, since a key the store
  generates cannot collide with one it generated before. Giving the materialized table a `UNIQUE`
  index instead is the obvious fix and the wrong one: a single-layer table is published without the
  `QUALIFY` that dedupes -- there is nothing to shadow -- so a file already holding a key twice is a
  lake that starts today and would stop.
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
  `Gateway.Duplicates` rather than a second path: what comes back with it is the behaviour above,
  including the part where the two modes disagree about what a duplicate even looks like.
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
- **A `bit` is converted for arithmetic, and only where the column resolves to one.** T-SQL makes a
  `bit` an integer to multiply it; DuckDB refuses `BOOLEAN * INTEGER` outright. The cast is written
  by `TSqlWriter.Operand`, which asks `TypeOf` what the column actually is -- `TSqlContext.Tables`
  is the catalog's types, and `Scope` binds each FROM clause before the items that read it, since
  the items are written first. Guessing instead is not available: `CASE WHEN 5` is *true* to DuckDB,
  so a coercion applied to a number would answer 1 rather than fail, and casting a DECIMAL that was
  taken for a `bit` would truncate it. A reference into a derived table resolves to nothing and is
  left alone -- DuckDB's error is better than a cast nobody can justify.
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
- **A DuckDB connection is not two threads' to share.** Sessions have one each, but the lake keeps
  its own for the work that belongs to no session -- persisting a table, seeding a sequence,
  rebuilding the catalog -- and everything that touches it holds `Gateway.gate`. `Promoting` reads
  what the catalog remembers under the same lock, since another session's commit is what writes it.
  Nothing here is visible as an error when it goes wrong: it is a native call two threads are inside
  of, and `ConcurrencyTests` is the only thing that would notice.
- **A DuckDB transaction fixes its catalog snapshot when it begins**, and that is what
  `Config.SerializeTransactions` is really for. Two open transactions writing the same row refuse
  each other, which ordering the writes would fix; but a transaction that began before another
  committed a *promotion* cannot see the write branch, cannot create one itself -- DuckDB calls that
  a catalog write-write conflict on create -- and stays that way until it ends. The two writes never
  overlap, so no amount of write ordering helps. Hence the turn is taken at `BEGIN` as well as at a
  write outside one, and given up when the transaction ends -- `transactions == 0`, or
  `transactionStatus == 'I'`, which is why a failed transaction ('E') keeps it until the ROLLBACK.
  It is also given up on dispose and on a pooled connection's reset, since a client that vanishes
  mid-transaction would otherwise keep the lake to itself. A `SemaphoreSlim` rather than a `Lock`,
  because a session's loop may resume on another thread between statements -- and a semaphore is not
  reentrant, so `turn` is the session's record of already holding it and has to be tested *before*
  `EnterTurn` is called. `turn |= … && EnterTurn()` reads as though it does that and does not: `|=`
  is not the short-circuiting `||`, so the second write of a transaction waited on the lock its own
  session was holding. The order is always the turn and then `gate`, never the reverse. A read
  outside a transaction takes nothing; a read-only transaction does take the turn, since nothing
  says in advance that it will stay read-only. Off by default, so a lake serving readers pays
  nothing for it.
- **A failure the client is told about is logged at warning.** A server that answers with an error
  and says nothing in its own log leaves a caller reporting a failure and nowhere to look -- which
  is what made an intermittent one look like the wire's fault rather than a statement's.
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
- **A join around that target folds into the write's own clauses.** `TSqlParser.Selecting` makes the
  other tables the write's `FROM` and their `ON` conditions part of its `WHERE`, which is the shape
  `Gateway.RewriteUpdate` already ran for a `MERGE`. Only an inner join folds: an outer one keeps the
  rows matching nothing, and those are rows the write would still touch, which a condition cannot say
  once the join is gone. `Gateway.RewriteDelete` had to learn the same clause and to qualify the keys
  it collects, since both tables may carry a column of that name. A join hint -- `INNER LOOP JOIN`,
  `HASH`, `MERGE`, `REMOTE` -- is read and dropped: it steers an optimiser this does not have, and
  says nothing about which rows come back.
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
- **`SCOPE_IDENTITY()` is per connection and `IDENT_CURRENT` is per table, which is why they are kept
  in two different places.** `TdsSession.identity` is the session's and `Gateway.identities` is the
  process's; `@@IDENTITY` is `SCOPE_IDENTITY()` without the scope, and without triggers there is no
  scope to tell them apart, so one value backs both -- answered in `TSqlWriter.Variable` rather than
  through the `Variables` dictionary, or the same number would have two sources. All three render as
  `numeric(38,0)`, which is what SQL Server answers whatever the column was declared as.
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
- **An OUTPUT parameter is answered in the type the caller declared, and the wire already says what
  that is.** `TdsTypes.ReadParameter` hands back the column beside the value, so nothing has to map a
  declared `int` onto a TDS token twice; `WriteValue` converts whatever DuckDB produced into it, which
  is how a `DECIMAL(38,0)` `SCOPE_IDENTITY()` reaches a client that declared `Int32`. The column is
  normalised on the way out rather than echoed: text and binary go back as a MAX of their own kind,
  since that is the only length `WriteValue` chunks correctly, and MONEY, the pre-2008 DATETIME and
  the legacy LOBs go back as what replaced them. SqlClient matches a RETURNVALUE to its own parameter
  **by name, with the `@`** -- `ParameterNameFixed` -- so a token named without it is dropped
  silently.
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
- **`TOP n PERCENT` is counted, not handed to DuckDB's `LIMIT n%`.** SQL Server rounds the share up
  and always answers at least one row; `LIMIT n%` rounds down and answers none for a small enough
  share. `TSqlWriter.Percent` limits by `CEIL(count(*) * n / 100)` over the body it just rendered --
  reusing the text rather than rendering it twice, so a CTE the body reads is still in scope.
- **A join's right operand is a join tree, not a table.** `a LEFT JOIN b JOIN c ON … ON …` nests, and
  the conditions close in reverse; parsing it left-deep leaves the last `ON` with nothing to attach
  to, which is what made a dacpac's view unpublishable. A join keyword arriving before this join's
  `ON` belongs to the operand, an `ON` ends it -- which is what keeps an ordinary chain a chain.
- **An application lock is granted by doing nothing.** `EXEC sp_getapplock` asks to be serialized
  against the other connections of a shared database; a lake serves the application that owns its
  files, so the exclusion is already there and `TSqlWriter` renders the statement as nothing --
  which `Gateway.Translate` already answers as `Plan.Empty`. Making it a real lock would promise
  more than a lake can keep, since the files under it may be served by another process. `EXEC` of
  anything else is refused by name: the parser covers the call so an ORM reaching for a procedure
  is told which one is missing, not that `EXEC` is unparseable.
- **A pooled connection announces itself in the packet header.** SqlClient sets the RESETCONNECTION
  bit on the first message it sends over a connection it took back out of the pool; only an older
  client calls `sp_reset_connection`, so a server that answers just the procedure never hears about
  the reuse. `TdsWire.ReadMessage` surfaces the bit and `TdsSession.Reset` acts on it, which is what
  keeps one session's `#table` out of the next one's.
- **A COLMETADATA name is counted in one byte**, so a name of 256 characters announces itself as
  empty and the client reads the bytes after it as the next token -- which surfaces as
  "Internal connection fatal error" from the parser, with the server looking innocent. DuckDB names
  an unaliased column after the text of the expression that produced it, and a
  `CASE WHEN EXISTS (...)` passes 255 without trying. `TdsSession.Named` cuts at 128, which is where
  SQL Server cuts (`sysname`); `TdsMsg.BVarchar` cuts at 255 as well, since a length prefix that
  cannot say what it carries is a desynchronized stream whatever the field was.
- **The reader's type names are its own**: `UnsignedBigInt`, `TimestampMs`, `HugeInt` -- not the SQL
  spellings a `CAST` is written with. Both `PgTypes.Oid` and `TdsTypes.Describe` key off them, and a
  name that matches nothing is published as text, silently. That is how summing an integer column
  reached SqlClient as a string: DuckDB sums into a HUGEINT, which was mapped nowhere.

## Tests

`dotnet test`. The suite is the specification — layer stacking, partitioned layouts, the write
layer, dacpac schemas, the T-SQL parser, and Npgsql + SqlClient conformance.
`TSqlTests` is the cheap one to iterate on: it needs no server and no DuckDB. `TestLake` builds a
lake in a temp directory from strings, and `Restart()` throws away everything in memory, which is
how persistence is told from luck.

The native DuckDB comes from `DuckDB.NET.Bindings.Full` via `PackageDownload` and a copy target —
downloaded, not referenced, because its managed assemblies would collide with the tool's. The copy
picks the RID folder by hand, so it has to know that the package ships macOS as one universal
binary under `osx` while every other platform has a folder per RID; `-p:DuckDbRid=osx` exercises
that path from anywhere.

A change to the merge-on-read SQL, the write path or the shims needs a test that would have failed
before it. A change to a protocol needs one in `ClientTests` (Npgsql) or `TdsTests` (SqlClient),
since those two clients are the bar. The test project turns `InvariantGlobalization` off, because
SqlClient refuses to run without ICU.

## Conventions

- Comments say **why**, never what. If a comment restates the code, delete it.
- Conventional Commits, one commit per logical unit, amend review feedback into the existing
  commit rather than stacking fixups.
- Warnings are errors; keep it that way.
