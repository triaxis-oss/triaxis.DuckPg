using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

sealed record Prepared(string Sql, int[] ParameterOids);

sealed record Portal(Prepared Statement, object?[] Arguments, short[] ResultFormats)
{
    public DbDataReader? Reader { get; set; }
    public DbCommand? Command { get; set; }
    public Plan? Plan { get; set; }
    public (int Oid, bool Binary)[]? Columns { get; set; }

    public void Reset()
    {
        Reader?.Dispose();
        Command?.Dispose();
        Reader = null;
        Command = null;
        Columns = null;
    }
}

/// One client connection: protocol state machine plus its own DuckDB connection.
sealed class PgSession(TcpClient client, Gateway gateway, DuckDBConnection duck, PgServer server, ILogger logger)
    : IDisposable
{
    readonly PgWire wire = new(client.GetStream(), logger);
    readonly Dictionary<string, Prepared> statements = new();
    readonly Dictionary<string, Portal> portals = new();
    readonly Dictionary<string, string> startup = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingPromotions = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingTombstones = new(StringComparer.OrdinalIgnoreCase);

    char transactionStatus = 'I';
    bool skipUntilSync;
    bool turn;

    public int ProcessId { get; } = Random.Shared.Next(1, int.MaxValue);
    public int Secret { get; } = Random.Shared.Next(1, int.MaxValue);

    public void Cancel() => NativeMethods.Startup.DuckDBInterrupt(duck.NativeConnection);

    public void Run()
    {
        if (!Handshake()) return;
        server.Register(this);

        while (true)
        {
            var message = wire.ReadMessage();
            if (message is null) break;
            var (type, body) = message.Value;
            if (type == 'X') break;

            if (skipUntilSync && type != 'S')
            {
                if (type is 'H') wire.Flush();
                continue;
            }

            try
            {
                logger.LogTrace("<< {Type}", type);
                Dispatch(type, body);
            }
            catch (Exception e)
            {
                SendError(e);
                if (type is 'Q')
                {
                    ReadyForQuery();
                }
                else
                {
                    skipUntilSync = true;
                }
            }
            wire.Flush();
        }
    }

    void Dispatch(char type, byte[] body)
    {
        var reader = new MsgReader(body);
        switch (type)
        {
            case 'Q': SimpleQuery(reader.Str()); break;
            case 'P': Parse(ref reader); break;
            case 'B': Bind(ref reader); break;
            case 'D': Describe(ref reader); break;
            case 'E': Execute(ref reader); break;
            case 'C': Close(ref reader); break;
            case 'S': skipUntilSync = false; ReadyForQuery(); break;
            case 'H': wire.Flush(); break;
            case 'p': break; // password: authentication is trust-only
            default: throw new ProtocolException($"unsupported message type '{type}'");
        }
    }

    // ---- startup ---------------------------------------------------------------------------------

    bool Handshake()
    {
        while (true)
        {
            var packet = wire.ReadStartup();
            if (packet is null) return false;
            var (code, body) = packet.Value;

            switch (code)
            {
                case 80877103: // SSLRequest
                case 80877104: // GSSENCRequest
                    client.GetStream().WriteByte((byte)'N');
                    client.GetStream().Flush();
                    continue;

                case 80877102: // CancelRequest
                    var cancel = new MsgReader(body);
                    server.CancelBackend(cancel.I32(), cancel.I32());
                    return false;

                case 196608: // protocol 3.0
                    ReadStartupParameters(body);
                    Authenticate();
                    return true;

                default:
                    throw new ProtocolException($"unsupported protocol version {code}");
            }
        }
    }

    void ReadStartupParameters(byte[] body)
    {
        var reader = new MsgReader(body);
        while (!reader.AtEnd)
        {
            var key = reader.Str();
            if (key.Length == 0) break;
            startup[key] = reader.Str();
        }

        // libpq's `options` carries "-c key=value" pairs; treat them as ordinary startup parameters.
        foreach (var option in (startup.GetValueOrDefault("options") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = option.TrimStart('-', 'c').Split('=', 2);
            if (pair.Length == 2) startup[pair[0]] = pair[1];
        }
    }

    void Authenticate()
    {
        wire.Send('R', new Msg().I32(0));

        foreach (var (key, value) in new[]
                 {
                     ("server_version", PgServer.ServerVersion),
                     ("server_encoding", "UTF8"),
                     ("client_encoding", "UTF8"),
                     ("DateStyle", "ISO, MDY"),
                     ("integer_datetimes", "on"),
                     ("standard_conforming_strings", "on"),
                     ("TimeZone", "UTC"),
                     ("application_name", startup.GetValueOrDefault("application_name") ?? ""),
                 })
            wire.Send('S', new Msg().Str(key).Str(value));

        wire.Send('K', new Msg().I32(ProcessId).I32(Secret));
        ApplySessionVariables();
        ReadyForQuery();
        wire.Flush();
    }

    /// Startup parameters become DuckDB session variables, so views can project or filter on who
    /// is connected. DuckDB scopes variables per connection, which is what makes this work.
    void ApplySessionVariables()
    {
        foreach (var (variable, source) in gateway.Config.SessionVariables)
        {
            var value = startup.GetValueOrDefault(source) ?? "";
            using var cmd = duck.CreateCommand();
            cmd.CommandText = $"SET VARIABLE {SqlText.Quote(variable)} = {SqlText.Literal(value)}";
            cmd.ExecuteNonQuery();
        }
    }

    // ---- simple query ----------------------------------------------------------------------------

    void SimpleQuery(string sql)
    {
        var statementCount = 0;
        foreach (var statement in SqlText.SplitStatements(sql))
        {
            statementCount++;
            RunPlan(gateway.Translate(statement), [], []);
        }
        if (statementCount == 0) wire.Send('I');
        ReadyForQuery();
    }

    // ---- extended query --------------------------------------------------------------------------

    void Parse(ref MsgReader reader)
    {
        var name = reader.Str();
        var sql = reader.Str();
        var oids = new int[reader.I16()];
        for (var i = 0; i < oids.Length; i++) oids[i] = reader.I32();

        statements[name] = new Prepared(SqlText.Rename(sql), oids);
        wire.Send('1');
    }

    void Bind(ref MsgReader reader)
    {
        var portalName = reader.Str();
        var statementName = reader.Str();
        if (!statements.TryGetValue(statementName, out var statement))
            throw new PgError("26000", $"unknown prepared statement `{statementName}`");

        var formats = new short[reader.I16()];
        for (var i = 0; i < formats.Length; i++) formats[i] = reader.I16();

        var arguments = new object?[reader.I16()];
        for (var i = 0; i < arguments.Length; i++)
        {
            var raw = reader.Value();
            var binary = formats.Length switch { 0 => false, 1 => formats[0] == 1, _ => formats[i] == 1 };
            var oid = i < statement.ParameterOids.Length ? statement.ParameterOids[i] : PgTypes.Unknown;
            arguments[i] = raw is null ? null
                : binary ? PgTypes.DecodeBinary(oid, raw)
                : PgTypes.DecodeText(oid, System.Text.Encoding.UTF8.GetString(raw));
        }

        var resultFormats = new short[reader.I16()];
        for (var i = 0; i < resultFormats.Length; i++) resultFormats[i] = reader.I16();

        portals.GetValueOrDefault(portalName)?.Reset();
        portals[portalName] = new Portal(statement, arguments, resultFormats);
        wire.Send('2');
    }

    void Describe(ref MsgReader reader)
    {
        var kind = (char)reader.U8();
        var name = reader.Str();

        if (kind == 'S')
        {
            if (!statements.TryGetValue(name, out var statement))
                throw new PgError("26000", $"unknown prepared statement `{name}`");

            var message = new Msg().I16(statement.ParameterOids.Length);
            foreach (var oid in statement.ParameterOids) message.I32(oid);
            wire.Send('t', message);
            DescribeStatement(statement);
            return;
        }

        var portal = portals.GetValueOrDefault(name) ?? throw new PgError("34000", $"unknown portal `{name}`");
        var plan = portal.Plan ??= gateway.Translate(portal.Statement.Sql);
        if (plan.Kind != PlanKind.Rows) { wire.Send('n'); return; }

        // Executing here is what makes the row description available; Execute drains the reader.
        Written(plan, portal.Arguments);
        portal.Command = Command(plan.Steps[^1], portal.Arguments);
        portal.Reader = Execute(portal.Command);
        portal.Columns = Columns(portal.Reader, portal.ResultFormats);
        SendRowDescription(portal.Reader, portal.Columns);
    }

    /// Describing an unexecuted statement means binding it without values. DuckDB cannot do that
    /// with open parameters, so they are stood in for by typed NULLs and the query run empty.
    /// Statement-level descriptions always advertise text format, per the protocol.
    void DescribeStatement(Prepared statement)
    {
        var plan = gateway.Translate(statement.Sql);
        if (plan.Kind != PlanKind.Rows) { wire.Send('n'); return; }

        var probe = statement.Sql;
        for (var i = statement.ParameterOids.Length; i >= 1; i--)
            probe = probe.Replace($"$p{i}", $"CAST(NULL AS {PgTypes.DuckDbType(statement.ParameterOids[i - 1])})");

        try
        {
            using var command = duck.CreateCommand();
            command.CommandText = $"SELECT * FROM ({probe}) LIMIT 0";
            using var reader = command.ExecuteReader();
            SendRowDescription(reader, Columns(reader, []));
        }
        catch (Exception)
        {
            wire.Send('n');
        }
    }

    void Execute(ref MsgReader reader)
    {
        var name = reader.Str();
        var maxRows = reader.I32();
        var portal = portals.GetValueOrDefault(name) ?? throw new PgError("34000", $"unknown portal `{name}`");
        var plan = portal.Plan ??= gateway.Translate(portal.Statement.Sql);

        // Execute never sends a row description -- the client already asked for one via Describe.
        if (plan.Kind == PlanKind.Rows)
        {
            if (portal.Reader is null)
            {
                Written(plan, portal.Arguments);
                portal.Command = Command(plan.Steps[^1], portal.Arguments);
                portal.Reader = Execute(portal.Command);
                portal.Columns = Columns(portal.Reader, portal.ResultFormats);
            }

            var sent = Stream(portal.Reader, maxRows, portal.Columns!);
            if (maxRows > 0 && sent == maxRows) { wire.Send('s'); return; }
            portal.Reset();
            wire.Send('C', new Msg().Str($"SELECT {sent}"));
            return;
        }
        RunPlan(plan, portal.Arguments, portal.ResultFormats);
    }

    /// What has to be true before a plan runs at all -- a reference nothing else may still be
    /// pointing at. Before, because a statement outside a transaction commits each step as it goes.
    void Checked(Plan plan, object?[] arguments)
    {
        foreach (var check in plan.Checks ?? [])
        {
            using var command = Command(check.Sql, arguments);
            using var reader = command.ExecuteReader();
            if (reader.Read()) throw new PgError(check.SqlState, check.Message);
        }
    }

    /// Everything a rows plan does before it has rows to give: an insert asked what it wrote puts
    /// them down first, and the last step is what answers.
    void Written(Plan plan, object?[] arguments)
    {
        if (!turn && (plan.Dirty is not null || plan.Tag == "BEGIN")) turn = gateway.EnterTurn();
        try
        {
            Checked(plan, arguments);
            Steps(plan, arguments, plan.Steps.Length - 1);
            if (plan.Steps.Length > 1) Persist(plan);
        }
        finally
        {
            if (transactionStatus == 'I') Release();
        }
    }

    /// The plan's write steps, in order, and how many rows the last of them touched. A step that
    /// generates a key hands it back instead of a count, so it is read rather than counted -- what
    /// the table last generated is a process's memory, and both front doors write to it.
    int Steps(Plan plan, object?[] arguments, int count)
    {
        var affected = 0;
        for (var i = 0; i < count; i++)
        {
            using var command = Command(plan.Steps[i], arguments);
            if (plan.Identity is not { } generated || i != plan.IdentityStep)
            {
                affected = command.ExecuteNonQuery();
                continue;
            }

            var (rows, value) = generated.Read(command);
            if (value is { } key) gateway.Identified(generated.Table, key);
            affected = rows;
        }
        return affected;
    }

    /// A serialized lake's turn to write, given up when the transaction that took it ends -- and
    /// with the session, so a client that vanishes mid-transaction cannot keep the lake to itself.
    void Release()
    {
        if (!turn) return;
        turn = false;
        gateway.ExitTurn();
    }

    void Close(ref MsgReader reader)
    {
        var kind = (char)reader.U8();
        var name = reader.Str();
        if (kind == 'S') statements.Remove(name);
        else if (portals.Remove(name, out var portal)) portal.Reset();
        wire.Send('3');
    }

    // ---- execution -------------------------------------------------------------------------------

    void RunPlan(Plan plan, object?[] arguments, short[] resultFormats)
    {
        if (!turn && (plan.Dirty is not null || plan.Tag == "BEGIN")) turn = gateway.EnterTurn();
        try
        {
            Perform(plan, arguments, resultFormats);
        }
        finally
        {
            if (transactionStatus == 'I') Release();
        }
    }

    void Perform(Plan plan, object?[] arguments, short[] resultFormats)
    {
        Checked(plan, arguments);

        switch (plan.Kind)
        {
            case PlanKind.Empty:
                wire.Send('I');
                return;

            case PlanKind.NoOp:
                wire.Send('C', new Msg().Str(plan.Tag));
                return;

            case PlanKind.Rows:
            {
                using var command = Command(plan.Steps[0], arguments);
                using var reader = Execute(command);
                var total = 0;
                do
                {
                    var columns = Columns(reader, resultFormats);
                    SendRowDescription(reader, columns);
                    total = Stream(reader, 0, columns);
                } while (reader.NextResult());
                gateway.Grew(plan, total);
                wire.Send('C', new Msg().Str($"SELECT {total}"));
                return;
            }

            case PlanKind.Count:
            {
                var started = Stopwatch.GetTimestamp();
                var affected = Steps(plan, arguments, plan.Steps.Length);
                if (plan.Affected is { } query)
                {
                    using var command = Command(query, arguments);
                    affected = Convert.ToInt32(command.ExecuteScalar());
                }
                gateway.Grew(plan, affected);
                logger.LogDebug("{Tag} {Affected} in {Elapsed:0.0} ms",
                    plan.Tag, affected, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                transactionStatus = plan.Tag switch
                {
                    "BEGIN" => 'T',
                    "COMMIT" or "ROLLBACK" => 'I',
                    _ => transactionStatus,
                };
                Persist(plan);
                wire.Send('C', new Msg().Str(CommandTag(plan.Tag, affected)));
                return;
            }
        }
    }

    /// A write is only on disk once DuckDB has committed it, so inside a transaction the tables
    /// touched are remembered and written out at COMMIT -- and forgotten at ROLLBACK.
    void Persist(Plan plan)
    {
        // A promotion is part of the write that caused it: it survives exactly as long, and a
        // rolled-back one is simply made again by the next write rather than assumed to be there.
        foreach (var promoted in plan.Promoted ?? [])
        {
            if (transactionStatus == 'T') pendingPromotions.Add(promoted);
            else gateway.Promoted(promoted);
        }

        foreach (var tombstoned in plan.Tombstoned ?? [])
        {
            if (transactionStatus == 'T') pendingTombstones.Add(tombstoned);
            else gateway.Tombstoned(tombstoned);
        }

        if (plan.Dirty is { Length: > 0 } dirty)
        {
            foreach (var table in dirty)
                if (transactionStatus == 'T') pendingWrites.Add(table);
                else gateway.Persist(table);
        }
        else if (plan.Tag == "COMMIT")
        {
            foreach (var table in pendingPromotions) gateway.Promoted(table);
            foreach (var table in pendingTombstones) gateway.Tombstoned(table);
            foreach (var table in pendingWrites) gateway.Persist(table);
            pendingPromotions.Clear();
            pendingTombstones.Clear();
            pendingWrites.Clear();
        }
        else if (plan.Tag == "ROLLBACK")
        {
            pendingPromotions.Clear();
            pendingTombstones.Clear();
            pendingWrites.Clear();
        }
    }

    static string CommandTag(string tag, int affected) => tag switch
    {
        "INSERT" => $"INSERT 0 {affected}",
        "UPDATE" or "DELETE" or "MERGE" or "COPY" => $"{tag} {affected}",
        _ => tag,
    };

    DbCommand Command(string sql, object?[] arguments)
    {
        var command = duck.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < arguments.Length; i++)
            command.Parameters.Add(new DuckDBParameter($"p{i + 1}", arguments[i]));
        return command;
    }

    /// The (oid, binary) decision per column, which the row description and the data rows must agree on.
    static (int Oid, bool Binary)[] Columns(DbDataReader reader, short[] resultFormats)
    {
        var columns = new (int Oid, bool Binary)[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            columns[i] = (PgTypes.Oid(reader.GetDataTypeName(i)),
                          resultFormats.Length switch { 0 => false, 1 => resultFormats[0] == 1, _ => resultFormats[i] == 1 });
        return columns;
    }

    void SendRowDescription(DbDataReader reader, (int Oid, bool Binary)[] columns)
    {
        var message = new Msg().I16(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
            message.Str(reader.GetName(i))
                   .I32(0)                       // table oid
                   .I16(i + 1)                   // column attribute number
                   .I32(columns[i].Oid)
                   .I16(PgTypes.TypeLength(columns[i].Oid))
                   .I32(-1)                      // type modifier
                   .I16(columns[i].Binary ? 1 : 0);
        wire.Send('T', message);
    }

    /// DuckDB executes on this call; the rows are then pulled lazily, so the two are timed apart.
    DbDataReader Execute(DbCommand command)
    {
        var started = Stopwatch.GetTimestamp();
        var reader = command.ExecuteReader();
        logger.LogDebug("executed in {Elapsed:0.0} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return reader;
    }

    int Stream(DbDataReader reader, int maxRows, (int Oid, bool Binary)[] columns)
    {
        var started = Stopwatch.GetTimestamp();
        var sent = SendRows(reader, maxRows, columns);
        logger.LogDebug("{Rows} rows in {Elapsed:0.0} ms", sent, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return sent;
    }

    int SendRows(DbDataReader reader, int maxRows, (int Oid, bool Binary)[] columns)
    {
        var sent = 0;
        var row = new Msg();

        // How each column is written is decided once here: the OID and the CLR type the reader hands
        // it back in are both fixed by its DuckDB type, so a value goes out in its own type rather
        // than through an `object` -- which on a wide result is a box a row a column.
        var fields = new PgField[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            fields[i] = PgField.For(reader.GetFieldType(i), columns[i].Oid, columns[i].Binary);

        while (reader.Read())
        {
            row.Clear();
            row.I16(columns.Length);
            for (var i = 0; i < columns.Length; i++)
            {
                if (reader.IsDBNull(i)) { row.I32(-1); continue; }
                var at = row.BeginField();
                fields[i].Write(row, reader, i);
                row.EndField(at);
            }
            wire.Send('D', row);
            if (++sent == maxRows) break;
        }
        return sent;
    }

    void ReadyForQuery() => wire.Send('Z', new Msg().U8((byte)transactionStatus));

    void SendError(Exception e)
    {
        if (transactionStatus == 'T') transactionStatus = 'E';
        var sqlState = PgError.SqlStateOf(e);
        // Told to the client, and said out loud: a failure only the client can see is one nobody
        // can look into afterwards.
        logger.LogWarning("{Error}", e.Message.ReplaceLineEndings(" "));
        wire.Send('E', new Msg()
            .U8((byte)'S').Str("ERROR")
            .U8((byte)'V').Str("ERROR")
            .U8((byte)'C').Str(sqlState)
            .U8((byte)'M').Str(e.Message.ReplaceLineEndings(" "))
            .U8(0));
    }

    public void Dispose()
    {
        server.Unregister(this);
        Release();
        foreach (var portal in portals.Values) portal.Reset();
        duck.Dispose();
        client.Dispose();
    }
}
