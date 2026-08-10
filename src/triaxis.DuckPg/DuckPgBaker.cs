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
    /// What a baked database is created with when nothing says otherwise, and the middle of the
    /// 16 KB to 256 KB DuckDB allows, both ends of which cost something: a block is allocated whole,
    /// so the largest makes a lake of many small tables mostly padding, and a compressed segment has
    /// to fit its block, so the smallest stops a big table compressing at all. It cannot be changed
    /// once the file exists.
    public const int DefaultBlockSize = 65536;

    /// Builds the lake `config` describes and writes it to `target`. `format` unset is taken from
    /// the name -- `.duckdb` is a database and anything else a directory of parquet -- and named is
    /// what it says, whatever the name looks like. `blockSize` is only read for a database.
    Task BakeAsync(Config config, string target, BakeFormat? format = null,
                   int blockSize = DefaultBlockSize, CancellationToken cancellation = default);
}

sealed class DuckPgBaker(ILoggerFactory loggers) : IDuckPgBaker
{
    public async Task BakeAsync(Config config, string target, BakeFormat? format = null,
                                int blockSize = IDuckPgBaker.DefaultBlockSize,
                                CancellationToken cancellation = default)
    {
        var database = (format ?? Inferred(target)) == BakeFormat.Database;
        if (database)
        {
            // Against what was handed over rather than the copy: `Materialized` turns it into a
            // store, and a complaint about a `store` nobody set names something the caller cannot
            // find. The session-dependent check is spelled out here for the same reason -- run over
            // the copy it would call itself `materialize`.
            config.ValidateShape();
            config.ValidateSessionless("a baked database");
            config = Materialized(config, target);
        }

        // A container of its own rather than a scope: each bake is configured differently, and a
        // scope cannot bring its own registrations. A fresh container inherits nothing, so the one
        // thing that has to cross is passed across -- without it every logger inside resolves to a
        // null one and a consumer's own logging never hears from the bake.
        // Injected rather than fetched out of the parent's provider: it is an ordinary dependency,
        // and `AddDuckPgCommon` has always registered it.
        var services = new ServiceCollection();
        services.AddSingleton(loggers);
        services.AddDuckPgBake(config, database ? blockSize : 0);

        await using var provider = services.BuildServiceProvider();
        var bake = provider.GetRequiredService<Bake>();

        if (database) await bake.WriteDatabaseAsync(target, cancellation);
        else await bake.WriteAsync(target, cancellation);
    }

    /// What a name says a bake is to write, for a caller that did not say. A layer's format is a
    /// property of the file, and this is the same reading applied to the output: `.duckdb` is one
    /// thing and nothing else is. Only ever a default -- `BakeFormat` is what decides.
    const string DatabaseExtension = ".duckdb";

    static BakeFormat Inferred(string target) =>
        Path.GetExtension(target).Equals(DatabaseExtension, StringComparison.OrdinalIgnoreCase)
            ? BakeFormat.Database : BakeFormat.Parquet;

    /// The same lake, collapsed into the file being written rather than served from views. A store
    /// that is the state is exactly what a baked database is, so `Keep` is what it is built as. It
    /// has to be decided here rather than by the bake itself: the connection is built from
    /// `Config.Store`, so the configuration has to say so before there is a container to resolve
    /// anything out of. On a copy, since the one the caller handed over describes a lake served from
    /// layers and is nobody's to rewrite.
    ///
    /// The file is deleted first, or that same `Keep` would have the build open what an earlier bake
    /// left and keep it instead of replacing it.
    static Config Materialized(Config config, string target)
    {
        if (Directory.Exists(target))
            throw new DuckPgConfigurationException(
                $"{target} is a directory, and a bake into a database writes one file");

        // As the parquet bake makes the directory it was pointed at: what is named is where the
        // output goes, not somewhere that has to be there first.
        if (Path.GetDirectoryName(Path.GetFullPath(target)) is { Length: > 0 } parent)
            Directory.CreateDirectory(parent);

        var baked = config.Copy();
        baked.Materialize = true;
        baked.Store = target;
        baked.StoreMode = StoreMode.Keep;
        return baked;
    }
}
