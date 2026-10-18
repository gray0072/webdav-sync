using System.Buffers.Binary;
using System.Text;

namespace WebDavSync.Core.Remote.MailRu;

/// <summary>The metaserver's answer does not have the expected format; nothing of it may be trusted.</summary>
public sealed class MailRuFormatException(string message) : Exception(message);

/// <summary>Request encoding of the metaserver's binary protocol (§4.7): unsigned LEB128 numbers; strings as length + 1, bytes, zero.</summary>
internal sealed class MailRuRequestWriter
{
    private readonly MemoryStream _buffer = new();

    public MailRuRequestWriter UVar(ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            _buffer.WriteByte(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return this;
    }

    public MailRuRequestWriter String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        UVar((ulong)bytes.Length + 1);
        _buffer.Write(bytes);
        _buffer.WriteByte(0);
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>Exact reads over a whole answer: running out of data is an error, never a shorter value.</summary>
internal sealed class MailRuReader(byte[] data)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public int Position { get; private set; }

    public bool AtEnd => Position == data.Length;

    public byte Byte()
    {
        Need(1);
        return data[Position++];
    }

    public ushort UInt16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position));
        Position += 2;
        return value;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        var span = data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public ulong UVar()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = Byte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new MailRuFormatException($"Number too long at byte {Position}.");
    }

    public string Utf8(int count)
    {
        var bytes = Bytes(count);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MailRuFormatException($"Invalid UTF-8 name at byte {Position - count}.");
        }
    }

    private void Need(int count)
    {
        if (count < 0 || data.Length - Position < count)
        {
            throw new MailRuFormatException($"Unexpected end of data at byte {Position} (need {count} more).");
        }
    }
}
