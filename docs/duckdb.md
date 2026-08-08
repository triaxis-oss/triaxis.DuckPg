# The native DuckDB library

Nothing here carries a native DuckDB, because every platform's library together is 420 MB and that is
not a dependency's decision to make. A lake finds one at startup, and where it looks is the same for
the `duckpg` tool and for a lake embedded in your own process.

## Where it is looked for

1. `DUCKDB_LIBRARY`, pointing at the library file itself. It outranks everything below.
2. A copy in the local application data directory, put there by `installDuckDb` or by
   `--install-duckdb-only`. It is preferred to whatever the machine has, since it is known to answer
   the C API this build speaks.
3. The machine's own — `brew install duckdb`, `apt install libduckdb-dev`. Homebrew's prefixes
   (`$HOMEBREW_PREFIX`, `/opt/homebrew`, `/usr/local`, linuxbrew) and the `opt/duckdb/lib` keg
   beneath each are probed on their own, because neither macOS nor Linux looks there by default.
4. What the project brought: `DuckDB.NET.Data.Full` added to an embedding project ships the native
   for every RID.

With none of those, the error says where it looked and what the ways out are, and the tool exits 69.

## Fetching one

`installDuckDb: true`, or `--install-duckdb`, fetches the version these bindings were built against
— from DuckDB's own releases, nothing newer — the first time a lake starts and finds none, and reuses
it forever after: one download per machine, not per run. The library lands in the local application
data directory; one that got half written is replaced.

`duckpg --install-duckdb-only` is that same fetch and nothing else: it provisions the machine and
exits without serving, which is what a setup script wants. `IDuckDbInstaller` is the same thing for a
caller embedding a lake. Nothing is ever downloaded unless one of these asks for it.

## A version that is not the one

A DuckDB of another version usually still works, so it is a warning rather than a refusal:

```
DuckDB 1.4.1 loaded from /usr/lib/libduckdb.so, where these bindings speak 1.5.5's C API
```

If a lake then fails somewhere odd, this line is the first thing to check.
