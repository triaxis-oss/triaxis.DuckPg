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
  so the bigger the packet the less of both a result set pays.
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
