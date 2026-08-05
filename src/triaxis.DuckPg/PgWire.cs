using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg;

/// Body builder for one backend message. Length and type byte are added on send. A row loop keeps
/// one of these and clears it, so streaming a result set allocates nothing per row.
sealed class Msg
{
    byte[] buf = new byte[1024];
    int len;

    public ReadOnlySpan<byte> Body => buf.AsSpan(0, len);

    public void Clear() => len = 0;

    Span<byte> Take(int count)
    {
        if (len + count > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + count));
        var target = buf.AsSpan(len, count);
        len += count;
        return target;
    }

    public Msg U8(byte v) { Take(1)[0] = v; return this; }

    public Msg I16(int v) { BinaryPrimitives.WriteInt16BigEndian(Take(2), (short)v); return this; }

    public Msg I32(int v) { BinaryPrimitives.WriteInt32BigEndian(Take(4), v); return this; }

    public Msg I64(long v) { BinaryPrimitives.WriteInt64BigEndian(Take(8), v); return this; }

    public Msg F32(float v) { BinaryPrimitives.WriteSingleBigEndian(Take(4), v); return this; }

    public Msg F64(double v) { BinaryPrimitives.WriteDoubleBigEndian(Take(8), v); return this; }

    public Msg Str(string s) => Utf8(s).U8(0);

    public Msg Raw(ReadOnlySpan<byte> s) { s.CopyTo(Take(s.Length)); return this; }

    /// UTF-8 with neither prefix nor terminator: how a value's text rendering goes out. The worst
    /// case is reserved up front and the surplus given back, which costs less than counting first.
    public Msg Utf8(string s)
    {
        var reserved = s.Length * 3;
        var written = Encoding.UTF8.GetBytes(s, Take(reserved));
        len -= reserved - written;
        return this;
    }

    /// Formats straight into the buffer, so a number never becomes a string on the way out.
    public Msg Format<T>(T value, ReadOnlySpan<char> format = default) where T : IUtf8SpanFormattable
    {
        int written;
        while (!value.TryFormat(buf.AsSpan(len), out written, format, CultureInfo.InvariantCulture))
            Array.Resize(ref buf, buf.Length * 2);
        len += written;
        return this;
    }

    /// A field whose length is only known once its value has been written.
    public int BeginField() { var at = len; Take(4); return at; }

    public void EndField(int at) => BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(at), len - at - 4);
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
    /// A DataRow is a handful of bytes and a socket write is a syscall, so responses go out through
    /// a buffer. Only the write side: a BufferedStream cannot serve both over a socket.
    readonly Stream output = new BufferedStream(stream, 64 * 1024);

    readonly byte[] scratch = new byte[4];

    public void Send(char type, Msg? msg = null)
    {
        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace(">> {Type}", type);
        ReadOnlySpan<byte> body = msg is null ? default : msg.Body;
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], body.Length + 4);
        lock (output)
        {
            output.Write(header);
            if (body.Length > 0) output.Write(body);
        }
    }

    public void Flush() { lock (output) output.Flush(); }

    /// Startup-phase packet: length-prefixed, no type byte. Returns the leading int32 plus the rest.
    public (int Code, byte[] Body)? ReadStartup()
    {
        Flush();
        if (!TryReadFully(scratch)) return null;
        var len = BinaryPrimitives.ReadInt32BigEndian(scratch);
        if (len < 8 || len > 1 << 20) throw new ProtocolException($"bad startup packet length {len}");
        var body = new byte[len - 4];
        if (!TryReadFully(body)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(body), body[4..]);
    }

    public (char Type, byte[] Body)? ReadMessage()
    {
        // Nothing may be left buffered while waiting on the client, whatever the caller flushed.
        Flush();
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
