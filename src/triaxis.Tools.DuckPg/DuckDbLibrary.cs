using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuckDB.NET.Native;

namespace triaxis.Tools.DuckPg;

/// The slim DuckDB.NET package carries no native library -- the point of using it is to link
/// against the DuckDB already installed on the machine. Neither macOS nor Linux looks in a
/// package manager's prefix by default, so `duckdb` is resolved by hand.
public static class DuckDbLibrary
{
    /// Escape hatch for a DuckDB that lives somewhere else entirely.
    public const string PathVariable = "DUCKDB_LIBRARY";

    /// The DuckDB these bindings were built against. A library answering another C API is worse
    /// than none, so a download asks for this one by name.
    public static string Version { get; } =
        typeof(DuckDBNativeConnection).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "";

    public static string FileName =>
        OperatingSystem.IsWindows() ? "duckdb.dll"
        : OperatingSystem.IsMacOS() ? "libduckdb.dylib"
        : "libduckdb.so";

    /// Where a downloaded library is kept. Versioned, so bumping the bindings looks for a matching
    /// one rather than loading whatever was fetched for the build before.
    public static string Downloaded => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "duckpg", Version, FileName);

    static bool registered;

    /// Runs on its own for anything referencing this assembly; callable directly for anything that
    /// wants to be explicit about it. Registering twice is an error, so it happens once.
    [ModuleInitializer]
    public static void Register()
    {
        if (registered) return;
        registered = true;
        NativeLibrary.SetDllImportResolver(typeof(DuckDBNativeConnection).Assembly, Resolve);
        RootNestedTypes();
    }

    /// DuckDB.NET materialises a STRUCT by calling Activator.CreateInstance on
    /// Dictionary&lt;string, object&gt;. Trimming drops that constructor's metadata, so it is declared
    /// as a dependency here. A MAP is materialised the same way but over the column's own key and
    /// value types, which cannot all be named ahead of time -- a lake that queries MAP columns has
    /// to declare its own combinations the same way.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(Dictionary<string, object>))]
    static void RootNestedTypes() { }

    /// Nothing found here lets the runtime's own probing have a go, which is what finds a library
    /// sitting next to the binary.
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == "duckdb" && Locate() is { } found ? found.Handle : IntPtr.Zero;

    /// Where the DuckDB in use was found, or null when the runtime's own probing found it instead --
    /// next to the binary, or somewhere on the loader's path.
    public static string? LoadedFrom => Locate()?.Path;

    /// What the loaded library answers for. Not always what was asked for: DuckDB's C API changes
    /// between releases, and the bindings speak one of them.
    public static string? LoadedVersion => NativeMethods.Startup.DuckDBLibraryVersion()?.TrimStart('v');

    static (string Path, IntPtr Handle)? Locate()
    {
        foreach (var candidate in SearchPath)
            if (Plausible(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return (candidate, handle);

        return null;
    }

    /// Whether a file is worth trying to load: loading a half-written library takes the process
    /// down with SIGBUS rather than failing, and a DuckDB is tens of megabytes.
    static bool Plausible(string path) => File.Exists(path) && new FileInfo(path).Length >= 1 << 20;

    /// A library that is really there and really loads -- what a download has to end up with, and
    /// what makes another one unnecessary.
    public static bool Usable(string path) => Plausible(path) && NativeLibrary.TryLoad(path, out _);

    /// Everywhere a DuckDB is looked for, in order -- for saying so when there is none.
    public static IEnumerable<string> SearchPath => Candidates();

    static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
            yield return configured;

        // A fetched library is the version these bindings were built against; a machine's own is
        // whatever it happens to have, so an exact match goes ahead of a guess.
        yield return Downloaded;

        var file = FileName;

        // Both the prefix's lib and the keg it links from, since a formula is not always linked.
        foreach (var prefix in Prefixes())
        {
            yield return Path.Combine(prefix, "lib", file);
            yield return Path.Combine(prefix, "opt", "duckdb", "lib", file);
        }

        yield return Path.Combine("/usr/lib", file);
    }

    static IEnumerable<string> Prefixes()
    {
        if (Environment.GetEnvironmentVariable("HOMEBREW_PREFIX") is { Length: > 0 } brew)
            yield return brew;

        yield return "/opt/homebrew";              // Homebrew, Apple silicon
        yield return "/usr/local";                 // Homebrew on Intel macOS, and the usual Unix prefix
        yield return "/home/linuxbrew/.linuxbrew"; // Homebrew on Linux
    }
}
