# Embedding a lake in your own process

The tool is a thin shell over a library, so a test can have the same lake in-process — a real
PostgreSQL and a real TDS front door, served over loopback, against files it wrote a moment ago. The
point is not to fake a database: it is to hold your *client* stack — SqlClient, EF Core, whatever you
actually ship — to the same wire it will meet in production, with no executable on `PATH`, no port
to coordinate, and a lake that lives and dies with the test. `triaxis.DuckPg` is the lake and both
front doors; `triaxis.DuckPg.Cli` is the `duckpg` command, which carries its own copy.

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
the host is up, `lake.Endpoint` is the port to connect to. Both doors are opt-in, and a lake needs at
least one, so a consumer speaking only TDS sets `Listen = null` and opens no listener it never uses.

For more than one lake — one per tenant, one per exported database — register a factory instead. Each
lake it hands back owns everything it was built from, so there is one thing to dispose and nothing to
dispose in order:

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
```

Lakes from a factory are independent, so starting several concurrently is ordinary. A factory-built
lake registers no hosted service, because the caller starts it; `AddDuckPg` is the one a host owns.
Either way, what cannot work is said before anything opens: a layer directory or dacpac that is not
there, a cache inside a layer, or no front door at all throws `DuckPgConfigurationException` naming
the part that is wrong, rather than a lake that starts empty and a binder error much later.

**Bring a native DuckDB.** Neither package carries one. Either the machine already has it, or
`DuckDB.NET.Data.Full` in your project brings the native for every RID, or `installDuckDb: true`
fetches the matching version the first time a lake finds none — see
[the native library](duckdb.md).

## Baking a lake from your own process

`IDuckPgBaker` is the pair to `IDuckPgLakeFactory`: it takes a `Config` per call and writes what that
lake publishes out instead of serving it. `BakeFormat.Parquet` writes a directory of one file a table,
read back as an ordinary layer; `BakeFormat.Database` writes the whole lake as a database, which
`Config.Base` then serves without a dacpac, a key or a configuration of its own. Left unsaid it is
taken from the name, `.duckdb` meaning a database.

```csharp
var services = new ServiceCollection().AddLogging().AddDuckPgBaker();
await using var provider = services.BuildServiceProvider();

await provider.GetRequiredService<IDuckPgBaker>()
              .BakeAsync(new Config { Layers = [seed], Dacpac = dacpac }, "seed.duckdb");
```

Which is what a fixture wants when the same initial state is served over and over — bake once, then
give every test its own lake over the same file:

```csharp
services.AddDuckPg(new Config { Base = "seed.duckdb", Writable = true, Listen = "127.0.0.1:0" });
```

Nothing is scanned, described, parsed, merged or keyed to start one, which on a 300-table lake is
643 ms against 1371 from its layers and a dacpac. The base is copied on the way up and never written
to, so the runs cannot see each other; with `Writable` the copy goes at exit, and with `Write` the
delta goes out as a layer beside it. See
[performance](performance.md#baking-the-whole-lake-as-a-database).

