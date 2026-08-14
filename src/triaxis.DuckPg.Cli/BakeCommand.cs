using Microsoft.Extensions.DependencyInjection;

namespace triaxis.DuckPg.Cli;

[Command("bake", Description = "Writes what a stack of layers publishes out as parquet, one file a table. Reading the result is the same lake as reading the layers it came from -- what it saves is parsing them again.")]
public class BakeCommand : LakeCommand
{
    [Option("--out", "-o", Required = true, Description =
        "Where the bake goes, and what --format defaults to: a path ending in .duckdb is a database " +
        "and anything else a directory. A directory must be outside the layers.")]
    public string Out { get; set; } = "";

    [Option("--format", Description =
        "What to write: Parquet for a directory of one file a table, read back as an ordinary layer, " +
        "or Database for the whole lake as a DuckDB file, served with --base. Taken from the name " +
        "when unset -- .duckdb is a database and anything else is a directory.")]
    public BakeFormat? Format { get; set; }

    [Option("--block-size", Description =
        "Block size a baked database is created with, in bytes. Small is what makes a lake of many " +
        "tables a small file and so a cheap copy; raise it where the tables are big enough for the " +
        "metadata of small blocks to cost more than the bytes it saves.")]
    public int BlockSize { get; set; } = IDuckPgBaker.DefaultBlockSize;

    [Inject] private readonly IDuckPgBaker _baker = null!;

    public static void Configure(IToolBuilder builder)
    {
        builder.ConfigureServices(services => services.AddDuckPgBaker());
    }

    public Task ExecuteAsync(CancellationToken cancellation) =>
        Guarded(() => _baker.BakeAsync(Configured(), Path.GetFullPath(Out), Format, BlockSize, cancellation));
}
