using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using Microsoft.Extensions.Logging;
using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg;

/// One SqlClient connection: the TDS state machine, its own DuckDB connection, and the T-SQL that
/// arrives on it. Statements are parsed and rendered into DuckDB SQL, then handed to the same
/// gateway the PostgreSQL side uses -- both front doors write the same files.
sealed class TdsSession(TcpClient client, Gateway gateway, DuckDBConnection duck, TdsServer server, ILogger logger)
    : IDisposable
{
    readonly TdsWire wire = new(client.GetStream(), logger);
    readonly Dictionary<string, string> login = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Prepared> prepared = [];
    readonly HashSet<string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingPromotions = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pendingTombstones = new(StringComparer.OrdinalIgnoreCase);

    /// What SqlClient believes the version of this server is. It gates features on it, and a
    /// version it does not know is a version it will not talk to.
    public const string ServerVersion = "16.0.1000";

    int tdsVersion = 0x74000004;
    long rowCount;
    int transactions;
    bool turn;
    long descriptor;
    int handles;

    sealed record Prepared(string Statement, string Declaration);

    static readonly Dictionary<string, object?> NoParameters = new();

    public void Run()
    {
        while (true)
        {
            var message = wire.ReadMessage();
            if (message is null) return;
            var (type, payload, reset) = message.Value;

            // A connection out of the pool is a session that never happened here: what the last one
            // prepared, made or left open goes before its first statement is read.
            if (reset) Reset();

            try
            {
                Dispatch(type, payload);
            }
            catch (Exception e) when (e is not (IOException or SocketException))
            {
                // A statement the client was told about is one the log should carry: a client that
                // reports a failure and a server that says nothing leave nowhere to look.
                logger.LogWarning("{Error}", e.Message.ReplaceLineEndings(" "));
                var msg = new TdsMsg();
                Error(msg, e);
                Done(msg, TdsToken.Done, Status.Error, 0);
                wire.Send(TdsMessage.Result, msg);
            }
        }
    }

    void Dispatch(byte type, byte[] payload)
    {
        switch (type)
        {
            case TdsMessage.PreLogin: PreLogin(payload); return;
            case TdsMessage.Login7: Login(payload); return;
            case TdsMessage.Batch: Batch(payload); return;
            case TdsMessage.Rpc: Rpc(payload); return;
            case TdsMessage.TransactionManager: TransactionManager(payload); return;

            case TdsMessage.Attention:
            {
                var msg = new TdsMsg();
                Done(msg, TdsToken.Done, Status.Attention, 0);
                wire.Send(TdsMessage.Result, msg);
                return;
            }

            default:
                throw new ProtocolException($"unsupported TDS message type {type}");
        }
    }

    // ---- handshake -------------------------------------------------------------------------------

    static class Option
    {
        public const byte Version = 0, Encryption = 1, Instance = 2, ThreadId = 3, Mars = 4,
                          TraceId = 5, FedAuthRequired = 6, Terminator = 0xFF;
    }

    /// Encryption is refused outright, which is what `Encrypt=False` in the connection string
    /// agrees to. TDS otherwise encrypts the login packet even when the session is plaintext, and
    /// that handshake needs a certificate this tool has no business owning.
    void PreLogin(byte[] payload)
    {
        var requested = new List<byte>();
        var reader = new TdsReader(payload);
        while (!reader.AtEnd)
        {
            var token = reader.U8();
            if (token == Option.Terminator) break;
            requested.Add(token);
            reader.Skip(4);
        }

        var options = new List<(byte Token, byte[] Value)>
        {
            (Option.Version, [16, 0, 0, 0, 0, 0]),
            (Option.Encryption, [2]), // ENCRYPT_NOT_SUP
            (Option.Mars, [0]),
        };
        if (requested.Contains(Option.FedAuthRequired)) options.Add((Option.FedAuthRequired, [0]));

        var msg = new TdsMsg();
        var offset = options.Count * 5 + 1;
        foreach (var (token, value) in options)
        {
            msg.U8(token).U8(offset >> 8).U8(offset & 0xFF).U8(value.Length >> 8).U8(value.Length & 0xFF);
            offset += value.Length;
        }
        msg.U8(Option.Terminator);
        foreach (var (_, value) in options) msg.Raw(value);

        wire.Send(TdsMessage.PreLogin, msg);
    }

    void Login(byte[] payload)
    {
        var reader = new TdsReader(payload);
        reader.Skip(4);
        tdsVersion = reader.I32();
        var packetSize = reader.I32();
        if (packetSize is >= 512 and <= 32767) wire.PacketSize = packetSize;

        // The fixed part is 36 bytes; after it come offset/length pairs into the same buffer.
        string Field(int index)
        {
            var pair = new TdsReader(payload);
            pair.Seek(36 + index * 4);
            var offset = pair.U16();
            var length = pair.U16();
            if (length == 0) return "";
            var value = new TdsReader(payload);
            value.Seek(offset);
            return value.Ucs2(length);
        }

        login["host"] = Field(0);
        login["user"] = Field(1);
        login["application_name"] = Field(3);
        login["database"] = Field(8) is { Length: > 0 } database ? database : "lake";

        ApplySessionVariables();

        var msg = new TdsMsg();
        EnvChange(msg, 1, login["database"], "");
        EnvChange(msg, 4, wire.PacketSize.ToString(), "4096");
        // Without a collation SqlClient has nothing to encode a string parameter with, and fails
        // inside its own RPC writer before anything reaches the wire.
        Collation(msg);

        // The version goes back big-endian, though the client sent it little-endian in LOGIN7 --
        // the one field in the handshake that changes byte order on the way home.
        var version = ServerVersion.Split('.');
        var build = int.Parse(version[2]);
        var ack = new TdsMsg()
            .U8(1)
            .U8(tdsVersion >> 24).U8(tdsVersion >> 16).U8(tdsVersion >> 8).U8(tdsVersion)
            .BVarchar("duckpg")
            .U8(int.Parse(version[0])).U8(int.Parse(version[1])).U8(build >> 8).U8(build);

        msg.U8(TdsToken.LoginAck).U16(ack.Length).Raw(ack.Body);
        Done(msg, TdsToken.Done, Status.Final, 0);
        wire.Send(TdsMessage.Result, msg);

        logger.LogDebug("{User}@{Application} connected to {Database}",
            login["user"], login["application_name"], login["database"]);
    }

    /// Startup values become DuckDB session variables, exactly as they do on the PostgreSQL side,
    /// so one `filter:` serves clients of either protocol.
    void ApplySessionVariables()
    {
        foreach (var (variable, source) in gateway.Config.SessionVariables)
        {
            using var command = duck.CreateCommand();
            command.CommandText =
                $"SET VARIABLE {SqlText.Quote(variable)} = {SqlText.Literal(login.GetValueOrDefault(source) ?? "")}";
            command.ExecuteNonQuery();
        }
    }

    static void Collation(TdsMsg msg)
    {
        var body = new TdsMsg().U8(7).U8(TdsTypes.Collation.Length).Raw(TdsTypes.Collation).U8(0);
        msg.U8(TdsToken.EnvChange).U16(body.Length).Raw(body.Body);
    }

    static void EnvChange(TdsMsg msg, byte type, string newValue, string oldValue)
    {
        var body = new TdsMsg().U8(type).BVarchar(newValue).BVarchar(oldValue);
        msg.U8(TdsToken.EnvChange).U16(body.Length).Raw(body.Body);
    }

    // ---- statements ------------------------------------------------------------------------------

    void Batch(byte[] payload)
    {
        var reader = new TdsReader(payload);
        SkipHeaders(ref reader);
        var sql = reader.Ucs2((payload.Length - reader.Position) / 2);

        var msg = new TdsMsg();
        Run(msg, sql, NoParameters, TdsToken.Done);
        wire.Send(TdsMessage.Result, msg);
    }

    /// Every batch and RPC carries a block of stream headers -- transaction descriptor, trace
    /// activity -- ahead of the payload proper.
    static void SkipHeaders(ref TdsReader reader)
    {
        var start = reader.Position;
        var total = reader.I32();
        if (total < 4 || total > 1 << 16) reader.Seek(start);
        else reader.Seek(start + total);
    }

    /// Runs one client statement, or several: a batch is a list of statements sharing a response,
    /// and every statement but the last says there is more to come.
    void Run(TdsMsg msg, string sql, IReadOnlyDictionary<string, object?> parameters, byte doneToken)
    {
        var context = new TSqlContext(gateway.Config.Schema, Variables(),
            (IReadOnlySet<string>)parameters.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), login["user"],
            gateway.Catalog.Types, gateway.Catalog.Functions);
        var statements = TSqlTranslator.Translate(sql, context);

        if (statements.Count == 0)
        {
            Done(msg, doneToken, Status.Final, 0);
            return;
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var last = i == statements.Count - 1;
            var plan = gateway.Translate(statements[i]);
            Execute(msg, plan, parameters, last ? doneToken : TdsToken.DoneInProc, last);
        }
    }

    void Execute(TdsMsg msg, Plan plan, IReadOnlyDictionary<string, object?> parameters, byte doneToken, bool last)
    {
        if (!turn && (plan.Dirty is not null || plan.Tag == "BEGIN")) turn = gateway.EnterTurn();
        try
        {
            Perform(msg, plan, parameters, doneToken, last);
        }
        finally
        {
            if (transactions == 0) Release();
        }
    }

    /// A serialized lake's turn to write, given up when the transaction that took it ends -- and
    /// with the session, so a client that vanishes mid-transaction cannot keep the lake to itself.
    void Release()
    {
        if (!turn) return;
        turn = false;
        gateway.ExitTurn();
    }

    void Perform(TdsMsg msg, Plan plan, IReadOnlyDictionary<string, object?> parameters, byte doneToken, bool last)
    {
        Checked(plan, parameters);

        switch (plan.Kind)
        {
            case PlanKind.Empty:
            case PlanKind.NoOp:
                Done(msg, doneToken, last ? Status.Final : Status.More, 0);
                return;

            case PlanKind.Rows:
            {
                // An insert asked what it wrote puts the rows down before it can answer with them;
                // whatever came first, the last step is the one that has rows.
                foreach (var step in plan.Steps[..^1])
                {
                    using var written = Command(step, parameters);
                    written.ExecuteNonQuery();
                }

                using var command = Command(plan.Steps[^1], parameters);
                using var reader = Execute(command);
                var rows = Rows(msg, reader);
                if (plan.Steps.Length > 1) Persist(plan);

                rowCount = rows;
                Done(msg, doneToken, (last ? Status.Final : Status.More) | Status.Count, rows);
                return;
            }

            case PlanKind.Count:
            {
                var affected = 0;
                var started = Stopwatch.GetTimestamp();
                foreach (var step in plan.Steps)
                {
                    using var command = Command(step, parameters);
                    affected = command.ExecuteNonQuery();
                }
                if (plan.Affected is { } query)
                {
                    using var command = Command(query, parameters);
                    affected = Convert.ToInt32(command.ExecuteScalar());
                }

                logger.LogDebug("{Tag} {Affected} in {Elapsed:0.0} ms",
                    plan.Tag, affected, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                transactions = plan.Tag switch
                {
                    "BEGIN" => transactions + 1,
                    "COMMIT" or "ROLLBACK" => Math.Max(0, transactions - 1),
                    _ => transactions,
                };
                Persist(plan);

                rowCount = affected;
                Done(msg, doneToken, (last ? Status.Final : Status.More) | Status.Count, affected);
                return;
            }
        }
    }

    /// What has to be true before a plan runs at all -- a reference nothing else may still be
    /// pointing at. Before, because a statement outside a transaction commits each step as it goes,
    /// so a rule enforced afterwards would be enforced on a row already gone.
    void Checked(Plan plan, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var check in plan.Checks ?? [])
        {
            using var command = Command(check.Sql, parameters);
            using var reader = command.ExecuteReader();
            if (reader.Read()) throw new PgError("23503", check.Message);
        }
    }

    /// A write is on disk once DuckDB has committed it -- inside a transaction that is at COMMIT,
    /// which is the same rule the PostgreSQL session follows.
    void Persist(Plan plan)
    {
        // A promotion is part of the write that caused it: it survives exactly as long, and a
        // rolled-back one is simply made again by the next write rather than assumed to be there.
        foreach (var promoted in plan.Promoted ?? [])
        {
            if (transactions > 0) pendingPromotions.Add(promoted);
            else gateway.Promoted(promoted);
        }

        foreach (var tombstoned in plan.Tombstoned ?? [])
        {
            if (transactions > 0) pendingTombstones.Add(tombstoned);
            else gateway.Tombstoned(tombstoned);
        }

        if (plan.Dirty is { Length: > 0 } dirty)
        {
            foreach (var table in dirty)
                if (transactions > 0) pendingWrites.Add(table);
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

    /// SQL Server caps an identifier at `sysname`, which is 128 characters, and answers with no name
    /// at all for a column no alias named. DuckDB instead names an unaliased column after the text
    /// of the expression that produced it, which a `CASE WHEN EXISTS (...)` passes 255 without
    /// trying -- and a name that long cannot go on the wire at all, since COLMETADATA counts it in
    /// one byte. Cut where SQL Server cuts.
    static string Named(string name) => name.Length <= 128 ? name : name[..128];

    long Rows(TdsMsg msg, DbDataReader reader)
    {
        var columns = new TdsColumn[reader.FieldCount];
        System.Data.DataTable? schema = null;
        msg.U8(TdsToken.ColMetadata).U16(reader.FieldCount);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = TdsTypes.Describe(reader.GetDataTypeName(i));
            // DuckDB reports a DECIMAL column as plain `Decimal`; its precision and scale are only
            // in the schema, and TDS has to declare both up front. A HUGEINT is sent as a decimal
            // too and has neither, so the shape it was described with stands.
            if (columns[i].Token == TdsTypes.DecimalN)
            {
                schema ??= reader.GetSchemaTable();
                if (schema!.Rows[i]["NumericPrecision"] is not DBNull)
                    columns[i] = TdsTypes.Decimal(
                        Convert.ToInt32(schema.Rows[i]["NumericPrecision"]),
                        Convert.ToInt32(schema.Rows[i]["NumericScale"]));
            }
            msg.I32(0).U16(0x0001); // no user type; nullable
            TdsTypes.WriteTypeInfo(msg, columns[i]);
            msg.BVarchar(Named(reader.GetName(i)));
        }

        var rows = 0L;
        var started = Stopwatch.GetTimestamp();

        var row = new TdsMsg();

        // Sends what the caller says is safe to send, and answers whether the client gave up while
        // it was going out -- a cancel is only visible between packets.
        bool Flush(int count)
        {
            wire.SendUpTo(TdsMessage.Result, msg, count);
            return Canceled();
        }

        while (reader.Read())
        {
            // Built on its own, as though it began a packet -- which is where it lands when it does
            // not fit in the one being filled, and the offset its values were chunked against.
            row.Clear();
            row.U8(TdsToken.Row);
            for (var i = 0; i < reader.FieldCount; i++)
                TdsTypes.WriteValue(row, columns[i], reader.IsDBNull(i) ? null : reader.GetValue(i), wire.Payload);

            // A row cut across the seam between two packets loses a client replaying a split read
            // its place, so one that does not fit ends the packet here and starts the next.
            if (msg.Length > 0 && msg.Length + row.Length > wire.Payload && Flush(msg.Length)) break;

            msg.Raw(row.Body);
            rows++;

            // A row too big for any packet has to be cut, and is cut on the packet boundaries its
            // own values were chunked to -- which are whole payloads from where the row started.
            if (msg.Length >= wire.Payload && Flush(msg.Length / wire.Payload * wire.Payload)) break;
        }

        logger.LogDebug("{Rows} rows in {Elapsed:0.0} ms", rows, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return rows;
    }

    /// An Attention interrupts DuckDB and ends the response; anything else arriving mid-query is
    /// a client that broke the request/response rule.
    bool Canceled()
    {
        if (!wire.DataAvailable) return false;

        var message = wire.ReadMessage();
        if (message?.Type != TdsMessage.Attention) return false;

        NativeMethods.Startup.DuckDBInterrupt(duck.NativeConnection);
        attention = true;
        return true;
    }

    bool attention;

    DbCommand Command(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        var command = duck.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new DuckDBParameter(name, value ?? DBNull.Value));
        return command;
    }

    DbDataReader Execute(DbCommand command)
    {
        var started = Stopwatch.GetTimestamp();
        var reader = command.ExecuteReader();
        logger.LogDebug("executed in {Elapsed:0.0} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return reader;
    }

    IReadOnlyDictionary<string, string> Variables() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["version"] = SqlText.Literal($"Microsoft SQL Server {ServerVersion} (duckpg)"),
        ["rowcount"] = rowCount.ToString(),
        ["trancount"] = transactions.ToString(),
        ["spid"] = ProcessId.ToString(),
        ["servername"] = SqlText.Literal("duckpg"),
        ["language"] = SqlText.Literal("us_english"),
        ["identity"] = "NULL",
    };

    public int ProcessId { get; } = Random.Shared.Next(1, short.MaxValue);

    // ---- remote procedure calls ------------------------------------------------------------------

    static class Procedure
    {
        public const ushort ExecuteSql = 10, Prepare = 11, Execute = 12, PrepExec = 13, Unprepare = 15;
    }

    void Rpc(byte[] payload)
    {
        var reader = new TdsReader(payload);
        SkipHeaders(ref reader);
        var msg = new TdsMsg();

        while (!reader.AtEnd)
        {
            var nameLength = reader.U16();
            var procedure = nameLength == 0xFFFF ? reader.U16() : (ushort)0;
            var name = nameLength == 0xFFFF ? "" : reader.Ucs2(nameLength);
            reader.Skip(2); // option flags

            var arguments = new List<(string Name, object? Value)>();
            while (!reader.AtEnd)
            {
                // A byte where a parameter name should start is the separator before the next call.
                if (payload[reader.Position] is 0x80 or 0xFF) break;
                var parameter = reader.BVarchar();
                reader.Skip(1); // status flags
                arguments.Add((parameter.TrimStart('@'), TdsTypes.ReadValue(ref reader)));
            }

            Call(msg, procedure, name, arguments);

            if (!reader.AtEnd) reader.Skip(1);
        }

        wire.Send(TdsMessage.Result, msg);
    }

    void Call(TdsMsg msg, ushort procedure, string name, List<(string Name, object? Value)> arguments)
    {
        if (name.Length > 0 && !name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            throw new PgError("42883", $"stored procedure {name} does not exist");

        var kind = procedure != 0 ? procedure : name.ToLowerInvariant() switch
        {
            "sp_executesql" => Procedure.ExecuteSql,
            "sp_prepare" => Procedure.Prepare,
            "sp_prepexec" => Procedure.PrepExec,
            "sp_execute" => Procedure.Execute,
            "sp_unprepare" => Procedure.Unprepare,
            // The connection reset a pooled connection asks for: state goes, files stay.
            "sp_reset_connection" => 0,
            _ => throw new PgError("42883", $"stored procedure {name} is not supported"),
        };

        switch (kind)
        {
            case Procedure.ExecuteSql:
                Run(msg, Text(arguments, 0), Bind(Declaration(arguments, 1), arguments.Skip(2)), TdsToken.DoneInProc);
                break;

            case Procedure.PrepExec:
            {
                var handle = ++handles;
                prepared[handle] = new Prepared(Text(arguments, 2), Declaration(arguments, 1));
                ReturnValue(msg, "handle", handle, wire.Payload);
                Run(msg, prepared[handle].Statement,
                    Bind(prepared[handle].Declaration, arguments.Skip(3)), TdsToken.DoneInProc);
                break;
            }

            case Procedure.Prepare:
            {
                var handle = ++handles;
                prepared[handle] = new Prepared(Text(arguments, 2), Declaration(arguments, 1));
                ReturnValue(msg, "handle", handle, wire.Payload);
                Done(msg, TdsToken.DoneInProc, Status.Final, 0);
                break;
            }

            case Procedure.Execute:
            {
                var handle = Convert.ToInt32(arguments[0].Value);
                if (!prepared.TryGetValue(handle, out var statement))
                    throw new PgError("26000", $"prepared handle {handle} is unknown");
                Run(msg, statement.Statement, Bind(statement.Declaration, arguments.Skip(1)), TdsToken.DoneInProc);
                break;
            }

            case Procedure.Unprepare:
                prepared.Remove(Convert.ToInt32(arguments[0].Value));
                Done(msg, TdsToken.DoneInProc, Status.Final, 0);
                break;

            default:
                Reset();
                Done(msg, TdsToken.DoneInProc, Status.Final, 0);
                break;
        }

        msg.U8(TdsToken.ReturnStatus).I32(0);
        Done(msg, TdsToken.DoneProc, Status.Final, 0);
    }

    void Reset()
    {
        prepared.Clear();
        pendingWrites.Clear();
        transactions = 0;
        Release();
        DropTemporaryTables();
    }

    /// A connection given back to the pool comes out of it as a new session, and SQL Server drops
    /// what the old one made. A `#table` left behind would be found by whoever gets this connection
    /// next, which is worse than not having it: a client that makes one usually asks first.
    void DropTemporaryTables()
    {
        var tables = new List<string>();
        using (var command = duck.CreateCommand())
        {
            command.CommandText = "SELECT table_name FROM duckdb_tables() WHERE temporary";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            using var command = duck.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS temp.main.{SqlText.Quote(table)}";
            command.ExecuteNonQuery();
        }
    }

    static string Text(List<(string Name, object? Value)> arguments, int index) =>
        index < arguments.Count ? arguments[index].Value?.ToString() ?? "" : "";

    static string Declaration(List<(string Name, object? Value)> arguments, int index) => Text(arguments, index);

    /// sp_executesql declares its parameters in a string; the values follow in the same order, so
    /// the declaration is only needed for the names a statement will refer to.
    static Dictionary<string, object?> Bind(string declaration, IEnumerable<(string Name, object? Value)> values)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var declared = declaration
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Select(name => name.TrimStart('@'))
            .Where(name => name.Length > 0)
            .ToList();

        var index = 0;
        foreach (var (name, value) in values)
        {
            var key = name.Length > 0 ? name : index < declared.Count ? declared[index] : $"p{index}";
            bound[key] = value;
            index++;
        }

        // A declared parameter the client did not send a value for is still a name the statement
        // may mention, and DuckDB needs every placeholder bound.
        foreach (var name in declared) bound.TryAdd(name, null);
        return bound;
    }

    static void ReturnValue(TdsMsg msg, string name, int value, int payload)
    {
        var column = new TdsColumn(TdsTypes.IntN, 4);
        msg.U8(0xAC).U16(0).BVarchar(name).U8(0x01).I32(0).U16(0x0001);
        TdsTypes.WriteTypeInfo(msg, column);
        TdsTypes.WriteValue(msg, column, value, payload);
    }

    // ---- transactions ----------------------------------------------------------------------------

    void TransactionManager(byte[] payload)
    {
        var reader = new TdsReader(payload);
        SkipHeaders(ref reader);
        var request = reader.U16();

        var msg = new TdsMsg();
        switch (request)
        {
            case 5: // TM_BEGIN_XACT
                Run(msg, "BEGIN TRANSACTION", NoParameters, TdsToken.Done);
                descriptor++;
                Transaction(msg, 8, descriptor, 0);
                break;

            case 7: // TM_COMMIT_XACT
                Run(msg, "COMMIT", NoParameters, TdsToken.Done);
                Transaction(msg, 9, 0, descriptor);
                break;

            case 8: // TM_ROLLBACK_XACT
                Run(msg, "ROLLBACK", NoParameters, TdsToken.Done);
                Transaction(msg, 10, 0, descriptor);
                break;

            default:
                throw new ProtocolException($"unsupported transaction request {request}");
        }

        Done(msg, TdsToken.Done, Status.Final, 0);
        wire.Send(TdsMessage.Result, msg);
    }

    /// The client keeps the descriptor and quotes it back in the headers of everything it sends
    /// while the transaction is open.
    static void Transaction(TdsMsg msg, byte type, long newValue, long oldValue)
    {
        var body = new TdsMsg().U8(type);
        if (newValue != 0) body.U8(8).I64(newValue); else body.U8(0);
        if (oldValue != 0) body.U8(8).I64(oldValue); else body.U8(0);
        msg.U8(TdsToken.EnvChange).U16(body.Length).Raw(body.Body);
    }

    // ---- tokens ----------------------------------------------------------------------------------

    static class Status
    {
        public const int Final = 0x0000, More = 0x0001, Error = 0x0002, Count = 0x0010, Attention = 0x0020;
    }

    void Done(TdsMsg msg, byte token, int status, long rows)
    {
        if (attention)
        {
            status |= Status.Attention;
            attention = false;
        }
        msg.U8(token).U16(status).U16(0).I64(rows);
    }

    /// SqlClient raises anything of class 11 or higher as a SqlException, and reads Number for the
    /// cases an application checks by hand.
    void Error(TdsMsg msg, Exception e)
    {
        var number = e switch
        {
            TSqlException => 102,
            _ => PgError.SqlStateOf(e) switch
            {
                "23503" => 547,     // a reference still points at the row
                "42P01" => 208,     // invalid object name
                "42703" => 207,     // invalid column name
                "42601" => 102,     // syntax error
                "22P02" => 245,     // conversion failed
                "23000" => 2627,    // constraint violation
                "42883" => 2812,    // could not find stored procedure
                _ => 50000,
            },
        };

        var body = new TdsMsg()
            .I32(number)
            .U8(1)                                  // state
            .U8(16)                                 // class: an error the client raises
            .UsVarchar(e.Message.ReplaceLineEndings(" "))
            .BVarchar("duckpg")
            .BVarchar("")
            .I32(e is TSqlException tsql ? tsql.Position : 0);

        msg.U8(TdsToken.Error).U16(body.Length).Raw(body.Body);
    }

    public void Dispose()
    {
        server.Unregister(this);
        Release();
        duck.Dispose();
        client.Dispose();
    }
}
