using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

enum PlanKind { Rows, Count, NoOp, Empty }

/// A client statement translated into what DuckDB should actually run. `Steps` execute in order;
/// the last one supplies the row count unless `Affected` names a query that knows better, which a
/// rewritten DML statement does -- what it touches is not what its last step writes.
sealed record Plan(PlanKind Kind, string[] Steps, string Tag, string? Affected = null, string? Dirty = null)
{
    public static Plan Rows(string sql) => new(PlanKind.Rows, [sql], "SELECT");
    public static Plan Count(string tag, params string[] steps) => new(PlanKind.Count, steps, tag);
    public static Plan NoOp(string tag) => new(PlanKind.NoOp, [], tag);
    public static readonly Plan Empty = new(PlanKind.Empty, [], "");
}

sealed class PgError(string sqlState, string message) : Exception(message)
{
    public string SqlState { get; } = sqlState;

    /// DuckDB reports errors as prefixed text. The SQLSTATE matters beyond cosmetics: Npgsql drops
    /// the connection on the catch-all XX000, and only reports cancellation properly on 57014.
    public static string SqlStateOf(Exception error) => error switch
    {
        PgError pg => pg.SqlState,
        // An interrupted DuckDB query surfaces as a cancellation, not as a DuckDB error.
        OperationCanceledException => "57014",
        _ => error.Message switch
        {
            var m when m.Contains("INTERRUPT") || m.Contains("cancel", StringComparison.OrdinalIgnoreCase) => "57014",
            var m when m.Contains("Catalog Error") && m.Contains("Table with name") => "42P01",
            var m when m.Contains("Catalog Error") => "42883",
            var m when m.Contains("Binder Error") => "42703",
            var m when m.Contains("Parser Error") => "42601",
            var m when m.Contains("Conversion Error") || m.Contains("Invalid Input") => "22P02",
            var m when m.Contains("Constraint Error") => "23000",
            var m when m.Contains("Out of Range") => "22003",
            _ => "XX000",
        },
    };
}

/// Translates PostgreSQL client statements into DuckDB statements: catalog shims, GUC no-ops,
/// and DML rewriting onto the write layer.
sealed class Gateway(Config config, Catalog catalog, WriteLayer write, DuckDBConnection admin, ILogger<Gateway> logger)
{
    readonly Lock gate = new();

    public Config Config { get; } = config;
    public Catalog Catalog { get; } = catalog;

    /// Statements that produce a result set. DuckDB also allows a bare FROM-first SELECT.
    static readonly HashSet<string> RowProducing =
        ["SELECT", "WITH", "VALUES", "TABLE", "FROM", "DESCRIBE", "SUMMARIZE", "EXPLAIN", "PIVOT", "UNPIVOT",
         "EXECUTE", "CALL", "PRAGMA"];

    /// Settings every PG client pokes at that DuckDB neither has nor needs.
    static readonly HashSet<string> IgnoredSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "extra_float_digits", "application_name", "datestyle", "client_encoding", "client_min_messages",
        "standard_conforming_strings", "intervalstyle", "timezone", "statement_timeout", "lc_monetary",
        "bytea_output", "search_path", "role", "session_authorization", "idle_in_transaction_session_timeout",
        // `SET SESSION AUTHORIZATION DEFAULT` and `RESET ALL` are part of Npgsql's connection reset.
        "authorization", "all",
        // What a SqlClient application sets on connect, none of which changes what a file holds.
        "nocount", "ansi_nulls", "ansi_padding", "ansi_warnings", "arithabort", "concat_null_yields_null",
        "quoted_identifier", "numeric_roundabort", "implicit_transactions", "cursor_close_on_commit",
        "deadlock_priority", "lock_timeout", "textsize", "xact_abort", "fmtonly", "dateformat", "language",
        "transaction",
    };

    static readonly Dictionary<string, string> SettingValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["server_version"] = PgServer.ServerVersion,
        ["server_encoding"] = "UTF8",
        ["client_encoding"] = "UTF8",
        ["datestyle"] = "ISO, MDY",
        ["standard_conforming_strings"] = "on",
        ["transaction_isolation"] = "read committed",
        ["timezone"] = "UTC",
        ["is_superuser"] = "on",
    };

    public Plan Translate(string sql)
    {
        sql = Shims.Apply(sql.Trim());
        logger.LogDebug("{Sql}", sql.ReplaceLineEndings(" "));
        if (sql.Length == 0) return Plan.Empty;

        var verb = SqlText.FirstWord(sql);
        return verb switch
        {
            "INSERT" => RewriteInsert(sql),
            "UPDATE" => RewriteUpdate(sql),
            "DELETE" => RewriteDelete(sql),
            "SET" or "RESET" => PlanSet(sql, verb),
            "SHOW" => PlanShow(sql),
            // The rest of Npgsql's reset script, none of which DuckDB has an equivalent for.
            "DISCARD" or "CLOSE" or "UNLISTEN" or "DEALLOCATE" => Plan.NoOp(verb),
            "BEGIN" or "START" => Plan.Count("BEGIN", "BEGIN TRANSACTION"),
            "COMMIT" or "END" => Plan.Count("COMMIT", "COMMIT"),
            "ROLLBACK" or "ABORT" => Plan.Count("ROLLBACK", "ROLLBACK"),
            "CALL" when Intercepted(sql) is { } intercepted => intercepted,
            _ => RowProducing.Contains(verb) ? Plan.Rows(sql) : Plan.Count(verb, sql),
        };
    }

    // ---- gateway-owned procedures -------------------------------------------------------------

    Plan? Intercepted(string sql)
    {
        var call = sql[SqlText.FindKeyword(sql, "CALL")..];
        if (SqlText.FindKeyword(call, "duckpg_reload") > 0)
        {
            lock (gate) Catalog.Rebuild(admin);
            return Plan.NoOp("CALL");
        }
        return null;
    }

    /// Writes a table's write layer back to its file. Called once a write is committed, so a
    /// rolled-back statement never reaches the disk.
    public void Persist(string name)
    {
        lock (gate)
            if (Catalog.Resolve(null, name) is { Writable: true } table)
                write.Persist(admin, table);
    }

    // ---- settings ------------------------------------------------------------------------------

    static Plan PlanSet(string sql, string verb)
    {
        var rest = sql[verb.Length..].TrimStart();
        foreach (var scope in (string[])["SESSION ", "LOCAL "])
            if (rest.StartsWith(scope, StringComparison.OrdinalIgnoreCase)) rest = rest[scope.Length..].TrimStart();

        var name = SqlText.ReadTableRef(rest, 0).Name;
        return IgnoredSettings.Contains(name) || name.Contains('.') ? Plan.NoOp("SET") : Plan.Count("SET", sql);
    }

    static Plan PlanShow(string sql)
    {
        var name = SqlText.ReadTableRef(sql[4..], 0).Name;
        return SettingValues.TryGetValue(name, out var value)
            ? Plan.Rows($"SELECT {SqlText.Literal(value)} AS {SqlText.Quote(name)}")
            : Plan.Rows(sql);
    }

    // ---- DML rewriting -------------------------------------------------------------------------

    Plan RewriteInsert(string sql)
    {
        var into = SqlText.FindKeyword(sql, "INTO");
        if (into < 0) return Plan.Count("INSERT", sql);

        var reference = SqlText.ReadTableRef(sql, into + 4);
        if (Writable(reference.Schema, reference.Name) is not { } table) return Plan.Count("INSERT", sql);

        var rest = sql[reference.End..].TrimStart();
        var columnList = ExplicitColumnList(rest);
        if (columnList is null)
            rest = $"({string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)))}) {rest}";
        else
            RejectVirtual(table, columnList, "INSERT");

        return Plan.Count("INSERT", $"INSERT INTO {table.WriteName} {rest}") with { Dirty = table.Name };
    }

    /// The parenthesised group after the table name is a column list unless it opens a subquery.
    static List<string>? ExplicitColumnList(string rest)
    {
        if (!rest.StartsWith('('))return null;
        var close = MatchingParen(rest);
        var inner = rest[1..close].Trim();
        return SqlText.FirstWord(inner) is "SELECT" or "VALUES" or "WITH" or "FROM"
            ? null
            : SqlText.SplitList(inner, ',').Select(c => c.Trim('"', ' ')).ToList();
    }

    Plan RewriteUpdate(string sql)
    {
        var reference = SqlText.ReadTableRef(sql, SqlText.FindKeyword(sql, "UPDATE") + 6);
        if (Writable(reference.Schema, reference.Name) is not { } table) return Plan.Count("UPDATE", sql);
        RequireKey(table, "UPDATE");

        var set = SqlText.FindKeyword(sql, "SET", reference.End);
        if (set < 0) throw new PgError("42601", "UPDATE without SET");
        var where = SqlText.FindKeyword(sql, "WHERE", set);
        var assignments = ParseAssignments(sql[(set + 3)..(where < 0 ? sql.Length : where)]);
        var predicate = where < 0 ? "TRUE" : sql[(where + 5)..];

        RejectVirtual(table, assignments.Keys, "UPDATE");

        var projection = string.Join(", ", table.Columns.Select(c =>
            assignments.TryGetValue(c.Name, out var assigned)
                ? $"({assigned}) AS {SqlText.Quote(c.Name)}"
                : SqlText.Quote(c.Name)));
        var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

        // Both the keys being replaced and the rows replacing them have to be computed before
        // anything is tombstoned -- afterwards the view no longer returns them.
        return Plan.Count("UPDATE",
            Keys(table, predicate),
            $"CREATE OR REPLACE TEMP TABLE duckpg_updated AS SELECT {projection} FROM {table.QualifiedName} WHERE {predicate}",
            Tombstone(table),
            Evict(table),
            $"INSERT INTO {table.WriteName} ({columns}) SELECT {columns} FROM duckpg_updated")
            with { Affected = "SELECT count(*) FROM duckpg_updated", Dirty = table.Name };
    }

    Plan RewriteDelete(string sql)
    {
        var from = SqlText.FindKeyword(sql, "FROM");
        if (from < 0) return Plan.Count("DELETE", sql);

        var reference = SqlText.ReadTableRef(sql, from + 4);
        if (Writable(reference.Schema, reference.Name) is not { } table) return Plan.Count("DELETE", sql);
        RequireKey(table, "DELETE");

        var where = SqlText.FindKeyword(sql, "WHERE", reference.End);
        return Plan.Count("DELETE",
            Keys(table, where < 0 ? "TRUE" : sql[(where + 5)..]),
            Tombstone(table),
            Evict(table))
            with { Affected = "SELECT count(*) FROM duckpg_keys", Dirty = table.Name };
    }

    /// The keys a statement touches, taken from the merged view before anything moves.
    static string Keys(Table table, string predicate) =>
        $"CREATE OR REPLACE TEMP TABLE duckpg_keys AS SELECT DISTINCT " +
        $"{string.Join(", ", table.Key.Select(SqlText.Quote))} FROM {table.QualifiedName} WHERE {predicate}";

    /// A tombstone hides the row in every layer below; the same key deleted twice is one tombstone.
    static string Tombstone(Table table) =>
        $"INSERT OR IGNORE INTO {table.TombstoneName} SELECT * FROM duckpg_keys";

    /// The write layer's own copy of a row is deleted outright -- nothing below it to hide.
    static string Evict(Table table) =>
        $"DELETE FROM {table.WriteName} AS w WHERE EXISTS (SELECT 1 FROM duckpg_keys k WHERE " +
        string.Join(" AND ", table.Key.Select(k => $"k.{SqlText.Quote(k)} IS NOT DISTINCT FROM w.{SqlText.Quote(k)}")) + ")";

    static Dictionary<string, string> ParseAssignments(string setClause)
    {
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SqlText.SplitList(setClause, ','))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) throw new PgError("42601", $"malformed assignment `{part}`");
            assignments[part[..eq].Trim().Trim('"')] = part[(eq + 1)..].Trim();
        }
        return assignments;
    }

    Table? Writable(string? schema, string name)
    {
        var table = Catalog.Resolve(schema, name);
        if (table is null) return null;
        if (!table.Writable) throw new PgError("42809", $"{table.Name} is read-only: no write layer accepts it");
        return table;
    }

    static void RequireKey(Table table, string operation)
    {
        if (table.Key.Length == 0)
            throw new PgError("0A000", $"{operation} on {table.Name} needs a `key` in the table config -- " +
                                       "a row in a file has no identity without one");
    }

    static void RejectVirtual(Table table, IEnumerable<string> columns, string operation)
    {
        foreach (var column in columns)
            if (table.Virtuals.Any(v => v.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                throw new PgError("42501", $"{operation} cannot write virtual column `{column}`");
    }

    static int MatchingParen(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        throw new PgError("42601", "unbalanced parentheses");
    }
}

/// Textual stand-ins for PostgreSQL catalog functions DuckDB does not provide.
static class Shims
{
    static readonly (string From, string To)[] Replacements =
    [
        ("pg_catalog.version()", $"'PostgreSQL {PgServer.ServerVersion} (duckpg)'"),
        ("pg_catalog.pg_class", "duckpg_pg_class"),
        ("::pg_catalog.regtype", "::VARCHAR"),
        ("pg_catalog.set_config", "duckpg_set_config"),
        ("pg_catalog.pg_table_is_visible", "duckpg_true"),
        ("pg_catalog.pg_get_userbyid", "duckpg_user"),
        ("pg_catalog.pg_encoding_to_char", "duckpg_encoding"),
        ("pg_catalog.pg_get_expr", "duckpg_empty"),
        ("pg_catalog.", ""),
    ];

    /// Installed once on the shared catalog. `duckpg_pg_class` fills the gaps in DuckDB's pg_class
    /// that psql's \d relies on -- this is the part of a PG frontend that never really finishes.
    public const string Macros = """
        CREATE OR REPLACE MACRO duckpg_set_config(name, value, is_local) AS value;
        CREATE OR REPLACE MACRO duckpg_true(oid) AS true;
        CREATE OR REPLACE MACRO duckpg_user(oid) AS 'duckdb';
        CREATE OR REPLACE MACRO duckpg_encoding(oid) AS 'UTF8';
        CREATE OR REPLACE MACRO duckpg_empty(a, b) AS '', (a, b, c) AS '';
        CREATE OR REPLACE MACRO pg_advisory_unlock_all() AS true;
        CREATE OR REPLACE VIEW duckpg_pg_class AS
            SELECT *, false AS relforcerowsecurity FROM pg_catalog.pg_class;
        CREATE OR REPLACE VIEW pg_namespace AS
            SELECT * FROM pg_catalog.pg_namespace
            UNION ALL
            SELECT * FROM (SELECT * REPLACE (11::BIGINT AS oid, 'pg_catalog' AS nspname)
                           FROM pg_catalog.pg_namespace LIMIT 1);
        CREATE OR REPLACE VIEW pg_type AS
            SELECT oid::BIGINT AS oid, typname, 11::BIGINT AS typnamespace, 10 AS typowner,
                   typlen::BIGINT AS typlen, false AS typbyval, 'b' AS typtype, typcategory,
                   false AS typispreferred, true AS typisdefined, ',' AS typdelim, 0 AS typrelid,
                   0 AS typelem, 0 AS typarray, 0 AS typinput, 0 AS typoutput, 0 AS typreceive,
                   0 AS typsend, false AS typnotnull, 0 AS typbasetype, -1 AS typtypmod,
                   0 AS typndims, 0 AS typcollation, NULL AS typdefault
            FROM (VALUES
                (16, 'bool', 'B', 1),       (17, 'bytea', 'U', -1),      (20, 'int8', 'N', 8),
                (21, 'int2', 'N', 2),       (23, 'int4', 'N', 4),        (25, 'text', 'S', -1),
                (114, 'json', 'U', -1),     (700, 'float4', 'N', 4),     (701, 'float8', 'N', 8),
                (705, 'unknown', 'X', -2),  (1043, 'varchar', 'S', -1),  (1082, 'date', 'D', 4),
                (1083, 'time', 'D', 8),     (1114, 'timestamp', 'D', 8), (1184, 'timestamptz', 'D', 8),
                (1186, 'interval', 'T', 16),(1700, 'numeric', 'N', -1),  (2950, 'uuid', 'U', 16)
            ) t(oid, typname, typcategory, typlen);
        CREATE OR REPLACE VIEW pg_range AS
            SELECT oid AS rngtypid, oid AS rngsubtype, oid AS rngmultitypid, oid AS rngcollation,
                   oid AS rngsubopc, oid AS rngcanonical, oid AS rngsubdiff
            FROM pg_catalog.pg_type WHERE false;
        """;

    /// psql qualifies operators and collations too; DuckDB's parser accepts neither form.
    static readonly System.Text.RegularExpressions.Regex Operator = new(@"OPERATOR\s*\(\s*pg_catalog\.(.+?)\s*\)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex Collate = new(@"\s+COLLATE\s+(pg_catalog\.)?""?default""?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Apply(string sql)
    {
        if (!sql.Contains("pg_catalog.", StringComparison.OrdinalIgnoreCase)) return sql;
        sql = Collate.Replace(Operator.Replace(sql, "$1"), "");
        foreach (var (from, to) in Replacements)
            sql = sql.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return sql;
    }
}
