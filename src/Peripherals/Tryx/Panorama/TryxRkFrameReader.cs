using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Splits the panel's IN byte stream into reply payloads. Firmware v2.0.6 frames every
/// reply as "TRYX" + uint32 LE length + protobuf, and a reply larger than one read (a 64 KiB
/// file_pull_response) spans reads; older firmware pushed its file list as one bare protobuf read.</summary>
public sealed class TryxRkFrameReader
{
    private const int HeaderSize = 8;
    // The largest reply is a file_pull_response (64 KiB of data plus envelope); a length far past
    // it is a corrupt header, so resync on the next magic instead of buffering toward it.
    private const int MaxPayload = 1 << 20;
    private static ReadOnlySpan<byte> Magic => "TRYX"u8;

    private byte[] _buf = new byte[64 * 1024];
    private int _len;
    // Set by the first framed reply: from then on an unframed read is a fragment to resync
    // past, not a legacy bare payload.
    private bool _framed;

    /// <summary>Appends one read and returns every reply payload it completes, in order.</summary>
    public List<byte[]> Append(ReadOnlySpan<byte> read)
    {
        var payloads = new List<byte[]>();
        if (_len == 0 && !_framed && !read.StartsWith(Magic))
        {
            if (!read.IsEmpty) payloads.Add(read.ToArray());
            return payloads;
        }

        if (_len + read.Length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + read.Length));
        }
        read.CopyTo(_buf.AsSpan(_len));
        _len += read.Length;

        var pos = 0;
        while (_len - pos >= HeaderSize)
        {
            var span = _buf.AsSpan(pos, _len - pos);
            if (!span.StartsWith(Magic))
            {
                var next = span[1..].IndexOf(Magic);
                pos = next < 0 ? _len : pos + 1 + next;
                continue;
            }
            var len = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            if (len > MaxPayload)
            {
                pos += 1;
                continue;
            }
            if (span.Length < HeaderSize + (int)len) break;
            payloads.Add(span.Slice(HeaderSize, (int)len).ToArray());
            pos += HeaderSize + (int)len;
            _framed = true;
        }

        if (pos > 0)
        {
            _buf.AsSpan(pos, _len - pos).CopyTo(_buf);
            _len -= pos;
        }
        return payloads;
    }
}
