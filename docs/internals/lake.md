# The lake: layers, the write path and the process it runs in

Working notes for changing the code. What a lake *does* is [layers.md](../layers.md), [performance.md](../performance.md) and [embedding.md](../embedding.md); this is why it does it that way.

## Layers and files

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
- **`hive_partitioning` is always passed explicitly.** Left to itself DuckDB turns it on and
  invents a column from any `k=v` directory above the lake. `LayerSource.Partitions` records the
  keys the layer actually declares, and only those survive into the published columns — it is what
  separates a partition the lake owns from one it merely sits under.
- **A partition column is part of the key.** `db=one/orders.parquet` and `db=two/orders.parquet`
  both have a row 1; without `db` in the key the `QUALIFY` would drop one of them. `KeyFor`
  appends partition columns to whatever key was found, and to nothing when no key was found.
- YamlDotNet's JSON emitter leaves control characters unescaped, and real exports carry tabs inside
  plain scalars. The conversion walks the node model and writes through `Utf8JsonWriter`.
- JSON type inference reads every integer as `BIGINT`; that is why a parquet layer's type wins, and
  why a dacpac is worth having.

## The write path

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
- **The write layer holds effective state, not a log.** A deleted row leaves the write table and
  gains a tombstone; a re-inserted one comes back with no tombstone bookkeeping. There is no
  per-row sequence number, and adding one back means re-deriving what shadows what.
- **Persistence happens after DuckDB commits**, never before — see `PgSession.Persist`. A write
  inside a transaction is remembered and written at `COMMIT`, dropped at `ROLLBACK`.

## Materializing, and the store

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
- **`Config.Lazy` defers the collapse and nothing else, and what stands under a deferred table is
  the merge rather than an empty table waiting to be filled.** That is the whole of why it is safe:
  the tables a statement is about are found by reading the *text* for names the catalog knows --
  `SqlText.Identifiers` over `Gateway.Translate`'s input, before anything is translated, since what
  stands under a name decides whether a write to it is rewritten into four statements or sent as one
  -- and a scan of text is a thing that can miss. A name it misses is answered by the merge the lake
  was serving all along: the same rows, at the layered price. An empty table would answer with no
  rows at all, and nothing about that answer would look like a miss. The scan costs a pass over the
  statement per statement, so `Catalog.Deferring` is asked first and goes false for good once
  everything has been named -- a lake that was asked about all of its tables costs an eager one.
  A declared view or macro is followed rather than left merged, `Catalog.Reads` recording what each
  one's own definition names, because a statement through a view names the view and nothing under
  it. Measured at 5.57 s to serve against 3.32, and ~7 ms a table on the statement that first names
  one. [performance](performance.md#paying-the-merge-once)
- **What moves with it is when the layers are found to be wrong.** A stack that puts two rows under
  one declared key is refused by `ALTER TABLE … ADD PRIMARY KEY`, which an eager lake reaches at
  startup and a lazy one reaches at the statement that first names the table. So a failed collapse
  leaves the table *deferred*: the next statement naming it fails the same way rather than the lake
  serving a table whose key quietly went missing -- which, since a materialized lake leaves the key
  to DuckDB, is a lake that would then accept a duplicate. `Catalog.Flush` skips what is still
  deferred for the same reason there is nothing to skip: a write names its table, so a table nothing
  named was never written to, and its write layer still holds -- in the files, untouched -- whatever
  the run before this one left there. Measuring a delta for it would measure those writes away.
- **A store is the one thing that carries a deferred table's view into the next run**, which is why
  `Catalog.Standing` asks once what each name is already holding -- the shape it has and whether
  DuckDB is keeping its key with it, which is what a stored start would otherwise ask table by table
  ([performance](performance.md#what-a-start-asks-the-catalog)) and `Catalog.Materialize` drops a
  view before it creates the table. DuckDB refuses to drop a table as a view or a view as a table,
  and every combination is reachable: `Keep` finds its own tables and serves them (nothing about
  those is deferred -- the layers are not read for them either way), `Spill` finds the last run's
  tables and has to make way for the views, and a lake switching between `--lazy` and plain
  `--materialize` over the same file finds whichever the other mode left. Asked in one question
  rather than per table, and only where there is a file at all, for the reason `Shapes` is.
- **What a store *means* is one answer, and `Catalog.Keeping` is where it is given.** The file is
  either the lake's state or only somewhere for its tables to live, and the two halves -- whether the
  layers are read for a table the file already carries, and whether a delta is written at shutdown --
  have to agree, or the run either loses its writes or writes them down twice. `StoreMode.Keep` is
  both halves on and `StoreMode.Spill` is both off, which is why one property answers both gates
  rather than each asking `config.Store` for itself. `Spill` exists for memory and nothing else: it
  behaves in every other way like an in-memory materialized lake, so `SpillTests` restarts twice for
  the same reason `DeltaTests` does. Nothing in a DuckDB file says which mode wrote it, so the two
  cannot share a path -- and there is no check that could tell.
- **What a materialized lake holds is uncompressed until something checkpoints it**, and
  `Config.Compress` is the only thing that does -- one `CHECKPOINT` at the end of `Catalog.Build`,
  worth 4.4x the memory and a wager on the read.
  [performance](performance.md#compressing-what-is-held)
- **DuckDB.NET duplicates an in-memory connection and no other.** `Duplicate()` throws
  "Duplication of the connection is only supported for in-memory connections", which is why
  `DuckDbSession.Of` opens the file again for a stored lake -- the driver holds one instance per
  connection string, so both reach the same database. Nothing says this in a stack trace: the
  sessions simply never get a connection, and every client times out in the pre-login handshake
  while the listener sits there accepting.

## Baking

- **Using a baked layer is semantically identical to using the layers it was baked from.** Every
  other note here is a consequence, and a change that cannot hold this one is a change that has to
  refuse instead. It is also the test the suite is written to: `BakeTests` bakes a lake, points a
  second one at the output, and asks the same questions of both.
- **A bake is the catalog with nothing in front of it, and that is the whole design.** `Bake` builds
  the same `Catalog` over the same `Config` and then does one `COPY (SELECT … FROM <view>)` a table.
  There is no second merge and no bake-specific SQL: whatever the view says a client would read is
  what lands in the file, so a change to the merge cannot leave the bake behind. It is registered
  through `AddDuckPgBake`, which is `AddDuckPgLake` plus `Bake` -- the servers and the gateway are
  registered and never resolved, which costs a factory nobody calls and means a catalog that grows a
  dependency does not have to be remembered in two places.
- **It is not a lake, so it does not need a door.** `Config.Validate` is what makes "no front door"
  an error, and everything else it asks is a bake's question too -- hence the split into
  `ValidateShape`, which the bake calls and `Validate` wraps. `Config.ValidateSessionless` is the
  other half of that split: a `filter:` and a `getvariable()` column were already refused for
  `materialize`, and rows written to a file every session reads are the same problem with the same
  answer.
- **`Config.Inside` is why the output cannot be a layer**, and it answers "is this directory, or one
  above it, a layer" rather than the strictly-below question `--cache` used to ask -- baking into the
  layer being read is the worse version of the same mistake and was the one case the old comparison
  let through.
- **The write layer is named rather than listed, because it is the one layer whose files do not say
  everything it holds.** Its deletes are keys in a `.deleted/` sidecar, and `Layer.Entries` skips
  dot-directories on purpose -- so a write directory handed to a bake as an ordinary layer bakes the
  rows it hides back into the lake and loses the deletes silently. There is no way to bake a lake
  that has a write layer without knowing which directory it is; told, `Catalog.Baked` folds it in
  through the same `Promoted`/`Tombstoned` state the served view uses, which is also what makes a
  bake the way an overgrown write layer is flattened back into the files below it. It is the same
  argument as `--key`: what a bake may not know, it may not merge.
- **Virtual columns and declared defaults are left out of what is written.** A baked directory stands
  in for the layers and not for the `duckpg.yaml` above them, so the bake copies `table.Columns` and
  not `table.Virtuals`. Baking those in would be wrong twice over: a table-level `columns:` entry is
  projected unconditionally, so the next run would emit the column a second time and DuckDB would
  refuse the view outright, and an `expr` over `_file` would have been frozen against a file that is
  no longer the one being read. A default is the same bargain `Cache` already makes -- `(getdate())`
  written into a file that outlives the run answers every later run with the moment this one started
  -- which is why `Catalog.Baked` passes `defaults: KeyedByDefault(table)` rather than `Deferrable`:
  a bake defers more than a copy can. A copy is read *through* a wrapper that recomputes the virtual
  columns from it, so a gap where a default should be reaches them; a bake writes no virtual column
  at all, and the run reading the file defaults in the branch before the projection sees it. What
  neither can defer is a default on a key column, which the `QUALIFY` reads -- hence the predicate
  split out of `Deferrable` rather than a second copy of it. `Merged`'s underlay branch had the
  `COALESCE` hardcoded, which made `defaults: false` a lie whenever a cache was configured.
- **Serving is the root command, so its argument and options stand in front of every verb.**
  `[Command]` with no name makes `ServeCommand` the root, and older System.CommandLine bound a token
  typed before a subcommand to the parent without an error -- `duckpg ./common bake ./tenant` baked
  half a lake and `duckpg --materialize bake` baked without it. Nothing declarative prevents that and
  `Configure` runs after the parse, so it cannot be answered here at all; triaxis.CommandLine 2.6.0
  answers it in the parser, which is why this takes that version. A recursive option is the exception
  and has to be: `-v` lands on the subcommand after the verb and on the root before it, and means the
  same either way.
- **`materialize` is refused rather than tolerated.** It produced the same rows -- `Catalog.Baked`
  would have had to un-materialize the table to build its merge anyway -- but a materialized table
  holds every declared default already stamped, which is exactly what a bake must not write. `store`
  needs `materialize`, so one refusal answers for both.
- **A partitioned layer is written back partitioned**, with `PARTITION_BY` over the columns
  `LayerSource.Partitions` contributed. Flat, `db` would be an ordinary column and `KeyFor` would
  stop adding it to the key, so one database's row 1 would shadow another's -- the hazard
  `PartitionTests` exists for, reached by a different road. DuckDB writes the value into the
  directory name and leaves it out of the file, which is exactly what the hive scan expects, so the
  round trip is the layout `Layer.Entries` already reads.
- **A COPY answers with the rows it wrote**, but only through `ExecuteNonQuery` -- `ExecuteScalar`
  returns null for it, and `Convert.ToInt64(null)` is a zero that looks like an empty table. Counting
  the view instead would be the whole merge a second time.
- **Nothing in the output is deleted.** A stray from an earlier bake is warned about by name and left
  where it is: the directory is the caller's, and a layer directory is read for whatever is in it --
  which is also why the warning is worth the enumeration.

## Baking a database, and serving one

- **A baked database is a materialized lake and nothing new**, which is why `Bake.Materialized`
  builds it as exactly that -- `Materialize` into the named file, `StoreMode.Keep` -- on a
  `Config.Copy`, since the configuration handed in describes a lake served from layers and is not the
  bake's to rewrite. The file is deleted first or the kept-store rule opens what an earlier bake left
  and writes nothing. `Config.Collapsed` is what the rest of the catalog asks instead of
  `config.Materialize`, because a base is one somebody else already collapsed.
- **The block size is most of what the mode is worth, and it cannot be set afterwards.** A block is
  allocated whole, so 300 tables holding 1500 rows between them came to 159 MB at DuckDB's 256 KB and
  18.7 MB at 16 KB -- 9 ms to copy against 172, paid by every run. It is fixed when the file is
  created, so `AddDuckPgBake` registers the connection *before* `AddDuckPgLake` does, `TryAdd` leaving
  the first standing.
- **64 KB is the middle of the range, and both ends of it cost something.** DuckDB takes 16 KB to
  256 KB and refuses either side. At the top those 300 tables are mostly padding; at the bottom a
  compressed segment no longer fits its block, and DuckDB's analyze pass takes the scheme that fits
  or none -- so a table big enough to want compression gets none, which is slower *and* bigger: 5M
  rows of five columns came to 138.9 MB and 3.0 s at 16 KB against 76.4 MB and 2.3 s at 64. A lake
  that is all of one shape says `--block-size` instead.
- **A factory, though, and never the connection itself.** The container closes what it built and
  leaves an instance handed to it alone, so registering one wrote the file and then held it open for
  the life of the process. What that costs is not the handle: the driver keeps a database per
  connection string, so the next bake over the same target deleted the file, was handed that same
  still-open database back, and wrote into an inode nothing could reach -- and on macOS the file it
  had just written could not be read at all while the process that wrote it was still holding it.
- **Attaching the base doubles the catalog, and DuckDB says nothing about it.** The base holds the
  same schema name and the same table names as the copy being served, and `information_schema` and
  the `duckdb_*` functions span every attached database -- so every table was found twice and every
  column arrived twice with it, which surfaced three layers away as a write branch refusing to be
  created for a duplicate column. Everything in `Adopt` names `current_database()`.
- **Asked per table, the catalog is quadratic.** `information_schema.columns` is answered by scanning
  the whole catalog, so 300 tables asking about their own columns cost 4.3 s against 17 ms for one
  query naming none of them -- which had `--base` slower than the layers it replaces. `Shapes` asks
  once. `Counted` is the same shape of mistake smaller: 300 `count(*)` scans for 126 ms, replaced by
  `duckdb_tables().estimated_size`, which is safe because `rows` is a bound and one that is too low
  costs a sort rather than a wrong answer.
- **A base still has a write layer over it.** `Adopt` scans the write directory like `Sources` does,
  since without a `WriteSource` a table carries only the keys that were deleted -- the tombstone
  sidecar is found either way, so a delete persisted and an update silently did not. `Apply` then
  deletes what a tombstone names and replaces what a written row shadows, because a materialized
  table has nothing to shadow with, and re-creates the identity sequences past whatever the write
  layer took.
- **`Catalog.Baseline` is the attached base rather than the `base` schema**, which is the only change
  the delta needed: it was always "what the layers said before anything was written", and here
  somebody else wrote that down. `BakedBase` owns the copy and deletes only the scratch one -- a store
  was named by whoever wanted it to outlive the run -- and is registered before the connection so it
  is disposed after it.

## Two threads and one DuckDB

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

## Starting, stopping, and what a lake owns

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
- **A lake that has stopped is a lake nobody is connected to.** Cancelling the accept loops stops
  new clients arriving and does nothing at all to the ones already there: a session blocks on its
  socket on a thread of its own, holding a DuckDB connection onto the lake's database, and DuckDB
  keeps a database alive for as long as one connection onto it is open. So a lake stopped with a
  client still on the wire went on answering it -- and a host starting one lake per tenant kept
  every lake it ever started, files, memory and all, with the last reference to it long gone.
  `Doorway` is the connections as well as the listener, and `Close` shuts the sockets so the threads
  end; `DrainAsync` waits for them, which is what puts the drain before `Gateway.Flush` -- a client
  that walked away mid-transaction holds the turn the flush needs. The socket is *shut down* rather
  than disposed, because a session reads the end of its socket as the end of its client, which every
  protocol here already answers; disposing the stream under a thread inside it is the same
  connection ending as whichever of half a dozen exceptions the timing lands on -- one of them
  `NotSupportedException` from a `BufferedStream` over a closed `NetworkStream`, which is not an
  exception any session would think to catch and which takes the process with it from a thread.
  What the suite found out about this is that a stopped lake now breaks a *pooled* connection the way
  a restarted PostgreSQL does: Npgsql keys a pool by the connection string and hands one out without
  testing it, so two fixtures landing on the same port -- which is what `Listen = "127.0.0.1:0"`
  invites -- had the second asking its lake over the first one's socket. It used to work, because the
  first lake was still there answering. `TestLake` puts its own directory in `Application Name`, and
  the tests that build a lake by hand say `Pooling=false`. SqlClient tests the connection it hands
  out, so the TDS side never had it.
- **A lake owns what it was built from, or nothing at all.** A factory-built lake holds its own
  container and releases it on disposal, which is what lets a caller hold one object instead of two
  with an ordering constraint; one resolved from someone else's container owns nothing of theirs.
  `DisposeAsync` waits for the listeners and for the clients, `Dispose` only closes them -- a
  synchronous dispose that blocked on a serving loop is the sync-over-async this exists to avoid.
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

## The native library, and saying what went wrong

- Loading a half-written native library aborts the process with SIGBUS rather than failing, so
  nothing is left to report it -- which is why `DuckDbLibrary` checks a candidate's size before
  trying it, and why a download is unpacked into a staging directory beside its target and renamed
  onto it -- beside it, so the rename stays on one filesystem and cannot half happen.
- A `CommandErrorException` whose template has more holes than arguments is logged as nothing at
  all -- the exit code arrives, the message does not. Count them, or build the text and pass it as
  one argument.

- **A failure the client is told about is logged at warning.** A server that answers with an error
  and says nothing in its own log leaves a caller reporting a failure and nowhere to look -- which
  is what made an intermittent one look like the wire's fault rather than a statement's.
