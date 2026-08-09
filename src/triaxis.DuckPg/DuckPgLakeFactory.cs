using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// Starts lakes, each with its own configuration and its own everything else. A lake per
/// partition -- one per exported database, each with different layers -- is a fleet of lakes rather
/// than a fleet of service providers, and the lake each call hands back owns what it was built from.
public interface IDuckPgLakeFactory
{
    /// Builds a lake, starts it, and hands it over. Disposing it stops it and releases everything it
    /// was made of; nothing else has to be kept or disposed in order.
    Task<Lake> StartAsync(Config config, CancellationToken cancellation = default);
}

sealed class DuckPgLakeFactory(ILoggerFactory loggers) : IDuckPgLakeFactory
{
    public async Task<Lake> StartAsync(Config config, CancellationToken cancellation = default)
    {
        // Before a container exists, so nothing has been built when the answer names what is wrong.
        config.Validate();

        // A container of its own rather than a scope: each lake is configured differently, and a
        // scope cannot bring its own registrations. A fresh container inherits nothing, so the one
        // thing that has to cross is passed across -- without it every logger inside resolves to a
        // null one and a consumer's own logging never hears from the lake.
        // Injected rather than fetched out of the parent's provider: it is an ordinary dependency,
        // and `AddDuckPgCommon` has always registered it.
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddDuckPgLake(config);

        var provider = services.BuildServiceProvider();
        var lake = provider.GetRequiredService<Lake>();
        lake.Owns(provider);

        try
        {
            await lake.StartAsync(cancellation);
        }
        catch
        {
            // A lake that could not start still holds a container and possibly an open DuckDB.
            await lake.DisposeAsync();
            throw;
        }

        return lake;
    }
}
