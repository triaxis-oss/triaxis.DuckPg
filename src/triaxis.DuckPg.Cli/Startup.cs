using Microsoft.Extensions.Configuration;

namespace triaxis.DuckPg.Cli;

/// Where every command's configuration comes from, in one place because scopes only order each
/// other within a single source: this machine's file, then this account's, then the environment,
/// and over all three the file `--config` names -- named deliberately, and so the last word before
/// the arguments.
///
/// A file in the working directory is read only when it is named, and never otherwise. Arguments
/// alone are enough to serve a directory, and a tool that helped itself to a `duckpg.yaml` from
/// whatever directory it happened to start in would be reading a lake nobody pointed it at -- and
/// would hand a bake the file describing the lake being served rather than the one being written
/// out. Named, the file has to exist, which `LakeCommand.Configured` is where it is said: a typo is
/// an error rather than a silent fall back to defaults. The machine's and the user's own files are
/// not that: they are somewhere nobody starts by accident, and what they hold is how this machine
/// runs duckpg.
internal static class Startup
{
    /// Taking the hook takes the generated entry point's logging with it, hence `UseDefaultLogging`
    /// -- and its `appsettings.json`, which is no loss when every file duckpg reads is YAML. The
    /// hook itself runs before the command line is parsed, and takes no parse result of its own;
    /// `Parse` is the one the host goes on to use.
    [Configure]
    public static void Configure(IToolBuilder builder)
    {
        // Before the parse below, since it is what puts `--verbosity` and its shorthands in the
        // tree, and the parse is cached from here on.
        builder.UseDefaultLogging();

        var named = builder.Parse().GetValue<string?>("--config");

        builder.UseScopedConfiguration(scoped => scoped
            .AddYamlOverrides("duckpg/config.yaml")
            .AddEnvironmentOverrides("DUCKPG_")
            .Add(ConfigurationScope.Override, configuration =>
            {
                // Optional here and required in `Configured`, which is the only difference between a
                // sentence naming the file and a FileNotFoundException out of the configuration
                // provider, thrown while the host is still being built and caught by nothing.
                if (named is { Length: > 0 } path)
                    configuration.AddYamlFile(Path.GetFullPath(path), optional: true, reloadOnChange: false);
            }));
    }
}
