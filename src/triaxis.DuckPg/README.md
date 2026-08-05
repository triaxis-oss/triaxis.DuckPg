# triaxis.DuckPg

A stack of YAML, JSON and parquet files, served in your own process over the PostgreSQL v3 wire
protocol and — on a second port — the TDS protocol Microsoft.Data.SqlClient speaks. Executed against
DuckDB. Point Npgsql, SqlClient or EF Core at files you wrote a moment ago, and hold your client
stack to the same wire it will meet in production.

Each table is published as a view over its layers, so it can come from a shared YAML seed, a
tenant's JSON overrides and a parquet export at once, with the topmost layer holding a row winning.
The top layer accepts writes, and what a client writes is an ordinary layer file another instance
can read.

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

The lake is an `IHostedService`, so a host owns it. For many lakes at once — one per tenant, one per
exported database — `AddDuckPgFactory` hands out lakes a caller starts and disposes itself.

**Bring a native DuckDB.** This package carries none, because every platform's library together is
420 MB. Use the machine's (`brew install duckdb`, `apt install libduckdb-dev`, or `DUCKDB_LIBRARY`),
add `DuckDB.NET.Data.Full`, or set `installDuckDb` to fetch the matching version once per machine.

`triaxis.DuckPg.Cli` is the same lake as a `duckpg` command.

Full documentation: https://github.com/triaxis-oss/triaxis.DuckPg
