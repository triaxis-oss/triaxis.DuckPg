# duckpg

Point any PostgreSQL or SQL Server client at a stack of YAML, JSON and parquet files — no driver, no
server, no Spark — and add columns, filters and writes the files never had.

duckpg speaks the PostgreSQL v3 wire protocol and, on a second port, the TDS protocol
`Microsoft.Data.SqlClient` speaks; both execute against DuckDB. Each table is published as a view
over its layers, so one table can come from a shared YAML seed, a tenant's JSON overrides and a
parquet export at once, with the topmost layer holding a row winning. The top layer accepts writes,
and what a client writes is an ordinary layer file another instance can read.

```
  psql, Npgsql     ┌──────────────────────────────┐
  ────────────────►│  local/      write layer     │  INSERT / UPDATE / DELETE land here
  pg wire protocol │  tenant/     JSON, parquet   │  a row shadows the same key below
  ────────────────►│  common/     YAML seed       │
  SqlClient, TDS   └──────────────────────────────┘
```

## Install

```shell
dotnet tool install -g triaxis.DuckPg.Cli
```

Requires .NET 10 and a native DuckDB, which the tool links against rather than bundling:
`brew install duckdb`, `apt install libduckdb-dev`, or `DUCKDB_LIBRARY` pointing at the library. On a
machine with neither, `duckpg --install-duckdb` fetches the right one — once, and never unasked. With
no library at all, the error says where it looked and what the ways out are, and exits 69; see
[the native library](docs/duckdb.md) for the full search order.

## Serving a lake

Nothing needs a configuration file:

```shell
duckpg ./common ./tenant --write ./local --key id
psql -h 127.0.0.1 -p 55432 -U admin -d lake
```

Positional arguments are the layer directories, lowest first; everything else has both a flag and a
key in `duckpg.yaml` — see [configuration](docs/configuration.md). `--tds 127.0.0.1:1433` opens the
SQL Server door beside the PostgreSQL one, and a lake needs at least one of them.

Tables are published into one schema, `lake` by default and `--schema` otherwise, and you never have
to name it: it goes in front of every session's search path, so `SELECT * FROM orders` works on a
fresh connection. Set `--schema public` if a tool of yours writes `public.orders` outright, as an
EF Core model built for PostgreSQL does.

`-v` traces each translated statement with its DuckDB execution time and row count; `-vv` adds the
wire messages in both directions. Ctrl+C and SIGTERM shut down cooperatively, and
`CALL duckpg_reload()` rebuilds the catalog from the filesystem without one.

See [`example/`](example) for a lake with all three formats, a `db=…` partitioned layer, a write
layer, virtual columns and per-user filtering — `cd example && duckpg`.

## What a lake is made of

A layer is a directory, and what it holds decides how each table is read:

| In the directory | Published as |
|---|---|
| `orders.yaml`, `orders.yml` | table `orders`, materialized through JSON for type inference |
| `orders.json` | table `orders`, `read_json_auto` |
| either, rooted in a mapping of mappings | the same table, the mapping keys filling the key column |
| `orders.parquet` | table `orders`, scanned in place |
| `orders/**/*.parquet` | table `orders`, one table over every file below, `union_by_name` |
| `orders/dt=…/*.parquet` | the same, with the partition keys as columns |
| `db=…/orders.parquet` | table `orders` across every `db=`, with `db` as a column |
| `.anything/` | ignored — dot-directories are the tool's own |

Layers stack in the order given, and where a key is declared the topmost layer holding a row wins.
`--write ./local` makes one directory the top of the stack and the only one that accepts writes: an
`INSERT` appends to it, an `UPDATE` rewrites the row there where it shadows what is beneath, and a
`DELETE` records a tombstone that hides the row in every layer below. A write is persisted as soon as
DuckDB commits it, in the format that table already has a file in, so restarting reads it back and no
database file is needed anywhere.

That merge is bound by DuckDB on every execution, which on a wide table over several layers is most
of the cost of a read. `--cache` writes the merged rows out once as parquet, and `--materialize`
collapses the stack into real tables at build — worth about 3.7× on a small ORM query.

## Documentation

| | |
|---|---|
| [Layers](docs/layers.md) | what each file publishes, keyed files, partitions, the write layer, transactions |
| [Configuration](docs/configuration.md) | every key and flag, virtual columns, filters and session variables |
| [Performance](docs/performance.md) | `--cache`, `--materialize`, `--store` and what each is worth |
| [Schema](docs/schema.md) | a dacpac as the declared schema: types, keys, defaults, references, views, functions |
| [Protocols](docs/protocols.md) | the PostgreSQL and TDS front doors, and what each client can rely on |
| [T-SQL](docs/tsql.md) | the dialect the TDS door accepts, and what it becomes |
| [Embedding](docs/embedding.md) | running a lake in your own process, against files your test wrote |
| [The native library](docs/duckdb.md) | where DuckDB is looked for, and how to put one there |

## Known limitations

- Trust auth only, on both protocols. No TLS, no SCRAM; TDS refuses encryption outright, so SqlClient
  needs `Encrypt=False`. Bind to localhost. A `filter:` is not a security boundary.
- Statement description runs the query `LIMIT 0` to learn its shape, so describing is not free and a
  statement that cannot be wrapped in a subquery falls back to `NoData`.
- A write is turned into layer operations by scanning the statement for its top-level clauses rather
  than by parsing it, so `UPDATE t [AS a] SET … [FROM …] WHERE …` and `DELETE FROM t [FROM …] WHERE …`
  are covered and CTEs, `DELETE … USING` and subqueries in the target are not. (The T-SQL dialect is a
  separate matter: that is parsed and rendered from the tree.)
- Statements are re-planned per execution; no plan cache.
- The `COPY` protocol (`\copy`, `NpgsqlBinaryImporter`) is not implemented.
- The catalog is built from the filesystem at startup and on `CALL duckpg_reload()`; no watcher.
- Nothing compacts the lower layers: the write layer grows until someone rewrites the files below.
- Two instances writing the same layer directory will overwrite each other. One writer per directory.
- Npgsql and Microsoft.Data.SqlClient are the two clients held to a conformance bar; anything else
  will need its own round of catalog shims.
- No `sys.*` or `INFORMATION_SCHEMA` emulation on the TDS side, so SQL Server tooling can query the
  lake but not browse it.

## Development

```shell
dotnet build
dotnet test          # layers, the write layer, dacpac schemas, the T-SQL parser,
                     # and Npgsql + SqlClient conformance
```

The tests carry their own DuckDB — the native library is pulled out of `DuckDB.NET.Bindings.Full` by a
build target and dropped next to the test binary — so a clean checkout and a clean CI runner both run
them with nothing installed. `dotnet pack -c Release` produces the tool package.

## License

MIT.
