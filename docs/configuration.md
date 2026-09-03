# Configuration

A YAML file named with `--config`, bound through `IConfiguration` — so the environment and this
machine's own files layer under it. Every key here has a command line flag or is positional, and the
flag wins where both are given; argument paths are relative to the working directory, file paths to
the file.

**Nothing in the working directory is read unless it is named.** There is no default file: a tool
that helped itself to a
`duckpg.yaml` from whatever directory it was started in would be serving a lake nobody pointed it at,
and would hand `bake` the file describing the lake being *served* rather than the one being written
out. A file that is named has to exist, so a typo is an error rather than a silent fall back to
defaults.

## Where a value comes from

Most specific last, which is what wins:

| Source | What it is for |
|---|---|
| `duckpg/config.yaml` under the machine's configuration folder | How this machine runs duckpg, for everyone on it. |
| `duckpg/config.yaml` under the user's | The same, for one account. |
| `DUCKPG_`-prefixed environment variables | One value, for one run, without a file to edit. |
| The file named with `--config` | The lake being served, named deliberately — which is why it is above the ambient ones. |
| Arguments | The last word, always. |

An environment variable is the key with `DUCKPG_` in front and `__` for every `:`, so
`DUCKPG_schema=staging`, `DUCKPG_layers__0=/srv/common` and `DUCKPG_tables__orders__key=order_id`.
`layers` takes the scalar form too, one directory being the common case.

Only the file named with `--config` has to exist, and none of the others is ever written by the
tool. The `config.yaml` probes sit under `%ProgramData%` and `%AppData%` on Windows, and under
`/usr/share` and `~/.config` (with `~/.local/share` read as the user's too) elsewhere. Those four
are the whole list: nothing beside the executable is read, and nothing in the working directory.

## Keys

| Key | Argument | Meaning |
|---|---|---|
| `listen` | `--pgwire`, `-l` | PostgreSQL listen address. Default `127.0.0.1:55432`; port 0 binds a free one. |
| `tds` | `--tds` | TDS listen address, e.g. `127.0.0.1:1433`. Off unless set. |
| `tdsPacketSize` | `--tds-packet-size` | Bytes a TDS packet carries, 512 to 32767. Default 32767: the server's answer settles it, whatever the client asked for. |
| `schema` | `--schema` | Schema the published views live in, and the front of every session's search path. Default `lake`. |
| `database` | `--database` | Database name a client sees itself connected to, in the connection strings a lake hands out and in what a TDS session reports back. Defaults to the schema's name. |
| `layers` | positional | Layer directories, lowest first. |
| `base` | `--base` | A baked database served instead of layers, copied on the way up and never written to. |
| `write` | `--write`, `-w` | Directory holding the writable top layer. |
| `writeFormat` | `--write-format` | `Parquet` (default), `Json` or `Yaml`, for tables with no file yet. |
| `writable` | `--writable` | Accept writes with no directory; they are lost on exit. |
| `materialize` | `--materialize` | Collapse the layers into real tables; a delta goes out at shutdown. |
| `lazy` | `--lazy` | Collapse a table when a statement first names it rather than every table at startup. Needs `materialize`. |
| `inline` | `--inline` | Publish no views: every statement carries the merge for the tables it names. Halves a start on a lake of many tables, and needs `tds` alone — nothing in DuckDB's catalog says the tables are there. |
| `store` | `--store` | DuckDB database file a materialized lake's tables live in, rather than memory. Its name becomes the database name, so it cannot be the schema's. |
| `storeMode` | `--store-mode` | `Keep` (default): the file is the state. `Spill`: only where the tables live. |
| `compress` | `--compress` | Checkpoint once the lake is built, so DuckDB compresses what it holds in memory. Off by default. |
| `threads` | `--threads` | Threads DuckDB serves with. Default: DuckDB's own, one per core. The catalog is built on one thread whatever this says, except on a materialized lake, whose build is the collapse. |
| `sortSmallTables` | `--no-sort-small-tables` | Sort and limit a small materialized table's rows here rather than in DuckDB. On by default; the flag turns it off. |
| `checkKeys` | `--no-check-keys` | Refuse a write that would put two rows under one declared key. On by default; the flag turns off the scan behind it, though a materialized table carries the key as a real `PRIMARY KEY` and refuses it anyway. |
| `serializeTransactions` | `--serialize-transactions` | One transaction at a time; the next waits for it. |
| `deriveIds` | `--derive-ids` | Give every row its own value for a `NEWID()` default, derived from its key, rather than one value for the whole run. Off by default. |
| `ignore` | `--ignore` | Files in a layer that are not tables, as globs over the path relative to the layer: `_*.yaml`, `reports/**`. A pattern without a `/` matches a name in any directory; one that is a path (`/data/a/*.yaml`, or `./a/*.yaml` from the file) is held against the whole path, so it names one layer's files and not another's. |
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

## The bake subcommand

`duckpg bake … --out <dir>` builds the same lake and writes it out as parquet instead of serving it,
so it reads the same keys and the same arguments — `layers`, `write`, `defaultKey`, `dacpac`,
`installDuckDb` and the per-table `key` and `columns` blocks. `--out` is its own and is required.
It has flags for those keys and for nothing else: serving's own options belong to `duckpg`, and one
typed in front of the verb is refused rather than bound to the wrong command.

`--format` says which of the two a bake writes: `Parquet` for a directory of one file a table, read
back as an ordinary layer, or `Database` for the whole lake as a DuckDB file. Unset it is taken from
the name — `.duckdb` is a database and anything else a directory — which is a default and not the
mechanism, so a caller that names one gets it whatever the file is called. `--block-size` is what a
database is created with — small, because a block is allocated whole and a lake of many small tables
is mostly blocks. Serving one takes `--base` and nothing else: no `layers`, no `defaultKey`, no
`dacpac`, since the file carries all three. Layers under a base are refused, as is a file that is not
a bake, one baked into another schema, and one stamped with a version this duckpg does not speak.

Usually a bake needs no file at all, since what it merges by is `--key` and a dacpac. Name one with
`-c` for a lake whose shape is in `tables:` blocks a flag cannot carry, and name the file describing
the lake being *baked* — the one describing where the result is served from is a different lake, and
it is the reason neither command reads a file it was not given.

No door is opened, so `listen` and `tds` say nothing, and `cache` only decides how the rows are
reached on the way out. `write` is read because a write layer is the only layer whose files do not
say everything it holds — its deletes live in a `.deleted/` sidecar the layer scan skips — so a bake
told about it applies those and a bake handed the same directory as a plain layer would bake the
hidden rows back in. Two things are refused rather than baked: a table with a `filter:`, which is
answered per session and cannot be a file every session reads, and `materialize` (and the `store` that
needs it), since collapsing the layers is what a bake already does.
See [performance](performance.md#baking-the-layers-once).

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
