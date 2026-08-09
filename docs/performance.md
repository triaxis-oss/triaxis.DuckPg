# Paying the merge once

A view is bound by DuckDB on **every execution** — a prepared statement re-plans exactly like a fresh
one — so everything a view definition says is paid for by every query that touches it. On a wide
table stacked over several layers that is most of the cost of reading it. Three settings buy it back,
giving up more each time.

**`--cache ./cache`** writes the merged rows of every table more than one layer carries out once, as a
ZSTD parquet, and publishes the view as a scan of that file. On a 300-table lake this materializes the
38 tables that actually merge, costs nothing measurable at startup, and cuts planning about threefold.
A table only one layer carries is already a single scan and needs no copy; a table with a write layer
keeps its copy as the layer *underneath* the write branch, where a written row shadows it and a
tombstone hides it exactly as they would a real layer.

Each copy is named for a hash of what produced it — the table's shape, key and filter, and the bytes
of every file it reads — so a restart over unchanged files reuses the copy, and a layer that *did*
change lands on a different name rather than being answered with the old rows. The cache is only
revisited on startup and on `CALL duckpg_reload()`, which is what makes it correct: nothing else can
change the files underneath while the lake is up. It must live outside the layer directories, or the
lake would read its own copies back as data — the tool refuses that rather than finding out later.

**`--materialize`** collapses the whole stack into real DuckDB tables at build and serves those. There
is then no merge to bind on every read, no write branch to earn, and no tombstone to hide anything: a
write goes to the table the reads come from, and a delete deletes. Measured on a 60-column table
filtered to one row, that is worth about 3.7× — 2.25 ms against 8.25 ms for the same statement over
the merge view. The cost is memory, since every table is resident, and that a table cannot carry
anything answered per session: a `filter:` or a `getvariable()` column is refused at startup rather
than quietly stopping.

A keyed table also carries its key as a real `PRIMARY KEY`, which a layered lake has nowhere to put.
That is what makes a lookup by key a lookup rather than a scan — 0.47 ms against 4.2 ms on a 10M-row
table whose rows are in no particular order, and about a fifth where they happen to arrive keyed and
the zone maps had already done the pruning. Any `UNIQUE` constraint or unique index the dacpac
declares past the key becomes an index here too. It also means the layers have to be right: a stack
that publishes one key twice, leaves the key empty, or breaks a declared unique cannot be built, and
the lake says so at startup rather than serving it.

A write by key is then sent as the one statement it already is. Over the layers, an `UPDATE` has to
be rewritten into four — collect the rows, collect the keys, evict, re-insert — because a written row
has to stand over the ones below it. A materialized table has nothing below it, so DuckDB's own
`UPDATE` is that same operation, finding its rows through the key. Measured on a 414-table lake, one
row by key: 8.0 ms as four statements, against 1.05 ms for DuckDB doing the same update directly. As
one statement it lands beside a `SELECT` by key — 1.2 ms against 0.85 on the same lake — and nearly
all of what went was time spent *preparing* four statements rather than running them. `DELETE` goes the same
way. The plan is still what runs for a layered lake, and for an `UPDATE ... FROM`, a row-limited
write, a moved key or a cascade, since each of those is something one statement cannot do.

**`EXPLAIN <statement>`** answers for what the gateway will actually run, which is not always what
was sent. Where that is a single query — every read, and a keyed write against a materialized table —
it is DuckDB's own plan, cost and all. Where it is several, it lists them in order, because a step
reads temp tables the step before it makes and there is nothing to explain until it has run. Either
way it works through both doors, which is the only way to see a rewrite short of a profiler.

The write directory is still a layer on the way in, so a delta a previous run left is read back and
collapsed with the rest, but nothing is kept while the lake runs. When it stops cleanly, what it holds
goes out once as a delta in the write layer's own format — the rows that are not what the layers said,
and the keys that were there and are not — which an ordinary lake reads back as the layer it is. Being
a set difference, that delta says *which* rows a table now has and not *how many of each*: on a table
with no key, a row inserted identical to one the layers hold does not come back. With a key, which
`UPDATE` and `DELETE` need anyway, the two modes agree on everything.

**`--store warehouse.duckdb`** gives that materialized lake a DuckDB database file to live in. The
layers are collapsed into it once; every start after that opens what is already there, and the layers
are only consulted for a table the file does not yet carry. A write survives by having been written
rather than by being worked out again at shutdown, so no delta is exported beside a store — and a
sequence keeps its place, so a declared `IDENTITY` carries on where it left off. The trade is that
the file *is* the state: editing a layer no longer changes a table the store already holds, and a
store made against a different schema is refused at startup by name rather than rebuilt, since
rebuilding would discard everything written to it. Delete it to start again. It needs
`--materialize`; a layered lake keeps its write layer in files and would apply every write twice.

**The file cannot be named after the schema.** DuckDB names the database after the file, so
`--store lake.duckdb` and the default `lake` schema leave every `lake.orders` ambiguous between the
two, and nothing the lake publishes can be bound. That combination is refused at startup by name —
call the file something else, or move the lake with `--schema`.

`--store-mode spill` takes that trade back and keeps only the memory saving: the layers are collapsed
into the file on every start and the delta goes out at shutdown, as for an in-memory materialized
lake. Use it when a lake is larger than the RAM you want to give it and the layers are still the
source of truth; use the default `keep` when the file is. The two must not share a path — nothing in a
DuckDB file says which mode wrote it, so a `spill` start would rebuild a kept store from the layers.
