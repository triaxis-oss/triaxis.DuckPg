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
| `DuckDbLibrary.cs` | Finding the machine's DuckDB, and the AOT dependencies DuckDB.NET needs. |
| `DuckDbDownload.cs` | Fetching that library from DuckDB's releases, when asked and only then. |

## Invariants worth not breaking

- **A layer's format is a property of the file, not of the layer.** Anything that special-cases
  "the parquet layer" or "the seed layer" is a step backwards; the only layer with a role is the
  write layer.
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
- **A view is bound on every execution, not once when the lake is built.** DuckDB re-plans even a
  prepared statement -- measured, `Prepare()` costs 0.06 ms and changes nothing -- so every
  expression in a view definition is paid for by every query touching it, and on a wide table that
  is most of what a small query costs. Hence: no cast to the type a layer already has, no merge
  wrapper around a table only one layer carries, and `--cache` writing a merged table out once as
  parquet. A plan cache above DuckDB would cache an object that re-plans anyway.
- **A lake owns what it was built from, or nothing at all.** A factory-built lake holds its own
  container and releases it on disposal, which is what lets a caller hold one object instead of two
  with an ordering constraint; one resolved from someone else's container owns nothing of theirs.
  `DisposeAsync` waits for the listeners, `Dispose` only cancels them -- a synchronous dispose that
  blocked on a serving loop is the sync-over-async this exists to avoid.
- **Both front doors are opt-in, and a lake needs one.** `PgServer.Enabled` and `TdsServer.Enabled`
  read the same way, so a consumer speaking one protocol opens one listener. `Config.Validate` is
  what makes "neither" an error rather than a lake nothing can reach.
- **The public surface is Config, Lake, IDuckPgLakeFactory, IDuckDbInstaller, LayerFormat,
  DuckPgConfigurationException and DuckDbLibrary -- nothing else.**
  The catalog, the gateway, the two protocols and `TSql/` are internal, which is why `Lake`'s
  constructor is internal and `AddDuckPg` assembles it by hand: a public constructor would have to
  take public parameters, and that would make every part of a lake an API. `InternalsVisibleTo`
  covers the tool and the suite.
- **AOT-clean**: no reflection-based serialization, no `JsonSerializer.Serialize<object>`. The
  project ships portable but the analysers stay on, so a regression shows up as a build warning
  (and warnings are errors here).

## Things that were learned the hard way

- Npgsql closes the connection on SQLSTATE `XX000`, so `PgError.SqlStateOf` mapping DuckDB's error
  text to real codes is what makes a failed query survivable. Cancellation must map to `57014`.
- Npgsql hands back every column as `String` regardless of OID until the data goes out in binary
  format — hence `PgTypes.WriteBinary`, including base-10000 `numeric`.
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
  It is only noticed between row packets; see `TdsSession.Cancelled`.
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
- **A declared reference is a rule over the merged view, not a constraint on a table.** DuckDB
  enforces foreign keys but refuses to point one at a view, and a lake publishes views -- so a
  constraint on the write table would only see the rows this process wrote, while the row pointing
  at a parent may live in any layer. `Gateway.Referenced` builds the question as a query instead,
  and `Plan.Checks` is what a session runs *before* the plan's steps: a statement outside a
  transaction commits each step as it goes, so a rule enforced after the tombstone would be enforced
  on a row already gone. The keys are selected twice for that -- once by the check, once by the plan
  -- and only for a table something points at. A cascade is warned about rather than performed, and
  the insert side is not checked at all: both would be more promise than a stack of files can keep,
  since what a read layer holds can change between runs.
- **A `bit` is converted for arithmetic, and only where the column resolves to one.** T-SQL makes a
  `bit` an integer to multiply it; DuckDB refuses `BOOLEAN * INTEGER` outright. The cast is written
  by `TSqlWriter.Operand`, which asks `TypeOf` what the column actually is -- `TSqlContext.Tables`
  is the catalog's types, and `Scope` binds each FROM clause before the items that read it, since
  the items are written first. Guessing instead is not available: `CASE WHEN 5` is *true* to DuckDB,
  so a coercion applied to a number would answer 1 rather than fail, and casting a DECIMAL that was
  taken for a `bit` would truncate it. A reference into a derived table resolves to nothing and is
  left alone -- DuckDB's error is better than a cast nobody can justify.
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
- **A failure the client is told about is logged at warning.** A server that answers with an error
  and says nothing in its own log leaves a caller reporting a failure and nowhere to look -- which
  is what made an intermittent one look like the wire's fault rather than a statement's.
- **A write's target can be an alias its own FROM clause binds.** `DELETE FROM [s] FROM [t] AS [s]`
  is what EF Core's `ExecuteDelete` writes, and `UPDATE [o] SET … FROM [t] AS [o]` is its
  `ExecuteUpdate`; both resolve through `TSqlParser.Aliased`, and taking `s` for a table name pushed
  the real one into `USING` and left the write against nothing. The alias then has to survive into
  the gateway, since the predicate names it too -- which is what `Gateway.DeleteAlias` keeps, and why
  the scan carries an `AS`.
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
  answered by writing the rows down first: `Gateway.RewriteReturning` materialises them into
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
- **An application lock is granted by doing nothing.** `EXEC sp_getapplock` asks to be serialised
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
  cannot say what it carries is a desynchronised stream whatever the field was.
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
