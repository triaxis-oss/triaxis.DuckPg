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

    /// One parameter of a call: what the caller sent, the column an answer to it goes back in, and
    /// whether it asked for one at all.
    sealed record Parameter(object? Value, TdsColumn Column, bool Output);

    static readonly Dictionary<string, Parameter> NoParameters = new();

    /// What the batch assigned to the caller's parameters, which is what an OUTPUT one is answered
    /// with. Per call: a statement that assigns nothing leaves the parameter as it went in.
    readonly Dictionary<string, object?> assigned = new(StringComparer.OrdinalIgnoreCase);

    /// The last key this session's writes generated, which is what `SCOPE_IDENTITY()` answers. Not
    /// cleared by a rollback: SQL Server does not give a generated key back either.
    decimal? identity;

    /// Whether anything on this connection has made a temporary table since the last reset. Asking
    /// DuckDB instead means enumerating the entire catalog -- every layer's tables and every
    /// published view, each one costing its own `CREATE` text -- and it costs that whether the
    /// session made one or not. A pooled checkout resets before every statement an ORM sends, so
    /// the common answer has to be free.
    bool temporaries;

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
        // What a client sends here is a request, and TDS lets the server answer with a size of its
        // own that both then use -- so how a response is chunked is the lake's to decide rather than
        // SqlClient's 8000-byte default's.
        var requested = reader.I32();
        wire.PacketSize = gateway.Config.TdsPacketSize;

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
        EnvChange(msg, 4, wire.PacketSize.ToString(), requested.ToString());
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
    void Run(TdsMsg msg, string sql, IReadOnlyDictionary<string, Parameter> parameters, byte doneToken)
    {
        var statements = TSqlParser.Parse(sql);

        if (statements.Count == 0)
        {
            Done(msg, doneToken, Status.Final, 0);
            return;
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var last = i == statements.Count - 1;

            // Rendered one at a time rather than the batch at once: `@@ROWCOUNT`, `@@TRANCOUNT` and
            // `SCOPE_IDENTITY()` go into the statement as literals, so a statement has to be
            // written after the one before it has run -- which is what `INSERT …; SELECT
            // SCOPE_IDENTITY()` is asking for.
            var translated = TSqlTranslator.Translate(statements[i], Context(parameters));
            var plan = gateway.Translate(translated.Sql);
            Execute(msg, plan, parameters, last ? doneToken : TdsToken.DoneInProc, last, translated);
        }
    }

    TSqlContext Context(IReadOnlyDictionary<string, Parameter> parameters) =>
        new(gateway.Config.Schema, Variables(),
            (IReadOnlySet<string>)parameters.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), login["user"],
            gateway.Catalog.Types, gateway.Catalog.Functions, false,
            gateway.Config.SortSmallTables ? gateway.Catalog.Rows : null,
            identity, gateway.IdentityOf);

    void Execute(TdsMsg msg, Plan plan, IReadOnlyDictionary<string, Parameter> parameters, byte doneToken, bool last,
                 Translated? translated = null)
    {
        if (!turn && (plan.Dirty is not null || plan.Tag == "BEGIN")) turn = gateway.EnterTurn();
        try
        {
            Perform(msg, plan, parameters, doneToken, last, translated);
        }
        catch (Exception e) when (plan.Violation is { } refused && refused.Caused(e))
        {
            throw new PgError(refused.SqlState, refused.Message);
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

    void Perform(TdsMsg msg, Plan plan, IReadOnlyDictionary<string, Parameter> parameters, byte doneToken,
                 bool last, Translated? translated)
    {
        Checked(plan, parameters);

        switch (plan.Kind)
        {
            case PlanKind.Empty:
            case PlanKind.NoOp:
                Done(msg, doneToken, last ? Status.Final : Status.More, 0);
                return;

            // An assignment select produces its values like any other query and hands none of them
            // to the client: they go into the caller's parameters, and back out with the call.
            case PlanKind.Rows when translated?.Assigns is { } variables:
            {
                using var command = Command(plan.Steps[^1], parameters);
                using var reader = Execute(command);

                // The last row wins, and a query that found none leaves the variables alone --
                // which is what SQL Server does, and why `@@ROWCOUNT` is what it counted.
                var rows = 0L;
                while (reader.Read())
                {
                    for (var i = 0; i < variables.Count; i++)
                        assigned[variables[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows++;
                }

                // `@@ROWCOUNT` counts what the query found, but the DONE token carries no count: an
                // assignment select is not a statement that affected rows, and a caller running
                // `INSERT …; SELECT @id = SCOPE_IDENTITY()` through `ExecuteNonQuery` is told about
                // the insert alone.
                rowCount = rows;
                Done(msg, doneToken, last ? Status.Final : Status.More, 0);
                return;
            }

            case PlanKind.Rows:
            {
                // An insert asked what it wrote puts the rows down before it can answer with them;
                // whatever came first, the last step is the one that has rows.
                Steps(plan, parameters, plan.Steps.Length - 1);

                using var command = Command(plan.Steps[^1], parameters);
                using var reader = Execute(command);

                // The ordering and the limit were taken off the statement before it was sent, so
                // they are applied to what came back -- and only ever for a table the catalog
                // counted small, which is what bounds how much is held to do it.
                using var result = translated?.Reorder is { } reorder ? (IRows)SortedRows.Of(reader, reorder)
                                                                      : new ReaderRows(reader);
                var rows = Rows(msg, result);
                gateway.Grew(plan, (int)rows);
                // Whether a plan wrote is what `Dirty` says and never what it is made of: a write
                // that needs no rewriting is one statement answering with `RETURNING`, and counting
                // steps would leave what it wrote unpersisted. `Persist` reads that itself.
                Persist(plan);

                rowCount = rows;
                Done(msg, doneToken, (last ? Status.Final : Status.More) | Status.Count, rows);
                return;
            }

            case PlanKind.Count:
            {
                var started = Stopwatch.GetTimestamp();
                var affected = Steps(plan, parameters, plan.Steps.Length);
                if (plan.Affected is { } query)
                {
                    using var command = Command(query, parameters);
                    affected = Convert.ToInt32(command.ExecuteScalar());
                }

                gateway.Grew(plan, affected);

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

    /// The plan's write steps, in order, and how many rows the last of them touched. One of them may
    /// be the step that generates a key, and that one is read rather than counted: what it hands
    /// back is what `SCOPE_IDENTITY()` and `IDENT_CURRENT` are later asked about.
    int Steps(Plan plan, IReadOnlyDictionary<string, Parameter> parameters, int count)
    {
        var affected = 0;
        for (var i = 0; i < count; i++)
        {
            using var command = Command(plan.Steps[i], parameters);
            if (plan.Identity is not { } generated || i != plan.IdentityStep)
            {
                affected = command.ExecuteNonQuery();
                continue;
            }

            var (rows, value) = generated.Read(command);
            if (value is { } key)
            {
                identity = key;
                gateway.Identified(generated.Table, key);
            }
            affected = rows;
        }
        return affected;
    }

    /// What has to be true before a plan runs at all -- a reference nothing else may still be
    /// pointing at. Before, because a statement outside a transaction commits each step as it goes,
    /// so a rule enforced afterwards would be enforced on a row already gone.
    void Checked(Plan plan, IReadOnlyDictionary<string, Parameter> parameters)
    {
        foreach (var check in plan.Checks ?? [])
        {
            using var command = Command(check.Sql, parameters);
            using var reader = command.ExecuteReader();
            if (reader.Read()) throw new PgError(check.SqlState, check.Message);
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

    long Rows(TdsMsg msg, IRows reader)
    {
        var columns = new TdsColumn[reader.FieldCount];
        var fields = new TdsField[reader.FieldCount];
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
                schema ??= reader.Schema;
                if (schema!.Rows[i]["NumericPrecision"] is not DBNull)
                    columns[i] = TdsTypes.Decimal(
                        Convert.ToInt32(schema.Rows[i]["NumericPrecision"]),
                        Convert.ToInt32(schema.Rows[i]["NumericScale"]));
            }
            // How a column's values are written is decided here rather than per row: the token and
            // the CLR the reader hands it back in are both fixed by its DuckDB type, so the value
            // can be read in its own type instead of going out through an `object`.
            fields[i] = TdsField.For(reader.GetFieldType(i), columns[i]);

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
                fields[i].Write(row, columns[i], reader, i, wire.Payload);

            // A row cut across the seam between two packets loses a client replaying a split read
            // its place, so one that does not fit ends the packet here and starts the next. One
            // that cut a packet of its own while it was built assumed it would start one, so it is
            // put where that is true.
            if (msg.Length > 0 && (msg.Length + row.Length > wire.Payload || row.HasCuts)
                && Flush(msg.Length)) break;

            msg.Append(row);
            rows++;

            // A row too big for any packet has to be cut, and is cut on the boundaries its own
            // values were chunked to -- whole payloads from where the row started, and sooner where
            // a value's framing ended a packet early to stay out of the seam.
            if (msg.Length >= wire.Payload && Flush(msg.Complete(wire.Payload))) break;
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

    DbCommand Command(string sql, IReadOnlyDictionary<string, Parameter> parameters)
    {
        temporaries = temporaries || SqlText.MakesTemporary(sql);
        var command = duck.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, parameter) in parameters)
            command.Parameters.Add(new DuckDBParameter(name, parameter.Value ?? DBNull.Value));
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

            var arguments = new List<Argument>();
            while (!reader.AtEnd)
            {
                // A byte where a parameter name should start is the separator before the next call.
                if (payload[reader.Position] is 0x80 or 0xFF) break;
                var parameter = reader.BVarchar();
                var status = reader.U8();
                var (column, value) = TdsTypes.ReadParameter(ref reader);

                // BY_REF_VALUE: the caller wants this one back, whatever the batch makes of it.
                arguments.Add(new Argument(parameter.TrimStart('@'), value, column, (status & 0x01) != 0));
            }

            Call(msg, procedure, name, arguments);

            if (!reader.AtEnd) reader.Skip(1);
        }

        wire.Send(TdsMessage.Result, msg);
    }

    /// One argument of a call, as it arrived: what the caller named it, what it sent, the column an
    /// answer goes back in, and whether it asked for one.
    sealed record Argument(string Name, object? Value, TdsColumn Column, bool Output);

    void Call(TdsMsg msg, ushort procedure, string name, List<Argument> arguments)
    {
        if (name.Length > 0 && !name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
            throw new PgError("42883", $"stored procedure {name} does not exist");

        assigned.Clear();

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
            {
                var bound = Bind(Declaration(arguments, 1), arguments.Skip(2));
                Run(msg, Text(arguments, 0), bound, TdsToken.DoneInProc);
                Outputs(msg, bound);
                break;
            }

            case Procedure.PrepExec:
            {
                var handle = ++handles;
                prepared[handle] = new Prepared(Text(arguments, 2), Declaration(arguments, 1));
                ReturnValue(msg, "handle", new TdsColumn(TdsTypes.IntN, 4), handle);

                var bound = Bind(prepared[handle].Declaration, arguments.Skip(3));
                Run(msg, prepared[handle].Statement, bound, TdsToken.DoneInProc);
                Outputs(msg, bound);
                break;
            }

            case Procedure.Prepare:
            {
                var handle = ++handles;
                prepared[handle] = new Prepared(Text(arguments, 2), Declaration(arguments, 1));
                ReturnValue(msg, "handle", new TdsColumn(TdsTypes.IntN, 4), handle);
                Done(msg, TdsToken.DoneInProc, Status.Final, 0);
                break;
            }

            case Procedure.Execute:
            {
                var handle = Convert.ToInt32(arguments[0].Value);
                if (!prepared.TryGetValue(handle, out var statement))
                    throw new PgError("26000", $"prepared handle {handle} is unknown");

                var bound = Bind(statement.Declaration, arguments.Skip(1));
                Run(msg, statement.Statement, bound, TdsToken.DoneInProc);
                Outputs(msg, bound);
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

    /// What the caller asked to have back. A parameter it marked OUTPUT carries whatever the batch
    /// assigned to it, in the type the caller declared -- and one nothing assigned comes back as it
    /// went in, which is what a procedure that never touched it hands back.
    void Outputs(TdsMsg msg, Dictionary<string, Parameter> bound)
    {
        foreach (var (name, parameter) in bound.Where(p => p.Value.Output))
            ReturnValue(msg, name, parameter.Column,
                        assigned.TryGetValue(name, out var value) ? value : parameter.Value);
    }

    void Reset()
    {
        prepared.Clear();
        transactions = 0;

        // What an abandoned transaction meant to do is not the next session's to finish, so all
        // three go together -- a promotion left here would be made by whatever that session commits.
        pendingWrites.Clear();
        pendingPromotions.Clear();
        pendingTombstones.Clear();

        // A key is session state, and this connection is now somebody else's: whoever gets it next
        // asking `SCOPE_IDENTITY()` must not be handed a row the last session wrote.
        identity = null;
        Release();
        if (temporaries) DropTemporaryTables();
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

        temporaries = false;
    }

    static string Text(List<Argument> arguments, int index) =>
        index < arguments.Count ? arguments[index].Value?.ToString() ?? "" : "";

    static string Declaration(List<Argument> arguments, int index) => Text(arguments, index);

    /// The names a parameter declaration lists, in order. Split only at top-level commas: a type's
    /// own -- `decimal(18,2)` -- is inside parentheses, and cutting there invents a parameter that
    /// shifts every name after it onto the wrong value for a caller that sends them unnamed.
    internal static List<string> DeclaredNames(string declaration) =>
        [.. SqlText.SplitList(declaration, ',')
            .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Select(name => name.TrimStart('@'))
            .Where(name => name.Length > 0)];

    /// sp_executesql declares its parameters in a string; the values follow in the same order, so
    /// the declaration is only needed for the names a statement will refer to.
    static Dictionary<string, Parameter> Bind(string declaration, IEnumerable<Argument> values)
    {
        var bound = new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
        var declared = DeclaredNames(declaration);

        var index = 0;
        foreach (var argument in values)
        {
            var key = argument.Name.Length > 0 ? argument.Name
                : index < declared.Count ? declared[index] : $"p{index}";
            bound[key] = new Parameter(argument.Value, argument.Column, argument.Output);
            index++;
        }

        // A declared parameter the client did not send a value for is still a name the statement
        // may mention, and DuckDB needs every placeholder bound. It cannot be one asked back for,
        // since a caller expecting an answer sends the parameter -- so its column is never written.
        foreach (var name in declared)
            bound.TryAdd(name, new Parameter(null, new TdsColumn(TdsTypes.NVarChar, TdsTypes.Max), false));
        return bound;
    }

    /// A value going back to the caller under the name it gave the parameter -- the `@` included,
    /// since that is the name SqlClient matches its own parameters by.
    void ReturnValue(TdsMsg msg, string name, TdsColumn column, object? value)
    {
        msg.U8(TdsToken.ReturnValue).U16(0).BVarchar("@" + name).U8(0x01).I32(0).U16(0x0001);
        TdsTypes.WriteTypeInfo(msg, column);
        TdsTypes.WriteValue(msg, column, value, wire.Payload);
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
                "23505" => 2627,    // the key is already there
                "42P01" => 208,     // invalid object name
                "42703" => 207,     // invalid column name
                "42601" => 102,     // syntax error
                "22P02" => 245,     // conversion failed
                "23000" => 2627,    // constraint violation
                "42883" => 2812,    // could not find stored procedure
                _ => 50000,
            },
        };

        // Cut where SQL Server cuts its own messages. The token counts the text in a U16 of
        // characters, so one past that would wrap the count and the client would read the rest of
        // the message's own bytes as tokens -- the same length rule `Named` enforces on a column.
        var message = e.Message.ReplaceLineEndings(" ");
        if (message.Length > 2048) message = message[..2048];

        var body = new TdsMsg()
            .I32(number)
            .U8(1)                                  // state
            .U8(16)                                 // class: an error the client raises
            .UsVarchar(message)
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
