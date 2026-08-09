using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    [Inject] private readonly ParseResult _parsed = null!;

    /// A configuration file is read when one is named, and never otherwise. Arguments alone are
    /// enough to serve a directory, and a tool that helped itself to a `duckpg.yaml` from whatever
    /// directory it happened to start in would be reading a lake nobody pointed it at -- and would
    /// hand a bake the file describing the lake being served rather than the one being written out.
    /// Named, the file has to exist: a typo is an error rather than a silent fall back to defaults.
    ///
    /// It is named on the command line, so the source can only be added once the arguments are
    /// parsed. The parse itself is registered along with it, because what a subcommand was given is
    /// not the whole of what was typed -- see `Stray`.
    protected static void AddConfigFile(IToolBuilder builder)
    {
        builder.ConfigureConfiguration((context, configuration) =>
        {
            // Optional here and required in `Configured`, which is the only difference between a
            // sentence naming the file and a FileNotFoundException out of the configuration
            // provider, thrown while the host is still being built and caught by nothing.
            if (context.GetInvocationContext().ParseResult.GetValue<string?>("--config") is { Length: > 0 } path)
                configuration.AddYamlFile(Path.GetFullPath(path), optional: true, reloadOnChange: false);
        });

        builder.ConfigureServices(services => services.AddSingleton(builder.Parse()));
    }

    /// Options given to the root command rather than to the one being run. Serving *is* the root
    /// command, so its `--pgwire` and its `--materialize` are also what stands in front of a verb,
    /// and there they bind to the root and the verb is handed nothing -- `duckpg --materialize bake`
    /// bakes without it and says nothing. A subcommand cannot decline what its parent accepts, so
    /// what is left is to notice.
    ///
    /// Only options: a positional in front of a verb is refused by the parser itself as of
    /// triaxis.CommandLine 2.6.0-beta.2, which is what `duckpg ./common bake ./tenant` used to bake
    /// half a lake over.
    ///
    /// A recursive option is every command's by declaration and is the one thing that reads the same
    /// on either side of the verb, so `duckpg -v bake` means what it looks like.
    IEnumerable<string> Stray() =>
        _parsed.RootCommandResult == _parsed.CommandResult
            ? []
            : _parsed.RootCommandResult.Children
                     .OfType<OptionResult>()
                     .Where(option => option is { Implicit: false, Option.Recursive: false })
                     .Select(option => option.Option.Name);

    /// The file, if there was one, with the arguments over the top of it. Paths in the file are
    /// relative to the file and paths in an argument to the working directory, which is the one
    /// thing here that cannot be worked out from either alone.
    protected Config Configured()
    {
        if (Stray().ToList() is { Count: > 0 } before)
            throw new CommandErrorException(
                // Two holes, two arguments: a template with more of the first than the second is
                // logged as nothing at all.
                "{Given} came before `{Verb}`, where it is an option of `duckpg` itself rather than " +
                "of the verb -- everything the verb is to act on has to follow its name.",
                string.Join(", ", before), _parsed.CommandResult.Command.Name)
            { ExitCode = 64 };

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
