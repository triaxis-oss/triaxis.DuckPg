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

    /// Where an unqualified name is looked for. DuckDB's own default is `main` and PostgreSQL's is
    /// `public`, so a lake publishing into neither is a lake every unqualified query misses -- which
    /// is what makes the schema's name something a first query has to know. Put it in front of the
    /// path and the name stops mattering: `SELECT * FROM orders` finds the table wherever it was
    /// published.
    ///
    /// `main` stays behind it, because the catalog shims are macros and views created there --
    /// `Shims.Apply` rewrites `pg_catalog.pg_class` to an unqualified `duckpg_pg_class`, and every
    /// client that reads the catalog would stop finding it. The lake goes first so that a table of
    /// its own wins over anything sharing the name.
    ///
    /// Per connection, which is what makes this a session's own rather than the database's. Temp
    /// tables are unaffected: every one duckpg builds says `TEMP`, which names the catalog outright.
    public static void SearchPath(DuckDBConnection connection, Config config)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path = {SqlText.Literal($"{config.Schema},main")}";
        command.ExecuteNonQuery();
    }
}
