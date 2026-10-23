using System;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hyte.Np50;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>Frame pins against the Web Hub capture (header, command bytes, block-write layout) and the OpenRGB-derived direct payload.</summary>
public class GalleonSdProtocolTests
{
    private static byte[] Trim(byte[] wire)
    {
        var end = wire.Length;
        while (end > 0 && wire[end - 1] == 0) end--;
        return wire[..end];
    }

    [Fact]
    public void Captured_command_frames_match_byte_for_byte()
    {
        Assert.Equal(new byte[] { 0, 1, 0, 0x01, 0x03, 0x00, 0x02 }, Trim(GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeSoftware)));
        Assert.Equal(new byte[] { 0, 1, 0, 0x01, 0x03, 0x00, 0x01 }, Trim(GalleonSdProtocol.SetMode(GalleonSdProtocol.ModeHardware)));
        Assert.Equal(new byte[] { 0, 1, 0, 0x02, 0xE1 }, Trim(GalleonSdProtocol.GetProperty(0xE1)));
        Assert.Equal(new byte[] { 0, 1, 0, 0x0D, 0x01, 0x67, 0x6D }, Trim(GalleonSdProtocol.StartTransaction(1, 0x6D67)));
        Assert.Equal(new byte[] { 0, 1, 0, 0x05, 0x01, 0x02 }, Trim(GalleonSdProtocol.StopTransaction(2)));
        Assert.All(new[] { GalleonSdProtocol.SetMode(2), GalleonSdProtocol.GetProperty(0x12) }, f => Assert.Equal(128, f.Length));
    }

    [Fact]
    public void Direct_start_uses_the_lighting_resource_little_endian()
    {
        Assert.Equal(new byte[] { 0, 1, 0, 0x0D, 0x00, 0x22 }, Trim(GalleonSdProtocol.StartTransaction(0, GalleonSdProtocol.DirectResource)));
    }

    [Fact]
    public void Block_write_reproduces_the_captured_two_packet_layer_write()
    {
        var data = new byte[138];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);

        var chunks = GalleonSdProtocol.BlockWrite(1, data);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new byte[] { 0, 1, 0, 0x06, 0x01, 0x8A, 0x00, 0x00, 0x00 }, chunks[0][..9]);
        Assert.Equal(data[..119], chunks[0][9..128]);
        Assert.Equal(new byte[] { 0, 1, 0, 0x07, 0x01 }, chunks[1][..5]);
        Assert.Equal(data[119..], chunks[1][5..24]);
        Assert.All(chunks[1][24..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(119, 1)]
    [InlineData(120, 2)]
    [InlineData(242, 2)]
    [InlineData(243, 3)]
    [InlineData(581, 5)]
    public void Block_write_chunks_across_06_then_07(int length, int packets)
    {
        var chunks = GalleonSdProtocol.BlockWrite(0, new byte[length]);

        Assert.Equal(packets, chunks.Count);
        Assert.Equal(GalleonSdProtocol.CmdBlockFirst, chunks[0][3]);
        Assert.All(chunks.Skip(1), c => Assert.Equal(GalleonSdProtocol.CmdBlockNext, c[3]));
        Assert.All(chunks, c => Assert.Equal(128, c.Length));
        Assert.Equal(length & 0xFF, chunks[0][5]);
        Assert.Equal(length >> 8, chunks[0][6]);
    }

    [Fact]
    public void Direct_payload_is_tag_then_triplets_indexed_by_key_id()
    {
        var leds = new RgbColor[GalleonSdProtocol.LedCount];
        leds[0] = new RgbColor(1, 2, 3);
        leds[^1] = new RgbColor(7, 8, 9);
        var payload = new byte[GalleonSdProtocol.DirectPayloadLength];

        GalleonSdProtocol.EncodeDirect(leds, payload);

        Assert.Equal(2 + (146 * 3), payload.Length);
        Assert.Equal(new byte[] { 0x12, 0x00 }, payload[..2]);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload[(2 + (4 * 3))..(2 + (4 * 3) + 3)]);
        Assert.Equal(new byte[] { 7, 8, 9 }, payload[(2 + (145 * 3))..]);
        Assert.Equal(0, payload[2]);
    }

    [Fact]
    public void Key_table_is_the_captured_126_ids_and_uv_stays_in_the_unit_square()
    {
        Assert.Equal(126, GalleonSdProtocol.LedCount);
        Assert.Equal(GalleonSdProtocol.KeyIds.OrderBy(x => x), GalleonSdProtocol.KeyIds);
        Assert.Equal(GalleonSdProtocol.LedCount, GalleonSdProtocol.KeyIds.Distinct().Count());
        var (u, v) = GalleonSdProtocol.ComputeUv();
        Assert.Equal(GalleonSdProtocol.LedCount, u.Length);
        Assert.All(u.Concat(v), x => Assert.InRange(x, 0f, 1f));
    }

    [Fact]
    public void Reply_parses_only_at_the_header_aligned_echo_and_ignores_a_leading_report_id()
    {
        var a = new byte[128];
        a[3] = 0x0D; a[4] = 0x03;
        Assert.Equal(new GalleonReply(true, 3), GalleonSdProtocol.ParseReply(0x0D, a));

        var inputReport = new byte[128];
        inputReport[1] = GalleonSdProtocol.CmdSet;
        Assert.False(GalleonSdProtocol.ParseReply(GalleonSdProtocol.CmdSet, inputReport).Parsed);

        var withId = new byte[129];
        a.CopyTo(withId, 1);
        Assert.Equal(new GalleonReply(true, 3), GalleonSdProtocol.ParseReply(0x0D, withId));

        Assert.False(GalleonSdProtocol.ParseReply(0x0D, new byte[128]).Parsed);
    }

    [Fact]
    public void Hid_buffer_leads_with_report_id_zero()
    {
        var buf = GalleonSdProtocol.ToHidBuffer(GalleonSdProtocol.GetProperty(0x12));
        Assert.Equal(129, buf.Length);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0x02, 0x12 }, buf[..6]);
    }
}
