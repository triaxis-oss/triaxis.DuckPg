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

/// What a rule DuckDB holds means to a client, carried beside the plan that can break it. A layered
/// lake asks first, since DuckDB sees only the rows this process wrote; a materialized table is
/// asked by DuckDB itself, and answers in its own words -- so the plan brings the words the client
/// would have been given either way.
sealed record Violation(string Message, string SqlState)
{
    /// Only a key. A unique index the dacpac declared is a rule the gateway never had words for,
    /// and dressing one of those as a PRIMARY KEY violation would name the wrong constraint.
    public bool Caused(Exception error) =>
        error.Message.Contains("Constraint Error") && error.Message.Contains("primary key constraint");
}

/// The two ways one statement can be refused for a key: the question asked before it runs, and the
/// words for what DuckDB refuses on its own. A lake uses one or the other and never both.
readonly record struct KeyRule(Check[] Checks, Violation? Violation)
{
    public static readonly KeyRule None = new([], null);
}

sealed record Plan(PlanKind Kind, string[] Steps, string Tag, string? Affected = null, string[]? Dirty = null,
                   string[]? Promoted = null, string[]? Tombstoned = null, Check[]? Checks = null,
                   Identity? Identity = null, Violation? Violation = null)
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
        ["SELECT", "WITH", "VALUES", "TABLE", "FROM", "DESCRIBE", "SUMMARIZE", "PIVOT", "UNPIVOT",
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
        return Logged(sql, verb switch
        {
            "INSERT" => RewriteInsert(sql),
            "UPDATE" => RewriteUpdate(sql),
            "DELETE" => RewriteDelete(sql),
            "EXPLAIN" => PlanExplain(sql),
            "SET" or "RESET" => PlanSet(sql, verb),
            "SHOW" => PlanShow(sql),
            // The rest of Npgsql's reset script, none of which DuckDB has an equivalent for.
            "DISCARD" or "CLOSE" or "UNLISTEN" or "DEALLOCATE" => Plan.NoOp(verb),
            "BEGIN" or "START" => Plan.Count("BEGIN", "BEGIN TRANSACTION"),
            "COMMIT" or "END" => Plan.Count("COMMIT", "COMMIT"),
            "ROLLBACK" or "ABORT" => Plan.Count("ROLLBACK", "ROLLBACK"),
            "CALL" when Intercepted(sql) is { } intercepted => intercepted,
            _ => RowProducing.Contains(verb) ? Plan.Rows(sql) : Plan.Count(verb, sql),
        });
    }

    /// What the rewrite actually sends. The line above logs what a client asked for, and for a
    /// statement that is rewritten those are not the same thing -- a four-statement plan read there
    /// as a plain UPDATE, which is worse than saying nothing. Only where the plan is more than the
    /// statement itself, so an ordinary query still costs one line and no formatting.
    Plan Logged(string sql, Plan plan)
    {
        if (!logger.IsEnabled(LogLevel.Debug) || (plan.Steps is [var only] && only == sql)) return plan;

        foreach (var check in plan.Checks ?? [])
            logger.LogDebug("  check: {Sql}", check.Sql.ReplaceLineEndings(" "));
        for (var i = 0; i < plan.Steps.Length; i++)
            logger.LogDebug("  step {Step}: {Sql}", i + 1, plan.Steps[i].ReplaceLineEndings(" "));
        if (plan.Affected is { } affected)
            logger.LogDebug("  affected: {Sql}", affected.ReplaceLineEndings(" "));
        return plan;
    }

    /// `EXPLAIN <statement>` of what the gateway will run rather than of what the client wrote --
    /// which for anything rewritten are different statements, and the difference is the reason this
    /// is answered here instead of being handed to DuckDB whole.
    ///
    /// A plan that is exactly one query is explained as that query, so the shape and the cost come
    /// back as DuckDB writes them -- which is every read, and now every write a materialized table
    /// takes by key. Anything else answers with the statements themselves, in order. It has to: a
    /// step reads temp tables the step before it made, and none of them exist until it runs. A check
    /// counts towards that even though it is not a step, since it is a query that runs and hiding it
    /// would leave the same gap between what this says and what happens that the log had.
    Plan PlanExplain(string sql)
    {
        var at = SqlText.FindKeyword(sql, "EXPLAIN");
        var prefix = sql[..(at + 7)];
        var inner = sql[(at + 7)..].TrimStart();

        // `EXPLAIN ANALYZE` runs the statement it explains, so it belongs to the prefix rather than
        // to what is translated -- and a plan of several steps cannot honour it at all.
        if (SqlText.FirstWord(inner) == "ANALYZE")
        {
            prefix += " ANALYZE";
            inner = inner["ANALYZE".Length..].TrimStart();
        }

        var planned = Translate(inner);
        if (planned.Steps is [var single] && planned.Checks is null or [] && planned.Affected is null)
            return Plan.Rows($"{prefix} {single}");

        List<(string Step, string Sql)> rows =
            [.. (planned.Checks ?? []).Select((c, i) => ($"check {i + 1}", c.Sql)),
             .. planned.Steps.Select((s, i) => ($"step {i + 1}", s)),
             .. planned.Affected is { } affected ? ((string, string)[])[("affected", affected)] : []];

        if (rows.Count == 0) rows.Add(("step 0", "-- nothing runs"));

        return Plan.Rows("SELECT * FROM (VALUES " +
                         string.Join(", ", rows.Select(r => $"({SqlText.Literal(r.Step)}, {SqlText.Literal(r.Sql)})")) +
                         ") AS \"_plan\"(\"step\", \"statement\")");
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
            : KeyRule.None;

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
            with { Dirty = [table.Name], Checks = duplicates.Checks, Violation = duplicates.Violation, Identity = identity });
    }

    /// A declared key is a rule about rows that may live in any layer, so for a layered lake it is
    /// kept here rather than by DuckDB. The write branch's own PRIMARY KEY sees only what this
    /// process wrote -- a key a file below already holds is one it lets through, and the row then
    /// quietly shadows the file's instead of being refused. So both halves are asked of the rows a
    /// statement is about to write: a key the table already publishes, and one the rows repeat among
    /// themselves.
    ///
    /// A materialized table holds its key as a real PRIMARY KEY, which sees everything the lake
    /// publishes because that is all there is. Asking first is then asking twice, and the scan
    /// behind the question is most of what a write costs -- 3.23 ms an insert against 1.49 without
    /// it. So the question is not asked and only the answer is kept: `Violation` carries the words
    /// this would have refused in, and a session reports them in place of DuckDB's.
    ///
    /// Only where the statement writes without first taking anything away, which is what `replacing`
    /// says. A plan that replaces evicts the rows before it re-inserts them, and its steps are not
    /// one transaction -- so a key DuckDB refuses at the insert is refused after the delete has
    /// committed, and the rows are simply gone. That is the whole reason a check runs *before* a
    /// plan rather than being left to the write, and it does not stop being true here.
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
    KeyRule Duplicates(Table table, string keys, bool replacing = false)
    {
        if (table.Key.Length == 0 || !Config.CheckKeys) return KeyRule.None;

        var refused = new Violation(
            $"Violation of PRIMARY KEY constraint on \"{table.Name}\". Cannot insert duplicate key in object " +
            $"\"{Config.Schema}.{table.Name}\".", "23505");

        if (table.Materialized && !replacing) return new KeyRule([], refused);

        var matched = string.Join(" AND ", table.Key.Select(k =>
            $"t.{SqlText.Quote(k)} IS NOT DISTINCT FROM r.{SqlText.Quote(k)}"));
        var kept = replacing
            ? $" ANTI JOIN (SELECT {string.Join(", ", table.Key.Select(k => SqlText.Quote(Was(k))))} " +
              "FROM \"_keys\") AS o ON " +
              string.Join(" AND ", table.Key.Select(k =>
                  $"o.{SqlText.Quote(Was(k))} IS NOT DISTINCT FROM r.{SqlText.Quote(k)}"))
            : "";

        return new KeyRule([new Check(
            $"WITH \"_keys\" AS MATERIALIZED ({keys}) " +
            $"SELECT 1 FROM (SELECT {KeyList(table)}, count(*) AS \"_count\" FROM \"_keys\" " +
            $"GROUP BY {KeyList(table)}) AS r WHERE r.\"_count\" > 1 " +
            $"UNION ALL SELECT 1 FROM \"_keys\" AS r " +
            $"SEMI JOIN {table.QualifiedName} AS t ON {matched}{kept} LIMIT 1",
            refused.Message, refused.SqlState)], refused);
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
    Plan RewriteReturning(Table table, List<string> columns, string rest, int returning, KeyRule duplicates)
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
            "INSERT") with { Dirty = [table.Name], Checks = duplicates.Checks, Violation = duplicates.Violation, Identity = identity });
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
        var update = SqlText.FindKeyword(sql, "UPDATE");
        var reference = SqlText.ReadTableRef(sql, update + 6);
        if (Writable(reference.Schema, reference.Name) is not { } table)
            return Plan.Count("UPDATE", Unlimited(Grounded(sql, reference), "UPDATE"));
        RequireKey(table, "UPDATE");

        // What the statement was asked to hand back is answered from the rows it wrote, so it comes
        // off the end before anything else is read: it is no part of the predicate that follows SET.
        var (answered, rest) = Answers(sql);
        var (rows, percent, limitless) = Limited(rest);
        sql = limitless;

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

        // Nothing lies beneath a materialized row, so there is no old one for a written one to
        // shadow -- which is the whole job the rest of this method does. Evict-and-reinsert exists
        // so a write branch can stand over the layers below it; with no layers below, DuckDB's own
        // UPDATE is that same operation in one statement, finding its rows through the table's key
        // rather than through a temp table built to stand in for one. Measured on a 414-table lake,
        // a single row by key: 8.0 ms as four statements against 1.05 for the one, and ~95% of the
        // difference is spent *preparing* statements rather than running them.
        //
        // The conditions are the ones the rest of the method already turns on: a FROM puts another
        // table in scope and may match a row twice, a TOP has to settle on which rows before
        // anything is written, and a moved key has to be checked against what the table already
        // publishes. Where none of them holds, the statement a client sent is the statement to run.
        if (table.Materialized && from < 0 && rows is null && !moves)
        {
            var passed = sql[..update] + "UPDATE " + table.QualifiedName + sql[reference.End..];

            // `OUTPUT` is answered off the rows as written, which is what DuckDB's own `RETURNING`
            // hands back -- `DELETED` is refused everywhere here, so there is no older row to want.
            return answered is null
                ? Plan.Count("UPDATE", passed) with { Dirty = [table.Name] }
                : new Plan(PlanKind.Rows, [$"{passed} RETURNING {answered}"], "UPDATE")
                    with { Dirty = [table.Name] };
        }

        // The rewritten row lands in the write layer under the key it already had, where it shadows
        // whatever is below it -- no tombstone needed. Only a statement that *moves* the key leaves
        // the old one behind with nothing above it, and that is what has to be hidden.
        // Nothing lies beneath a materialized row, so a key that moves leaves nothing to hide.
        var tombstones = moves && !table.Materialized;

        // With a FROM clause the target's own columns are no longer the only ones in scope, so
        // everything the statement did not assign has to say which table it came from. A clause
        // opening with the target itself carries the whole join tree the rows are picked through,
        // target included, so the tree stands alone as the scan.
        var target = alias is null ? table.QualifiedName : $"{table.QualifiedName} AS {SqlText.Quote(alias)}";
        var scan = target;
        var qualifier = "";

        if (from >= 0)
        {
            var clause = sql[(from + 4)..(where < 0 ? sql.Length : where)];
            scan = Embedded(clause, reference, alias) ? clause : $"{target}, {clause}";
            qualifier = SqlText.Quote(alias ?? table.Name) + ".";
        }

        var projection = string.Join(", ", table.Columns.Select(c =>
            assignments.TryGetValue(c.Name, out var assigned)
                ? $"({assigned}) AS {SqlText.Quote(c.Name)}"
                : qualifier + SqlText.Quote(c.Name)));
        var columns = string.Join(", ", table.Columns.Select(c => SqlText.Quote(c.Name)));

        // Which rows a `TOP (n)` settled on is decided once, on the keys, and everything after it
        // reads that choice back rather than making it again: the steps off the key set written
        // down, a check off the query that produced it, since a check runs before any step does.
        // The target has to be named even where nothing else is in scope, or the key column would be
        // as much the chosen set's as the row's.
        var owner = SqlText.Quote(alias ?? table.Name) + ".";
        var counted = Keyed(table, scan, qualifier, predicate);
        var keyed = rows is null ? counted
            : Keyed(table, scan, qualifier, predicate, Limit(rows, percent, counted));
        var touched = rows is null ? predicate
            : $"({predicate}) AND {Within(table, "SELECT * FROM duckpg_keys", owner)}";
        var checking = rows is null ? predicate : $"({predicate}) AND {Within(table, keyed, owner)}";

        // A key that moves may land on one the lake already publishes, and a join around the target
        // may hand the same row to the projection twice -- but two identical rows are one write, so
        // a joined statement collapses what it writes first and only rows that *differ* under one
        // key are a collision, which the write branch's own PRIMARY KEY catches and a materialized
        // table does not. The check reads the whole row for the same reason; an update without a
        // join reads the merged view, already keyed, so its key columns alone answer more cheaply.
        var was = string.Join(", ", table.Key.Select(k =>
            $"{qualifier}{SqlText.Quote(k)} AS {SqlText.Quote(Was(k))}"));
        var duplicates = moves || from >= 0
            ? Duplicates(table,
                         from >= 0
                             ? $"SELECT DISTINCT {projection}, {was} FROM {scan} WHERE {checking}"
                             : $"SELECT {Moved(table, assignments, qualifier)}, {was} FROM {scan} WHERE {checking}",
                         replacing: true)
            : KeyRule.None;

        // Both the keys being replaced and the rows replacing them have to be computed before
        // anything is tombstoned -- afterwards the view no longer returns them.
        string[] steps = [
            Keys(keyed),
            $"CREATE OR REPLACE TEMP TABLE duckpg_updated AS " +
            $"SELECT {(from < 0 ? "" : "DISTINCT ")}{projection} FROM {scan} WHERE {touched}",
            .. tombstones ? (string[])[Tombstone(table)] : [],
            Evict(table),
            $"INSERT INTO {table.WriteName} ({columns}) SELECT {columns} FROM duckpg_updated"];

        // Every row the update touched, as the update left it -- which is what `duckpg_updated`
        // already holds, so answering costs one more read of a table the plan had to build anyway.
        if (answered is not null)
            return Promoting(table, new Plan(PlanKind.Rows,
                [.. steps, $"SELECT {answered} FROM duckpg_updated"], "UPDATE")
                with { Dirty = [table.Name], Checks = duplicates.Checks, Violation = duplicates.Violation }, tombstones);

        return Promoting(table, Plan.Count("UPDATE", steps)
            with
            {
                Affected = "SELECT count(*) FROM duckpg_updated", Dirty = [table.Name],
                Checks = duplicates.Checks, Violation = duplicates.Violation,
            },
            tombstones);
    }

    Plan RewriteDelete(string sql)
    {
        var from = SqlText.FindKeyword(sql, "FROM");
        if (from < 0) return Plan.Count("DELETE", sql);

        var reference = SqlText.ReadTableRef(sql, from + 4);
        if (Writable(reference.Schema, reference.Name) is not { } table)
            return Plan.Count("DELETE", Detached(Unlimited(sql, "DELETE"), reference));
        RequireKey(table, "DELETE");

        var (answered, rest) = Answers(sql);
        var (rows, percent, limitless) = Limited(rest);
        sql = limitless;

        // The target may be named by an alias the statement bound to it, and the predicate will
        // then say so too -- so the scan carries the alias rather than dropping it.
        var alias = DeleteAlias(sql, reference.End);
        var joined = SqlText.FindKeyword(sql, "USING", reference.End);
        var where = SqlText.FindKeyword(sql, "WHERE", joined < 0 ? reference.End : joined + 5);

        var scan = alias is null ? table.QualifiedName : $"{table.QualifiedName} AS {SqlText.Quote(alias)}";
        var qualifier = "";

        // With another table in scope the key columns have to say which side they came from -- both
        // may carry a column of that name, and the delete is against one of them. A clause opening
        // with the target itself carries the whole join tree the rows are picked through, target
        // included, so the tree stands alone as the scan.
        if (joined >= 0)
        {
            var clause = sql[(joined + 5)..(where < 0 ? sql.Length : where)];
            scan = Embedded(clause, reference, alias) ? clause : $"{scan}, {clause}";
            qualifier = SqlText.Quote(alias ?? table.Name) + ".";
        }

        var predicate = where < 0 ? "TRUE" : sql[(where + 5)..];

        // Which rows a `TOP (n)` settled on is the key set the plan writes down, and the checks read
        // the query behind it rather than the table -- they run before any step does.
        var counted = Keyed(table, scan, qualifier, predicate);
        var keyed = rows is null ? counted
            : Keyed(table, scan, qualifier, predicate, Limit(rows, percent, counted));

        // A cascade goes before the rows it depends on are hidden, and every level answers for the
        // references that do not cascade -- a row two tables down may be held by one of those.
        List<string> cascade = [];
        List<Check> checks = [];
        List<(Table Table, bool Tombstones)> promote = [(table, true)];
        Cascading(table, "duckpg_keys", keyed, cascade, checks, promote);

        // The one-statement path an UPDATE takes, for the same reason: nothing lies beneath a
        // materialized row, so there is no tombstone to write and so no key set to write it from.
        // Only where the plan has nothing else to do with those keys -- a cascade reads them back
        // one table down, a USING puts another table in scope, a TOP has to settle on rows first,
        // and an OUTPUT is answered from keys read before the rows went. The reference checks stay:
        // they run before any step and ask the query rather than the temp table.
        if (table.Materialized && cascade.Count == 0 && joined < 0 && rows is null && answered is null)
            return Plan.Count("DELETE", sql[..from] + "FROM " + table.QualifiedName + sql[reference.End..])
                with { Dirty = [table.Name], Checks = checks.ToArray() };

        string[] steps =
            [Keys(keyed), .. cascade,
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
                $"The conflict occurred in database \"{Config.DatabaseName}\", table \"{reference.Table}\", " +
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
    static string Keys(string keyed) => $"CREATE OR REPLACE TEMP TABLE duckpg_keys AS {keyed}";

    /// `limit` is what a `TOP (n)` write may touch, and it is applied to the keys because that is
    /// what one row of a lake's table is. Ordered, though SQL Server leaves the choice unsaid and
    /// any n rows would answer it: this query is evaluated more than once -- the plan writes it down
    /// and every check re-asks it, since a check runs before the first step -- and an arbitrary n
    /// taken twice is two different sets, which would have the checks answering for rows that stayed.
    static string Keyed(Table table, string scan, string qualifier, string predicate, string? limit = null)
    {
        var keys = string.Join(", ", table.Key.Select(k => qualifier + SqlText.Quote(k)));
        return $"SELECT DISTINCT {keys} FROM {scan} WHERE {predicate}" +
               (limit is null ? "" : $" ORDER BY {keys} LIMIT {limit}");
    }

    /// The row limit the writer put on the end of a write, taken off again. Neither dialect has a
    /// place for one -- SQL Server writes it before the target and DuckDB has no `DELETE ... LIMIT`
    /// at all -- so a top-level one here is duckpg's own spelling and can be nobody else's.
    static (string? Rows, bool Percent, string Statement) Limited(string sql)
    {
        var at = SqlText.FindKeyword(sql, "LIMIT");
        if (at < 0) return (null, false, sql);

        var rows = sql[(at + "LIMIT".Length)..].Trim();
        var percent = SqlText.FindKeyword(rows, "PERCENT");
        return (percent < 0 ? rows : rows[..percent].TrimEnd(), percent >= 0, sql[..at]);
    }

    /// How many rows that limit stands for. A share is counted over the rows it is a share of and
    /// rounded up, which is what SQL Server does with `TOP n PERCENT`: one percent of anything at
    /// all is a row, and of a hundred and one rows it is two.
    static string Limit(string rows, bool percent, string counted) =>
        percent
            ? $"(SELECT CAST(CEIL(count(*) * ({rows}) / 100.0) AS BIGINT) FROM ({counted}) AS \"_percent\")"
            : rows;

    /// Whether a row is one of those the limit settled on, as a condition over the row itself. The
    /// key set stands as a derived table rather than being joined to: it is built over the same scan
    /// under the same names, and only a subquery keeps that copy's aliases from swallowing the
    /// comparison's other side.
    static string Within(Table table, string keys, string owner) =>
        $"EXISTS (SELECT 1 FROM ({keys}) AS \"_top\" WHERE " +
        string.Join(" AND ", table.Key.Select(k =>
            $"\"_top\".{SqlText.Quote(k)} IS NOT DISTINCT FROM {owner}{SqlText.Quote(k)}")) + ")";

    /// A `TOP (n)` is performed on the key the lake declares for a table, so a target the lake does
    /// not publish -- a session's own `#temp`, which is the only other thing a write can name here --
    /// has nothing to perform it on. Refused rather than passed on: DuckDB would answer for the
    /// `LIMIT` and not for the statement it was written on.
    static string Unlimited(string sql, string operation) =>
        SqlText.FindKeyword(sql, "LIMIT") < 0
            ? sql
            : throw new PgError("0A000", $"TOP on a {operation} counts the rows of a table the lake " +
                                         "publishes, and this is not one of them");

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

    static readonly HashSet<string> JoinWords =
        new(StringComparer.OrdinalIgnoreCase) { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "JOIN" };

    /// Whether a joined write's clause opens with the write's own target under the write's own
    /// alias -- duckpg's spelling, made by `TSqlParser.Selecting`, for a join tree that picks the
    /// rows itself with the target inside it: the tree only selects, so its target occurrence is
    /// the scan and nothing is put beside it. Nobody else writes this: SQL Server refuses the
    /// doubled name outright, and reading it as a self-join is what has to be avoided here, since
    /// DuckDB does exactly that rather than refusing -- see `Detached`.
    static bool Embedded(string clause, (string? Schema, string Name, int Start, int End) target, string? alias)
    {
        var first = SqlText.ReadTableRef(clause, 0);
        if (!string.Equals(first.Schema, target.Schema, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(first.Name, target.Name, StringComparison.OrdinalIgnoreCase)) return false;

        var word = SqlText.ReadTableRef(clause, first.End);
        var bound = word.Name.Equals("AS", StringComparison.OrdinalIgnoreCase)
            ? SqlText.ReadTableRef(clause, word.End).Name
            : word.Name.Length == 0 || JoinWords.Contains(word.Name) ? null : word.Name;

        return string.Equals(bound, alias, StringComparison.OrdinalIgnoreCase);
    }

    /// The tree-carrying spelling against a table the lake does not publish -- a session's `#temp`
    /// joined through a graph of its own. There is no declared key to collect the rows by, but the
    /// table is DuckDB's own, so its rowid takes the key's place: the tree moves into a subquery
    /// picking rowids.
    ///
    /// Handed on as written it is not refused -- DuckDB reads the target's second occurrence as a
    /// *separate* binding and deletes every row pairing with any the tree kept, which on a three-row
    /// scratch table filtered to two deletes all three. Nothing says it went wrong, which is why
    /// this path exists rather than a refusal: keeping the one binding one is the whole of it.
    static string Detached(string sql, (string? Schema, string Name, int Start, int End) reference)
    {
        var alias = DeleteAlias(sql, reference.End);
        var joined = SqlText.FindKeyword(sql, "USING", reference.End);
        if (joined < 0) return sql;

        var (answered, rest) = Answers(sql);
        var where = SqlText.FindKeyword(rest, "WHERE", joined + 5);
        var clause = rest[(joined + 5)..(where < 0 ? rest.Length : where)].Trim();
        if (!Embedded(clause, reference, alias)) return sql;

        return rest[..joined] +
               $"WHERE rowid IN (SELECT {SqlText.Quote(alias ?? reference.Name)}.rowid FROM {clause} " +
               $"WHERE {(where < 0 ? "TRUE" : rest[(where + 5)..].Trim())})" +
               (answered is null ? "" : $" RETURNING {answered}");
    }

    /// The same spelling on an UPDATE has nowhere to go without a key: the assignments read the
    /// tree, so it cannot move aside into a subquery the way a DELETE's rowids can. Refused here,
    /// while the statement can still be named for what it is -- handed on, it would be read as a
    /// self-join and write the wrong rows without saying so, which is what `Detached` measures.
    static string Grounded(string sql, (string? Schema, string Name, int Start, int End) reference)
    {
        var set = SqlText.FindKeyword(sql, "SET", reference.End);
        var from = set < 0 ? -1 : SqlText.FindKeyword(sql, "FROM", set + 3);
        if (from < 0) return sql;

        var where = SqlText.FindKeyword(sql, "WHERE", from + 4);
        var clause = sql[(from + 4)..(where < 0 ? sql.Length : where)];
        if (!Embedded(clause, reference, ReadAlias(sql, reference.End))) return sql;

        throw new PgError("0A000", "an UPDATE picked through a join tree carrying its own target " +
                                   $"needs the lake's keys, and {reference.Name} is not a table the lake publishes");
    }

    /// A tombstone hides the row in every layer below; the same key deleted twice is one tombstone.
    /// The columns are named because the orders differ: the key set carries them in the key's own
    /// order, and the tombstone table holds them in the table's -- positionally, a key of one type
    /// throughout would land swapped, burying another row.
    static string Tombstone(Table table, string keys = "duckpg_keys") =>
        $"INSERT OR IGNORE INTO {table.TombstoneName} ({KeyList(table)}) SELECT * FROM {keys}";

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

    /// The table the `pg_constraint` shim reads. Filled by the catalog, because what a lake enforces
    /// is what it was declared with rather than anything DuckDB holds.
    public const string Constraints = "duckpg_constraints";

    /// Installed once on the shared catalog. `duckpg_pg_class` fills the gaps in DuckDB's pg_class
    /// that psql's \d relies on -- this is the part of a PG frontend that never really finishes.
    ///
    /// A GUI client asks the catalog more than psql does. `regclass` is a type rather than another
    /// entry in `Replacements` because `Apply` only reaches SQL that names `pg_catalog.`, and a
    /// client casting `'"lake"."orders"'::regclass` need not; `pg_get_viewdef` is shadowed because
    /// DuckDB's own takes an oid and nothing else, and arity is checked before the cast is even
    /// looked at -- so asking for the pretty form is refused before the argument means anything.
    /// Ours answers to what a regclass literal actually holds, which is a name.
    ///
    /// A size is NULL rather than 0: what a lake publishes is a view over files, and 0 would read
    /// as a table with nothing in it.
    ///
    /// `pg_constraint` is replaced rather than filled in. DuckDB's has the wrong shape -- `confkey`
    /// is an integer where PostgreSQL has a list, so a client unnesting it is refused -- and the
    /// wrong contents: a layered table is a view, which holds no constraints at all, while the keys
    /// and references the lake does enforce are rules over the merged view that only the catalog
    /// knows. So the rows come from `duckpg_constraints`, and the shape is put back here.
    public const string Macros = """
        CREATE OR REPLACE MACRO duckpg_set_config(name, value, is_local) AS value;
        CREATE OR REPLACE MACRO duckpg_true(oid) AS true;
        CREATE OR REPLACE MACRO duckpg_user(oid) AS 'duckdb';
        CREATE OR REPLACE MACRO duckpg_encoding(oid) AS 'UTF8';
        CREATE OR REPLACE MACRO duckpg_empty(a, b) AS '', (a, b, c) AS '';
        CREATE OR REPLACE MACRO pg_advisory_unlock_all() AS true;
        CREATE OR REPLACE MACRO pg_total_relation_size(rel) AS NULL::BIGINT;
        CREATE OR REPLACE MACRO pg_table_size(rel) AS NULL::BIGINT;
        CREATE OR REPLACE MACRO pg_indexes_size(rel) AS NULL::BIGINT;
        -- Only what has to be quoted, as PostgreSQL does it: a client that shows the answer shows
        -- the quotes too.
        CREATE OR REPLACE MACRO quote_ident(name) AS
            CASE WHEN regexp_full_match(name, '[a-z_][a-z0-9_$]*') THEN name
                 ELSE '"' || replace(name, '"', '""') || '"' END;
        CREATE TYPE IF NOT EXISTS regclass AS VARCHAR;
        CREATE OR REPLACE MACRO duckpg_viewdef(rel) AS (
            SELECT sql FROM duckdb_views()
            WHERE rel::VARCHAR = view_oid::VARCHAR
               OR lower(replace(rel::VARCHAR, '"', ''))
                  IN (lower(view_name), lower(schema_name || '.' || view_name))
            LIMIT 1);
        CREATE OR REPLACE MACRO pg_get_viewdef(rel) AS duckpg_viewdef(rel),
                                             (rel, pretty) AS duckpg_viewdef(rel);
        CREATE OR REPLACE VIEW pg_statio_user_tables AS
            SELECT c.oid AS relid, n.nspname AS schemaname, c.relname,
                   0::BIGINT AS heap_blks_read, 0::BIGINT AS heap_blks_hit,
                   0::BIGINT AS idx_blks_read, 0::BIGINT AS idx_blks_hit,
                   0::BIGINT AS toast_blks_read, 0::BIGINT AS toast_blks_hit,
                   0::BIGINT AS tidx_blks_read, 0::BIGINT AS tidx_blks_hit
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema');
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
        -- The alias is its own, because the caller's is in scope inside a macro body: a client that
        -- calls this with `pg_proc p` and an alias of `p` here compares the row to itself, which
        -- matches every function there is. Several of them share an oid, so where the catalog cannot
        -- tell them apart this answers for the first of them rather than for the one meant.
        CREATE OR REPLACE MACRO pg_get_function_identity_arguments(func) AS (
            SELECT coalesce(list_aggregate(duckpg_proc.proargtypes, 'string_agg', ', '), '')
            FROM pg_catalog.pg_proc AS duckpg_proc WHERE duckpg_proc.oid = func ORDER BY 1 LIMIT 1);
        -- DuckDB reports a window function as an aggregate and never says `w`, so nothing here is
        -- one; `pg_proc` is otherwise its own.
        CREATE OR REPLACE VIEW pg_proc AS
            SELECT *, false AS proiswindow FROM pg_catalog.pg_proc;
        CREATE TABLE IF NOT EXISTS duckpg_constraints (
            conname VARCHAR, contype VARCHAR, nspname VARCHAR, relname VARCHAR, colname VARCHAR,
            ord INTEGER, parentname VARCHAR, parentcol VARCHAR,
            confupdtype VARCHAR, confdeltype VARCHAR, conord BIGINT);
        CREATE OR REPLACE VIEW pg_constraint AS
            SELECT (c.oid * 1000000 + d.conord)::BIGINT AS oid, d.conname,
                   n.oid::BIGINT AS connamespace, d.contype,
                   false AS condeferrable, false AS condeferred, true AS convalidated,
                   c.oid::BIGINT AS conrelid, 0 AS contypid, 0 AS conindid, 0 AS conparentid,
                   coalesce(p.oid, 0)::BIGINT AS confrelid,
                   d.confupdtype, d.confdeltype, 's' AS confmatchtype,
                   true AS conislocal, 0 AS coninhcount, false AS connoinherit,
                   list(ca.attnum::BIGINT ORDER BY d.ord) AS conkey,
                   CASE WHEN d.contype = 'f' THEN list(pa.attnum::BIGINT ORDER BY d.ord) END AS confkey,
                   NULL::INTEGER AS conpfeqop, NULL::INTEGER AS conppeqop, NULL::INTEGER AS conffeqop,
                   NULL::INTEGER AS conexclop, NULL::VARCHAR AS conbin
            FROM duckpg_constraints AS d
            JOIN pg_catalog.pg_namespace AS n ON n.nspname = d.nspname
            JOIN pg_catalog.pg_class AS c ON c.relnamespace = n.oid AND c.relname = d.relname
            JOIN pg_catalog.pg_attribute AS ca ON ca.attrelid = c.oid AND ca.attname = d.colname
            LEFT JOIN pg_catalog.pg_class AS p ON p.relnamespace = n.oid AND p.relname = d.parentname
            LEFT JOIN pg_catalog.pg_attribute AS pa ON pa.attrelid = p.oid AND pa.attname = d.parentcol
            GROUP BY d.conname, d.contype, d.confupdtype, d.confdeltype, d.conord, n.oid, c.oid, p.oid;
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
