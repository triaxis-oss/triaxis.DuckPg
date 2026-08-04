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

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "duckdb") return IntPtr.Zero;

        foreach (var candidate in Candidates())
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;

        return IntPtr.Zero; // nothing found: let the runtime's own probing report the failure
    }

    static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
            yield return configured;

        var file = OperatingSystem.IsWindows() ? "duckdb.dll"
                 : OperatingSystem.IsMacOS() ? "libduckdb.dylib"
                 : "libduckdb.so";

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
