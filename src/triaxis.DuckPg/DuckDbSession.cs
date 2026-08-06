using DuckDB.NET.Data;

namespace triaxis.DuckPg;

/// A connection of a session's own onto the lake's database. DuckDB.NET duplicates an in-memory
/// connection and refuses to duplicate any other -- "Duplication of the connection is only supported
/// for in-memory connections" -- so a lake kept in a file opens the file again instead. That reaches
/// the same database rather than a second one, since the driver holds a single instance per
/// connection string, which is what duplicating an in-memory connection is doing anyway.
static class DuckDbSession
{
    public static DuckDBConnection Of(DuckDBConnection root) =>
        root.ConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            ? (DuckDBConnection)root.Duplicate()
            : new DuckDBConnection(root.ConnectionString);
}
