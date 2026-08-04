# duckpg

Point any PostgreSQL tool at a stack of YAML, JSON and parquet files — no driver, no server, no
Spark — and add columns, access rules and writes the files never had.

duckpg speaks the PostgreSQL v3 wire protocol and executes against DuckDB. Each table is published
as a view over its layers, so a table can come from a shared YAML seed, a tenant's JSON overrides
and a parquet export at once, with the topmost layer holding a row winning. The top layer accepts
writes, and what a client writes is an ordinary layer file another instance can read.

```
                 ┌──────────────────────────────┐
  psql, Npgsql   │  local/      write layer     │  INSERT / UPDATE / DELETE land here
  ──────────────►│  tenant/     JSON, parquet   │  a row shadows the same key below
  wire protocol  │  common/     YAML seed       │
                 └──────────────────────────────┘
```

## Install

```shell
dotnet tool install -g triaxis.Tools.DuckPg
```

The tool links against the DuckDB installed on the machine rather than bundling one:
`brew install duckdb`, `apt install libduckdb-dev`, or point `DUCKDB_LIBRARY` at the library
directly. Homebrew's prefixes (`$HOMEBREW_PREFIX`, `/opt/homebrew`, `/usr/local`, linuxbrew) and
the `opt/duckdb/lib` keg beneath each are probed on their own, because neither macOS nor Linux
looks there by default.

Requires .NET 10.

## Serving a lake

Nothing needs a configuration file:

```shell
duckpg ./common ./tenant --write ./local --key id
psql -h 127.0.0.1 -p 55432 -U admin -d lake
```

Positional arguments are the layer directories, lowest first. `--listen`, `--write`,
`--write-format`, `--writable`, `--schema`, `--key` (repeatable), `--dacpac` and `--config` each
override the file when both are given; argument paths are relative to the working directory, file
paths to the file. A file named explicitly with `--config` must exist, so a typo is an error rather
than a silent fallback to defaults.

`-v` traces each translated statement with its DuckDB execution time and row count (execution and
row streaming are timed apart, since DuckDB returns before the rows are pulled); `-vv` adds the
wire messages in both directions. Ctrl+C and SIGTERM shut down cooperatively.

See [`example/`](example) for a lake with all three formats, a `db=…` partitioned layer, a write
layer, virtual columns and per-user row filtering — `cd example && duckpg`.

## Layers

A layer is a directory. What it holds decides how each table is read:

| In the directory | Published as |
|---|---|
| `orders.yaml`, `orders.yml` | table `orders`, materialised through JSON for type inference |
| `orders.json` | table `orders`, `read_json_auto` |
| `orders.parquet` | table `orders`, scanned in place |
| `orders/**/*.parquet` | table `orders`, one table over every file below, `union_by_name` |
| `orders/dt=…/*.parquet` | the same, with the partition keys as columns |
| `db=…/orders.parquet` | table `orders` across every `db=`, with `db` as a column |
| `.anything/` | ignored — dot-directories are the tool's own |

A directory named `k=v` is a partition *above* the tables rather than a table: the table is the
file below it, so `db=one/orders.parquet` and `db=two/orders.parquet` are one `orders` with a `db`
column — one view across many databases. Partitions nest (`db=one/year=2026/orders.parquet`), and
any other directory is still a table of its own files, so both layouts can live in one layer.

**A partition column joins the key**, whatever the key would otherwise be. Rows are only unique
within a partition — every database has its own row 1 — so without it one would shadow the other
and a database would quietly lose rows. `--key order_id` over a `db=…` lake means a row is
identified by `(order_id, db)`, which is also what a write to such a table has to supply.

Layers stack in the order given. Where a key is declared the topmost layer holding a row wins
(`QUALIFY row_number() OVER (PARTITION BY key ORDER BY _seq DESC) = 1`); without a key there is no
way to tell rows apart, so the layers simply concatenate. Columns are the union of the layers, in
the topmost layer's order, and each layer is cast to the published type — where a parquet layer has
the column its type wins, because a parquet file carries a real schema while YAML and JSON types
are inferred from the values.

Table names are matched case-insensitively, so an export that disagrees with itself about
capitalisation still lands on one table. Two files of different formats claiming one table is a
mistake rather than a merge: the first is used and the other is named in a warning.

`hive_partitioning` is always stated explicitly, because DuckDB's default is to turn it on — and it
then derives a column from *any* `k=v` directory above the files, including one the lake merely
happens to live under. Only the partitions the layer itself declares become columns, so a lake that
sits under `/data/tenant=acme/` does not grow a stray `tenant` column.

## The write layer

`--write ./local` (or `write:` in the file) makes one directory the topmost layer and the only one
that accepts writes:

- `INSERT` appends to the write layer.
- `DELETE` removes the row from the write layer and records its key in `local/.deleted/<table>`,
  which hides that row in every layer below.
- `UPDATE` is the two together: the new rows are computed first, then the old keys tombstoned, then
  the new rows appended.

A write is persisted as soon as DuckDB commits it — immediately for a bare statement, at `COMMIT`
for one inside a transaction, and never for one that is rolled back. Restarting the gateway reads
the same files back, so a written row survives without a database file anywhere.

A table is persisted in the format it already has a file in, so a hand-written `notes.yaml` stays
YAML rather than turning into parquet the first time someone writes to it. `--write-format` decides
what a table with no file yet gets; the default is parquet. An emptied table takes its file with
it.

Writes need identity: `UPDATE` and `DELETE` require a key, from the table's own `key:`, from
`--key`, or from a dacpac's primary key. `INSERT` does not — a new row needs no identity to be told
from an old one. Virtual columns reject writes.

`--writable` accepts writes with no directory at all; they live in memory and are lost on exit,
which is what a test wants.

`CALL duckpg_reload()` rebuilds the catalog from the filesystem, picking up files that appeared
since startup.

## Columns the files do not contain

Each table is published as a generated view, so an extra column is just an extra projection:

```yaml
tables:
  orders:
    columns:
      - name: currency
        const: EUR
      - name: source_file
        expr: coalesce(regexp_extract("_file", '[^/]+$'), 'seed')
      - name: eur_cents
        expr: amount * 100
        type: BIGINT
```

Constants fold away at planning time, so this costs nothing. A parquet or JSON scan is always given
`filename=true` and the result exposed internally as `_file`, so expressions can use file provenance
without leaking the column. A YAML layer is read through a converted copy, so it has no file to
name and `_file` is NULL there.

A top-level `columns:` block does the same for *every* table that does not already have a column of
that name — for the audit columns a compact export strips because they are bulky and say little:

```yaml
columns:
  - name: created_at
    const: 2020-01-01 00:00:00
    type: TIMESTAMP
    except: [regions, currencies]   # tables that never had it
  - name: colour
    expr: NULL
    type: VARCHAR
    only: [products, categories]    # when naming the exceptions is longer
```

Faked columns are appended after the real ones, which is where an export that kept them puts them.

## The schema, from a dacpac

`dacpac: app.dacpac` (or `--dacpac`) makes the declared schema authoritative: column names, order
and types, plus the primary key. A dacpac is a zip holding `model.xml`, so this is `ZipArchive` and
`XDocument` — DacFx never enters into it. Columns no layer carries are published as typed `NULL`s,
layer columns are cast to the declared type rather than to whatever inference guessed, and
`SqlPrimaryKeyConstraint` supplies the key so `--key` becomes unnecessary.

A declared table no layer carries is published as well — empty, with its declared shape — so the
catalog is the schema rather than a reflection of which files turned up. Such a table is writable
like any other: an `INSERT` lands in the write layer and reads back.

**Autodetected when not given**: a single `.dacpac` sitting in a layer directory is used on its
own. Several means none is assumed, and the tool says so — name one with `--dacpac`.

## Row-level security, for free

DuckDB scopes `SET VARIABLE` per connection, and every session gets its own connection, so startup
parameters can be pushed into variables and read back by the view:

```yaml
sessionVariables:
  tenant: user

tables:
  customers:
    filter: getvariable('tenant') = 'admin' OR region = getvariable('tenant')
```

`psql -U emea` then sees the EMEA customers and `psql -U admin` sees all of them, from one view
definition. It also works through libpq's `PGOPTIONS="-c key=value"`.

## Configuration

`duckpg.yaml` next to the working directory, bound through `IConfiguration` — so environment
variables and the tool's usual override files layer over it for free.

| Key | Argument | Meaning |
|---|---|---|
| `listen` | `--listen`, `-l` | Listen address. Default `127.0.0.1:55432`; port 0 binds a free one. |
| `schema` | `--schema` | Schema the published views live in. Default `lake`. |
| `layers` | positional | Layer directories, lowest first. |
| `write` | `--write`, `-w` | Directory holding the writable top layer. |
| `writeFormat` | `--write-format` | `Parquet` (default), `Json` or `Yaml`, for tables with no file yet. |
| `writable` | `--writable` | Accept writes with no directory; they are lost on exit. |
| `defaultKey` | `--key`, `-k` | Key for tables that name none, applied only where the columns exist. |
| `dacpac` | `--dacpac` | The declared schema. Autodetected from the layers when absent. |
| `sessionVariables` | — | DuckDB variable → startup parameter name. |
| `columns` | — | Virtual columns added to every table lacking them. |
| `tables.<name>.key` | — | What identifies a row in this table. |
| `tables.<name>.writable` | — | Opts one table out of a writable lake, or into a read-only one. |
| `tables.<name>.columns` | — | Virtual columns for this table. |
| `tables.<name>.filter` | — | Predicate ANDed into the view. |

## What works

| | |
|---|---|
| Startup, trust auth, SSL/GSS refusal, `ParameterStatus`, `BackendKeyData` | psql and Npgsql connect |
| Simple query protocol, multi-statement, transaction status | |
| Extended protocol: Parse/Bind/Describe/Execute/Sync, portals, `maxRows` suspension | |
| Explicit `Prepare()`, transactions, `NpgsqlBatch`, connection pooling | |
| PG-accurate SQLSTATEs, so errors are recoverable and cancellation is `OperationCanceledException` | |
| Text **and binary** result formats, binary parameters incl. PG's base-10000 `numeric` | |
| Cancellation over the second connection → `duckdb_interrupt` | |
| psql introspection: `\dv`, `\d` | |

Type mapping covers the scalar types; `LIST`, `STRUCT` and `MAP` are surfaced as text holding their
JSON rendering, which is what a PG client can actually consume.

Npgsql is the client held to a conformance bar, and `ClientTests` is that bar: the open-ended cost
of a PostgreSQL frontend is emulating `pg_catalog` well enough for each client, and every client
fails differently. psql works too, but it is not what the shims are maintained for.

## Known limitations

- Trust auth only. No TLS, no SCRAM — bind to localhost.
- Statement description runs the query `LIMIT 0` to learn its shape, so describing is not free and
  a statement that cannot be wrapped in a subquery falls back to `NoData`.
- DML rewriting is textual — it handles `UPDATE t SET a = …, b = … WHERE …` and `DELETE FROM t
  WHERE …` on a single table, not `FROM`/`USING` clauses, CTEs or subqueries in the target.
- Statements are re-planned per execution; no plan cache.
- The `COPY` protocol (`\copy`, `NpgsqlBinaryImporter`) is not implemented.
- The catalog is built from the filesystem at startup and on `CALL duckpg_reload()`; no watcher.
- Nothing compacts the lower layers: the write layer grows until someone rewrites the files below.
- Two instances writing the same layer directory will overwrite each other. One writer per
  directory.
- Only Npgsql is held to a conformance bar; anything else will need its own round of catalog shims.

## Development

```shell
dotnet build
dotnet test          # 54 tests: layers, the write layer, dacpac schemas, Npgsql conformance
```

The tests carry their own DuckDB — the native library is pulled out of `DuckDB.NET.Bindings.Full`
by a build target and dropped next to the test binary — so a clean checkout and a clean CI runner
both run them with nothing installed.

`dotnet pack -c Release` produces the tool package.

## Licence

MIT.
