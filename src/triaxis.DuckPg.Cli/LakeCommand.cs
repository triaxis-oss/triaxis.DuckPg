using Microsoft.Extensions.Configuration;

namespace triaxis.DuckPg.Cli;

/// What every command built out of a lake's configuration takes: the layers, the file the rest of it
/// can be written in, and the arguments that win over that file. Serving adds the doors and
/// everything only a running lake has; baking adds where the parquet goes.
public abstract class LakeCommand : LoggingCommand
{
    [Argument(Description = "Layer directories, lowest first. Overrides the configuration.")]
    public string[] Layers { get; set; } = [];

    [Option("--config", "-c", Description = "Configuration file. None is read unless one is named.")]
    public string? ConfigPath { get; set; }

    [Option("--write", "-w", Description = "Directory holding the topmost layer, the one that accepts writes.")]
    public string? Write { get; set; }

    [Option("--key", "-k", Description = "Column identifying a row, for tables that name no key of their own. Repeatable.")]
    public string[] Key { get; set; } = [];

    [Option("--dacpac", Description = "A .dacpac to take column names, order, types and keys from.")]
    public string? Dacpac { get; set; }

    [Option("--install-duckdb", Description = "Download the DuckDB library on the way up if none is found, rather than failing.")]
    public bool InstallDuckDb { get; set; }

    [Inject] private readonly IConfiguration _configuration = null!;

    /// The file, if there was one, with the arguments over the top of it. Paths in the file are
    /// relative to the file and paths in an argument to the working directory, which is the one
    /// thing here that cannot be worked out from either alone.
    protected Config Configured()
    {
        // The same answer a missing layer directory gets, for the same reason: what was named is not
        // there, and falling back to defaults would serve something nobody asked for.
        if (ConfigPath is { } named && !File.Exists(named))
            throw new CommandErrorException("configuration file not found: {Path}", Path.GetFullPath(named))
            { ExitCode = 64 };

        var config = _configuration.Get<Config>() ?? new Config();
        // `layers:` may be written as a single directory; only the list form binds on its own.
        if (_configuration["layers"] is { Length: > 0 } single) config.Layers = [single];
        config.ResolvePaths(ConfigPath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path))!
            : Directory.GetCurrentDirectory());

        if (Layers.Length > 0) config.Layers = [.. Layers.Select(Path.GetFullPath)];
        if (Write is not null) config.Write = Path.GetFullPath(Write);
        if (Key.Length > 0) config.DefaultKey = Key;
        if (Dacpac is not null) config.Dacpac = Path.GetFullPath(Dacpac);
        if (InstallDuckDb) config.InstallDuckDb = true;
        return config;
    }

    /// The tool links against the machine's own DuckDB, and the first native call is where a missing
    /// one would otherwise surface: a DllNotFoundException out of the bindings, naming a library the
    /// reader never asked for by that name. Say where it was looked for instead, and what the ways
    /// out are -- and where one was found that these bindings do not speak, say that too, since
    /// everything past this point would fail one call at a time.
    protected async Task<T> Guarded<T>(Func<Task<T>> build)
    {
        try
        {
            var built = await build();

            if (DuckDbLibrary.LoadedVersion is { } loaded && loaded != DuckDbLibrary.Version)
                Logger.LogWarning(
                    "DuckDB {Loaded} loaded from {Path}, where these bindings speak {Expected}'s C " +
                    "API -- `--install-duckdb` fetches a matching one",
                    loaded,
                    DuckDbLibrary.LoadedFrom ?? "the loader's own search path",
                    DuckDbLibrary.Version);

            return built;
        }
        catch (DuckPgConfigurationException problem)
        {
            // EX_USAGE: what it was told to build does not add up, and the message names the part.
            throw new CommandErrorException("{Problem}", problem.Message) { ExitCode = 64 };
        }
        catch (DllNotFoundException)
        {
            throw new CommandErrorException(
                "DuckDB {Version} was not found. Looked in:" + Environment.NewLine + "{Searched}" +
                Environment.NewLine +
                "Install it (`brew install duckdb`, `apt install libduckdb-dev`), point " +
                DuckDbLibrary.PathVariable + " at the library, or add `--install-duckdb` to fetch " +
                "it into {Downloaded} on the way up.",
                DuckDbLibrary.Version,
                string.Join(Environment.NewLine, DuckDbLibrary.SearchPath.Select(path => "  " + path)),
                DuckDbLibrary.Downloaded)
            { ExitCode = 69 }; // EX_UNAVAILABLE: the tool is fine, what it needs is not here
        }
    }

    protected Task Guarded(Func<Task> build) =>
        Guarded(async () => { await build(); return 0; });
}
