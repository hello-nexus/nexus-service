using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Helper;

/// <summary>
/// Length-prefix framing over a byte-mode pipe. Each frame is a 4-byte
/// little-endian unsigned length followed by exactly that many UTF-8 JSON
/// bytes. Used instead of message-mode pipes, which silently truncate or
/// block when the payload exceeds the negotiated buffer size; this also lets
/// the receiver cap memory before reading attacker-controlled bytes.
/// </summary>
internal static class Framing
{
    /// <summary>Hard cap on a single envelope. Album-art payloads are the
    /// largest realistic case (a few hundred KB); 8 MB is comfortable
    /// headroom and bounds the receive-side memory footprint.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream pipe, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length > MaxFrameBytes)
            throw new InvalidOperationException($"helper frame exceeds {MaxFrameBytes} bytes ({bytes.Length})");

        var header = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)bytes.Length);
            await pipe.WriteAsync(header.AsMemory(0, 4), ct).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    /// <summary>
    /// Reads one frame's payload bytes from the pipe. Returns null when
    /// the pipe closes cleanly between frames. Throws on truncated reads
    /// (peer died mid-frame) and on length-prefix overflow.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        var headerRead = await ReadExactAsync(pipe, header, ct).ConfigureAwait(false);
        if (!headerRead) return null;

        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        // Zero-length is reserved as malformed - we never emit empty
        // envelopes and the JSON parser would throw on receipt, killing
        // the read loop. Treat as a protocol error.
        if (len == 0)
            throw new InvalidDataException("helper frame has zero-length payload");
        if (len > MaxFrameBytes)
            throw new InvalidDataException($"helper frame length {len} exceeds cap {MaxFrameBytes}");

        var payload = new byte[len];
        var ok = await ReadExactAsync(pipe, payload, ct).ConfigureAwait(false);
        if (!ok) throw new EndOfStreamException("helper pipe closed mid-frame");
        return payload;
    }

    private static async Task<bool> ReadExactAsync(Stream pipe, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0) return offset == 0 ? false : throw new EndOfStreamException("helper pipe closed mid-frame");
            offset += n;
        }
        return true;
    }
}
