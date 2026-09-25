using System;
using System.Linq;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRkFrameReaderTests
{
    private static byte[] Frame(byte[] payload)
    {
        var f = new byte[8 + payload.Length];
        "TRYX"u8.CopyTo(f);
        BitConverter.TryWriteBytes(f.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(f, 8);
        return f;
    }

    [Fact]
    public void A_whole_frame_in_one_read_yields_its_payload()
    {
        var reader = new TryxRkFrameReader();

        var payloads = reader.Append(Frame([1, 2, 3]));

        Assert.Equal([[1, 2, 3]], payloads);
    }

    [Fact]
    public void A_frame_split_across_reads_yields_once_complete()
    {
        var reader = new TryxRkFrameReader();
        var payload = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        var frame = Frame(payload);

        Assert.Empty(reader.Append(frame.AsSpan(0, 5)));
        Assert.Empty(reader.Append(frame.AsSpan(5, 40_000)));
        var done = reader.Append(frame.AsSpan(40_005));

        Assert.Single(done);
        Assert.Equal(payload, done[0]);
    }

    [Fact]
    public void Two_frames_in_one_read_yield_both_in_order()
    {
        var reader = new TryxRkFrameReader();

        var payloads = reader.Append([.. Frame([1]), .. Frame([2, 2])]);

        Assert.Equal([[1], [2, 2]], payloads);
    }

    [Fact]
    public void An_unframed_read_is_one_payload()
    {
        // Older firmware pushed its file list as bare protobuf.
        var reader = new TryxRkFrameReader();
        var bare = Encoding.ASCII.GetBytes("bare");

        Assert.Equal([bare], reader.Append(bare));
    }

    [Fact]
    public void An_unframed_read_after_framed_replies_is_resynced_past()
    {
        var reader = new TryxRkFrameReader();
        reader.Append(Frame([1]));

        Assert.Empty(reader.Append([0x0a, 0x02, 0x08, 0x01, 0x12, 0x00, 0x52, 0x06]));
        Assert.Equal([[3]], reader.Append(Frame([3])));
    }

    [Fact]
    public void Garbage_before_a_frame_is_skipped()
    {
        var reader = new TryxRkFrameReader();
        reader.Append(Frame([9]).AsSpan(0, 6));

        var payloads = reader.Append([.. Frame([9]).AsSpan(6), 0xEE, 0xEE, .. Frame([7])]);

        Assert.Equal([[9], [7]], payloads);
    }

    [Fact]
    public void A_corrupt_length_does_not_stall_the_stream()
    {
        var reader = new TryxRkFrameReader();
        byte[] corrupt = [.. "TRYX"u8, 0xFF, 0xFF, 0xFF, 0x7F];

        var payloads = reader.Append([.. corrupt, .. Frame([5])]);

        Assert.Equal([[5]], payloads);
    }
}
