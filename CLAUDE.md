# duckpg — working notes

A PostgreSQL wire-protocol frontend for a stack of YAML/JSON/parquet layers, executing against
DuckDB. Read `README.md` first: it is the user-facing contract, and this file only adds what
someone changing the code needs.

## Where things are

| File | What it owns |
|---|---|
| `ServeCommand.cs` | The CLI: arguments, the config file, argument-over-file precedence. |
| `Config.cs` | The bound configuration. Every property here is part of the contract. |
| `Lake.cs` | One configured lake: DuckDB connection, catalog, gateway, listener. Tests use it too. |
| `Layer.cs` | Scanning a layer directory, reading a source, writing one back. YAML ↔ JSON. |
| `Catalog.cs` | The published shape: which tables exist, their columns, keys, and the view SQL. |
| `WriteLayer.cs` | The top layer: DuckDB tables loaded from files, and persisted back to them. |
| `DacpacSchema.cs` | `model.xml` out of a dacpac zip. No DacFx. |
| `Gateway.cs` | Statement translation: catalog shims, GUC no-ops, DML rewriting. `Shims` lives here. |
| `PgWire.cs`, `PgTypes.cs`, `PgServer.cs`, `PgSession.cs` | The protocol. Rarely the thing that is wrong. |
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
- **The type catalog describes the OIDs the gateway puts on the wire**, not DuckDB's own types.
  `Shims.Macros` replaces `pg_type` wholesale for that reason; DuckDB's has NULL oids and its own
  type names.
- **AOT-clean**: no reflection-based serialization, no `JsonSerializer.Serialize<object>`. The
  project ships portable but the analysers stay on, so a regression shows up as a build warning
  (and warnings are errors here).

## Things that were learned the hard way

- Npgsql closes the connection on SQLSTATE `XX000`, so `PgError.SqlStateOf` mapping DuckDB's error
  text to real codes is what makes a failed query survivable. Cancellation must map to `57014`.
- Npgsql hands back every column as `String` regardless of OID until the data goes out in binary
  format — hence `EncodeBinary`, including base-10000 `numeric`.
- `Describe('S')` must answer, or `cmd.Prepare()` fails. DuckDB cannot bind a statement with open
  parameters, so typed `NULL`s are substituted and the query run `LIMIT 0`.
- YamlDotNet's JSON emitter leaves control characters unescaped, and real exports carry tabs inside
  plain scalars. The conversion walks the node model and writes through `Utf8JsonWriter`.
- JSON type inference reads every integer as `BIGINT`; that is why a parquet layer's type wins, and
  why a dacpac is worth having.

## Tests

`dotnet test`. The suite is the specification — 54 tests across layer stacking, partitioned
layouts, the write layer, dacpac schemas and Npgsql conformance. `TestLake` builds a lake in a temp
directory from strings, and `Restart()` throws away everything in memory, which is how persistence
is told from luck.

The native DuckDB comes from `DuckDB.NET.Bindings.Full` via `PackageDownload` and a copy target —
downloaded, not referenced, because its managed assemblies would collide with the tool's.

A change to the merge-on-read SQL, the write path or the shims needs a test that would have failed
before it. A change to the protocol needs one in `ClientTests`, since Npgsql is the bar.

## Conventions

- Comments say **why**, never what. If a comment restates the code, delete it.
- Conventional Commits, one commit per logical unit, amend review feedback into the existing
  commit rather than stacking fixups.
- Warnings are errors; keep it that way.
