using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// Fetching the native DuckDB on demand, for a caller that wants it now rather than when a lake
/// starts -- provisioning a machine, which is all `--install-duckdb-only` does.
/// `Config.InstallDuckDb` is the same fetch, made where a lake would otherwise fail to start.
public interface IDuckDbInstaller
{
    /// Downloads the library these bindings were built against unless one is already there, and
    /// answers where it ended up.
    Task<string> InstallAsync(CancellationToken cancellation = default);
}

sealed class DuckDbInstaller(ILogger<DuckDbInstaller> logger) : IDuckDbInstaller
{
    public Task<string> InstallAsync(CancellationToken cancellation = default) =>
        DuckDbDownload.InstallAsync(logger, cancellation);
}
