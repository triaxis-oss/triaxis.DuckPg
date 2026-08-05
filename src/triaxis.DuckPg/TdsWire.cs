using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// TDS message types, as they appear in the packet header.
static class TdsMessage
{
    public const byte Batch = 1, Rpc = 3, Result = 4, Attention = 6, TransactionManager = 14,
                      Login7 = 16, PreLogin = 18;
}

/// The tokens a response stream is made of.
static class TdsToken
{
    public const byte ReturnStatus = 0x79, ColMetadata = 0x81, Error = 0xAA, Info = 0xAB,
                      LoginAck = 0xAD, Row = 0xD1, EnvChange = 0xE3, Done = 0xFD, DoneProc = 0xFE,
                      DoneInProc = 0xFF;
}

/// Body builder for a response. Everything inside a TDS packet is little-endian; only the packet
/// header itself is not.
sealed class TdsMsg
{
    readonly MemoryStream buf = new();

    public int Length => (int)buf.Length;

    public TdsMsg U8(int v) { buf.WriteByte((byte)v); return this; }

    public TdsMsg U16(int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
        buf.Write(b);
        return this;
    }

    public TdsMsg I32(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        buf.Write(b);
        return this;
    }

    public TdsMsg I64(long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        buf.Write(b);
        return this;
    }

    /// Strings on the wire are UTF-16, counted in characters rather than bytes.
    public TdsMsg BVarchar(string s) => U8(s.Length).Raw(Encoding.Unicode.GetBytes(s));

    public TdsMsg UsVarchar(string s) => U16(s.Length).Raw(Encoding.Unicode.GetBytes(s));

    public TdsMsg Raw(ReadOnlySpan<byte> s) { buf.Write(s); return this; }

    public void Clear() => buf.SetLength(0);

    public ReadOnlySpan<byte> Body => buf.GetBuffer().AsSpan(0, (int)buf.Length);

    /// Drops bytes already sent, so a long result streams instead of being held whole.
    public void Consume(int count)
    {
        var rest = Body[count..].ToArray();
        buf.SetLength(0);
        buf.Write(rest);
    }
}

/// Cursor over a received message body.
struct TdsReader(byte[] body)
{
    int pos = 0;

    public readonly bool AtEnd => pos >= body.Length;

    public readonly int Position => pos;

    public void Skip(int count) => pos += count;

    public void Seek(int position) => pos = position;

    public byte U8() => body[pos++];

    public ushort U16()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        pos += 2;
        return v;
    }

    public int I32()
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
        pos += 4;
        return v;
    }

    public ulong U64()
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(pos));
        pos += 8;
        return v;
    }

    public byte[] Bytes(int count)
    {
        var v = body[pos..(pos + count)];
        pos += count;
        return v;
    }

    /// <paramref name="characters"/>, not bytes -- UTF-16 is two bytes each.
    public string Ucs2(int characters)
    {
        var s = Encoding.Unicode.GetString(body, pos, characters * 2);
        pos += characters * 2;
        return s;
    }

    public string BVarchar() => Ucs2(U8());

    public string UsVarchar() => Ucs2(U16());

    public readonly byte[] Rest() => body[pos..];
}

/// TDS packet framing: a message is a run of packets, each with an 8-byte header, the last one
/// carrying the end-of-message bit.
sealed class TdsWire(Stream stream, ILogger logger)
{
    readonly byte[] header = new byte[8];

    /// What the client asked for in LOGIN7, within reason. Responses are chunked to it.
    public int PacketSize { get; set; } = 4096;

    /// What one packet carries, and so where a value has to break off and start a new chunk. TDS's
    /// smallest legal packet is 512, and a client that negotiated one has a buffer that size.
    public int Payload => Math.Max(PacketSize, 512) - 8;

    /// A client that cancels sends an Attention on the same connection, so a query in flight has
    /// to be able to notice one arriving without blocking on the socket.
    public bool DataAvailable => stream is NetworkStream { DataAvailable: true };

    /// `Reset` is how a pooled connection says it is a new session: SqlClient marks the first
    /// message it sends on a connection it took back out of the pool, rather than calling
    /// `sp_reset_connection` the way an older client does.
    public (byte Type, byte[] Payload, bool Reset)? ReadMessage()
    {
        var payload = new MemoryStream();
        byte type;
        var reset = false;

        while (true)
        {
            if (!TryReadFully(header)) return null;
            type = header[0];
            var status = header[1];
            reset |= (status & 0x08) != 0;
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            if (length < 8) throw new ProtocolException($"bad packet length {length}");

            var body = new byte[length - 8];
            if (!TryReadFully(body)) return null;
            payload.Write(body);

            if ((status & 0x01) != 0) break;
        }

        logger.LogTrace("<< {Type} {Length}", type, payload.Length);
        return (type, payload.ToArray(), reset);
    }

    /// Sends the first <paramref name="count"/> bytes as packets that do not end the message; the
    /// rest stays buffered. The last of them may be short -- a packet is allowed to be, and ending
    /// one early is how a caller keeps a row out of the seam between two packets.
    public void SendUpTo(byte type, TdsMsg msg, int count)
    {
        var body = msg.Body;
        var sent = 0;

        lock (stream)
        {
            while (count - sent > Payload)
            {
                Packet(type, body.Slice(sent, Payload), end: false);
                sent += Payload;
            }
            if (count > sent)
            {
                Packet(type, body.Slice(sent, count - sent), end: false);
                sent = count;
            }
            stream.Flush();
        }

        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace(">> {Type} {Length}", type, sent);
        msg.Consume(sent);
    }

    /// Sends everything the buffer holds, ending the message.
    public void Send(byte type, TdsMsg msg)
    {
        var body = msg.Body;
        var sent = 0;

        lock (stream)
        {
            while (body.Length - sent > Payload)
            {
                Packet(type, body.Slice(sent, Payload), end: false);
                sent += Payload;
            }

            Packet(type, body[sent..], end: true);
            stream.Flush();
        }

        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace(">> {Type} {Length} end", type, body.Length);
        msg.Consume(body.Length);
    }

    void Packet(byte type, ReadOnlySpan<byte> body, bool end)
    {
        header[0] = type;
        header[1] = (byte)(end ? 0x01 : 0x00);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(body.Length + 8));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 0);
        header[6] = 1;
        header[7] = 0;

        stream.Write(header);
        stream.Write(body);
    }

    bool TryReadFully(Span<byte> target)
    {
        var read = 0;
        while (read < target.Length)
        {
            var n = stream.Read(target[read..]);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
