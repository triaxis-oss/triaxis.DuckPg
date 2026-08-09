using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace triaxis.DuckPg;

/// Registers a lake in a service collection. Everything it is made of is a service, so a host owns
/// its lifetime and its logging rather than the lake owning them -- which is what makes the call in
/// a test fixture and the call in the tool the same call.
///
/// `AddDuckPg` is one lake, owned by the host: it registers an `IHostedService`, so the host starts
/// and stops it. Without a host, resolve `Lake` and start it directly; the registration costs
/// nothing then.
///
/// `AddDuckPgFactory` is many lakes, each owned by whoever asked for it -- a lake per partition,
/// say, where every one has different layers. Those register no hosted service, because the caller
/// starts them.
public static class DuckPgServiceCollectionExtensions
{
    /// Configured in code.
    public static IServiceCollection AddDuckPg(this IServiceCollection services, Action<Config> configure)
    {
        services.AddOptions<Config>().Configure(configure);
        return services.AddDuckPgServices();
    }

    /// Configured from a `duckpg.yaml`, or any other section a `Config` binds to.
    public static IServiceCollection AddDuckPg(this IServiceCollection services, IConfiguration configuration,
                                               Action<Config>? configure = null)
    {
        var options = services.AddOptions<Config>().Bind(configuration);
        if (configure is not null) options.Configure(configure);
        return services.AddDuckPgServices();
    }

    /// Already built -- what the tool has once its arguments have won over its configuration file.
    public static IServiceCollection AddDuckPg(this IServiceCollection services, Config config)
    {
        services.AddSingleton(config);
        return services.AddDuckPgServices();
    }

    /// A factory for lakes a caller starts and disposes itself. Registers no lake and no hosted
    /// service: `Config` arrives per call rather than per container.
    public static IServiceCollection AddDuckPgFactory(this IServiceCollection services)
    {
        services.AddDuckPgCommon();
        services.TryAddSingleton<IDuckPgLakeFactory, DuckPgLakeFactory>();
        return services;
    }

    /// What every entry point needs before anything else: the bindings find the machine's DuckDB
    /// through a resolver, which has to be in place before the first native call rather than at
    /// some point after it.
    static IServiceCollection AddDuckPgCommon(this IServiceCollection services)
    {
        DuckDbLibrary.Register();

        services.AddLogging();
        services.TryAddSingleton<IDuckDbInstaller, DuckDbInstaller>();
        return services;
    }

    static IServiceCollection AddDuckPgServices(this IServiceCollection services)
    {
        services.AddDuckPgLake();

        // Hosted only here: a factory-built lake is started by whoever asked for it.
        services.AddHostedService(p => p.GetRequiredService<Lake>());
        return services;
    }

    /// A bake, which is the same catalog over the same layers with nothing in front of it. The
    /// lake's own registrations rather than a second set of them, so a catalog that grows a
    /// dependency does not have to be remembered here too -- what listens is registered and never
    /// resolved, which costs a factory nobody calls.
    internal static IServiceCollection AddDuckPgBake(this IServiceCollection services, Config config)
    {
        services.AddDuckPgLake(config);
        services.TryAddSingleton<Bake>();
        return services;
    }

    /// The lake itself, with the configuration it is to be built from.
    internal static IServiceCollection AddDuckPgLake(this IServiceCollection services, Config config)
    {
        services.AddSingleton(config);
        return services.AddDuckPgLake();
    }

    static IServiceCollection AddDuckPgLake(this IServiceCollection services)
    {
        services.AddDuckPgCommon();
        services.AddOptions<Config>();

        // A Config registered directly wins, which is how the already-built overload gets away with
        // binding nothing.
        services.TryAddSingleton(p => p.GetRequiredService<IOptions<Config>>().Value);

        // One DuckDB behind the whole lake: the catalog is built on it and every session borrows
        // from it, so it is the one thing that cannot be transient.
        // A store makes it a database on disk rather than one in memory; everything else about it
        // is the same, including that every session borrows a connection from this one.
        services.TryAddSingleton(p => new DuckDBConnection(
            p.GetRequiredService<Config>().Store is { Length: > 0 } store
                ? $"Data Source={store}"
                : "Data Source=:memory:"));
        services.TryAddSingleton<WriteLayer>();
        services.TryAddSingleton<DacpacSchema>();
        services.TryAddSingleton<Catalog>();
        services.TryAddSingleton<Gateway>();
        services.TryAddSingleton<PgServer>();
        services.TryAddSingleton<TdsServer>();

        // Assembled by hand, because what a lake is made of is internal and a public constructor
        // could not take it -- which is the point: the parts are not the contract.
        services.TryAddSingleton(p => new Lake(
            p.GetRequiredService<Config>(), p.GetRequiredService<Catalog>(),
            p.GetRequiredService<Gateway>(), p.GetRequiredService<PgServer>(),
            p.GetRequiredService<TdsServer>(), p.GetRequiredService<DuckDBConnection>(),
            p.GetRequiredService<IDuckDbInstaller>()));

        return services;
    }
}
