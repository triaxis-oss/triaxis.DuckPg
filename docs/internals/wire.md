# The two front doors, from the inside

What a client can rely on is [protocols.md](../protocols.md); this is what each protocol demanded and does not say out loud.

## Types on the wire

- **The type catalog describes the OIDs the gateway puts on the wire**, not DuckDB's own types.
  `Shims.Macros` replaces `pg_type` wholesale for that reason; DuckDB's has NULL oids and its own
  type names.
- **The reader's type names are its own**: `UnsignedBigInt`, `TimestampMs`, `HugeInt` -- not the SQL
  spellings a `CAST` is written with. Both `PgTypes.Oid` and `TdsTypes.Describe` key off them, and a
  name that matches nothing is published as text, silently. That is how summing an integer column
  reached SqlClient as a string: DuckDB sums into a HUGEINT, which was mapped nowhere.

## PostgreSQL

- Npgsql closes the connection on SQLSTATE `XX000`, so `PgError.SqlStateOf` mapping DuckDB's error
  text to real codes is what makes a failed query survivable. Cancellation must map to `57014`.
- Npgsql hands back every column as `String` regardless of OID until the data goes out in binary
  format — hence `PgTypes.WriteBinary`, including base-10000 `numeric`.
- A DataRow is a few bytes and a socket write is a syscall, which is what once held the PostgreSQL
  wire to ~250k rows/s. Responses go out through a `BufferedStream`; only the write side, because
  one cannot interleave reads and writes over a socket, and `PgWire` flushes before every read so
  nothing can sit in the buffer while the server waits on the client.
- With the syscalls gone, allocation is what is left: a row is formatted straight into a `Msg` that
  the loop reuses (`Utf8`, `Format`, `BeginField`), never into a `byte[]` or a `string` per value.
  Anything on the row path that returns a fresh array puts the ceiling back.
- **A GUI client reads the catalog through casts and arities psql never uses.** `regclass` is a real
  type in `Shims.Macros` rather than another textual replacement, because `Shims.Apply` only touches
  SQL that names `pg_catalog.` and a client casting `'"lake"."orders"'::regclass` need not. DuckDB's
  own `pg_get_viewdef` takes an oid and nothing else, and arity is checked before the argument's type
  is looked at, so the two-argument pretty form is refused whatever the name resolves to -- which is
  why it is shadowed by one that takes both, and answers to a name as well as to an oid. A size
  (`pg_total_relation_size` and its kin) is NULL rather than 0: 0 is what an empty table reports, and
  a view over files is not one.
- **`pg_constraint` is the lake's, not DuckDB's.** Its shape is wrong -- `confkey` is an integer
  where PostgreSQL has a list, so a client unnesting it is refused before the row count matters --
  and so are its contents: a layered table is a view, which carries no constraint at all, while the
  keys and references the lake enforces are rules over the merged view that only the catalog knows
  ([schema](schema.md)). So `Catalog.Constraints` writes what is declared into `duckpg_constraints`
  after the relations exist -- attribute numbers are DuckDB's to hand out -- and the shim view joins
  it back into PostgreSQL's shape. Uniques are left out: publishing one would promise a rule a
  layered lake does not keep.
- **A macro body sees the caller's aliases.** `pg_get_function_identity_arguments` reads `pg_proc`
  with an alias of its own for that reason: a client selecting `FROM pg_proc p` and a body that also
  said `p` compares the row to itself, and every function comes back with the same argument list --
  an answer, and the wrong one, which is worse than the error it replaced. DuckDB gives several
  functions one oid, so where its catalog cannot tell two apart neither can this.
- `Describe('S')` must answer, or `cmd.Prepare()` fails. DuckDB cannot bind a statement with open
  parameters, so typed `NULL`s are substituted and the query run `LIMIT 0`.

## TDS

- **The TDS version in LOGINACK is big-endian**, though LOGIN7 sends it little-endian. Get it wrong
  and SqlClient says "invalid or unsupported protocol version" with no further clue.
- **The login response must carry a collation (ENVCHANGE 7).** Without one SqlClient throws a
  `NullReferenceException` inside its own RPC writer the first time a string parameter is sent —
  the failure never reaches the wire, so the server looks innocent.
- **DuckDB reports a decimal column as plain `Decimal`.** Precision and scale come from
  `reader.GetSchemaTable()`, and TDS has to declare both in COLMETADATA or every value truncates
  to an integer.
- **Encryption is refused with `ENCRYPT_NOT_SUP`**, which is why `Encrypt=False` is part of the
  connection string. TDS otherwise encrypts the login packet even in a plaintext session.
- **A cancel arrives on the same connection as the query**, unlike PostgreSQL's second connection.
  It is only noticed between row packets; see `TdsSession.Canceled`.
- **The packet size a client asks for in LOGIN7 is a request, not a setting.** TDS lets the server
  answer with its own, in ENVCHANGE 4, and both then use that -- SqlClient resizes its buffers on the
  token before it reads anything else. So `Config.TdsPacketSize` is what the door chunks to, and the
  default is 32767 rather than the 8000 SqlClient asks for: a packet costs a header and a write to
  the socket, and one that a row ends early to stay out of the seam gives up whatever was left of it,
  so the bigger the packet the less of both a result set pays. Except on macOS, where the answer is
  clipped to 16000 (`TdsSession.MaxPacketSize`): the loopback MTU there is 16K, so a 32K packet
  always arrives in two reads, and coalescing the writes alone did not stop SqlClient's partial-read
  replay from intermittently losing its place in the field -- a packet that rides one segment never
  enters that path at all.
- **Nothing may be cut across the seam between two packets.** SqlClient reassembles a read that
  ended mid-packet by replaying the framing it had begun, and framing split across the seam loses
  it its place -- it then reads response bytes as a length, and the failure surfaces much later,
  usually as an `ArgumentOutOfRangeException` while the reader is being closed. Two mechanisms keep
  it out of the seam, and they have to agree about where the seam is:
  - Each row is built on its own (`TdsSession.Rows`) as though it began a packet. One that does not
    fit in the packet being filled ends that packet where it is -- short -- and starts the next.
  - A MAX value's chunks stop at the packet boundary rather than running through it
    (`TdsTypes.WritePlp`), measured from the row's own start, which is why the row is built at
    offset zero: for a row too big for any packet, that is where the cuts really fall.
  - The lengths around the chunks obey the same rule as the chunks: a total length, chunk header or
    terminator that would sit across the seam ends the packet early instead (`TdsMsg.Fit`), and the
    wire cuts where the message says to (`TdsMsg.NextBoundary`). Sizing the data to the seam is not
    enough on its own -- a value starting a few bytes short of one puts its framing across it, which
    is how a stringy row wider than a packet could still lose a replaying client its place, a few
    offsets in every payload's worth. SQL Server ends the packet and starts the chunk in the next;
    so does this.

  Flushing after the row that overflows instead cuts inside it, which is a different bug that looks
  the same. This is invisible on a fast loopback and constant over a real network, because TCP
  decides how often a read ends mid-packet. `TdsTests.LongResultsSurviveASplitRead` forces the split
  so it is deterministic, and `PacketsEndWhereRowsDo` and `PlpFramingStaysOutOfTheSeam` check the
  framing itself rather than the client's tolerance of it -- SqlClient survives some violations and
  not others, which is how the first version of this fix passed while leaving wide rows broken, and
  how the framing half went unnoticed while the chunks behaved.
- **A packet reaches the socket as one write.** Header and body written separately are two TCP
  segments under TCP_NODELAY, the first a naked 8-byte header the client gets as a read of its own --
  and on macOS loopback, where a 16K MTU already splits every 32K packet, a production capture showed
  1,701 of them. SqlClient reassembles partial reads by replaying framing, so a stream arriving as
  header-sized crumbs runs that replay on every packet and intermittently loses its place -- a
  desync that reads as another column's bytes and reproduces roughly one run in three there, and
  never on Linux. The TDS side writes through a `BufferedStream` the way the PostgreSQL side always
  has, and `TdsTests.PacketsLeaveTheSocketCoalesced` counts the writes rather than trusting the
  segmentation.
- **The legacy LOB parameter types are still in use.** LLBLGen on the old `System.Data.SqlClient`
  types a string parameter as `NTEXT`, so `TdsTypes.ReadValue` has to know `TEXT`/`NTEXT`/`IMAGE`:
  four bytes of declared maximum instead of two, a collation on the text ones, and a four-byte
  value length where -1 is null. Nothing here ever sends them back.
- **An OUTPUT parameter is answered in the type the caller declared, and the wire already says what
  that is.** `TdsTypes.ReadParameter` hands back the column beside the value, so nothing has to map a
  declared `int` onto a TDS token twice; `WriteValue` converts whatever DuckDB produced into it, which
  is how a `DECIMAL(38,0)` `SCOPE_IDENTITY()` reaches a client that declared `Int32`. The column is
  normalised on the way out rather than echoed: text and binary go back as a MAX of their own kind,
  since that is the only length `WriteValue` chunks correctly, and MONEY, the pre-2008 DATETIME and
  the legacy LOBs go back as what replaced them. SqlClient matches a RETURNVALUE to its own parameter
  **by name, with the `@`** -- `ParameterNameFixed` -- so a token named without it is dropped
  silently.
- **A pooled connection announces itself in the packet header.** SqlClient sets the RESETCONNECTION
  bit on the first message it sends over a connection it took back out of the pool; only an older
  client calls `sp_reset_connection`, so a server that answers just the procedure never hears about
  the reuse. `TdsWire.ReadMessage` surfaces the bit and `TdsSession.Reset` acts on it, which is what
  keeps one session's `#table` out of the next one's -- and since that happens before every statement
  an ORM sends, what it does has to be free when there is nothing to do.
  [performance](performance.md#what-a-pooled-checkout-costs)
- **A COLMETADATA name is counted in one byte**, so a name of 256 characters announces itself as
  empty and the client reads the bytes after it as the next token -- which surfaces as
  "Internal connection fatal error" from the parser, with the server looking innocent. DuckDB names
  an unaliased column after the text of the expression that produced it, and a
  `CASE WHEN EXISTS (...)` passes 255 without trying. `TdsSession.Named` cuts at 128, which is where
  SQL Server cuts (`sysname`); `TdsMsg.BVarchar` cuts at 255 as well, since a length prefix that
  cannot say what it carries is a desynchronized stream whatever the field was.

## Bulk load

`SqlBulkCopy` is three exchanges, and each one demanded something of its own.

- **The metadata probe is procedural since SqlClient 7**: DECLAREs, an `IF` over
  `SERVERPROPERTY`, a walk of `sys.all_columns` through `sp_executesql` to leave graph columns out
  of the column list, and an `EXEC` of the SELECT it built. None of that is a statement the parser
  should learn -- it is one client's private handshake, not T-SQL an application writes -- so
  `TdsSession.Probe` recognizes it by fingerprint and answers the plain question inside it: the
  transaction count, the destination's shape, its collations. The destination is spliced out of
  the `DECLARE @Object_ID INT = OBJECT_ID('…')` declaration, the one place the probe names it
  whole -- and that declaration, with `sp_tablecollations` beside it, is the fingerprint: an
  application's own batch may open with the same trancount check and still mention `OBJECT_ID`,
  and a match on less than the probe's own body would swallow it. The answer must also carry as
  many result sets as the client's probe expects back -- the client indexes them by position --
  which is why a probe carrying `#Column_Aliases` (the next SqlClient's fourth rowset, its graph
  aliases) is answered with an empty fourth rowset: a lake has no graph columns, so no aliases is
  the true answer. This is the TDS side's one text-level rewrite, and it lives beside the
  statement path for the same reason the PostgreSQL side's catalog shims do: the alternative is a
  procedural batch interpreter.
- **FMTONLY is scoped to the batch and executes nothing.** ON and OFF always travel together in
  the one probe batch a client sends, and a session flag would outlive a batch that failed between
  them -- leaving every later query on the pooled connection silently answering shape-only. Inside
  the window a query answers with its shape and a write is skipped rather than run: SQL Server
  executes no statement under FMTONLY, and a metadata probe must not mutate the lake.
- **FMTONLY's shape comes from `DESCRIBE`, not from an empty result.** DuckDB.NET reports a
  decimal's precision and scale off the data chunks, so a result with no rows says `DECIMAL(0,0)`
  -- and SqlDecimal refuses precision zero on the client before a single row is sent.
  `TdsSession.Shape` has DuckDB bind the statement and say each column's type in full, which also
  costs no materialization. `TdsTypes.Describe` therefore reads `DESCRIBE`'s SQL spellings beside
  the reader's own names. What `DESCRIBE` cannot say is which column is an identity, so the shape
  never sets the identity flag -- which means SqlBulkCopy behaves as though `KeepIdentity` were
  always on: source values land verbatim, the divergence twin of KEEP_NULLS below.
- **The collations rowset is read by column ordinal, and an empty one is refused** -- the client
  indexes row *i* for destination column *i* and only looks at `collation_name`. So
  `sp_tablecollations_100` is answered from `information_schema.columns`, one row per column in
  SELECT * order, every collation null: everything this door puts on the wire is Unicode under the
  one collation the login advertised, and a null is what tells the client to send no COLLATE back.
- **`INSERT BULK` is parsed and remembered, not run** (`InsertBulkStatement`): it declares where
  the next bulk load message lands, and only the column names and their order are kept -- the
  stream re-declares the types in its own COLMETADATA. A `BatchSize` on the client repeats the
  whole pair, so the arming is consumed by each stream and set again by the next statement -- and
  it is good for exactly the message after it: any batch or call in between cancels it, the way
  SQL Server refuses a stray stream after intervening traffic.
- **The stream is the server's own token vocabulary written backwards** -- COLMETADATA, ROWs, DONE
  -- which is why `TdsTypes.ReadTypeInfo`/`ReadValue` are the RPC parameter reader split in two:
  the types arrive once and the values per row. One asymmetry earned its own reader
  (`ReadBulkValue`): a bulk decimal is always a sign and sixteen bytes of magnitude, while the
  length prefix repeats the declared length -- SqlClient writes 9 and sends 17, and SQL Server
  reads it that way too.
- **The rows go through the insert plan, not around it.** Each slice of the stream becomes a
  multi-row parameterized `INSERT` through the same translator and gateway a statement takes, so
  declared keys, references, defaults and identities hold for a bulk load exactly as for an INSERT
  -- an appender into the write layer would have bypassed every one of them. One statement serves
  every full slice: the SQL depends only on the row count, so the translation and the plan are
  paid once and only the values are bound per slice -- not the plan cache the performance note
  bans, which is a held DuckDB plan baking in statistics; the SQL text carries none. The whole
  stream runs in one transaction unless the client brought its own, so the write layer is
  persisted once per stream rather than once per slice, and a mid-stream failure takes the rows
  already landed with it -- and a rollback that itself cannot be delivered resets the session's
  transaction bookkeeping rather than leaving writes deferred to a COMMIT nobody will send. Two
  limits follow from the shape: `KEEP_NULLS` semantics are what always happens (a null in the
  stream is a null in the row, never a default), and the message is buffered whole before it is
  read -- a load big enough to mind that sets `BatchSize`, which bounds the message too.
