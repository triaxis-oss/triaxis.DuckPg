using System.IO.Compression;
using System.Runtime.InteropServices;

namespace triaxis.Tools.DuckPg;

/// Fetching the native library from DuckDB's own releases, for a machine that has no DuckDB and no
/// package manager anyone wants to argue with. Never on its own -- only when asked.
static class DuckDbDownload
{
    /// The archive DuckDB publishes for this machine. macOS is one universal build; the rest are
    /// per architecture.
    public static string Url(string version) =>
        $"https://github.com/duckdb/duckdb/releases/download/v{version}/libduckdb-{Platform()}.zip";

    public static string Platform() =>
        OperatingSystem.IsMacOS() ? "osx-universal"
        : (OperatingSystem.IsWindows() ? "windows-" : "linux-") +
          (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64");

    /// Downloads the library duckpg's bindings were built against and leaves it where the search
    /// path already looks. Returns where it landed.
    public static async Task<string> InstallAsync(ILogger logger, CancellationToken cancellation)
    {
        var url = Url(DuckDbLibrary.Version);
        var target = DuckDbLibrary.Downloaded;

        // Already fetched, and it still loads: there is nothing to fetch.
        if (DuckDbLibrary.Usable(target))
        {
            logger.LogInformation("DuckDB {Version} is already in {Path}", DuckDbLibrary.Version, target);
            return target;
        }

        logger.LogInformation("downloading {Url}", url);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}");

        await UnpackAsync(await response.Content.ReadAsStreamAsync(cancellation), target, cancellation);

        if (!DuckDbLibrary.Usable(target))
            throw new InvalidOperationException($"{target} was downloaded but does not load on this machine");

        logger.LogInformation("DuckDB {Version} installed in {Path}", DuckDbLibrary.Version, target);
        return target;
    }

    /// Unpacks the archive into a directory of its own and renames that directory into place. The
    /// staging directory sits beside the target rather than in the system's temp, so publishing it
    /// is a rename on one filesystem -- a copy across two could fail half way, which is the thing
    /// being avoided. Nothing partial is ever visible where the search path looks, and a download
    /// that dies leaves neither a library nor a scratch directory behind.
    internal static async Task UnpackAsync(Stream download, string target, CancellationToken cancellation)
    {
        var home = Path.GetDirectoryName(target)!;
        var staging = home + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(home)!);
        Discard(staging);
        Directory.CreateDirectory(staging);

        try
        {
            // To disk rather than into memory: ZipArchive has to seek to the archive's own
            // directory, and holding a whole release in RAM to avoid one file is a poor trade.
            var zip = Path.Combine(staging, "download.zip");
            await using (var file = File.Create(zip))
                await download.CopyToAsync(file, cancellation);

            var name = Path.GetFileName(target);
            using (var archive = ZipFile.OpenRead(zip))
                (archive.GetEntry(name) ?? throw new InvalidOperationException($"the archive carries no {name}"))
                    .ExtractToFile(Path.Combine(staging, name));

            File.Delete(zip);

            // Whatever is there answered `Usable` with no, so it is half a download or another
            // machine's -- worth replacing rather than keeping.
            Discard(home);
            Directory.Move(staging, home);
        }
        catch
        {
            Discard(staging);
            throw;
        }
    }

    static void Discard(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
