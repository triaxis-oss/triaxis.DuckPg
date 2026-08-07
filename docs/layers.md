# How a lake is read and written

A layer is a directory, and a lake is a stack of them. This is what each file in one publishes, how
a layer shadows the one below, what a write does to the stack, and how concurrent transactions are
kept out of each other's way.

## What a file publishes

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

Layers stack in the order given. Where a key is declared the topmost layer holding a row wins
(`QUALIFY row_number() OVER (PARTITION BY key ORDER BY _seq DESC) = 1`); without a key there is no
way to tell rows apart, so the layers simply concatenate. Columns are the union of the layers, in the
topmost layer's order, and each layer is cast to the published type — where a parquet layer has the
column its type wins, since a parquet file carries a real schema while YAML and JSON types are
inferred from the values.

Table names are matched case-insensitively, so an export that disagrees with itself about
capitalization still lands on one table. Two files of different formats claiming one table is a
mistake rather than a merge: the first is used and the other is named in a warning.

## Naming rows instead of listing them

Where a table has a single key column, a YAML or JSON file whose root is a mapping of mappings is
read as one row per entry, with the entry's key filling that column:

```yaml
test1:                  # the same as
  foo: 2                #   - id: test1
  bar: 3                #     foo: 2
test2:                  #     bar: 3
  foo: 4                #   - id: test2
  bar: 6                #     foo: 4
```

with `--key id`, or a key from `tables:` or the dacpac; JSON says the same as
`{"test1": {"foo": 2}, …}`. It stacks, shadows and is written back like any other layer — what it
saves is repeating the key on every row of a hand-written one.

Three things decide it and all three have to hold: the root is a mapping, every value in it is a
mapping, and the table has exactly *one* key column, since a mapping key is one value and cannot be
two. Otherwise the document is a single row, which is what an object-rooted JSON file has always
been. A row carrying the key column inside itself as well is dropped in favour of the entry's key. A
key is typed like any other YAML scalar, so `1:` is a number and `"007":` is text; JSON has no
non-string mapping key, so a keyed JSON layer cannot hold `007` as text and a table needing one
should be YAML. A text key is always written back quoted, which also survives a `007`.

## What a lake says it made of your files

That fallback to a single row is also what a mistake looks like, so every table's layers are logged
as they are read with what each one turned out to publish:

```
lake.orders  <- ./common/orders.yaml (2 rows, 2 columns: id, amount)
lake.widgets <- ./common/widgets.yaml (1 row, 1 column: widgets)
```

Two shapes are warned about by name: a `widgets:` key wrapping the rows, and named rows where the
table has no single key column to put the names in. Neither changes what is published — unwrapping
would be right most of the time and, the rest of the time, wrong with no more signal than there was
before it. A lake with **no tables at all** is warned about too, since it otherwise serves and
answers every query with "no such table". Neither is an error: `CALL duckpg_reload()` reads the
layers again, so a lake started before its files arrive is a thing someone may mean.

## Partitions

A directory named `k=v` is a partition *above* the tables rather than a table: the table is the file
below it, so `db=one/orders.parquet` and `db=two/orders.parquet` are one `orders` with a `db` column
— one view across many databases. Partitions nest (`db=one/year=2026/orders.parquet`), and any other
directory is still a table of its own files, so both layouts can live in one layer.

**A partition column joins the key**, whatever the key would otherwise be: rows are only unique
within a partition — every database has its own row 1 — so without it one would shadow the other.
`--key order_id` over a `db=…` lake means a row is identified by `(order_id, db)`, which is also what
a write to such a table has to supply. Only the partitions a layer itself declares become columns, so
a lake sitting under `/data/tenant=acme/` grows no stray `tenant` column from it.

## The write layer

`--write ./local` (or `write:` in the file) makes one directory the topmost layer and the only one
that accepts writes:

- `INSERT` appends to the write layer, and refuses a key the table already publishes — whichever
  layer holds it, and whether or not the write layer has a copy of its own. So does one statement
  carrying the same key twice.
- `DELETE` removes the row from the write layer and records its key in `local/.deleted/<table>`,
  which hides that row in every layer below.
- `UPDATE` computes the new rows first, then replaces them in the write layer, where they shadow
  whatever is beneath. Only an update that *moves* a row's key leaves the old key behind with nothing
  above it, and only that one records a tombstone. A `FROM` clause joins the target to somewhere else
  for its new values, which is also what a matched-only `MERGE` becomes. A key moved onto one the
  lake already publishes is refused, as is a join that matches a row twice — both would leave two
  rows under one key. A key that moves onto one this same statement is taking away is not:
  `SET id = id + 1` over a whole table shifts it.

The key check is over the *merged view*, like a reference's, and it runs before any row lands; a key
the lake generates is not checked, since it cannot collide with one it generated before. Keeping that
rule costs one scan of what the table publishes, per insert and per key-moving update — on a layered
lake that is the merge, and there is no index over a merge, so it is most of what a write costs.
`--no-check-keys` gives it back to a lake whose writers are known to be sending fresh keys, a bulk
load out of a trusted source above all; what returns with it is the old behaviour, which is not even
the same in both modes — layered, the written row shadows the one below it, and materialized, both
stay. Reads, deletes and ordinary updates never paid it either way.

A write is persisted as soon as DuckDB commits it — immediately for a bare statement, at `COMMIT` for
one inside a transaction, and never for one that is rolled back. Restarting reads the same files
back, so a written row survives without a database file anywhere. A table is persisted in the format
it already has a file in, so a hand-written `notes.yaml` stays YAML rather than turning into parquet
the first time someone writes to it; `--write-format` decides what a table with no file yet gets, and
the default is parquet. An emptied table takes its file with it.

Writes need identity: `UPDATE` and `DELETE` require a key, from the table's own `key:`, from `--key`,
or from a dacpac's primary key. `INSERT` does not — a new row needs no identity to be told from an
old one. Virtual columns reject writes. `--writable` accepts writes with no directory at all; they
live in memory and are lost on exit, which is what a test wants. A writable table the directory holds
nothing for costs nothing to read: it is published without its write branch and grows one in the plan
of the write that first needs it, since otherwise every read of a table nobody has written to would
pay for the branch — measurably, +22% on a table and +50% on a view over several.

Two sessions writing at once is a matter for the front doors rather than for the stack, and
`--serialize-transactions` is [documented with them](protocols.md#transactions-and-serializing-them).

`CALL duckpg_reload()` rebuilds the catalog from the filesystem, picking up files that appeared since
startup.
