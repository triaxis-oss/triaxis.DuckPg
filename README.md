# duckpg

Point any PostgreSQL tool at a stack of YAML, JSON and parquet files — no driver, no server, no
Spark — and add columns, access rules and writes the files never had.

duckpg speaks the PostgreSQL v3 wire protocol — and, on a second port, the TDS protocol that
Microsoft.Data.SqlClient speaks — and executes against DuckDB. Each table is published as a view
over its layers, so a table can come from a shared YAML seed, a tenant's JSON overrides and a
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

The tool links against the DuckDB installed on the machine rather than bundling one:
`brew install duckdb`, `apt install libduckdb-dev`, or point `DUCKDB_LIBRARY` at the library
directly. Homebrew's prefixes (`$HOMEBREW_PREFIX`, `/opt/homebrew`, `/usr/local`, linuxbrew) and
the `opt/duckdb/lib` keg beneath each are probed on their own, because neither macOS nor Linux
looks there by default.

On a machine with no DuckDB and no package manager worth arguing with:

```shell
duckpg --install-duckdb
```

which downloads the library from DuckDB's own releases — the version these bindings were built
against, nothing newer — and leaves it in the local application data directory. Running it again
with the library already there does nothing; one that got half written is replaced. That copy is
then preferred to whatever the machine has, since it is known to answer the C API this build speaks;
only `DUCKDB_LIBRARY` outranks it.

A DuckDB of another version usually still works, so it is a warning rather than a refusal:

```
DuckDB 1.4.1 loaded from /usr/lib/libduckdb.so, where these bindings speak 1.5.5's C API
```

Nothing is ever downloaded unless asked for: without it, a missing library is an error that says
where it looked and what the ways out are, and exits 69.

Requires .NET 10.

## Serving a lake

Nothing needs a configuration file:

```shell
duckpg ./common ./tenant --write ./local --key id
psql -h 127.0.0.1 -p 55432 -U admin -d lake
```

Positional arguments are the layer directories, lowest first. `--pgwire`, `--write`,
`--write-format`, `--writable`, `--materialize`, `--store`, `--store-mode`,
`--no-sort-small-tables`, `--serialize-transactions`, `--schema`, `--key` (repeatable), `--dacpac`,
`--cache` and `--config` each override the file when both are given; argument paths are relative to
the working directory, file paths to the file. A file named explicitly with `--config` must exist,
so a typo is an error rather than a silent fallback to defaults.

`-v` traces each translated statement with its DuckDB execution time and row count (execution and
row streaming are timed apart, since DuckDB returns before the rows are pulled); `-vv` adds the
wire messages in both directions. Ctrl+C and SIGTERM shut down cooperatively.

See [`example/`](example) for a lake with all three formats, a `db=…` partitioned layer, a write
layer, virtual columns and per-user row filtering — `cd example && duckpg`.

## Embedding it

The tool is a thin shell over a library, so a test can have the same lake in-process — a real
PostgreSQL and a real TDS front door, served over loopback, against files it wrote a moment ago.
The point is not to fake a database: it is to hold your *client* stack — SqlClient, EF Core,
whatever you actually ship — to the same wire it will meet in production.

| Package | What it is |
|---|---|
| `triaxis.DuckPg` | the lake and both front doors |
| `triaxis.DuckPg.Cli` | the `duckpg` command, which carries its own copy |

Everything is registered through `Microsoft.Extensions.DependencyInjection`, and the lake is an
`IHostedService`, so a host owns it:

```csharp
services.AddDuckPg(config =>
{
    config.Layers = ["./common", "./tenant"];
    config.Write = "./local";
    config.Tds = "127.0.0.1:0";     // port 0: the OS picks, and the lake says which
});

// after host.StartAsync
var lake = host.Services.GetRequiredService<Lake>();
using var connection = new SqlConnection(lake.SqlConnectionString());
```

`AddDuckPg` also takes an `IConfiguration` to bind, or a `Config` already built. The listeners bind
during `StartAsync` rather than when serving begins, which is what makes port 0 useful: by the time
the host is up, `lake.Endpoint` is the port to connect to.

**Both doors are opt-in.** `listen` opens the PostgreSQL one, `tds` opens SQL Server's, and a lake
needs at least one. A consumer speaking only TDS sets `Listen = null` and opens no listener it never
uses.

For more than one lake — one per tenant, one per exported database, each with different layers —
register a factory instead. Each lake it hands back owns everything it was built from, so there is
one thing to dispose and nothing to dispose in order:

```csharp
services.AddDuckPgFactory();

var factory = provider.GetRequiredService<IDuckPgLakeFactory>();
await using var lake = await factory.StartAsync(new Config
{
    Layers = [seed, exportDirectory],
    Dacpac = dacpac,
    Writable = true,              // writes live in memory; no directory needed
    Tds = "127.0.0.1:0",
    Listen = null,
}, cancellation);

// lake.SqlConnectionString()
```

Lakes from a factory are independent, so starting several concurrently is ordinary. A factory-built
lake registers no hosted service, because the caller starts it; `AddDuckPg` is the one a host owns.

**What cannot work is said before anything opens.** A layer directory or dacpac that is not there,
a cache inside a layer, or no front door at all throws `DuckPgConfigurationException` naming the
part that is wrong — rather than a lake that starts empty and a binder error much later.

**Bring a native DuckDB.** Neither package carries one, because every platform's library together is
420 MB and that is not a dependency's decision to make. Three ways, in the order they are looked for:

- the machine already has one — `brew install duckdb`, `apt install libduckdb-dev`, or
  `DUCKDB_LIBRARY` pointing at it;
- add `DuckDB.NET.Data.Full` to your project, which brings the native for every RID;
- `installDuckDb: true` in the configuration, which fetches the matching version into the local
  application data directory the first time a lake finds none, and reuses it forever after. One
  download per machine, not per run. `IDuckDbInstaller` is the same fetch on demand, for a caller
  that would rather provision than discover.

Without one, the error says where it looked and what the ways out are, rather than a
`DllNotFoundException` naming a library nobody asked for.

**Embedding is not free.** Measured by a consumer replaying a parquet export through EF Core and a
legacy ORM: 37–38 s in-process against 30–32 s out-of-process for the same work, on a four-core box
under load. The lake stops being a separate process and starts sharing a heap, a garbage collector
and a thread pool with everything else the host is doing. What you buy is no executable on `PATH`,
no port to coordinate, and a lake that lives and dies with the test — not speed.

## The schema, and not having to know it

Tables are published into one schema, `lake` by default and `--schema` otherwise. You do not have to
name it: it goes in front of every session's search path, so

```sql
SELECT * FROM orders           -- finds lake.orders, or whatever you called it
```

works on a fresh connection, and `current_schema()` says which one answered. `main` stays behind it,
because the `pg_catalog` shims live there and every client that reads the catalog needs them.

That matters because neither default would have been right on its own: DuckDB's own default schema is
`main`, PostgreSQL's is `public`, and a lake publishing into either would still be the wrong one for
half its callers. Set `--schema public` if a tool of yours writes `public.orders` outright — an
EF Core model built for PostgreSQL does — and the unqualified form keeps working either way.

The TDS door never needed this: `[orders]` and `[dbo].[orders]` are both written into the lake's
schema by the T-SQL renderer, whatever it is called.

## Layers

A layer is a directory. What it holds decides how each table is read:

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

**A YAML or JSON file may name its rows instead of listing them.** Where a table has a single key
column, a file whose root is a mapping of mappings is read as one row per entry, with the entry's key
filling that column:

```yaml
test1:                  # the same as
  foo: 2                #   - id: test1
  bar: 3                #     foo: 2
test2:                  #     bar: 3
  foo: 4                #   - id: test2
  bar: 6                #     foo: 4
```

with `--key id`, or a key from `tables:` or the dacpac. It is an ordinary layer either way — it
stacks, shadows and is written back like any other; what it saves is repeating the key on every row
of a hand-written one. JSON says the same thing as `{"test1": {"foo": 2}, …}`.

Three things decide it, and all three have to hold: the root is a mapping, every value in it is a
mapping, and the table has exactly *one* key column — a mapping key is one value and cannot be two.
Otherwise the document is a single row, which is what an object-rooted JSON file has always been. A
row that also carries the key column inside itself is a second answer to a question the file already
answered; the entry's key is the one it was organized by, and the inner one is dropped.

A key is typed like any other YAML scalar, so `1:` is a number and `"007":` is text. JSON has no way
to write a mapping key that is not a string, so there the quoting says nothing and the text is what
the key is read from — which means a keyed JSON layer cannot hold `007` as text, and a table that
needs one should be YAML or should say so in a dacpac. Written back, a text key always comes out
quoted: it reads back as the same string either way, and quoted it also survives a `007`.

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
capitalization still lands on one table. Two files of different formats claiming one table is a
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
- `UPDATE` computes the new rows first, then replaces them in the write layer, where they shadow
  whatever is beneath. Only an update that *moves* a row's key leaves the old key behind with
  nothing above it, and only that one records a tombstone. A `FROM` clause joins the target to
  somewhere else for its new values, which is also what a matched-only `MERGE` becomes.

A write is persisted as soon as DuckDB commits it — immediately for a bare statement, at `COMMIT`
for one inside a transaction, and never for one that is rolled back. Restarting the gateway reads
the same files back, so a written row survives without a database file anywhere.

A writable table the directory holds nothing for costs nothing to read: it is published without its
write branch or its tombstone check, and grows them when a write first arrives. Since a view is
bound on every execution, that branch would otherwise be paid for by every read of a table nobody
has written to — measurably, +22% on a table and +50% on a view over several. The promotion travels
in the plan of the write that caused it, so a rolled-back write leaves nothing behind and the next
one simply promotes again. The tombstone check arrives the same way, separately: it costs the same
flat ~1 ms whatever the table looks like, so it is not bound until a row has actually been hidden.

## Caching the merge

A view is bound by DuckDB on **every execution** — a prepared statement re-plans exactly like a
fresh one — so everything a view definition says is paid for by every query that touches it. On a
wide table stacked over several layers that is most of the cost of reading it: the union, the row
numbering that picks a winner, and a cast per column per layer.

`--cache ./cache` writes the merged rows of every table more than one layer carries out once, as a
ZSTD parquet, and publishes the view as a scan of that file. On a 300-table lake this materializes
the 38 tables that actually merge, costs nothing measurable at startup, and cuts planning about
threefold. ZSTD rather than snappy or none: a third smaller for the same read, and a compressed
scan beats an uncompressed one outright, because there is less to move.

What it does **not** cover is a table with a write layer — its rows change under any copy of them,
so it keeps the merge. A table only one layer carries is already a single scan and needs no copy.
Each copy is named for a hash of what produced it — the table's published shape, its key, its
filter, and the bytes of every file it reads — so a restart over unchanged files reuses the copy
instead of deriving it again, and a layer that *did* change lands on a different name rather than
being answered with the old rows. Stale copies of a table are removed as it is rewritten. The cache
is otherwise only revisited on startup and on `CALL duckpg_reload()`, which is what makes it
correct: nothing else can change the files underneath while the lake is up.

A declared default is not written into the copy: it stays with the view, so a `(getdate())` column
is stamped by whoever reads it rather than frozen into a file that outlives the process. The
exceptions are the defaults the merge itself depends on — one on a key column decides which row
shadows which, and a filter or a virtual column reads the merged row, defaults and all — which are
materialized with the rows they affect. The hash ignores what a default evaluated to either way:
keying on it would rebuild every stamped table on every restart, which in a real schema is most
of them.

A copy is the read layers, and a write does not touch those — so a table that is written to keeps
its copy as the layer underneath the write branch, rather than going back to reading every layer
again. A written row shadows it and a tombstone hides it, exactly as they would a real layer.

The cache must live outside the layer directories, or the lake would read its own copies back as
data — the tool refuses that rather than discovering it later.

A table is persisted in the format it already has a file in, so a hand-written `notes.yaml` stays
YAML rather than turning into parquet the first time someone writes to it. `--write-format` decides
what a table with no file yet gets; the default is parquet. An emptied table takes its file with
it.

Writes need identity: `UPDATE` and `DELETE` require a key, from the table's own `key:`, from
`--key`, or from a dacpac's primary key. `INSERT` does not — a new row needs no identity to be told
from an old one. Virtual columns reject writes.

`--writable` accepts writes with no directory at all; they live in memory and are lost on exit,
which is what a test wants.

`--materialize` collapses the whole stack into real DuckDB tables at build and serves those. There
is then no merge to bind on every read, no write branch to earn, and no tombstone to hide anything:
a write goes to the table the reads come from, and a delete deletes. The write directory is still a
layer on the way in — a delta a previous run left is read back and collapsed with the rest — but
nothing is kept while the lake runs. When it stops cleanly, what it holds goes out once as a delta
in the write layer's own format: the rows that are not what the layers said, and the keys that were
there and are not. An ordinary lake reads that back as the layer it is.

It is for a test suite that wants a plain database rather than the layer machinery, and for anything
that would rather pay the merge once than on every query — which, measured on a 60-column table
filtered to one row, is worth about 3.7×: 2.25 ms against 8.25 ms for the same statement over the
merge view. Almost all of the difference is planning, since a view is bound on every execution and
the merge is most of what there is to bind. The cost is memory — every table is
resident — and that a table cannot carry anything answered per session: a `filter:` or a
`getvariable()` column is refused at startup rather than quietly stopping.

One more thing that delta cannot say. It is a set difference, so it carries *which* rows a table now
has and not *how many of each*: on a table with no key, a row inserted identical to one the layers
already hold is not in the difference, and does not come back. A layered lake keeps it, because it
kept the row it was given rather than working it out afterwards. With a key — which `UPDATE` and
`DELETE` need anyway, and which a dacpac supplies — the two modes agree on everything.

`--store lake.duckdb` gives that materialized lake a DuckDB database file to live in. The layers are
collapsed into it once; every start after that opens what is already there, and the layers are only
consulted for a table the file does not yet carry. A write survives by having been written rather
than by being worked out again at shutdown, so no delta is exported beside a store — the file is the
state, and one answer to that question is enough. It also means a sequence keeps its place, so a
declared `IDENTITY` carries on where it left off instead of being reseeded from the files.

The trade is that the file *is* the state: editing a layer no longer changes a table the store
already holds, and a store made against a different schema is refused at startup by name rather than
rebuilt, since rebuilding would discard everything written to it. Delete it to start again. It needs
`--materialize`; a layered lake keeps its write layer in files and would apply every write twice.

`--store-mode spill` takes that trade back and keeps only the memory. The file is then somewhere for
the tables to live and nothing more: the layers are collapsed into it on every start, a layer edited
underneath is read, and the delta goes out at shutdown exactly as it does for an in-memory
materialized lake. Use it when a lake is larger than the RAM you want to give it and the layers are
still the source of truth; use the default `keep` when the file is. The two must not be pointed at
the same path, since nothing in a DuckDB file says which one wrote it — a `spill` start would rebuild
a kept store's tables from the layers and lose everything written to them.

`--serialize-transactions` lets one transaction run at a time. Two things go wrong without it, and
neither is something an application written against SQL Server or PostgreSQL has reason to expect.
The first is that DuckDB does not make a second writer wait: the moment two open transactions touch
the same row it refuses one of them outright. The second is worse, because the writes need never
overlap for it — a DuckDB transaction fixes its **catalog** snapshot when it begins, so a write
branch created by anybody afterwards is invisible to it for as long as it lives, and it cannot
create one itself either. A transaction that begins, reads, and only then writes to a table someone
else wrote to first fails with `Table with name … does not exist` against a table that is plainly
there. Ordering only the writes cannot close that; ordering the transactions can.

So a session takes the lake's turn at `BEGIN` — or at its first write, for a statement outside a
transaction — and gives it up when that transaction ends. The next one waits, indefinitely, the way
a real database does with the default `LOCK_TIMEOUT`. A read outside a transaction never waits; a
read-only transaction does take the turn, since nothing says in advance that it will stay read-only.
It is off by default: a lake serving one connection pays a lock for nothing.

`CALL duckpg_reload()` rebuilds the catalog from the filesystem, picking up files that appeared
since startup.

## The TDS front door

`--tds 127.0.0.1:1433` (or `tds:` in the file) opens a second listener speaking the protocol
SQL Server speaks, so an application built on `Microsoft.Data.SqlClient` reads the same lake with
no driver change:

```csharp
using var connection = new SqlConnection("Server=127.0.0.1,1433;Database=lake;User ID=sa;Encrypt=False");
using var command = new SqlCommand("SELECT TOP 10 [order_id], ISNULL([note], '') FROM [dbo].[orders]", connection);
```

**`Encrypt=False` is required.** duckpg answers PRELOGIN with `ENCRYPT_NOT_SUP`, because TDS
encrypts the login packet even when the session itself is plaintext, and that needs a certificate
this tool has no business owning. Bind it to localhost.

Both doors share one lake, one catalog and one write layer: a row inserted over TDS is in the same
file a `psql` session reads a moment later, and `sessionVariables` filtering works the same, keyed
on the login's user name.

What SqlClient does, and what answers it:

| | |
|---|---|
| Login, `SELECT`, typed `SqlDataReader` reads, `NULL`s | COLMETADATA / ROW / DONE |
| Parameterized commands (`sp_executesql`) | RPC, with values bound as DuckDB parameters |
| Parameters typed `NTEXT`, `TEXT`, `IMAGE` by an older client | read as the strings and blobs they are |
| `cmd.Prepare()`, repeated execution (`sp_prepexec` / `sp_execute` / `sp_unprepare`) | handles held per session |
| `SqlTransaction` commit and rollback | transaction manager requests, ENVCHANGE descriptors |
| Multi-statement batches, `NextResult()` | one DONE per statement, the last one final |
| `INSERT` / `UPDATE` / `DELETE` | the same write layer the PostgreSQL side writes |
| Errors an application can recover from | ERROR tokens with SQL Server's own numbers (208, 102, 245, …) |
| `CommandTimeout`, `cmd.Cancel()` | Attention → `duckdb_interrupt` → DONE with the attention bit |
| Connection pooling, the reset a reused connection carries | prepared handles and `#tables` dropped, files untouched |
| `SET NOCOUNT ON` and its relatives | accepted and ignored |
| `OPENJSON(@p) WITH (…)` — EF Core's list parameter | a derived table over the JSON, one row per element |
| `MERGE … WHEN MATCHED THEN UPDATE` — its bulk update | a joined `UPDATE`; the other branches are refused by name |
| `COUNT`, `COUNT_BIG` | an `int` and a `bigint`, as on SQL Server — DuckDB counts in BIGINT either way |
| `SUM` of an integer column, `UBIGINT`, `HUGEINT` | `DECIMAL(38,0)` — a number, since no SQL Server integer is that wide |

Not implemented: TLS and SQL logins are not verified (trust auth, as on the PostgreSQL side), MARS,
`SqlBulkCopy`, table-valued parameters, output parameters, and `sys.*` / `INFORMATION_SCHEMA`
emulation — so SSMS and EF Core scaffolding will not introspect the lake, though hand-written
queries run.

## The T-SQL it accepts

A client sends T-SQL; DuckDB does not speak it. duckpg **parses** it — lexer, recursive-descent
parser, and a renderer that emits DuckDB SQL from the tree. Nothing is rewritten by pattern
matching on text, which is why `'a' + b` and `1 + 2` can be told apart at all.

| Written | Becomes |
|---|---|
| `[bracketed]`, `"quoted"` names | quoted identifiers |
| `dbo.orders`, `app.dbo.orders`, bare `orders` | the lake's schema |
| `[dbo].[orders].[id]`, `[app].[dbo].[orders].[id]` | the same schema, so a qualified column still finds its table |
| `SELECT TOP 5`, `OFFSET … FETCH NEXT` | `LIMIT` / `OFFSET` |
| `N'text'`, `0xDEAD` | `'text'`, `from_hex('DEAD')` |
| `ISNULL`, `LEN`, `IIF`, `CHARINDEX`, `NEWID`, `GETDATE`, `GETUTCDATE`, `CEILING` | their DuckDB equivalents, argument order and all |
| `DATEPART(day, d)`, `DATEDIFF`, `DATEADD` | `date_part('day', d)`, `date_diff`, interval arithmetic |
| `CAST(x AS NVARCHAR(MAX))`, `INT`, `BIT`, `DATETIME2`, `UNIQUEIDENTIFIER`, `MONEY` | `VARCHAR`, `INTEGER`, `BOOLEAN`, `TIMESTAMP`, `UUID`, `DECIMAL(19,4)` |
| `CONVERT(INT, x)` | `CAST(x AS INTEGER)` |
| `CONVERT(varchar, d, 120)` and the other styles | the date format the style names, applied in .NET |
| `pwdencrypt`, `pwdcompare` | SQL Server's own hash: version, salt, SHA-512 over UTF-16 |
| `SUSER_SNAME()`, `SUSER_NAME()`, `USER_NAME()`, `ORIGINAL_LOGIN()` | the session's login name, as a literal |
| `@@VERSION`, `@@ROWCOUNT`, `@@TRANCOUNT`, `@@SPID` | the session's own values |
| `MERGE t a USING s ON … WHEN MATCHED THEN UPDATE SET …` | `UPDATE t AS a SET … FROM s WHERE …` |
| `DELETE FROM [s] FROM [t] AS [s] WHERE …` — EF Core's `ExecuteDelete` | a delete against the table the alias binds |
| `UPDATE [o] SET … FROM [t] AS [o] WHERE …` — its `ExecuteUpdate` | the same, on the other write |
| either of those joined to another table | the other tables become the write's own `FROM`, their conditions its `WHERE` |
| `INNER LOOP JOIN`, `HASH`, `MERGE`, `REMOTE` join hints | dropped |
| `MERGE t USING (VALUES …) i (…) ON 1=0 WHEN NOT MATCHED THEN INSERT …` — EF Core's batch insert | one multi-row `INSERT` |
| `OUTPUT INSERTED.[id], i._Position` | the rows are written down first, then answered from |
| `UPDATE … OUTPUT 1 WHERE …`, `DELETE … OUTPUT 1` | one row per row the statement touched |
| `a LEFT JOIN b JOIN c ON … ON …` — a join nested in a join | the same tree, parenthesized |
| `SELECT … INTO #t FROM …`, `DROP TABLE [IF EXISTS] #t` | `CREATE TEMP TABLE #t AS …`, `DROP TABLE …` |
| `SELECT TOP 50 PERCENT … ORDER BY …` | `LIMIT` the counted share, rounded up as SQL Server rounds it |
| `[flag] * [n]` where `flag` is a `bit` | `CAST(flag AS INTEGER) * n`, as T-SQL converts it |
| `WITH (NOLOCK)` and other table hints | dropped |
| `SET NOCOUNT ON`, isolation levels | no-ops |
| `SAVE TRANSACTION x` | nothing; `ROLLBACK TRANSACTION x` is refused rather than faked |
| `EXEC sp_getapplock @Resource = …`, `sp_releaseapplock` | granted; every other `EXEC` is refused by name |

`+` becomes `||` only where one side is provably text — a string literal, a `CAST` to a character
type, or a function that returns one. Everywhere else it stays arithmetic, because guessing would
turn `1 + 2` into `'12'`.

An ORM that qualifies everything it writes — LLBLGen Pro among them — is what this is for: table
references, column references and `TOP(@p)` paging over a row-numbered derived table all land on
the lake without the application knowing what it is talking to.

An application lock is granted by doing nothing. `sp_getapplock` serializes a caller against the
other connections of a shared database, and a lake is not one — it serves the application that owns
its files, so the exclusion is already there. `EXEC` of anything else is refused by name, which is
the answer an ORM calling a stored procedure gets rather than a syntax error about `EXEC`.

A `#table` is a temporary table, and DuckDB's belong to a connection exactly as SQL Server's belong
to a session — including going away when a pooled connection is handed out again. `##global` ones
are refused: another connection cannot see them here. `SELECT … INTO` and `DROP TABLE` accept
nothing else, since a lake's tables are the files under it.

EF Core sends a batch of rows as a `MERGE` over `ON 1=0`, which is a multi-row insert with the
matched branch made unreachable, and it is translated as one. Its `OUTPUT INSERTED.[key], i._Position`
is answered: the rows are materialized, written from there, and read back from the same copy — so
each key comes back beside the position of the row that got it. A column the rows do not carry and
nothing generates is refused by name rather than answered with a null.

**A declared default is answered for too.** EF Core treats every column with a database default as
store-generated: it leaves the column out of the INSERT and reads the server's value back through
`OUTPUT`. A lake knows that value — the dacpac declared it — so the default is stamped into the rows
being written and the answer comes off those same rows, which is what keeps a `getdate()` default
from being one thing in the file and another in the caller's hand. A column with no declared default
that the caller did not send is still refused by name.

**A declared reference is kept on delete.** A dacpac's foreign keys are read, and a `DELETE` of a
row something still points at fails the way SQL Server fails it — error 547, naming the constraint —
rather than quietly leaving an orphan. The check is over the *merged view*, since a row pointing at
this one may live in any layer, and it runs before anything is hidden.

**`ON DELETE CASCADE` is performed**, as the same delete against the table that pointed — and again
against whatever pointed at that, however deep the chain goes. The rows it takes may live in any
layer, so it hides them exactly as a delete does, and the count answered for is the target's own,
as SQL Server answers it. Every level still answers for the references that do *not* cascade: a row
two tables down held by one of those refuses the whole delete, before anything goes. Where a cascade
cannot be performed — into a table this lake will not write to or has no key for, or one that could
reach back to where it started, which SQL Server refuses to declare at all — the reference is kept
as one that refuses instead, and the reason is logged at startup. Orphaning the rows is the one
answer that is wrong either way.

**`ON DELETE SET NULL` and `SET DEFAULT` are performed too**, as what they mean: the rows that
pointed stay where they are, with the pointing columns emptied — to NULL, or to the column's declared
default where it has one, which is what SQL Server does with a `SET DEFAULT` that has none. That is
an update rather than a delete, so nothing is hidden and nothing recurses: the rows are still there
afterwards, and nothing below them is orphaned. A clear reaches below a cascade like anything else in
the chain. It has the same requirements a cascade has — a child this lake will write to, and a key to
shadow the rewritten row with — plus one of its own: a reference pointing with part of the child's
*own* key cannot be cleared, since emptying a key column would collapse every cleared row onto one
key. Where it cannot be performed the reference is kept as one that refuses, and the reason is
logged at startup.

What is not done: the insert side is unchecked (a row may name a parent that is not there, and a
`SET DEFAULT` may write one), and a reference to columns that are not the table's key is skipped — a
delete collects the key, so that is what a reference can be checked against. Nothing is enforced over
files another process edits between runs; `duckpg` sees what its own writes do.

**A declared scalar function is published as a macro.** A dacpac's `SqlScalarFunction` becomes a
DuckDB macro beside the tables, so `[dbo].[Doubled]([order_id])` resolves onto the lake exactly as a
table reference does. The body is translated on the tree like any other T-SQL — `+` over text
concatenates, `ISNULL` is a coalesce — with `@parameter` rendering as the macro's own parameter, and
the whole thing cast to what the function declared it returns, so a body DuckDB widens still answers
in the type SQL Server would have. A macro is an expression, so that is the only body that can
become one: anything with a variable, a branch or a second statement is a procedure, and is left
undeclared with the reason logged at startup rather than half-translated. Table-valued functions are
not read at all.

**A store-generated key needs a dacpac that declares one.** A column the model marks `IsIdentity`
draws from a sequence in the write layer, seeded past the highest value the files already hold when
the table first grows a write branch — so keys carry on from the lake's own state, and a restart
asks the files again rather than trusting what a previous process handed out. That sequence lives in
one duckpg process: two of them serving the same write directory would hand out the same keys. It is
the one place a lake invents a value; everything else it stores is what it was given.

A savepoint is the one thing that is refused rather than approximated. `SAVE TRANSACTION` renders to
nothing — marking a point costs nothing — but DuckDB has no savepoints to return to, so
`ROLLBACK TRANSACTION x` fails loudly instead of quietly keeping the writes it was asked to discard.
EF Core marks one whenever it saves inside a transaction the caller opened.

A handful of functions are answered in .NET rather than rewritten into DuckDB expressions, because
their meaning is .NET's: `CONVERT`'s styles are the date formats `DateTime.ToString` already knows,
and `pwdencrypt` hashes UTF-16 text the way SQL Server does — so a hash your real database wrote
verifies here, and one written here verifies there. A style or a hash format that is not covered is
named rather than approximated. They are registered on the database at startup, so both front doors
find them; they are not for a view, since a view is bound and evaluated on every execution and these
are a managed call per row.

A statement the parser does not cover — DDL, procedural batches, cursors, `DECLARE`, the `MERGE`
branches that add or remove rows — is refused with a syntax error naming it, rather than passed
through to fail somewhere less obvious. `LIKE` patterns use `%` and `_`; SQL Server's
`[a-z]` ranges have no DuckDB equivalent.

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
  - name: color
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

### Defaults

A `SqlDefaultConstraint` fills in the column where a row has no value for it — a row that leaves the
column out and one that spells out a null read the same by the time a file has been scanned, so both
get the default. The expression is T-SQL and goes through the same translator as any statement, so
`(getdate())`, `('new')` and `((0))` all mean what they say.

**In the read layers it is evaluated once, when the lake is built.** `GETDATE()` becomes the moment
duckpg started, not the moment a row was read: the view holds a value rather than a function, so a
table scanned twice answers the same both times and a row's stamp does not depend on when someone
looked at it. The same goes for `NEWID()` — one id for the run, not one per row. There is no better
answer available: a row that was already in a file when duckpg opened it never said when it was
written.

**A written row is stamped as it is written.** The write layer declares the default on its own
table, as the expression rather than as the frozen value, so an `INSERT` that omits the column gets
`GETDATE()` answered then and there — and persists with the value in it, so the file says what the
row is without the dacpac standing next to it. Nothing fills in the write layer afterwards: a row
written with an explicit `NULL` stays null, because the write layer says what it holds.

`SUSER_SNAME()` — the other half of a stock audit column — is answered too, as a string rather
than as a principal DuckDB would have to keep: the session's login name for a statement a client
sent, and the account duckpg itself runs as for a default, since nobody is connected when the lake
is built. `USER_NAME()` and `ORIGINAL_LOGIN()` say the same thing.

A default DuckDB cannot answer at all (`NEWSEQUENTIALID()` and friends) is dropped with a warning,
on both sides, and the column keeps its `NULL`.

### Views

The dacpac's own views are published beside the tables they read, so a report a client already
knows by name is there without being rewritten as a layer. The query is T-SQL and goes through the
same translator as a statement a client sends: `[dbo]` lands on the lake's schema, so a view over
`[dbo].[orders]` reads the stacked layers and everything a view of it could reasonably do —
`ISNULL`, `TOP`, joins, a view of a view — comes with it.

Order does not matter. A view that reads another is retried once the other is in, and a view that
still fails when nothing else can be published is named in a warning and left out rather than
stopping the lake. A view whose name a layer already carries as a table is left out too — the files
win. Views are read-only: they are DuckDB views over the published ones, and a write to one is
refused by DuckDB rather than rewritten onto a layer.

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
| `listen` | `--pgwire`, `-l` | PostgreSQL listen address. Default `127.0.0.1:55432`; port 0 binds a free one. |
| `tds` | `--tds` | TDS listen address, e.g. `127.0.0.1:1433`. Off unless set. |
| `schema` | `--schema` | Schema the published views live in, and the front of every session's search path. Default `lake`. |
| `layers` | positional | Layer directories, lowest first. |
| `write` | `--write`, `-w` | Directory holding the writable top layer. |
| `writeFormat` | `--write-format` | `Parquet` (default), `Json` or `Yaml`, for tables with no file yet. |
| `writable` | `--writable` | Accept writes with no directory; they are lost on exit. |
| `materialize` | `--materialize` | Collapse the layers into real tables; a delta goes out at shutdown. |
| `store` | `--store` | DuckDB database file a materialized lake's tables live in, rather than memory. |
| `storeMode` | `--store-mode` | `Keep` (default): the file is the state. `Spill`: only where the tables live. |
| `sortSmallTables` | `--no-sort-small-tables` | Sort and limit a small materialized table's rows here rather than in DuckDB. On by default; the flag turns it off. |
| `serializeTransactions` | `--serialize-transactions` | One transaction at a time; the next waits for it. |
| `defaultKey` | `--key`, `-k` | Key for tables that name none, applied only where the columns exist. |
| `dacpac` | `--dacpac` | The declared schema. Autodetected from the layers when absent. |
| `cache` | `--cache` | Directory for merged copies of multi-layer tables, as ZSTD parquet. |
| `installDuckDb` | — | Fetch the native DuckDB when the machine has none, rather than failing. |
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

- Trust auth only, on both protocols. No TLS, no SCRAM; TDS refuses encryption outright, so
  SqlClient needs `Encrypt=False`. Bind to localhost.
- Statement description runs the query `LIMIT 0` to learn its shape, so describing is not free and
  a statement that cannot be wrapped in a subquery falls back to `NoData`.
- DML rewriting is textual — it handles `UPDATE t [AS a] SET a = …, b = … [FROM …] WHERE …` and
  `DELETE FROM t WHERE …`, not CTEs, `DELETE … USING`, or subqueries in the target.
- Statements are re-planned per execution; no plan cache.
- The `COPY` protocol (`\copy`, `NpgsqlBinaryImporter`) is not implemented.
- The catalog is built from the filesystem at startup and on `CALL duckpg_reload()`; no watcher.
- Nothing compacts the lower layers: the write layer grows until someone rewrites the files below.
- Two instances writing the same layer directory will overwrite each other. One writer per
  directory.
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

The tests carry their own DuckDB — the native library is pulled out of `DuckDB.NET.Bindings.Full`
by a build target and dropped next to the test binary — so a clean checkout and a clean CI runner
both run them with nothing installed.

`dotnet pack -c Release` produces the tool package.

## License

MIT.
