# The native DuckDB library

Nothing here carries a native DuckDB, because every platform's library together is 420 MB and that is
not a dependency's decision to make. A lake finds one at startup, and where it looks is the same for
the `duckpg` tool and for a lake embedded in your own process.

## Where it is looked for

1. `DUCKDB_LIBRARY`, pointing at the library file itself. It outranks everything below.
2. A copy in the local application data directory, put there by `--install-duckdb` or by
   `installDuckDb: true`. It is preferred to whatever the machine has, since it is known to answer
   the C API this build speaks.
3. The machine's own — `brew install duckdb`, `apt install libduckdb-dev`. Homebrew's prefixes
   (`$HOMEBREW_PREFIX`, `/opt/homebrew`, `/usr/local`, linuxbrew) and the `opt/duckdb/lib` keg
   beneath each are probed on their own, because neither macOS nor Linux looks there by default.
4. What the project brought: `DuckDB.NET.Data.Full` added to an embedding project ships the native
   for every RID.

With none of those, the error says where it looked and what the ways out are, and the tool exits 69.

## Fetching one

`duckpg --install-duckdb` downloads the version these bindings were built against — from DuckDB's own
releases, nothing newer — leaves it in the local application data directory, and exits without
serving. Running it again with the library already there does nothing; one that got half written is
replaced.

For an embedded lake, `installDuckDb: true` does the same fetch the first time a lake finds no
library, and reuses it forever after: one download per machine, not per run. `IDuckDbInstaller` is
that fetch on demand, for a caller that would rather provision than discover. Nothing is ever
downloaded unless one of these asks for it.

## A version that is not the one

A DuckDB of another version usually still works, so it is a warning rather than a refusal:

```
DuckDB 1.4.1 loaded from /usr/lib/libduckdb.so, where these bindings speak 1.5.5's C API
```

If a lake then fails somewhere odd, this line is the first thing to check.
