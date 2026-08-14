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
    public const byte ReturnStatus = 0x79, ColMetadata = 0x81, ReturnValue = 0xAC, Error = 0xAA,
                      Info = 0xAB, LoginAck = 0xAD, Row = 0xD1, EnvChange = 0xE3, Done = 0xFD,
                      DoneProc = 0xFE, DoneInProc = 0xFF;
}

/// Body builder for a response. Everything inside a TDS packet is little-endian; only the packet
/// header itself is not.
sealed class TdsMsg
{
    readonly MemoryStream buf = new();

    /// Where a packet has to end early: a value's framing that would sit across the seam ends the
    /// packet instead, the way SQL Server fills a packet and starts a chunk in the next. The wire
    /// cuts at these, and at whole payloads in between.
    readonly List<int> cuts = [];

    public int Length => (int)buf.Length;

    public bool HasCuts => cuts.Count > 0;

    /// The position within the packet being formed -- measured from the last early end, with whole
    /// payloads in between belonging to the packets the wire will cut there.
    public int Offset(int payload) => (Length - (cuts.Count > 0 ? cuts[^1] : 0)) % payload;

    /// Ends the forming packet here unless <paramref name="bytes"/> more still fit in front of the
    /// seam -- which is what keeps a length out of it: split across two packets, a client replaying
    /// a partial read loses its place in it.
    public TdsMsg Fit(int bytes, int payload)
    {
        if (payload - Offset(payload) < bytes) cuts.Add(Length);
        return this;
    }

    /// Where the packet holding <paramref name="from"/> ends: at the next early end, or a whole
    /// payload after the last one before it.
    public int NextBoundary(int from, int payload)
    {
        var anchor = 0;
        foreach (var cut in cuts)
        {
            if (cut <= from) { anchor = cut; continue; }
            return Math.Min(cut, from + payload - (from - anchor) % payload);
        }
        return from + payload - (from - anchor) % payload;
    }

    /// How much of the buffer is whole packets -- the last boundary the buffer reaches.
    public int Complete(int payload)
    {
        var at = 0;
        while (true)
        {
            var next = NextBoundary(at, payload);
            if (next > Length) return at;
            at = next;
        }
    }

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

    /// Strings on the wire are UTF-16, counted in characters rather than bytes. A single-byte
    /// length cannot say more than 255, so a longer string is cut rather than allowed to wrap: a
    /// 256-character name would announce itself as empty and the client would read every byte
    /// after it as the next token.
    public TdsMsg BVarchar(string s)
    {
        if (s.Length > 255) s = s[..255];
        return U8(s.Length).Raw(Encoding.Unicode.GetBytes(s));
    }

    public TdsMsg UsVarchar(string s) => U16(s.Length).Raw(Encoding.Unicode.GetBytes(s));

    public TdsMsg F32(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        return Raw(b);
    }

    public TdsMsg F64(double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        return Raw(b);
    }

    public TdsMsg Raw(ReadOnlySpan<byte> s) { buf.Write(s); return this; }

    /// Another message's bytes, with the early ends they were built against.
    public TdsMsg Append(TdsMsg other)
    {
        foreach (var cut in other.cuts) cuts.Add(Length + cut);
        return Raw(other.Body);
    }

    public void Clear()
    {
        buf.SetLength(0);
        cuts.Clear();
    }

    public ReadOnlySpan<byte> Body => buf.GetBuffer().AsSpan(0, (int)buf.Length);

    /// Drops bytes already sent, so a long result streams instead of being held whole. What is sent
    /// always ends a packet, so what remains starts one -- which is what the cuts are rebased to.
    public void Consume(int count)
    {
        var rest = Body[count..].ToArray();
        buf.SetLength(0);
        buf.Write(rest);
        cuts.RemoveAll(cut => cut <= count);
        for (var i = 0; i < cuts.Count; i++) cuts[i] -= count;
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

    /// What the handshake settled on. Until it has, TDS's own default, which is what a client sends
    /// its login in and reads the answer to that login out of.
    public int PacketSize { get; set; } = 4096;

    /// What one packet carries, and so where a value has to break off and start a new chunk.
    public int Payload => PacketSize - 8;

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
    /// rest stays buffered. A packet ends where the message says one may -- a whole payload in, or
    /// early, at a cut keeping a length out of the seam -- and the last may be short at
    /// <paramref name="count"/> itself, which is how a caller keeps a row out of it too.
    public void SendUpTo(byte type, TdsMsg msg, int count)
    {
        var body = msg.Body;
        var sent = 0;

        lock (stream)
        {
            while (sent < count)
            {
                var end = Math.Min(msg.NextBoundary(sent, Payload), count);
                Packet(type, body[sent..end], end: false);
                sent = end;
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
            while (true)
            {
                var end = msg.NextBoundary(sent, Payload);
                if (end >= body.Length) break;
                Packet(type, body[sent..end], end: false);
                sent = end;
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
