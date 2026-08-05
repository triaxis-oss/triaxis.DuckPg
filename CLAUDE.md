# duckpg — working notes

A PostgreSQL wire-protocol frontend for a stack of YAML/JSON/parquet layers, executing against
DuckDB. Read `README.md` first: it is the user-facing contract, and this file only adds what
someone changing the code needs.

## Where things are

| File | What it owns |
|---|---|
| `ServeCommand.cs` | The CLI: arguments, the config file, argument-over-file precedence. |
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
| `SqlText.cs` | Enough SQL scanning to find top-level keywords without a parser. |
| `DuckDbLibrary.cs` | Finding the machine's DuckDB, and the AOT dependencies DuckDB.NET needs. |

## Invariants worth not breaking

- **A layer's format is a property of the file, not of the layer.** Anything that special-cases
  "the parquet layer" or "the seed layer" is a step backwards; the only layer with a role is the
  write layer.
- **Layer sequence numbers decide everything.** Read layers are 0..n-1 in configured order, the
  write layer is n. `QUALIFY … ORDER BY _seq DESC` is what makes a higher layer shadow a lower one,
  and a tombstone only hides rows with `_seq < writeSeq`.
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
  half of that.
- **The type catalog describes the OIDs the gateway puts on the wire**, not DuckDB's own types.
  `Shims.Macros` replaces `pg_type` wholesale for that reason; DuckDB's has NULL oids and its own
  type names.
- **The dialect is translated on the tree, never on the text.** `TSql/` parses T-SQL into an AST and
  renders DuckDB SQL from it. A regex "fix" for a dialect difference belongs in the renderer as a
  case, not in a string replacement — this is why `'a' + b` concatenates and `1 + 2` adds.
- **A statement the parser does not cover is refused**, with the position. Passing unknown text
  through to DuckDB moves the failure somewhere harder to read.
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
