# The two front doors

Both are opt-in — `listen` opens the PostgreSQL one, `tds` opens SQL Server's — and a lake needs at
least one. They share one lake, one catalog and one write layer.

## The lake's schema, on either door

Tables are published into one schema, `lake` by default and `--schema` otherwise, and it goes in
front of every session's search path, so `SELECT * FROM orders` finds it on a fresh connection and
`current_schema()` says which one answered. Neither database's default would have been right on its
own: DuckDB's is `main` and PostgreSQL's is `public`, so a lake publishing into either would be the
wrong one for half its callers. Set `--schema public` if a tool of yours writes `public.orders`
outright, as an EF Core model built for PostgreSQL does, and the unqualified form keeps working.

The schema is not the database, though it stands in for one unless `--database` says otherwise: a
lake serves one database, neither door routes on its name, and what the name is for is the ORM
writing it into a migration and the tool showing it in a connection list. Set it to whatever the
database this lake stands in for is called, and the connection strings a lake hands out carry that
instead.

`main` stays behind the lake's schema in the path, because the `pg_catalog` shims live there and
every client that reads the catalog needs them. The TDS door never needed any of this: `[orders]` and
`[dbo].[orders]` alike are written into the lake's schema by the T-SQL renderer, whatever it is
called.

## Transactions, and serializing them

`--serialize-transactions` lets one transaction run at a time, across both doors. Two things go wrong
without it, neither of which an application written against SQL Server or PostgreSQL has reason to
expect: DuckDB does not make a second writer wait, but refuses one of two open transactions the moment
they touch the same row — and, worse, a transaction fixes its **catalog** snapshot when it begins, so
a write branch created by anybody afterwards is invisible to it and it cannot create one itself
either. A transaction that begins, reads, and only then writes to a table someone else wrote to first
therefore fails with `Table with name … does not exist` against a table that is plainly there.
Ordering the writes cannot close that; ordering the transactions can.

So a session takes the lake's turn at `BEGIN` — or at its first write, outside one — and gives it up
when that transaction ends; the next waits indefinitely, the way a real database does with the default
`LOCK_TIMEOUT`. A read outside a transaction never waits; a read-only transaction does take the turn,
since nothing says in advance that it will stay read-only. It is off by default: a lake serving one
connection pays a lock for nothing.

## The PostgreSQL door

`--pgwire` (or `listen:` in the file) is the default door, on `127.0.0.1:55432`. What a client can
rely on:

- Startup, trust auth, SSL/GSS refusal, `ParameterStatus`, `BackendKeyData` — psql and Npgsql connect.
- Simple query protocol, multi-statement batches, transaction status.
- Extended protocol: Parse/Bind/Describe/Execute/Sync, portals, `maxRows` suspension.
- Explicit `Prepare()`, transactions, `NpgsqlBatch`, connection pooling.
- PG-accurate SQLSTATEs, so errors are recoverable and cancellation is `OperationCanceledException`.
- Text **and binary** result formats, binary parameters including PG's base-10000 `numeric`.
- Cancellation over the second connection → `duckdb_interrupt`.
- psql introspection: `\dv`, `\d`.
- The catalog a GUI client reads: a view definition by name (`pg_get_viewdef('"lake"."orders"'::regclass)`),
  `pg_statio_user_tables`, `quote_ident`. A relation's size on disk is answered with NULL — what a
  lake publishes is a view over files.
- The function list: `pg_get_function_identity_arguments`, and a `pg_proc` carrying the
  `proiswindow` DuckDB's lacks — nothing is one, since DuckDB reports a window function as an
  aggregate.
- `pg_constraint` publishes the keys and references the **lake** declares, since those are rules over
  the merged view and not constraints DuckDB holds. A declared unique is not among them: a layered
  lake does not keep one.

Type mapping covers the scalar types; `LIST`, `STRUCT` and `MAP` are surfaced as text holding their
JSON rendering, which is what a PG client can actually consume. Npgsql is the client held to a
conformance bar, and `ClientTests` is that bar: the open-ended cost of a PostgreSQL frontend is
emulating `pg_catalog` well enough for each client, and every client fails differently. psql works
too, but it is not what the shims are maintained for.

## The TDS door

`--tds 127.0.0.1:1433` (or `tds:` in the file) opens a second listener speaking the protocol
SQL Server speaks, so an application built on `Microsoft.Data.SqlClient` reads the same lake with no
driver change:

```csharp
using var connection = new SqlConnection("Server=127.0.0.1,1433;Database=lake;User ID=sa;Encrypt=False");
using var command = new SqlCommand("SELECT TOP 10 [order_id], ISNULL([note], '') FROM [dbo].[orders]", connection);
```

**`Encrypt=False` is required.** duckpg answers PRELOGIN with `ENCRYPT_NOT_SUP`, because TDS encrypts
the login packet even when the session itself is plaintext, and that needs a certificate this tool has
no business owning. Bind it to localhost. Both doors share one lake, one catalog and one write layer:
a row inserted over TDS is in the same file a `psql` session reads a moment later.

**The packet size is the server's to decide.** A client asks for one — SqlClient's `Packet Size`
defaults to 8000 — and the login response answers with what the door will actually use, which is
`tdsPacketSize`, 32767 by default. Lowering it is for a client that cannot be talked out of a small
buffer; nothing else wants smaller packets.

| What SqlClient does | What answers it |
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
| `SqlBulkCopy` — `INSERT BULK` and the bulk load stream | the same insert path a statement takes: keys, references and defaults hold, one transaction per stream |

Not implemented: TLS and SQL logins are not verified (trust auth, as on the PostgreSQL side), MARS,
table-valued parameters, output parameters, and `sys.*` / `INFORMATION_SCHEMA` emulation — so SSMS
and EF Core scaffolding will not introspect the lake, though hand-written queries run.

## The T-SQL it accepts

A client sends T-SQL; DuckDB does not speak it. duckpg **parses** it — lexer, recursive-descent
parser, and a renderer that emits DuckDB SQL from the tree — so `'a' + b` concatenates and `1 + 2`
adds, which no rewriting on text could tell apart. The same translator reads a dacpac's views, scalar
functions and default expressions.

**[tsql.md](tsql.md)** is the list: identifiers and the lake's schema, `TOP` and `OFFSET … FETCH`,
the function and type equivalents, the write shapes EF Core and LLBLGen send, `#tables`, application
locks, what is answered in .NET, and what is refused by name.
