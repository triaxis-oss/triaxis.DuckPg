using System.IO.Compression;

namespace triaxis.DuckPg.Tests;

/// The tool links against the machine's own DuckDB, so what it looks for -- and what it offers to
/// fetch when there is none -- has to keep agreeing with the bindings it was built against.
public class DuckDbLibraryTests
{
    [Fact]
    public void TheDownloadIsTheVersionTheBindingsWereBuiltAgainst()
    {
        var bindings = typeof(DuckDB.NET.Data.DuckDBConnection).Assembly.GetName().Version!;

        Assert.Equal(bindings.ToString(3), DuckDbLibrary.Version);
        Assert.Equal($"https://github.com/duckdb/duckdb/releases/download/v{bindings.ToString(3)}/" +
                     $"libduckdb-{DuckDbDownload.Platform()}.zip",
                     DuckDbDownload.Url(DuckDbLibrary.Version));
    }

    /// A fetched library is the version asked for; a machine's own is whatever it has. So it is
    /// looked for ahead of the system prefixes, behind only the variable that names one outright,
    /// and it is kept under the version it answers for rather than under one name forever.
    [Fact]
    public void AFetchedLibraryIsPreferredToWhateverTheMachineHas()
    {
        var searched = DuckDbLibrary.SearchPath.ToList();

        Assert.Equal(DuckDbLibrary.Downloaded, searched.First(path => path != Environment.GetEnvironmentVariable(DuckDbLibrary.PathVariable)));
        Assert.EndsWith(Path.Combine("duckpg", DuckDbLibrary.Version, DuckDbLibrary.FileName),
                        DuckDbLibrary.Downloaded);
    }

    /// A half-written library is passed over rather than loaded: loading one takes the process down
    /// with SIGBUS instead of failing, which leaves nothing able to report it or replace it.
    [Fact]
    public void AFileTooSmallToBeALibraryIsNeverLoaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duckpg-{Guid.NewGuid():N}{Path.GetExtension(DuckDbLibrary.FileName)}");
        File.WriteAllBytes(path, new byte[1000]);

        try
        {
            Assert.False(DuckDbLibrary.Usable(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// A download is unpacked beside where it will live and renamed into place, so the search path
    /// never sees a directory that is half a release -- whatever the network does to the transfer.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnpackedLibraryArrivesWholeOrNotAtAll(bool truncated)
    {
        var root = Path.Combine(Path.GetTempPath(), $"duckpg-{Guid.NewGuid():N}");
        var target = Path.Combine(root, DuckDbLibrary.Version, DuckDbLibrary.FileName);
        var staging = Path.GetDirectoryName(target) + ".part";

        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(DuckDbLibrary.FileName).Open())
            entry.Write(new byte[2 << 20]);

        // A transfer that stopped short is not a zip at all, which is the failure a partially
        // written library would otherwise be indistinguishable from.
        var bytes = archive.ToArray();
        var download = new MemoryStream(truncated ? bytes[..(bytes.Length / 2)] : bytes);

        try
        {
            if (truncated)
                await Assert.ThrowsAnyAsync<Exception>(() => DuckDbDownload.UnpackAsync(download, target, TestContext.Current.CancellationToken));
            else
                await DuckDbDownload.UnpackAsync(download, target, TestContext.Current.CancellationToken);

            Assert.Equal(!truncated, File.Exists(target));
            if (!truncated) Assert.Equal(2 << 20, new FileInfo(target).Length);

            // Nothing is left for the next attempt to trip over, and the zip itself does not stay
            // where the library goes.
            Assert.False(Directory.Exists(staging));
            if (!truncated) Assert.Equal([target], Directory.GetFiles(Path.GetDirectoryName(target)!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// The library actually in use answers for itself, which is what tells a mismatch from a match.
    [Fact]
    public void TheLoadedLibrarySaysWhichVersionItIs()
    {
        Assert.Equal(DuckDbLibrary.Version, DuckDbLibrary.LoadedVersion);
    }
}
