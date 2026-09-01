# duckpg — working notes

A PostgreSQL and SQL Server wire-protocol frontend for a stack of YAML/JSON/parquet layers,
executing against DuckDB. `README.md` is the user-facing contract and `docs/` is the reference for
what a lake does; read those first. This file is the map for someone changing the code, and
`docs/internals/` is why the code is the way it is.

## Where things are

Two projects, and a package each: `src/triaxis.DuckPg` is the lake, and `src/triaxis.DuckPg.Cli` is
the `duckpg` command and nothing else. The tool packs its whole publish output, so it carries the
library rather than depending on it.

`TestLake` stays in the test project. What a fixture owes a caller is a directory of layers and a
connection string, and a caller embedding a lake writes its own layers anyway -- shipping one would
publish a temp-directory convention as API.

| File | What it owns |
|---|---|
| `Startup.cs` | Where the whole tool's configuration comes from: the machine's file, the user's, the environment, and the file `--config` names over all three. |
| `LakeCommand.cs` | What both commands take: the layers, the bound configuration with the arguments over it, and the answer for a missing DuckDB. |
| `ServeCommand.cs` | The `duckpg` command itself: the doors and everything only a running lake has. |
| `BakeCommand.cs` | `duckpg bake`: the same lake, written out instead of served. |
| `DuckPgServiceCollectionExtensions.cs` | `AddDuckPg` and `AddDuckPgFactory`: what a lake is made of, as registrations. |
| `DuckPgLakeFactory.cs` | Lakes on demand, each owning the container it came out of. |
| `DuckPgBaker.cs` | `IDuckPgBaker`: the same, for a lake written down instead of served. |
| `DuckDbInstaller.cs` | `IDuckDbInstaller`: fetching DuckDB, for a lake starting and for `--install-duckdb` alike. |
| `Config.cs` | The bound configuration. Every property here is part of the contract. |
| `Lake.cs` | The composition root: DuckDB connection, schema, catalog, gateway, listeners. Tests use it too. |
| `Layer.cs` | Scanning a layer directory, reading a source, writing one back. YAML ↔ JSON. |
| `Catalog.cs` | The published shape: which tables exist, their columns, keys, the view SQL, and the dacpac's own views. |
| `Bake.cs` | `duckpg bake`: the same catalog with no doors, written out as one parquet a table or as the database a materialized lake would hold. |
| `BakedBase.cs` | The copy of a baked database a run serves, and the scratch file it goes in when there is no store. |
| `WriteLayer.cs` | The top layer: DuckDB tables loaded from files, and persisted back to them. |
| `DacpacSchema.cs` | The declared schema as a service: finds the dacpac, reads `model.xml` for columns, keys, uniques, defaults and views. No DacFx. |
| `Gateway.cs` | Statement translation: catalog shims, GUC no-ops, DML rewriting. `Shims` lives here. |
| `Doorway.cs` | A front door's sockets: the listener, the connections it accepted, and closing both. |
| `PgWire.cs`, `PgTypes.cs`, `PgServer.cs`, `PgSession.cs` | The PostgreSQL protocol. Rarely the thing that is wrong. |
| `TdsWire.cs`, `TdsTypes.cs`, `TdsServer.cs`, `TdsSession.cs` | The TDS protocol: packets, tokens, RPC, transactions. |
| `TSql/` | Lexer, parser, AST and DuckDB renderer for the T-SQL a client sends. |
| `HostFunctions.cs` | The SQL Server functions answered in .NET: `pwdencrypt`, `pwdcompare`, `CONVERT` styles. |
| `SqlText.cs` | Enough SQL scanning to find top-level keywords without a parser. |
| `SortedRows.cs` | `IRows`, and a result held and sorted here rather than by DuckDB. |
| `DuckDbLibrary.cs` | Finding the machine's DuckDB, and the AOT dependencies DuckDB.NET needs. |
| `DuckDbDownload.cs` | Fetching that library from DuckDB's releases, when asked and only then. |

## Where the reasoning is

Each note under `docs/internals/` sits beside the user-facing doc for the same thing, and says what
that doc cannot: what was measured, what was tried and taken back out, and which client's shape a
rule exists for. Most of it was expensive to learn, so a change that contradicts one of them wants
to answer the note before it lands -- and a change that outdates one edits it in the same commit.

| Internals | Beside | About |
|---|---|---|
| [lake.md](docs/internals/lake.md) | [layers.md](docs/layers.md), [performance.md](docs/performance.md) | layer scanning and shapes, the write path, materializing and the store, two threads and one DuckDB, startup and shutdown |
| [schema.md](docs/internals/schema.md) | [schema.md](docs/schema.md) | reading a dacpac, and enforcing keys, references, cascades, defaults and identities over a stack of files |
| [tsql.md](docs/internals/tsql.md) | [tsql.md](docs/tsql.md) | the parser and renderer, and how each ORM's write is found and rewritten |
| [wire.md](docs/internals/wire.md) | [protocols.md](docs/protocols.md) | what each protocol demanded and does not say out loud, and the type names both doors key off |
| [performance.md](docs/internals/performance.md) | [performance.md](docs/performance.md) | what the merge costs, why there is no plan cache, and the small-table sort path |

## Rules that outlive a change

Each of these is the short form; the note behind it is where the argument is.

- **A layer's format is a property of the file, not of the layer.** The only layer with a role is
  the write layer, and anything special-casing "the parquet layer" is a step backwards.
  [lake](docs/internals/lake.md#layers-and-files)
- **Layer sequence numbers decide everything.** Read layers are 0..n-1 in configured order and the
  write layer is n; `QUALIFY … ORDER BY _seq DESC` is what shadows, and a tombstone hides only rows
  with `_seq < writeSeq`. A partition column joins the key, and `hive_partitioning` is always passed
  explicitly. [lake](docs/internals/lake.md#layers-and-files)
- **A parquet source's columns come out of the footers, and nothing about a lake's shape is learned
  by binding a statement per table.** What a bind costs is flat, so the only way to make a start
  cheaper is to issue fewer statements; `Layer.Footers` asks once for every file, and a source it
  cannot answer for is described rather than approximated.
  [performance](docs/internals/performance.md#what-a-start-asks-the-catalog)
- **The write layer holds effective state, not a log**, it is persisted only after DuckDB commits,
  and a write branch is earned rather than assumed.
  [lake](docs/internals/lake.md#the-write-path)
- **Nothing shared is named after something a second lake would name the same way.** A scratch tree
  is a `Directory.CreateTempSubdirectory`, not a path derived from the layer.
  [lake](docs/internals/lake.md#layers-and-files)
- **A declared key, a reference and a cascade are rules over the merged view**, not constraints on a
  table -- DuckDB's own would see only the rows this process wrote.
  [schema](docs/internals/schema.md#keys)
- **Using a baked layer is semantically identical to using the layers it was baked from.** That is
  the whole of what `duckpg bake` promises: it is why the command is told the key and the write
  layer, why a virtual column and a declared default are left to the run that reads the file, why a
  partitioned layer is written back partitioned -- and why what cannot be kept identical is refused
  rather than written. [lake](docs/internals/lake.md#baking)
- **A deferred table is published as the merge, never as an empty one.** `Config.Lazy` finds the
  tables a statement is about by reading its text for names the catalog knows, and a scan of text can
  miss: what a miss costs has to be the layered price rather than the wrong answer. A failed collapse
  leaves the table deferred, and `Flush` skips what was never collapsed.
  [lake](docs/internals/lake.md#materializing-and-the-store)
- **A baked database is a materialized lake somebody else already collapsed**, served from a copy
  and never written to, with the shutdown delta measured against it -- and it freezes the defaults a
  materialized lake freezes. `Config.Collapsed`, not `config.Materialize`, is what the catalog asks.
  [lake](docs/internals/lake.md#baking-a-database-and-serving-one)
- **A declared default is a value in the read layers and an expression in the write layer.**
  [schema](docs/internals/schema.md#defaults)
- **The dialect is translated on the tree, never on the text**, and a statement the parser does not
  cover is refused with its position rather than passed through.
  [tsql](docs/internals/tsql.md#the-tree-never-the-text)
- **The type catalog describes the OIDs the gateway puts on the wire**, not DuckDB's own types.
  [wire](docs/internals/wire.md#types-on-the-wire)
- **A view is bound on every execution**, so everything a view definition says is paid for by every
  query touching it -- and there is no plan cache, because a held plan bakes in the statistics it
  was made against. [performance](docs/internals/performance.md#the-merge-and-paying-it-once)
- **A DuckDB connection is not two threads' to share**, and the order is always the turn and then
  `gate`, never the reverse. [lake](docs/internals/lake.md#two-threads-and-one-duckdb)
- **A shutdown that is not reached writes nothing**, so every way out has to reach the flush; a lake
  owns what it was built from, or nothing at all.
  [lake](docs/internals/lake.md#starting-stopping-and-what-a-lake-owns)
- **A lake that has stopped is a lake nobody is connected to.** A session left on the wire holds a
  DuckDB connection onto the lake's database, which is the whole lake outliving the last reference
  to it. `Doorway` owns the sockets as well as the listener.
  [lake](docs/internals/lake.md#starting-stopping-and-what-a-lake-owns)
- **The lake's schema goes in front of every session's search path, and `main` stays behind it**;
  both front doors are opt-in, and a lake needs one.
  [lake](docs/internals/lake.md#starting-stopping-and-what-a-lake-owns)
- **The public surface is Config, Lake, IDuckPgLakeFactory, IDuckPgBaker, IDuckDbInstaller,
  LayerFormat, BakeFormat, StoreMode, DuckPgConfigurationException and DuckDbLibrary -- nothing
  else.**
  The catalog, the gateway, the two protocols and `TSql/` are internal, which is why `Lake`'s
  constructor is internal and `AddDuckPg` assembles it by hand: a public constructor would have to
  take public parameters, and that would make every part of a lake an API. `InternalsVisibleTo`
  covers the tool and the suite.
- **AOT-clean**: no reflection-based serialization, no `JsonSerializer.Serialize<object>`. The
  project ships portable but the analyzers stay on, so a regression shows up as a build warning
  (and warnings are errors here).

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
