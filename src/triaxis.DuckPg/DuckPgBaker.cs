using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// Writes a lake down instead of serving it. What comes out is decided by the name: a directory
/// takes one parquet a table and is read back as an ordinary layer, and a path ending in `.duckdb`
/// takes the database a materialized lake would hold, served with `Config.Base` and needing no
/// dacpac, key or configuration of its own.
///
/// The pair to `IDuckPgLakeFactory`, and the same bargain: the configuration arrives per call rather
/// than per container, so one process can bake as many lakes as it has configurations for, and each
/// bake owns and releases everything it was built from.
public interface IDuckPgBaker
{
    /// What a baked database is created with when nothing says otherwise. A block is allocated
    /// whole, so a lake of many small tables is mostly blocks -- 300 of them came to 159 MB at
    /// DuckDB's own 256 KB and 18.7 MB at this. It cannot be changed once the file exists.
    public const int DefaultBlockSize = 16384;

    /// Builds the lake `config` describes and writes it to `target`, which is a directory for
    /// parquet and a `.duckdb` path for a database. `blockSize` is only read for the latter.
    Task BakeAsync(Config config, string target, int blockSize = DefaultBlockSize,
                   CancellationToken cancellation = default);
}

sealed class DuckPgBaker(IServiceProvider parent) : IDuckPgBaker
{
    public async Task BakeAsync(Config config, string target, int blockSize = IDuckPgBaker.DefaultBlockSize,
                                CancellationToken cancellation = default)
    {
        // A database bake is a materialized lake written into the file it is named after, so it is
        // that configuration that gets built -- on a copy, since the one it was handed describes a
        // lake served from layers and is nobody's to rewrite.
        var database = Bake.IsDatabase(target);
        if (database) config = Bake.Materialized(config, target);

        // A container of its own, as a lake gets: what a bake is made of is configured per bake.
        var services = new ServiceCollection();
        if (parent.GetService<ILoggerFactory>() is { } loggers) services.AddSingleton(loggers);
        services.AddDuckPgBake(config, database ? blockSize : 0);

        await using var provider = services.BuildServiceProvider();
        var bake = provider.GetRequiredService<Bake>();

        if (database) await bake.WriteDatabaseAsync(target, cancellation);
        else await bake.WriteAsync(target, cancellation);
    }
}
