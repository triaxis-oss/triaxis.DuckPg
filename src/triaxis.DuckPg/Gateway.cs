using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

enum PlanKind { Rows, Count, NoOp, Empty }

/// A client statement translated into what DuckDB should actually run. `Steps` execute in order;
/// the last one supplies the row count unless `Affected` names a query that knows better, which a
/// rewritten DML statement does -- what it touches is not what its last step writes.
/// A query that must find nothing, and what to say when it does. What a check stands for is a rule
/// the layers keep rather than DuckDB: a constraint over the merged view is not one a table can
/// declare, since the row it protects may live in any layer.
sealed record Check(string Sql, string Message, string SqlState);

/// The key a plan makes up rather than being given: the step that writes the rows hands it back with
/// `RETURNING`, so what a caller is later told it got is what was actually written down.
sealed record Identity(string Table, string Column)
{
    /// That step has to be read rather than counted -- a statement carrying `RETURNING` reports
    /// nothing through `ExecuteNonQuery` -- and of the keys a multi-row write hands back, the last
    /// is the one a caller is answered with, since that is the row it wrote last.
    public (int Rows, decimal? Value) Read(DbCommand command)
    {
        var rows = 0;
        decimal? value = null;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            value = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
            rows++;
        }
        return (rows, value);
    }
}

sealed record Plan(PlanKind Kind, string[] Steps, string Tag, string? Affected = null, string[]? Dirty = null,
                   string[]? Promoted = null, string[]? Tombstoned = null, Check[]? Checks = null,
                   Identity? Identity = null)
{
    /// Which step returns that key: the last one, unless the plan answers with rows of its own --
    /// and then the one before the answer, since the write comes first and the answer is read off
    /// what it wrote. Derived rather than stored, so a promotion prepended to the plan cannot make
    /// it point at the wrong step.
    public int IdentityStep => Kind == PlanKind.Rows ? Steps.Length - 2 : Steps.Length - 1;

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

    /// One transaction at a time when the lake was asked for it. Not thread-affine, since a
    /// session's loop may resume on another thread between statements.
    readonly SemaphoreSlim turns = new(1, 1);

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

    // ---- serialized transactions ------------------------------------------------------------------

    /// Waits for the lake's turn, indefinitely -- which is what SQL Server does with the default
    /// LOCK_TIMEOUT. False when the option is off, and then nothing is held.
    public bool EnterTurn()
    {
        if (!Config.SerializeTransactions) return false;
        turns.Wait();
        return true;
    }

    public void ExitTurn() => turns.Release();

    /// Everything a materialized lake was given, out as the delta the write directory keeps. Under
    /// `gate` like everything else that touches the lake's own connection: a session's commit
    /// reaches it through `Persist`, and the two must not be on it at once.
    public void Flush()
    {
        lock (gate) Catalog.Flush(admin);
    }

    /// Writes a table's write layer back to its file. Called once a write is committed, so a
    /// rolled-back statement never reaches the disk. A materialized lake has no write layer and
    /// keeps nothing: what it holds goes out once, at shutdown, as a delta.
    /// What an insert added, told to the catalog so a table that outgrows `FastOrder.Small` stops
    /// being sorted here. Only an insert: a materialized table's UPDATE is an evict and a re-insert
    /// of the same rows and its DELETE only removes, so counting either would push a table nothing
    /// grew past the threshold and cost it the fast path for the life of the process.
    public void Grew(Plan plan, int rows)
    {
        if (plan.Tag != "INSERT" || plan.Dirty is not { Length: > 0 } dirty) return;
        lock (gate)
            foreach (var table in dirty) Catalog.Grew(table, rows);
    }

    public void Persist(string name)
    {
        lock (gate)
            if (Catalog.Resolve(null, name) is { Writable: true, Materialized: false } table)
                write.Persist(admin, table);
    }

    // ---- generated keys --------------------------------------------------------------------------

    /// The last key generated for each table, by whichever session wrote it -- which is what
    /// `IDENT_CURRENT` answers, and it is a process's memory rather than a file's: nothing in a
    /// layer says which of its rows was written last, so a restart has nothing to answer with.
    readonly ConcurrentDictionary<string, decimal> identities = new(StringComparer.OrdinalIgnoreCase);

    public void Identified(string table, decimal value) => identities[table] = value;

    public decimal? IdentityOf(string table) => identities.TryGetValue(table, out var value) ? value : null;

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

        // The names the rows arrive under, whether the statement gave them or the table did -- which
        // is what says whether the key is among them.
        List<string> columns = columnList ?? [.. table.Columns.Select(c => c.Name)];
        var returning = SqlText.FindKeyword(rest, "RETURNING");

        // Only where the statement carries every key column: a key the store generates cannot
        // collide with one it generated before, and one the statement leaves out is not there to
        // compare.
        var duplicates = table.Key.All(k => columns.Contains(k, StringComparer.OrdinalIgnoreCase))
            ? Duplicates(table, $"SELECT {KeyList(table)} FROM {Source(columns, returning > 0 ? rest[..returning] : rest)}")
            : [];

        if (returning > 0)
            return RewriteReturning(table, columns, rest, returning, duplicates);

        // A declared identity the statement leaves out is filled where every other value comes
        // from -- in the rows being written, so the same statement decides it and writes it.
        var generated = Generated(table, columns);
        if (generated.Count > 0)
        {
            var source = rest[(MatchingParen(rest) + 1)..].Trim();
            rest = $"({string.Join(", ", columns.Select(SqlText.Quote).Concat(Quoted(generated)))}) " +
                   $"SELECT *, {string.Join(", ", NextValues(table, generated))} FROM ({source}) AS \"_rows\"";
        }

        // A caller with no OUTPUT clause reads its key back through SCOPE_IDENTITY, which is a
        // question about what this statement wrote -- so the write says, rather than the sequence
        // being asked afterwards where another session's insert may already have moved it.
        var identity = Identifying(table, generated);

        return Promoting(table, Plan.Count("INSERT", $"INSERT INTO {table.WriteName} {rest}{Returns(identity)}")
            with { Dirty = [table.Name], Checks = duplicates, Identity = identity });
    }

    /// A declared key is a rule about rows that may live in any layer, so it is kept here rather than
    /// by DuckDB. The write branch's own PRIMARY KEY sees only what this process wrote -- a key a
    /// file below already holds is one it lets through, and the row then quietly shadows the file's
    /// instead of being refused. A materialized table carries no constraint at all: it is built as a
    /// copy of the merge, and `CREATE TABLE AS` keeps no key. So both halves are asked of the rows
    /// a statement is about to write: a key the table already publishes, and one the rows repeat
    /// among themselves.
    ///
    /// `keys` produces the key each row will land under, and -- where the statement replaces rows as
    /// it writes them, which an UPDATE does -- the key each is taking away beside it. A row landing
    /// on a key that is going is landing on nothing; without that half, a key that merely stayed
    /// where it was would read as a collision with the row it belongs to.
    ///
    /// A semi join rather than a correlated EXISTS, and the source read once into a materialized CTE:
    /// what this costs is then one scan of what the table publishes, which for a layered lake is the
    /// merge and is the floor. Measured on a two-layer 25k-row lake: 6.95 ms for the insert form
    /// against 6.45 for a bare `count(*)` over the same view, where a correlated EXISTS cost 7.62;
    /// and 10.87 for the update form against 15.89 for the same question asked with three scans.
    Check[] Duplicates(Table table, string keys, bool replacing = false)
    {
        if (table.Key.Length == 0 || !Config.CheckKeys) return [];

        var matched = string.Join(" AND ", table.Key.Select(k =>
            $"t.{SqlText.Quote(k)} IS NOT DISTINCT FROM r.{SqlText.Quote(k)}"));
        var kept = replacing
            ? $" ANTI JOIN (SELECT {string.Join(", ", table.Key.Select(k => SqlText.Quote(Was(k))))} " +
              "FROM \"_keys\") AS o ON " +
              string.Join(" AND ", table.Key.Select(k =>
                  $"o.{SqlText.Quote(Was(k))} IS NOT DISTINCT FROM r.{SqlText.Quote(k)}"))
            : "";

        return [new Check(
            $"WITH \"_keys\" AS MATERIALIZED ({keys}) " +
            $"SELECT 1 FROM (SELECT {KeyList(table)}, count(*) AS \"_count\" FROM \"_keys\" " +
            $"GROUP BY {KeyList(table)}) AS r WHERE r.\"_count\" > 1 " +
            $"UNION ALL SELECT 1 FROM \"_keys\" AS r " +
            $"SEMI JOIN {table.QualifiedName} AS t ON {matched}{kept} LIMIT 1",
            $"Violation of PRIMARY KEY constraint on \"{table.Name}\". Cannot insert duplicate key in object " +
            $"\"{Config.Schema}.{table.Name}\".",
            "23505")];
    }

    static string KeyList(Table table) => string.Join(", ", table.Key.Select(SqlText.Quote));

    /// What a key was before the statement moved it, kept beside what it becomes.
    static string Was(string key) => "_was_" + key;

    /// The rows an insert lists or selects, as a source of its own -- under the names its column
    /// list gives them, which is what lets the key be read out of it.
    static string Source(List<string> columns, string rows) =>
        $"({rows[(MatchingParen(rows) + 1)..].Trim()}) AS \"_rows\" ({string.Join(", ", columns.Select(SqlText.Quote))})";

    /// An insert that is asked what it wrote -- `OUTPUT INSERTED.<key>`, which is how EF Core gets a
    /// store-generated key back. The rows are materialized first, so what the store generates is
    /// decided once and can be both written down and answered from; the answer is the last step,
    /// which is what makes this a plan that returns rows with a write in front of it.
    Plan RewriteReturning(Table table, List<string> columns, string rest, int returning, Check[] duplicates)
    {
        var select = rest[(MatchingParen(rest) + 1)..returning];
        var from = SqlText.FindKeyword(select, "FROM");
        var answered = rest[(returning + "RETURNING".Length)..];

        // A client writing DuckDB's own `INSERT ... VALUES ... RETURNING` names no source of its
        // own, so the rows it lists become one -- under the names the column list gives them.
        var (projection, source) = from >= 0
            ? (select[(SqlText.FindKeyword(select, "SELECT") + 6)..from], select[from..])
            : (string.Join(", ", columns.Select(SqlText.Quote)),
               $"FROM ({select.Trim()}) AS \"_rows\" ({string.Join(", ", columns.Select(SqlText.Quote))})");

        // A column the rows do not carry is one the store would have to make up. A declared identity
        // is the only one anything here makes up, and it is made up once, in the rows below.
        if (SqlText.SplitList(answered, ',').Any(item => item.Trim() == "*"))
            throw new PgError("0A000", "RETURNING * cannot be answered: name the columns, since what a lake " +
                                       "writes down is what it was given");

        var generated = Generated(table, columns);
        var carried = new List<string>();
        foreach (var name in Answered(answered))
        {
            if (columns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (generated.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            // A declared default is a value the lake knows, so a column the statement leaves to it
            // is answerable -- stamped into the rows being written, which is where the answer comes
            // from, so what is stored and what is read back cannot drift apart.
            if (table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                is { Default: not null } defaulted) generated.Add(defaulted);
            else if (table.Has(name))
                throw new PgError("0A000", $"OUTPUT of `{name}` cannot be answered: a lake stores the row it is " +
                                           "given, and nothing here generates one the caller did not send");
            else carried.Add(name);
        }

        var written = string.Join(", ", columns.Select(SqlText.Quote).Concat(Quoted(generated)));
        var identity = Identifying(table, generated);

        return Promoting(table, new Plan(PlanKind.Rows, [
            $"CREATE OR REPLACE TEMP TABLE duckpg_written AS SELECT {projection}" +
            string.Concat(carried.Select(c => $", {SqlText.Quote(c)}")) +
            string.Concat(generated.Zip(NextValues(table, generated))
                .Select(g => $", {g.Second} AS {SqlText.Quote(g.First.Name)}")) +
            $" {source}",
            $"INSERT INTO {table.WriteName} ({written}) SELECT {written} FROM duckpg_written{Returns(identity)}",
            $"SELECT {answered} FROM duckpg_written"],
            "INSERT") with { Dirty = [table.Name], Checks = duplicates, Identity = identity });
    }

    /// The declared identities a statement does not name, and so leaves to the store.
    static List<Column> Generated(Table table, List<string> columns) =>
        [.. table.Columns.Where(c => c.Identity && !columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))];

    /// The key this statement makes up, out of everything it fills in -- a declared default is
    /// filled in too and is not one, since a caller asking what it got back means the identity.
    static Identity? Identifying(Table table, List<Column> generated) =>
        generated.FirstOrDefault(c => c.Identity) is { } column ? new Identity(table.Name, column.Name) : null;

    static string Returns(Identity? identity) =>
        identity is null ? "" : $" RETURNING {SqlText.Quote(identity.Column)}";

    static IEnumerable<string> Quoted(IEnumerable<Column> columns) => columns.Select(c => SqlText.Quote(c.Name));

    /// A sequence counts in BIGINT whatever the column is declared as, and the answer is read off
    /// the rows rather than off the table -- so the declared type has to be put back here, or a
    /// caller reading its own `int` key gets a long.
    static IEnumerable<string> NextValues(Table table, IEnumerable<Column> columns) =>
        columns.Select(c => c.Identity
            ? $"CAST(nextval({SqlText.Literal(Catalog.Sequence(table, c))}) AS {c.Type})"
            : $"CAST({c.Default!.Expr} AS {c.Type})");

    /// The columns an OUTPUT clause reads, which is the first name of each item it lists.
    static IEnumerable<string> Answered(string clause) =>
        SqlText.SplitList(clause, ',')
            .Select(item => item.Trim())
            .Where(item => item.StartsWith('"'))
            .Select(item => item[1..item.IndexOf('"', 1)]);

    /// The parenthesized group after the table name is a column list unless it opens a subquery.
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

        // What the statement was asked to hand back is answered from the rows it wrote, so it comes
        // off the end before anything else is read: it is no part of the predicate that follows SET.
        var (answered, rest) = Answers(sql);
        sql = rest;

        var alias = ReadAlias(sql, reference.End);
        var set = SqlText.FindKeyword(sql, "SET", reference.End);
        if (set < 0) throw new PgError("42601", "UPDATE without SET");
        var from = SqlText.FindKeyword(sql, "FROM", set + 3);
        var where = SqlText.FindKeyword(sql, "WHERE", from < 0 ? set + 3 : from + 4);
        var end = from < 0 ? where : from;
        var assignments = ParseAssignments(sql[(set + 3)..(end < 0 ? sql.Length : end)]);
        var predicate = where < 0 ? "TRUE" : sql[(where + 5)..];

        RejectVirtual(table, assignments.Keys, "UPDATE");

        var moves = table.Key.Any(assignments.ContainsKey);

        // The rewritten row lands in the write layer under the key it already had, where it shadows
        // whatever is below it -- no tombstone needed. Only a statement that *moves* the key leaves
        // the old one behind with nothing above it, and that is what has to be hidden.
        // Nothing lies beneath a materialized row, so a key that moves leaves nothing to hide.
        var tombstones = moves && !table.Materialized;

        // With a FROM clause the target's own columns are no longer the only ones in scope, so
        // everything the statement did not assign has to say which table it came from.
        var target = alias is null ? table.QualifiedName : $"{table.QualifiedName} AS {SqlText.Quote(alias)}";
        var scan = from < 0
            ? target
            : $"{target}, {sql[(from + 4)..(where < 0 ? sql.Length : where)]}";
        var qualifier = from < 0 ? "" : SqlText.Quote(alias ?? table.Name) + ".";

        var projection = string.Join(", ", table.Columns.Select(c =>
            assignments.TryGetValue(c.Name, out var assigned)
                ? $"({assigned}) AS {SqlText.Quote(c.Name)}"
                : qualifier + SqlText.Quote(c.Name)));
        var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

        // A key that moves may land on one the lake already publishes, and a join around the target
        // may produce the same row twice -- both write two rows under one key, which the write
        // branch's own PRIMARY KEY catches and a materialized table does not. Where each row lands
        // and what it is taking away are read together, so the scan behind them is paid for once.
        // An update that neither moves a key nor joins cannot make a duplicate, since what it reads
        // is the merged view and the merged view is already keyed.
        var duplicates = moves || from >= 0
            ? Duplicates(table,
                         $"SELECT {Moved(table, assignments, qualifier)}, " +
                         string.Join(", ", table.Key.Select(k =>
                             $"{qualifier}{SqlText.Quote(k)} AS {SqlText.Quote(Was(k))}")) +
                         $" FROM {scan} WHERE {predicate}",
                         replacing: true)
            : [];

        // Both the keys being replaced and the rows replacing them have to be computed before
        // anything is tombstoned -- afterwards the view no longer returns them.
        string[] steps = [
            Keys(table, scan, qualifier, predicate),
            $"CREATE OR REPLACE TEMP TABLE duckpg_updated AS SELECT {projection} FROM {scan} WHERE {predicate}",
            .. tombstones ? (string[])[Tombstone(table)] : [],
            Evict(table),
            $"INSERT INTO {table.WriteName} ({columns}) SELECT {columns} FROM duckpg_updated"];

        // Every row the update touched, as the update left it -- which is what `duckpg_updated`
        // already holds, so answering costs one more read of a table the plan had to build anyway.
        if (answered is not null)
            return Promoting(table, new Plan(PlanKind.Rows,
                [.. steps, $"SELECT {answered} FROM duckpg_updated"], "UPDATE")
                with { Dirty = [table.Name], Checks = duplicates }, tombstones);

        return Promoting(table, Plan.Count("UPDATE", steps)
            with { Affected = "SELECT count(*) FROM duckpg_updated", Dirty = [table.Name], Checks = duplicates },
            tombstones);
    }

    Plan RewriteDelete(string sql)
    {
        var from = SqlText.FindKeyword(sql, "FROM");
        if (from < 0) return Plan.Count("DELETE", sql);

        var reference = SqlText.ReadTableRef(sql, from + 4);
        if (Writable(reference.Schema, reference.Name) is not { } table) return Plan.Count("DELETE", sql);
        RequireKey(table, "DELETE");

        var (answered, rest) = Answers(sql);
        sql = rest;

        // The target may be named by an alias the statement bound to it, and the predicate will
        // then say so too -- so the scan carries the alias rather than dropping it.
        var alias = DeleteAlias(sql, reference.End);
        var joined = SqlText.FindKeyword(sql, "USING", reference.End);
        var where = SqlText.FindKeyword(sql, "WHERE", joined < 0 ? reference.End : joined + 5);

        var scan = alias is null ? table.QualifiedName : $"{table.QualifiedName} AS {SqlText.Quote(alias)}";
        var qualifier = "";

        // With another table in scope the key columns have to say which side they came from -- both
        // may carry a column of that name, and the delete is against one of them.
        if (joined >= 0)
        {
            scan += ", " + sql[(joined + 5)..(where < 0 ? sql.Length : where)];
            qualifier = SqlText.Quote(alias ?? table.Name) + ".";
        }

        var predicate = where < 0 ? "TRUE" : sql[(where + 5)..];

        // A cascade goes before the rows it depends on are hidden, and every level answers for the
        // references that do not cascade -- a row two tables down may be held by one of those.
        List<string> cascade = [];
        List<Check> checks = [];
        List<(Table Table, bool Tombstones)> promote = [(table, true)];
        Cascading(table, "duckpg_keys", Keyed(table, scan, qualifier, predicate), cascade, checks, promote);

        string[] steps =
            [Keys(table, scan, qualifier, predicate), .. cascade,
             .. table.Materialized ? (string[])[] : [Tombstone(table)], Evict(table)];
        var referenced = checks.ToArray();

        // A deleted row is gone by the time anything could read it, so what can be answered for is
        // what was collected before it went: the keys, and whatever the statement made of them.
        if (answered is not null)
        {
            foreach (var name in Answered(answered))
                if (!table.Key.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new PgError("0A000", $"OUTPUT of `{name}` on a DELETE cannot be answered: " +
                                               "the row is gone, and only its key was read first");

            return Promoting(promote, new Plan(PlanKind.Rows,
                [.. steps, $"SELECT {answered} FROM duckpg_keys"], "DELETE")
                with { Dirty = [.. promote.Select(p => p.Table.Name)], Checks = referenced });
        }

        return Promoting(promote, Plan.Count("DELETE", steps)
            with
            {
                Affected = "SELECT count(*) FROM duckpg_keys",
                Dirty = [.. promote.Select(p => p.Table.Name)],
                Checks = referenced,
            });
    }

    /// The `RETURNING` clause a statement ends with, and the statement without it. A lake answers
    /// one off the rows it wrote rather than out of the target, so it is taken apart here rather
    /// than handed to DuckDB -- which would see the target's columns and none of the plan's.
    static (string? Answered, string Statement) Answers(string sql)
    {
        var at = SqlText.FindKeyword(sql, "RETURNING");
        return at < 0 ? (null, sql) : (sql[(at + "RETURNING".Length)..].Trim(), sql[..at]);
    }

    /// What a delete has to be sure of before anything goes: that nothing points at the rows it
    /// collected. The check reads the referencing table as published -- a row pointing at this one
    /// may live in any layer, and the write branch is only the topmost of them.
    ///
    /// It runs before the tombstone rather than after: a statement outside a transaction commits
    /// each step as it goes, so a check that failed afterwards would have nothing left to undo.
    IEnumerable<Check> Referenced(Table table, string keys)
    {
        foreach (var reference in Catalog.Referencing(table))
        {
            var child = Catalog.Tables[reference.Table];
            var matched = Matching(reference);

            // A table pointing at itself would find the rows going as rows still there, so the ones
            // going are taken out of the question.
            var others = reference.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase)
                ? " AND NOT (" + string.Join(" AND ", table.Key.Select(key =>
                      $"c.{SqlText.Quote(key)} IS NOT DISTINCT FROM k.{SqlText.Quote(key)}")) + ")"
                : "";

            yield return new Check(
                $"SELECT 1 FROM {child.QualifiedName} AS c, ({keys}) AS k WHERE {matched}{others} LIMIT 1",
                $"The DELETE statement conflicted with the REFERENCE constraint \"{reference.Name}\". " +
                $"The conflict occurred in database \"{Config.Schema}\", table \"{reference.Table}\", " +
                $"column '{reference.Columns[0]}'.",
                "23503");
        }
    }

    /// The key each row will carry once the update has run: what the statement assigns to it, or
    /// what it already had where the statement leaves it alone.
    static string Moved(Table table, Dictionary<string, string> assignments, string qualifier) =>
        string.Join(", ", table.Key.Select(k => assignments.TryGetValue(k, out var assigned)
            ? $"({assigned}) AS {SqlText.Quote(k)}"
            : qualifier + SqlText.Quote(k)));

    /// The keys a statement touches, taken from the merged view before anything moves.
    static string Keys(Table table, string scan, string qualifier, string predicate) =>
        $"CREATE OR REPLACE TEMP TABLE duckpg_keys AS {Keyed(table, scan, qualifier, predicate)}";

    static string Keyed(Table table, string scan, string qualifier, string predicate) =>
        $"SELECT DISTINCT {string.Join(", ", table.Key.Select(k => qualifier + SqlText.Quote(k)))} " +
        $"FROM {scan} WHERE {predicate}";

    /// What a DELETE calls its target, spelled out or not -- and nothing at all when the word after
    /// the table is the clause that follows it.
    static readonly HashSet<string> DeleteClauses =
        new(StringComparer.OrdinalIgnoreCase) { "WHERE", "USING", "RETURNING" };

    static string? DeleteAlias(string sql, int from)
    {
        var word = SqlText.ReadTableRef(sql, from);
        if (word.Name.Equals("AS", StringComparison.OrdinalIgnoreCase))
            return SqlText.ReadTableRef(sql, word.End).Name;
        return word.Name.Length == 0 || DeleteClauses.Contains(word.Name) ? null : word.Name;
    }

    /// The name after the target, which is an alias unless it is the `SET` that follows a bare one.
    static string? ReadAlias(string sql, int from)
    {
        var word = SqlText.ReadTableRef(sql, from);
        if (word.Name.Length == 0 || word.Name.Equals("SET", StringComparison.OrdinalIgnoreCase)) return null;
        return word.Name.Equals("AS", StringComparison.OrdinalIgnoreCase)
            ? SqlText.ReadTableRef(sql, word.End).Name
            : word.Name;
    }

    /// A tombstone hides the row in every layer below; the same key deleted twice is one tombstone.
    static string Tombstone(Table table, string keys = "duckpg_keys") =>
        $"INSERT OR IGNORE INTO {table.TombstoneName} SELECT * FROM {keys}";

    /// The write layer's own copy of a row is deleted outright -- nothing below it to hide.
    static string Evict(Table table, string keys = "duckpg_keys") =>
        $"DELETE FROM {table.WriteName} AS w WHERE EXISTS (SELECT 1 FROM {keys} k WHERE " +
        string.Join(" AND ", table.Key.Select(k => $"k.{SqlText.Quote(k)} IS NOT DISTINCT FROM w.{SqlText.Quote(k)}")) + ")";

    /// A declared `ON DELETE CASCADE` performed as what it means: the same delete against the table
    /// pointing at this one, keyed off what the level above collected. The steps read that as a temp
    /// table, since it is already there by then; the checks read it as the query that produced it,
    /// since a check runs before any step does.
    ///
    /// Every level answers for the references that do not cascade, not just the one deleted from --
    /// a row two tables down may be held by a reference nothing cascades, and orphaning it because
    /// something above it cascaded is the one answer that is wrong.
    void Cascading(Table table, string keys, string keyed, List<string> steps, List<Check> checks,
                   List<(Table Table, bool Tombstones)> promote)
    {
        checks.AddRange(Referenced(table, keyed));

        foreach (var reference in Catalog.Cascading(table))
        {
            var child = Catalog.Tables[reference.Table];
            var matched = Matching(reference);
            var collected = string.Join(", ", child.Key.Select(k => "c." + SqlText.Quote(k)));

            // One per level, and a level adds exactly one table to promote -- a cascade that reaches
            // the same table twice collects for each parent separately, which is what it means.
            var childKeys = $"duckpg_cascade_{promote.Count}";
            promote.Add((child, true));

            steps.Add($"CREATE OR REPLACE TEMP TABLE {childKeys} AS SELECT DISTINCT {collected} " +
                      $"FROM {child.QualifiedName} AS c, {keys} AS k WHERE {matched}");
            if (!child.Materialized) steps.Add(Tombstone(child, childKeys));
            steps.Add(Evict(child, childKeys));

            Cascading(child, childKeys,
                      $"SELECT DISTINCT {collected} FROM {child.QualifiedName} AS c, ({keyed}) AS k WHERE {matched}",
                      steps, checks, promote);
        }

        Clearing(table, keys, steps, promote);
    }

    /// A declared `ON DELETE SET NULL` or `SET DEFAULT` performed as what it means: the rows pointing
    /// at one that goes stay where they are, with what pointed emptied. That is an UPDATE, so it is
    /// built like one -- the rows as the merged view has them, with the pointing columns replaced,
    /// written into the child's branch over the copies they replace.
    ///
    /// Nothing recurses and nothing is checked: the rows are still there afterwards, so nothing
    /// below them is orphaned and nothing further down has to answer for them. Nor is a tombstone
    /// needed -- the rewritten row keeps its key and shadows what is beneath it on its own, which is
    /// why a reference pointing with its own key is refused at build rather than cleared here.
    void Clearing(Table table, string keys, List<string> steps, List<(Table Table, bool Tombstones)> promote)
    {
        foreach (var reference in Catalog.Clearing(table))
        {
            var child = Catalog.Tables[reference.Table];
            var projection = string.Join(", ", child.Columns.Select(c =>
                reference.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                    ? $"{Cleared(reference, c)} AS {SqlText.Quote(c.Name)}"
                    : "c." + SqlText.Quote(c.Name)));
            var columns = string.Join(", ", child.Columns.Select(c => SqlText.Quote(c.Name)));

            var cleared = $"duckpg_cleared_{promote.Count}";
            promote.Add((child, false));

            steps.Add($"CREATE OR REPLACE TEMP TABLE {cleared} AS SELECT {projection} " +
                      $"FROM {child.QualifiedName} AS c, {keys} AS k WHERE {Matching(reference)}");
            steps.Add(Evict(child, cleared));
            steps.Add($"INSERT INTO {child.WriteName} ({columns}) SELECT {columns} FROM {cleared}");
        }
    }

    /// What a cleared column is set to. `SET DEFAULT` means the column's declared default, and NULL
    /// where it has none -- which is what SQL Server does with it too, and what makes `SET NULL` the
    /// same expression with the question not asked.
    static string Cleared(Reference reference, Column column) =>
        column.Default is { Expr: var expr }
        && reference.OnDelete.Equals(Catalog.SetDefault, StringComparison.OrdinalIgnoreCase)
            ? $"CAST({expr} AS {column.Type})"
            : "NULL";

    /// A child row pointing at a parent key the delete collected.
    static string Matching(Reference reference) =>
        string.Join(" AND ", reference.Columns.Zip(reference.ParentColumns)
            .Select(pair => $"c.{SqlText.Quote(pair.First)} = k.{SqlText.Quote(pair.Second)}"));

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

    Plan Promoting(Table table, Plan plan, bool tombstones = false) =>
        Promoting([(table, tombstones)], plan);

    /// A table nobody has written to carries no write branch, so the first write puts one there
    /// before the rest of the plan runs -- on the session's own connection and inside its own
    /// transaction, so a statement that rolls back takes the promotion with it. A cascade writes to
    /// more than one table, and each of them earns its branch the same way.
    Plan Promoting(List<(Table Table, bool Tombstones)> tables, Plan plan)
    {
        // Under the same lock as everything else the lake's own connection does: seeding a sequence
        // reads through it, and a DuckDB connection is not two threads' to share. What the catalog
        // remembers about a table is read here too, and another session's commit is what writes it.
        lock (gate)
        {
            List<string> steps = [], promoted = [], tombstoned = [];
            foreach (var (table, tombstones) in tables)
            {
                if (table.Materialized) continue;
                if (Catalog.Promoted(table) && (!tombstones || Catalog.Tombstoned(table))) continue;
                steps.AddRange(Catalog.Promotion(admin, table, tombstones));
                promoted.Add(table.Name);
                if (tombstones) tombstoned.Add(table.Name);
            }

            if (steps.Count == 0) return plan;

            return plan with
            {
                Steps = [.. steps, .. plan.Steps],
                Promoted = [.. promoted],
                Tombstoned = [.. tombstoned],
            };
        }
    }

    /// Remembered only once the write has committed: a promotion that rolled back is simply made
    /// again by the next write, which is why `Promotion` is repeatable.
    public void Promoted(string name)
    {
        lock (gate) Catalog.Promote(name);
    }

    /// The first row hidden below is what puts the tombstone check into the view -- until then it is
    /// a subquery over an empty table that every read binds and no read needs.
    public void Tombstoned(string name)
    {
        lock (gate) Catalog.Tombstone(name);
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
