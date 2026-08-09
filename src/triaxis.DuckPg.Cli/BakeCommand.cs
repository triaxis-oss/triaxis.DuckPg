using Microsoft.Extensions.DependencyInjection;

namespace triaxis.DuckPg.Cli;

[Command("bake", Description = "Writes what a stack of layers publishes out as parquet, one file a table. Reading the result is the same lake as reading the layers it came from -- what it saves is parsing them again.")]
public class BakeCommand : LakeCommand
{
    [Option("--out", "-o", Required = true, Description =
        "Where the bake goes: a directory, written as one parquet a table and read back as an " +
        "ordinary layer, or a path ending in .duckdb, written as the database a materialized lake " +
        "would hold. A directory must be outside the layers.")]
    public string Out { get; set; } = "";

    [Option("--block-size", Description =
        "Block size a baked database is created with, in bytes. Small is what makes a lake of many " +
        "tables a small file and so a cheap copy; raise it where the tables are big enough for the " +
        "metadata of small blocks to cost more than the bytes it saves.")]
    public int BlockSize { get; set; } = IDuckPgBaker.DefaultBlockSize;

    [Inject] private readonly IDuckPgBaker _baker = null!;

    public static void Configure(IToolBuilder builder)
    {
        AddConfigFile(builder);
        builder.ConfigureServices(services => services.AddDuckPgBaker());
    }

    public Task ExecuteAsync(CancellationToken cancellation) =>
        Guarded(() => _baker.BakeAsync(Configured(), Path.GetFullPath(Out), BlockSize, cancellation));
}
