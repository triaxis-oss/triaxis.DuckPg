using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;

namespace triaxis.Tools.DuckPg;

/// Body builder for one backend message. Length and type byte are added on send.
sealed class Msg
{
    readonly MemoryStream buf = new();

    public Msg U8(byte v) { buf.WriteByte(v); return this; }

    public Msg I16(int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, (short)v);
        buf.Write(b);
        return this;
    }

    public Msg I32(int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        buf.Write(b);
        return this;
    }

    public Msg Str(string s)
    {
        buf.Write(Encoding.UTF8.GetBytes(s));
        buf.WriteByte(0);
        return this;
    }

    public Msg Raw(ReadOnlySpan<byte> s) { buf.Write(s); return this; }

    public ReadOnlySpan<byte> Body => buf.GetBuffer().AsSpan(0, (int)buf.Length);
}

/// Cursor over a received frontend message body.
struct MsgReader(byte[] body)
{
    int pos = 0;

    public readonly bool AtEnd => pos >= body.Length;

    public byte U8() => body[pos++];

    public short I16()
    {
        var v = BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(pos));
        pos += 2;
        return v;
    }

    public int I32()
    {
        var v = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(pos));
        pos += 4;
        return v;
    }

    public string Str()
    {
        var end = Array.IndexOf(body, (byte)0, pos);
        if (end < 0) end = body.Length;
        var s = Encoding.UTF8.GetString(body, pos, end - pos);
        pos = end + 1;
        return s;
    }

    /// Length-prefixed value as used by Bind; null when the length is -1.
    public byte[]? Value()
    {
        var len = I32();
        if (len < 0) return null;
        var v = body[pos..(pos + len)];
        pos += len;
        return v;
    }
}

/// PostgreSQL v3 message framing over a stream.
sealed class PgWire(Stream stream, ILogger logger)
{
    readonly byte[] scratch = new byte[4];

    public void Send(char type, Msg? msg = null)
    {
        logger.LogTrace(">> {Type}", type);
        ReadOnlySpan<byte> body = msg is null ? default : msg.Body;
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], body.Length + 4);
        lock (stream)
        {
            stream.Write(header);
            if (body.Length > 0) stream.Write(body);
        }
    }

    public void Flush() { lock (stream) stream.Flush(); }

    /// Startup-phase packet: length-prefixed, no type byte. Returns the leading int32 plus the rest.
    public (int Code, byte[] Body)? ReadStartup()
    {
        if (!TryReadFully(scratch)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(scratch);
        if (len < 8 || len > 1 << 20) throw new ProtocolException($"bad startup packet length {len}");
        var body = new byte[len - 4];
        if (!TryReadFully(body)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(body), body[4..]);
    }

    public (char Type, byte[] Body)? ReadMessage()
    {
        var typeByte = stream.ReadByte();
        if (typeByte < 0) return null;
        if (!TryReadFully(scratch)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(scratch);
        if (len < 4 || len > 1 << 26) throw new ProtocolException($"bad message length {len}");
        var body = new byte[len - 4];
        if (!TryReadFully(body)) return null;
        return ((char)typeByte, body);
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

sealed class ProtocolException(string message) : Exception(message);
