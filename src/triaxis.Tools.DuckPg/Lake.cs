using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

/// A configured lake, ready to serve: the DuckDB catalog behind it and the listener in front.
/// Views live in an in-memory catalog rebuilt from the layer directories on every start, so the
/// files are the state and this is only what is currently made of them.
sealed class Lake : IDisposable
{
    readonly DuckDBConnection duck = new("Data Source=:memory:");

    public Catalog Catalog { get; }

    public PgServer Server { get; }

    /// Null unless a TDS address was configured; a second front door is opt-in.
    public TdsServer? Tds { get; }

    public Lake(Config config, ILoggerFactory loggers)
    {
        duck.Open();
        using (var command = duck.CreateCommand())
        {
            command.CommandText = Shims.Macros;
            command.ExecuteNonQuery();
        }

        var write = new WriteLayer(config, loggers.CreateLogger<WriteLayer>());
        if (write.Directory is { } directory) Directory.CreateDirectory(directory);

        var schema = new DacpacSchema(config, loggers.CreateLogger<DacpacSchema>());
        Catalog = new Catalog(config, write, schema, loggers.CreateLogger<Catalog>());
        Catalog.Build(duck);

        var gateway = new Gateway(config, Catalog, write, duck, loggers.CreateLogger<Gateway>());
        Server = new PgServer(config, gateway, duck, loggers);
        if (config.Tds is { Length: > 0 } tds) Tds = new TdsServer(tds, gateway, duck, loggers);
    }

    public Task ListenAsync(CancellationToken cancellation) =>
        Tds is null
            ? Server.ListenAsync(cancellation)
            : Task.WhenAll(Server.ListenAsync(cancellation), Tds.ListenAsync(cancellation));

    public void Dispose() => duck.Dispose();
}
