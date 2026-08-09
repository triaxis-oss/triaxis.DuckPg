# Configuration

`duckpg.yaml` next to the working directory, bound through `IConfiguration` — so environment
variables and the tool's usual override files layer over it for free. Every key here has a command
line flag or is positional, and the flag wins where both are given; argument paths are relative to
the working directory, file paths to the file. A file named explicitly with `--config` must exist, so
a typo is an error rather than a silent fallback to defaults.

## Keys

| Key | Argument | Meaning |
|---|---|---|
| `listen` | `--pgwire`, `-l` | PostgreSQL listen address. Default `127.0.0.1:55432`; port 0 binds a free one. |
| `tds` | `--tds` | TDS listen address, e.g. `127.0.0.1:1433`. Off unless set. |
| `schema` | `--schema` | Schema the published views live in, and the front of every session's search path. Default `lake`. |
| `layers` | positional | Layer directories, lowest first. |
| `write` | `--write`, `-w` | Directory holding the writable top layer. |
| `writeFormat` | `--write-format` | `Parquet` (default), `Json` or `Yaml`, for tables with no file yet. |
| `writable` | `--writable` | Accept writes with no directory; they are lost on exit. |
| `materialize` | `--materialize` | Collapse the layers into real tables; a delta goes out at shutdown. |
| `store` | `--store` | DuckDB database file a materialized lake's tables live in, rather than memory. Its name becomes the database name, so it cannot be the schema's. |
| `storeMode` | `--store-mode` | `Keep` (default): the file is the state. `Spill`: only where the tables live. |
| `compress` | `--compress` | Checkpoint once the lake is built, so DuckDB compresses what it holds in memory. Off by default. |
| `sortSmallTables` | `--no-sort-small-tables` | Sort and limit a small materialized table's rows here rather than in DuckDB. On by default; the flag turns it off. |
| `checkKeys` | `--no-check-keys` | Refuse a write that would put two rows under one declared key. On by default; the flag turns off the scan behind it, though a materialized table carries the key as a real `PRIMARY KEY` and refuses it anyway. |
| `serializeTransactions` | `--serialize-transactions` | One transaction at a time; the next waits for it. |
| `deriveIds` | `--derive-ids` | Give every row its own value for a `NEWID()` default, derived from its key, rather than one value for the whole run. Off by default. |
| `defaultKey` | `--key`, `-k` | Key for tables that name none, applied only where the columns exist. |
| `dacpac` | `--dacpac` | The declared schema. Autodetected from the layers when absent. |
| `cache` | `--cache` | Directory for merged copies of multi-layer tables, as ZSTD parquet. |
| `installDuckDb` | `--install-duckdb` | Fetch the native DuckDB when a lake starts and finds none, rather than failing. `--install-duckdb-only` does that fetch and exits without serving. |
| `sessionVariables` | — | DuckDB variable → startup parameter name. |
| `columns` | — | Virtual columns added to every table lacking them. |
| `tables.<name>.key` | — | What identifies a row in this table. |
| `tables.<name>.writable` | — | Opts one table out of a writable lake, or into a read-only one. |
| `tables.<name>.columns` | — | Virtual columns for this table. |
| `tables.<name>.filter` | — | Predicate ANDed into the view. |

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
without leaking the column; a YAML layer is read through a converted copy, so `_file` is NULL there.

A top-level `columns:` block does the same for *every* table that does not already have a column of
that name — for the audit columns a compact export strips because they are bulky and say little:

```yaml
columns:
  - name: created_at
    const: 2020-01-01 00:00:00
    type: TIMESTAMP
    except: [regions, currencies]   # tables that never had it
  - name: color
    expr: NULL
    type: VARCHAR
    only: [products, categories]    # when naming the exceptions is longer
```

Faked columns are appended after the real ones, which is where an export that kept them puts them.

## Filters and session variables

A table's `filter:` is a predicate ANDed into its view, so a lake can publish less than its files
hold. It can be a constant condition, and it can also read the session: DuckDB scopes `SET VARIABLE`
per connection, and every session gets its own, so a startup parameter can be pushed into a variable
the view reads back.

```yaml
sessionVariables:
  tenant: user

tables:
  customers:
    filter: getvariable('tenant') = 'admin' OR region = getvariable('tenant')
```

`psql -U emea` then sees the EMEA customers and `psql -U admin` sees all of them, from one view
definition. It works through libpq's `PGOPTIONS="-c key=value"` too, and over TDS, keyed on the
login's user name.

**It is not a security boundary.** Both doors are trust auth: a client picks the name it connects as,
so this serves one lake to callers that each want a slice of it — it does not keep them apart.
