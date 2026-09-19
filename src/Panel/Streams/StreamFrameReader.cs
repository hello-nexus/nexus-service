using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Parses <see cref="StreamFraming"/>-framed bytes off a Kestrel PipeReader
/// buffer. Segment boundaries are arbitrary, so every read goes through
/// SequenceReader rather than assuming a frame's header or payload lands in
/// one segment.
/// </summary>
public static class StreamFrameReader
{
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out StreamFrame? frame)
    {
        frame = null;

        if (buffer.Length < StreamFraming.HeaderSize)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(buffer);

        Span<byte> header = stackalloc byte[StreamFraming.HeaderSize];
        reader.TryCopyTo(header);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var flags = header[4];

        if ((flags & ~StreamFraming.KnownFlags) != 0)
        {
            throw new InvalidDataException($"Unknown stream frame flags: 0x{flags:X2}");
        }

        if (length > StreamFraming.MaxPayloadBytes)
        {
            throw new InvalidDataException($"Stream frame payload too large: {length} bytes");
        }

        var isControl = (flags & StreamFraming.FlagControl) != 0;
        if (isControl && length != 0)
        {
            throw new InvalidDataException("Control frame must carry a zero-length payload");
        }

        if (buffer.Length < StreamFraming.HeaderSize + (long)length)
        {
            return false;
        }

        reader.Advance(StreamFraming.HeaderSize);

        // Raw-BGRA panels ingest megabytes per frame; a fresh array each time is LOH churn
        // and a gen2 collection every couple of seconds, so the payload comes from a pool
        // and every consumer path returns it via StreamFrame.Release.
        var payload = length == 0 ? Array.Empty<byte>() : StreamFrame.Pool.Rent((int)length);
        if (length > 0)
        {
            reader.TryCopyTo(payload.AsSpan(0, (int)length));
            reader.Advance(length);
        }

        frame = new StreamFrame
        {
            Flags = flags,
            Payload = payload,
            Length = (int)length,
            Pooled = length > 0,
        };
        buffer = buffer.Slice(reader.Position);
        return true;
    }
}
