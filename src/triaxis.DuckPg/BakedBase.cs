using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// The copy of a baked database a run actually serves, and the reason serving one is cheap. The base
/// itself is never opened for writing: a thousand runs share one file and every one of them is
/// entitled to the state it was baked with, so what a run gets is bytes of its own. Copying is the
/// whole cost -- 9 ms for a seed-sized lake against the seconds that building the same schema takes,
/// which is why a bake is written with small blocks.
///
/// Where the copy goes is the store when there is one and a scratch file otherwise, and only the
/// scratch one is this to delete. A store already holding a kept lake is left exactly as it is: the
/// file is the state, and copying over it would throw away everything ever written to it.
sealed class BakedBase(Config config, ILogger<BakedBase> logger) : IDisposable
{
    string? scratch;

    /// The database file to open, or null for one held in memory. Answered once, because the copy
    /// happens here and the connection is built from what comes back.
    public string? Path => path ??= Resolve();
    string? path;

    string? Resolve()
    {
        if (config.Base is not { Length: > 0 } source) return config.Store;

        // A kept store is the state and has already been served from; the base is what it was made
        // out of, once, and re-copying would be that state thrown away rather than kept.
        if (config.Store is { Length: > 0 } store)
        {
            if (config.StoreMode == StoreMode.Keep && File.Exists(store))
            {
                logger.LogDebug("{Store} is already there and kept, so {Base} is not copied again", store, source);
                return store;
            }

            Copy(source, store);
            return store;
        }

        // Nothing shared is named after something a second lake would name the same way: a thousand
        // runs of the same base are a thousand copies, and they are each other's neighbours.
        scratch = Directory.CreateTempSubdirectory("duckpg-").FullName;
        var copy = System.IO.Path.Combine(scratch, System.IO.Path.GetFileName(source));
        Copy(source, copy);
        return copy;
    }

    void Copy(string source, string target)
    {
        if (System.IO.Path.GetDirectoryName(target) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        // The write-ahead log beside it is part of the state, and a base that was checkpointed on
        // the way out has none -- but one copied without it would be read as of the last checkpoint,
        // silently missing whatever the bake did after it.
        File.Copy(source, target, overwrite: true);
        foreach (var log in (string[])[source + ".wal"])
            if (File.Exists(log)) File.Copy(log, target + ".wal", overwrite: true);
            else if (File.Exists(target + ".wal")) File.Delete(target + ".wal");

        logger.LogDebug("serving a copy of {Base} at {Target}", source, target);
    }

    /// The scratch copy goes with the run that made it. A store does not: it was named by whoever
    /// wanted it to outlive this.
    public void Dispose()
    {
        if (scratch is null) return;
        try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        scratch = null;
    }
}
