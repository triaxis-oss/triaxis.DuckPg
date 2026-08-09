namespace triaxis.DuckPg.Cli;

[Command("bake", Description = "Writes what a stack of layers publishes out as parquet, one file a table. Reading the result is the same lake as reading the layers it came from -- what it saves is parsing them again.")]
public class BakeCommand : LakeCommand
{
    [Option("--out", "-o", Description = "Directory the parquet layer is written into. Must be outside the layers.", Required = true)]
    public string Out { get; set; } = "";

    [Inject] private readonly ILoggerFactory _loggers = null!;

    public static void Configure(IToolBuilder builder) => AddConfigFile(builder);

    public Task ExecuteAsync(CancellationToken cancellation) =>
        Guarded(() => Bake.RunAsync(Configured(), Path.GetFullPath(Out), _loggers, cancellation));
}
