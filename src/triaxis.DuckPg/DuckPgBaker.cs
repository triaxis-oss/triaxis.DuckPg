using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// What a bake writes, which is two quite different things: a layer to stack under the next lake,
/// or a whole lake to serve in place of one.
public enum BakeFormat
{
    /// A directory holding one parquet a table, read back as an ordinary layer -- it stacks, its
    /// declared defaults are left to whoever reads it, and the configuration above it still applies.
    Parquet,

    /// The database a materialized lake would hold: collapsed tables, their keys and indexes, and
    /// the declared views and macros. Served with `Config.Base`, which needs no dacpac, key or
    /// configuration of its own, and holds every declared default already stamped.
    Database,
}

/// Writes a lake down instead of serving it, as a layer or as a database. Which one is the caller's
/// to say; unsaid, it is taken from the name, since `.duckdb` means one of them and nothing else
/// does.
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

    /// Builds the lake `config` describes and writes it to `target`. `format` unset is taken from
    /// the name -- `.duckdb` is a database and anything else a directory of parquet -- and named is
    /// what it says, whatever the name looks like. `blockSize` is only read for a database.
    Task BakeAsync(Config config, string target, BakeFormat? format = null,
                   int blockSize = DefaultBlockSize, CancellationToken cancellation = default);
}

sealed class DuckPgBaker(IServiceProvider parent) : IDuckPgBaker
{
    public async Task BakeAsync(Config config, string target, BakeFormat? format = null,
                                int blockSize = IDuckPgBaker.DefaultBlockSize,
                                CancellationToken cancellation = default)
    {
        var database = (format ?? Bake.Inferred(target)) == BakeFormat.Database;

        // A database bake is a materialized lake written into the file it is named after, so it is
        // that configuration that gets built -- on a copy, since the one it was handed describes a
        // lake served from layers and is nobody's to rewrite.
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
