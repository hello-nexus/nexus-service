using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class StreamFrameReaderTests
{
    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public static ReadOnlySequence<byte> Build(params byte[][] chunks)
        {
            BufferSegment? first = null;
            BufferSegment? last = null;
            long running = 0;

            foreach (var chunk in chunks)
            {
                var segment = new BufferSegment { Memory = chunk, RunningIndex = running };
                running += chunk.Length;

                if (first == null)
                {
                    first = segment;
                }
                else
                {
                    last!.Next = segment;
                }

                last = segment;
            }

            if (first == null || last == null)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }
    }

    private static byte[] BuildFrameBytes(byte flags, byte[] payload)
    {
        var bytes = new byte[StreamFraming.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)payload.Length);
        bytes[4] = flags;
        payload.CopyTo(bytes, StreamFraming.HeaderSize);
        return bytes;
    }

    private static byte[] Payload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)(i + 1);
        }

        return payload;
    }

    [Fact]
    public void Single_complete_frame_in_one_segment_is_read()
    {
        var payload = Payload(4);
        var frameBytes = BuildFrameBytes(StreamFraming.FlagIdr, payload);
        var buffer = new ReadOnlySequence<byte>(frameBytes);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.True(read);
        Assert.NotNull(frame);
        Assert.Equal(StreamFraming.FlagIdr, frame!.Flags);
        Assert.Equal(payload, frame!.Bytes.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Header_split_across_two_segments_is_read()
    {
        var payload = Payload(3);
        var frameBytes = BuildFrameBytes(0, payload);

        var segment1 = frameBytes[..2];
        var segment2 = frameBytes[2..];
        var buffer = BufferSegment.Build(segment1, segment2);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.True(read);
        Assert.Equal(payload, frame!.Bytes.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Payload_split_across_three_segments_is_read()
    {
        var payload = Payload(9);
        var frameBytes = BuildFrameBytes(StreamFraming.FlagIdr, payload);

        var header = frameBytes[..StreamFraming.HeaderSize];
        var chunk1 = frameBytes[StreamFraming.HeaderSize..(StreamFraming.HeaderSize + 3)];
        var chunk2 = frameBytes[(StreamFraming.HeaderSize + 3)..(StreamFraming.HeaderSize + 6)];
        var chunk3 = frameBytes[(StreamFraming.HeaderSize + 6)..];
        var buffer = BufferSegment.Build(header, chunk1, chunk2, chunk3);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.True(read);
        Assert.Equal(payload, frame!.Bytes.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Two_back_to_back_frames_are_consumed_in_sequence()
    {
        var payloadA = Payload(2);
        var payloadB = Payload(5);
        var frameA = BuildFrameBytes(StreamFraming.FlagIdr, payloadA);
        var frameB = BuildFrameBytes(0, payloadB);

        var combined = new byte[frameA.Length + frameB.Length];
        frameA.CopyTo(combined, 0);
        frameB.CopyTo(combined, frameA.Length);
        var buffer = new ReadOnlySequence<byte>(combined);

        var readFirst = StreamFrameReader.TryReadFrame(ref buffer, out var first);
        Assert.True(readFirst);
        Assert.Equal(payloadA, first!.Bytes.ToArray());
        Assert.Equal(frameB.Length, buffer.Length);

        var readSecond = StreamFrameReader.TryReadFrame(ref buffer, out var second);
        Assert.True(readSecond);
        Assert.Equal(payloadB, second!.Bytes.ToArray());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Zero_length_control_frame_is_read()
    {
        var frameBytes = BuildFrameBytes(StreamFraming.FlagControl, Array.Empty<byte>());
        var buffer = new ReadOnlySequence<byte>(frameBytes);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.True(read);
        Assert.True(frame!.Bytes.IsEmpty);
        Assert.True(frame.IsControl);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Incomplete_header_returns_false_without_consuming()
    {
        var partial = new byte[] { 1, 2, 3 };
        var buffer = new ReadOnlySequence<byte>(partial);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.False(read);
        Assert.Null(frame);
        Assert.Equal(3, buffer.Length);
    }

    [Fact]
    public void Incomplete_payload_returns_false_without_consuming()
    {
        var frameBytes = BuildFrameBytes(0, Payload(10));
        var truncated = frameBytes[..(StreamFraming.HeaderSize + 4)];
        var buffer = new ReadOnlySequence<byte>(truncated);

        var read = StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.False(read);
        Assert.Null(frame);
        Assert.Equal(truncated.Length, buffer.Length);
    }

    [Fact]
    public void Reserved_flag_bit_throws()
    {
        var header = new byte[StreamFraming.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0);
        header[4] = 0x04;
        var buffer = new ReadOnlySequence<byte>(header);

        Assert.Throws<InvalidDataException>(() =>
        {
            var localBuffer = buffer;
            StreamFrameReader.TryReadFrame(ref localBuffer, out _);
        });
    }

    [Fact]
    public void Oversize_length_throws()
    {
        var header = new byte[StreamFraming.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)StreamFraming.MaxPayloadBytes + 1);
        header[4] = 0;
        var buffer = new ReadOnlySequence<byte>(header);

        Assert.Throws<InvalidDataException>(() =>
        {
            var localBuffer = buffer;
            StreamFrameReader.TryReadFrame(ref localBuffer, out _);
        });
    }

    [Fact]
    public void Control_frame_with_nonzero_length_throws()
    {
        var header = new byte[StreamFraming.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 5);
        header[4] = StreamFraming.FlagControl;
        var buffer = new ReadOnlySequence<byte>(header);

        Assert.Throws<InvalidDataException>(() =>
        {
            var localBuffer = buffer;
            StreamFrameReader.TryReadFrame(ref localBuffer, out _);
        });
    }

    [Fact]
    public void Idr_flag_surfaces_via_IsIdr()
    {
        var frameBytes = BuildFrameBytes(StreamFraming.FlagIdr, Payload(1));
        var buffer = new ReadOnlySequence<byte>(frameBytes);

        StreamFrameReader.TryReadFrame(ref buffer, out var frame);

        Assert.True(frame!.IsIdr);
        Assert.False(frame.IsControl);
    }

    [Fact]
    public void Pooled_payload_is_returned_once_and_release_is_idempotent()
    {
        var payload = Payload(64);
        var buffer = new ReadOnlySequence<byte>(BuildFrameBytes(StreamFraming.FlagIdr, payload));

        Assert.True(StreamFrameReader.TryReadFrame(ref buffer, out var frame));
        Assert.True(frame!.Pooled);
        Assert.Equal(payload.Length, frame.Length);
        Assert.True(frame.Payload.Length >= payload.Length);

        // Double release would hand the same array to the pool twice, which later
        // rents it out to two frames at once.
        frame.Release();
        frame.Release();
    }

    [Fact]
    public void Zero_length_frame_is_not_pooled()
    {
        var buffer = new ReadOnlySequence<byte>(BuildFrameBytes(StreamFraming.FlagControl, Array.Empty<byte>()));

        Assert.True(StreamFrameReader.TryReadFrame(ref buffer, out var frame));
        Assert.False(frame!.Pooled);
        Assert.True(frame.Bytes.IsEmpty);
    }
}
