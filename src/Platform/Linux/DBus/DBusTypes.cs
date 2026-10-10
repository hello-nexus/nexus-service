using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux.DBus;

public enum DBusMessageType : byte
{
    Invalid = 0,
    MethodCall = 1,
    MethodReturn = 2,
    Error = 3,
    Signal = 4,
}

/// <summary>
/// A raw D-Bus message - header + body. Little-endian encoding only.
/// Body bytes are stored pre-marshalled; use <see cref="DBusWriter"/> to build them
/// and <see cref="DBusReader"/> to parse them.
/// </summary>
public sealed class DBusMessage
{
    public DBusMessageType Type;
    public byte Flags;
    public uint Serial;
    public uint ReplySerial;
    public string? Path;
    public string? Interface;
    public string? Member;
    public string? Destination;
    public string? Sender;
    public string? ErrorName;
    public string Signature = "";
    public byte[] Body = Array.Empty<byte>();

    public byte[] Encode()
    {
        var header = new DBusWriter();
        header.WriteByte((byte)'l'); // little-endian
        header.WriteByte((byte)Type);
        header.WriteByte(Flags);
        header.WriteByte(1); // protocol version
        header.WriteUInt32((uint)Body.Length);
        header.WriteUInt32(Serial);

        header.OpenArray(alignment: 8);
        if (Path is not null)
            WriteHeaderField(header, 1, "o", w => w.WriteObjectPath(Path));
        if (Interface is not null)
            WriteHeaderField(header, 2, "s", w => w.WriteString(Interface));
        if (Member is not null)
            WriteHeaderField(header, 3, "s", w => w.WriteString(Member));
        if (ErrorName is not null)
            WriteHeaderField(header, 4, "s", w => w.WriteString(ErrorName));
        if (ReplySerial != 0)
            WriteHeaderField(header, 5, "u", w => w.WriteUInt32(ReplySerial));
        if (Destination is not null)
            WriteHeaderField(header, 6, "s", w => w.WriteString(Destination));
        if (Sender is not null)
            WriteHeaderField(header, 7, "s", w => w.WriteString(Sender));
        if (Signature.Length > 0)
            WriteHeaderField(header, 8, "g", w => w.WriteSignature(Signature));
        header.CloseArray();

        header.AlignTo(8);
        var headerBytes = header.ToArray();
        var result = new byte[headerBytes.Length + Body.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(Body, 0, result, headerBytes.Length, Body.Length);
        return result;
    }

    private static void WriteHeaderField(DBusWriter header, byte fieldCode, string sig, Action<DBusWriter> write)
    {
        header.AlignTo(8);
        header.WriteByte(fieldCode);
        header.WriteVariant(sig, write);
    }

    public static async Task<DBusMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var prefix = await ReadExactAsync(stream, 16, ct);
        if (prefix is null)
        {
            return null;
        }
        if (prefix[0] != (byte)'l')
        {
            throw new InvalidOperationException("non-little-endian D-Bus not supported");
        }
        var bodyLen = BitConverter.ToUInt32(prefix, 4);
        var serial = BitConverter.ToUInt32(prefix, 8);
        var fieldsLen = BitConverter.ToUInt32(prefix, 12);

        var fields = await ReadExactAsync(stream, (int)fieldsLen, ct)
            ?? throw new EndOfStreamException();

        var padLen = (int)((8 - ((16 + fieldsLen) & 7)) & 7);
        if (padLen > 0)
        {
            _ = await ReadExactAsync(stream, padLen, ct);
        }

        var body = bodyLen > 0
            ? await ReadExactAsync(stream, (int)bodyLen, ct) ?? throw new EndOfStreamException()
            : Array.Empty<byte>();

        var msg = new DBusMessage
        {
            Type = (DBusMessageType)prefix[1],
            Flags = prefix[2],
            Serial = serial,
            Body = body,
        };
        ParseHeaderFields(msg, fields);
        return msg;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream s, int count, CancellationToken ct)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (count == 0)
        {
            return Array.Empty<byte>();
        }
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                return null;
            }
            read += n;
        }
        return buf;
    }

    private static void ParseHeaderFields(DBusMessage msg, byte[] fields)
    {
        var r = new DBusReader(fields);
        while (!r.AtEnd)
        {
            r.AlignTo(8);
            if (r.AtEnd)
            {
                break;
            }
            var fieldCode = r.ReadByte();
            var sig = r.ReadSignature();
            switch (fieldCode)
            {
                case 1:
                    msg.Path = r.ReadObjectPath();
                    break;
                case 2:
                    msg.Interface = r.ReadString();
                    break;
                case 3:
                    msg.Member = r.ReadString();
                    break;
                case 4:
                    msg.ErrorName = r.ReadString();
                    break;
                case 5:
                    msg.ReplySerial = r.ReadUInt32();
                    break;
                case 6:
                    msg.Destination = r.ReadString();
                    break;
                case 7:
                    msg.Sender = r.ReadString();
                    break;
                case 8:
                    msg.Signature = r.ReadSignature();
                    break;
                default:
                    _ = sig;
                    r.SkipToEnd();
                    break;
            }
        }
    }
}

public sealed class DBusWriter
{
    private byte[] _buf = new byte[256];
    private int _pos;
    private readonly System.Collections.Generic.Stack<ArrayFrame> _arrays = new();

    private struct ArrayFrame
    {
        public int LengthOffset;
        public int DataStart;
    }

    public void WriteByte(byte b)
    {
        Ensure(1);
        _buf[_pos++] = b;
    }

    public void WriteInt32(int v)
    {
        AlignTo(4);
        Ensure(4);
        BitConverter.TryWriteBytes(_buf.AsSpan(_pos, 4), v);
        _pos += 4;
    }

    public void WriteUInt32(uint v)
    {
        AlignTo(4);
        Ensure(4);
        BitConverter.TryWriteBytes(_buf.AsSpan(_pos, 4), v);
        _pos += 4;
    }

    /// <summary>D-Bus `x`: 64-bit signed, 8-byte aligned.</summary>
    public void WriteInt64(long v)
    {
        AlignTo(8);
        Ensure(8);
        BitConverter.TryWriteBytes(_buf.AsSpan(_pos, 8), v);
        _pos += 8;
    }

    public void WriteBool(bool v) => WriteUInt32(v ? 1u : 0u);

    public void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteUInt32((uint)bytes.Length);
        Ensure(bytes.Length + 1);
        Buffer.BlockCopy(bytes, 0, _buf, _pos, bytes.Length);
        _pos += bytes.Length;
        _buf[_pos++] = 0;
    }

    public void WriteObjectPath(string s) => WriteString(s);

    public void WriteSignature(string sig)
    {
        var bytes = Encoding.ASCII.GetBytes(sig);
        Ensure(bytes.Length + 2);
        _buf[_pos++] = (byte)bytes.Length;
        Buffer.BlockCopy(bytes, 0, _buf, _pos, bytes.Length);
        _pos += bytes.Length;
        _buf[_pos++] = 0;
    }

    public void WriteVariant(string signature, Action<DBusWriter> writeValue)
    {
        WriteSignature(signature);
        writeValue(this);
    }

    public void OpenArray(int alignment)
    {
        AlignTo(4);
        Ensure(4);
        var lenOffset = _pos;
        _pos += 4;
        AlignTo(alignment);
        _arrays.Push(new ArrayFrame { LengthOffset = lenOffset, DataStart = _pos });
    }

    public void CloseArray()
    {
        var frame = _arrays.Pop();
        var length = _pos - frame.DataStart;
        BitConverter.TryWriteBytes(_buf.AsSpan(frame.LengthOffset, 4), (uint)length);
    }

    public void AlignTo(int alignment)
    {
        if (alignment <= 1)
        {
            return;
        }
        var newPos = (_pos + alignment - 1) & ~(alignment - 1);
        if (newPos > _pos)
        {
            Ensure(newPos - _pos);
            while (_pos < newPos)
            {
                _buf[_pos++] = 0;
            }
        }
    }

    public int Length => _pos;

    public byte[] ToArray()
    {
        var r = new byte[_pos];
        Buffer.BlockCopy(_buf, 0, r, 0, _pos);
        return r;
    }

    private void Ensure(int count)
    {
        if (_pos + count <= _buf.Length)
        {
            return;
        }
        var newSize = _buf.Length * 2;
        while (newSize < _pos + count)
        {
            newSize *= 2;
        }
        var nb = new byte[newSize];
        Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
        _buf = nb;
    }
}

public sealed class DBusReader
{
    private readonly byte[] _data;
    private int _pos;

    public DBusReader(byte[] data)
    {
        _data = data;
    }

    public bool AtEnd => _pos >= _data.Length;
    public int Position => _pos;

    public void AlignTo(int alignment)
    {
        if (alignment <= 1)
        {
            return;
        }
        var newPos = (_pos + alignment - 1) & ~(alignment - 1);
        _pos = Math.Min(newPos, _data.Length);
    }

    public byte ReadByte() => _data[_pos++];

    public int ReadInt32()
    {
        AlignTo(4);
        var v = BitConverter.ToInt32(_data, _pos);
        _pos += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        AlignTo(4);
        var v = BitConverter.ToUInt32(_data, _pos);
        _pos += 4;
        return v;
    }

    public ulong ReadUInt64()
    {
        AlignTo(8);
        var v = BitConverter.ToUInt64(_data, _pos);
        _pos += 8;
        return v;
    }

    public bool ReadBool() => ReadUInt32() != 0;

    public string ReadString()
    {
        var len = (int)ReadUInt32();
        var s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len + 1;
        return s;
    }

    public string ReadObjectPath() => ReadString();

    public string ReadSignature()
    {
        var len = _data[_pos++];
        var s = Encoding.ASCII.GetString(_data, _pos, len);
        _pos += len + 1;
        return s;
    }

    public void SkipToEnd() => _pos = _data.Length;

    /// <summary>Skip a value of a simple signature (single type char). Useful for skipping variants.</summary>
    public void SkipSimple(string sig)
    {
        switch (sig)
        {
            case "y":
                _pos += 1;
                break;
            case "b":
            case "i":
            case "u":
                AlignTo(4);
                _pos += 4;
                break;
            case "x":
            case "t":
            case "d":
                AlignTo(8);
                _pos += 8;
                break;
            case "s":
            case "o":
                AlignTo(4);
                var len = (int)BitConverter.ToUInt32(_data, _pos);
                _pos += 4 + len + 1;
                break;
            case "g":
                var gLen = _data[_pos];
                _pos += 1 + gLen + 1;
                break;
            default:
                // Fallback - can't safely skip complex types without a type parser. Drop to end.
                _pos = _data.Length;
                break;
        }
    }

    /// <summary>Read a variant and return its signature + raw bytes (for forwarding / ignoring).</summary>
    public void SkipVariant()
    {
        var sig = ReadSignature();
        SkipSimple(sig);
    }

    /// <summary>Read an array of strings (signature <c>as</c>) at the current position.</summary>
    public System.Collections.Generic.List<string> ReadStringArray()
    {
        var result = new System.Collections.Generic.List<string>();
        AlignTo(4);
        var len = (int)ReadUInt32();
        var end = _pos + len;
        while (_pos < end)
            result.Add(ReadString());
        return result;
    }

    /// <summary>
    /// Read a string-keyed variant dictionary (signature <c>a{sv}</c>) at the
    /// current position into a map of string -&gt; boxed value. Used for the
    /// MPRIS <c>Properties.GetAll</c> reply and the nested <c>Metadata</c> dict.
    /// Values are boxed as: string, long (all integer types), double, bool,
    /// <see cref="System.Collections.Generic.List{Object}"/> (arrays) or a nested
    /// dictionary; unknown types resolve to null.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, object?> ReadStringVariantDict()
    {
        var dict = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal);
        AlignTo(4);
        var len = (int)ReadUInt32();
        AlignTo(8); // array of dict-entry (struct) aligns to 8
        var end = _pos + len;
        while (_pos < end)
        {
            AlignTo(8);
            var key = ReadString();
            var sig = ReadSignature(); // variant signature
            var si = 0;
            dict[key] = ReadOne(sig, ref si);
        }
        return dict;
    }

    /// <summary>Read one complete value of the type at <paramref name="sig"/>[<paramref name="si"/>], advancing si past it.</summary>
    private object? ReadOne(string sig, ref int si)
    {
        var t = sig[si++];
        switch (t)
        {
            case 'y': return (long)ReadByte();
            case 'b': return ReadBool();
            case 'n': AlignTo(2); { var v = BitConverter.ToInt16(_data, _pos); _pos += 2; return (long)v; }
            case 'q': AlignTo(2); { var v = BitConverter.ToUInt16(_data, _pos); _pos += 2; return (long)v; }
            case 'i': return (long)ReadInt32();
            case 'u': return (long)ReadUInt32();
            case 'x': AlignTo(8); { var v = BitConverter.ToInt64(_data, _pos); _pos += 8; return v; }
            case 't': AlignTo(8); { var v = BitConverter.ToUInt64(_data, _pos); _pos += 8; return (long)v; }
            case 'd': AlignTo(8); { var v = BitConverter.ToDouble(_data, _pos); _pos += 8; return v; }
            case 's': return ReadString();
            case 'o': return ReadString();
            case 'g': return ReadSignature();
            case 'v':
            {
                var vsig = ReadSignature();
                var vi = 0;
                return ReadOne(vsig, ref vi);
            }
            case 'a':
            {
                var elemStart = si;
                AlignTo(4);
                var len = (int)ReadUInt32();
                AlignTo(AlignmentOf(sig[si]));
                var end = _pos + len;
                if (sig[si] == '{')
                {
                    var dict = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal);
                    while (_pos < end)
                    {
                        AlignTo(8);
                        var ki = si + 1; // skip '{'
                        var key = ReadOne(sig, ref ki);
                        var val = ReadOne(sig, ref ki); // ki now at '}'
                        dict[Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? ""] = val;
                    }
                    si = SkipType(sig, si);
                    return dict;
                }
                var list = new System.Collections.Generic.List<object?>();
                while (_pos < end)
                {
                    var ei = elemStart;
                    list.Add(ReadOne(sig, ref ei));
                }
                si = SkipType(sig, elemStart);
                return list;
            }
            case '(':
            {
                AlignTo(8);
                var items = new System.Collections.Generic.List<object?>();
                while (sig[si] != ')')
                    items.Add(ReadOne(sig, ref si));
                si++; // skip ')'
                return items;
            }
            default:
                _pos = _data.Length; // unknown - bail safely
                return null;
        }
    }

    private static int SkipType(string sig, int i)
    {
        var c = sig[i++];
        if (c == 'a') return SkipType(sig, i);
        if (c == '(')
        {
            while (sig[i] != ')') i = SkipType(sig, i);
            return i + 1;
        }
        if (c == '{')
        {
            i = SkipType(sig, i);
            i = SkipType(sig, i);
            return i + 1; // skip '}'
        }
        return i;
    }

    private static int AlignmentOf(char t) => t switch
    {
        'n' or 'q' => 2,
        'b' or 'i' or 'u' or 's' or 'o' or 'a' => 4,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => 1,
    };
}
