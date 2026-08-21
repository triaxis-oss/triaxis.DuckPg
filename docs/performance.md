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

**`--lazy`** pays that build a table at a time, when a statement first names one, rather than all of
it before the first client connects. What is deferred is the collapse and only the collapse — the
rest of the build is what a layered lake does anyway — so an application that reads twenty of three
hundred tables collapses twenty. Measured on 300 tables of 200 rows over two layers, reading twenty
of them:

| | to serving | first read of the 20 | 20 reads by key after |
|---|---|---|---|
| layers | **3.25 s** | 34 ms | 42 ms |
| `--materialize` | 5.57 s | 19 ms | **20 ms** |
| `--materialize --lazy` | **3.32 s** | 153 ms | **17 ms** |

The collapse is not avoided, it is moved: ~7 ms a table on the statement that first names one, and
everything after that is what a materialized table costs. What it buys is 2.2 s of startup and the
memory of 280 tables nobody asked about — both of which grow with the tables a lake publishes and
not with the ones it serves.

What stands under a table nothing has named yet is the merge, exactly as a layered lake publishes it,
so a reference that goes unspotted answers with the same rows at the layered price rather than with
no rows at all. Two things move with it. The first statement to name a big table waits for its
collapse, where an eager lake had already paid. And layers that break a declared key or unique are
refused by that statement rather than by the start — the same refusal, and the same one every time
the table is named, rather than the key quietly dropped so the next statement can pass. `--compress`
covers what was collapsed when the build ended, which in a lazy lake is nothing.

A write by key is then sent as the one statement it already is. Over the layers, an `UPDATE` has to
be rewritten into four — collect the rows, collect the keys, evict, re-insert — because a written row
has to stand over the ones below it. A materialized table has nothing below it, so DuckDB's own
`UPDATE` is that same operation, finding its rows through the key. Measured on a 414-table lake, one
row by key: 8.0 ms as four statements, against 1.05 ms for DuckDB doing the same update directly. As
one statement it lands beside a `SELECT` by key — 1.2 ms against 0.85 on the same lake — and nearly
all of what went was time spent *preparing* four statements rather than running them. `DELETE` goes the same
way. The plan is still what runs for a layered lake, and for an `UPDATE ... FROM`, a row-limited
write, a moved key or a cascade, since each of those is something one statement cannot do.

An `INSERT` was already one statement, and what it paid past the write was being asked about a key
the table's own `PRIMARY KEY` already answers — 3.23 ms against 1.48 once it stopped being asked.
What DuckDB refuses comes back in the same words and under the same `23505` the question used, so
nothing about that changes but the time. A write that *replaces* rows is still asked first: its steps
are not one transaction, so a key refused at the insert would be refused after the eviction had
already committed.

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

## Baking the layers once

Everything above buys the merge back inside a run that is starting anyway. **`duckpg bake`** takes it
out of the runs altogether: it builds the same catalog over the same layers, writes what each table
publishes out as one ZSTD parquet, and stops.

```shell
duckpg bake ./common ./tenant --write ./local --key id --out ./baked
duckpg ./baked --key id
```

What that saves is not only the merge. A parquet layer is scanned where it lies, but YAML and JSON
are read through a converted copy and materialized into a table on every start, with their types
inferred from the values each time — the one cost a lake pays that has nothing to do with what is
asked of it. Bake once and a later run scans a file that already carries a real schema.

**Using the baked layer is semantically identical to using the layers it was baked from.** That is
the whole contract; everything below is what it takes to keep it.

**It is told the key, and so the dacpac.** Without one the layers concatenate instead of shadowing,
and a later `--key id` over the single file that produced would answer differently than the same key
over the stack.

**It is told the write directory rather than handed it as a layer.** A write layer is the only layer
whose files do not say everything it holds: its deletes are keys in a `.deleted/` sidecar that the
layer scan skips, so naming that directory as an ordinary layer would bake the rows it hides straight
back into the lake and lose the deletes without a word. Named as what it is, it is folded in like the
top of the stack it is, tombstones applied — which is also how a write layer that has outgrown the
files below it is flattened back into them. Point the next run at a fresh write directory; the old
one's rows are in the bake.

**It writes the table's own columns and nothing the configuration adds on top of them**, because that
configuration is still there on the next run. The virtual columns a `columns:` block adds are
projected by the run reading the file exactly as they were by this one, and a **declared default is
left empty** rather than written out: the file outlives the run that wrote it, so a `(getdate())`
baked into it would answer every later run with the moment this one started. The next run stamps it,
which is what the default meant before there was a bake — the same bargain `--cache` makes with the
same defaults. The exception is a default on a *key* column, which the merge itself reads: deferring
that one would shadow the wrong row, so it is written out. An id answered per row under
`--derive-ids` is deferred like any other default, and safely, because it is derived and not
generated: the run reading the file works the same id out of the same key.

**It writes a partitioned layer back partitioned**, as `<table>/db=…/`, since a partition column
joins the key and one flattened into an ordinary column would let one database's row 1 shadow
another's. Everything else is `<table>.parquet`. The directory must live outside the layers, or the
next run reads the copies back as a layer of their own — the same refusal `--cache` gets, for the
same reason.

**What cannot be kept identical is refused rather than written.** A table carrying a `filter:`,
because that is answered per session and a file every session reads cannot carry one — which is what
`--materialize` says about it too. And `materialize:` itself, since collapsing the layers is what a
bake *is*: doing it into memory first is the same work twice, and a materialized table holds every
declared default already stamped, which is the one thing a baked file must not. `store:` goes with
it. Both are keys rather than flags here — `bake` has no option for either, so they only arrive when
`-c` names a file written for the run that serves, and being refused by name beats being obeyed.
Nothing is deleted: what an earlier bake left for a table this one no longer publishes is named in a
warning and left where it is, because a layer directory is read for whatever is in it and that
directory is yours.

## Baking the whole lake as a database

`--format Database` writes what a materialized lake *holds* rather than the layer it publishes: the
collapsed tables, their keys and indexes, the declared views and macros. `--base` serves it. The
format is taken from the name when it is not given, so an `--out` ending in `.duckdb` means the same
thing without saying it.

```shell
duckpg bake ./common ./tenant --dacpac schema.dacpac --out seed.duckdb
duckpg --base seed.duckdb --write ./local
```

Nothing is scanned, described, parsed, merged or keyed on the way up, because all of it is in the
bytes already. On a 300-table lake holding 1500 rows between them:

| to serving | |
|---|---|
| the process, the host and DuckDB, publishing nothing | 383 ms |
| **`--base seed.duckdb`** | **643 ms** |
| layers + dacpac | 1371 ms |
| layers + dacpac + `--materialize` | 3543 ms |

Most of what a start costs is finishing the schema rather than reading the rows, which is why the
gap barely moves with the data and why this is the mode for serving one initial state over and over.
A base also needs no dacpac, no key and no configuration: everything DuckDB can hold is in its
catalog, and what it cannot — a declared reference with its `ON DELETE`, and the columns a schema
says the store fills in — is in a `duckpg` schema in the same file.

**The base is never written to.** The run copies it — to the `--store` if there is one and to a
scratch file otherwise — and serves the copy, so a thousand runs share one file and each gets the
state it was baked with. Copying is the whole cost, which is why the file is created with a block
size smaller than DuckDB's own: a block is allocated whole, so a lake of many small tables is mostly
blocks. Those 300 tables came to 159 MB at DuckDB's 256 KB and 18.7 MB at 16 KB, which is 9 ms to
copy against 172. That 16 KB is DuckDB's smallest and has a cost of its own — a compressed segment
has to fit its block, so a big table stops compressing — which is why the file is created at 64 KB,
the middle of the 16-to-256 DuckDB allows. `--block-size` moves it either way for a lake that is all
of one shape.

**Writes persist exactly as they do without a bake.** The base stays attached read-only and is what
the delta at shutdown is measured against — the same thing the `base` schema holds for a lake cut
from layers. A write directory beside it is read on the way up and applied over the copy: a tombstone
deletes the row it names, and a written row replaces the one it shadows, since a materialized table
has nothing to shadow *with*. So a restart reads its own delta back, and the run after that one too.

What is refused rather than guessed at: layers under a base, which is a whole lake already collapsed;
a file that is not a bake; one baked into a different schema than this lake publishes; and one
stamped with a version this duckpg does not speak.

**A baked database freezes what a materialized lake freezes.** Its tables hold every declared default
already stamped and there is no reader left to stamp one, so `(getdate())` is the moment the bake ran
— exactly as in a `--store`, and unlike the parquet bake, which leaves the column empty because a
view still reads it. `--derive-ids` is the answer for the ids among them: derived from the key rather
than generated, so the value is the same whoever works it out.

## Compressing what is held

**`--compress`** is the other answer to a materialized lake's memory, and it keeps the tables where
they are. DuckDB writes table data uncompressed and compresses it at a checkpoint, which nothing
drives an in-memory database to — so a lake that collapsed its layers holds every column in its
widest form until something asks. This asks, once, when the build ends. Measured over 5M rows of a
five-column table, medians through the wire:

| | as built | compressed |
|---|---|---|
| held in memory | 279.7 MB | **64.0 MB** |
| build | 0.7 s | 1.1 s |
| `count(*) WHERE note = 'order-42'` | 14.6 ms | **4.3 ms** |
| `sum(amount) WHERE bucket = 3` | **3.5 ms** | 6.5 ms |
| a row by its key | 0.63 ms | 0.60 ms |
| a one-row `UPDATE` | 9.9 ms | 10.0 ms |

So it is off by default, because it is a wager rather than a win: filtering a string is compared
against the dictionary instead of against every row and gets ~3.4× faster, while an aggregate over a
bit-packed column is unpacked a vector at a time and gets ~1.9× slower. Lookups by key and writes
never notice either way. Turn it on for the memory, and for a lake read mostly by predicates over
repetitive text; leave it off for one that scans numbers.

A checkpoint is the whole database's, so the tables a YAML or JSON layer was read into are
compressed too and a layered lake can ask for this as well — though there it is only those tables,
parquet being scanned where it lies. Only what is there when the build ends is covered: a row
written after that is held as it arrived, and nothing checkpoints a second time.
