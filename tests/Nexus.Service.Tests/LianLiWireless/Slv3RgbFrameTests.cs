using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3RgbFrameTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");
    private static readonly byte[] MasterMac = Convert.FromHexString("AABBCCDDEEFF");
    private static readonly byte[] EffectIndex = { 0x01, 0x02, 0x03, 0x04 };

    [Fact]
    public void BuildFrameBuffer_at_full_brightness_passes_color_through()
    {
        var leds = new[] { new RgbColor(100, 50, 10) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        // 100 * 255 >> 8 = 99 (the bit-shift formula is not a perfect
        // identity even at full brightness), matching GetRgbData exactly.
        Assert.Equal(new byte[] { 99, 49, 9 }, buf);
    }

    [Fact]
    public void BuildFrameBuffer_at_zero_brightness_is_black()
    {
        var leds = new[] { new RgbColor(255, 255, 255) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, buf);
    }

    [Fact]
    public void BuildFrameBuffer_caps_sum_at_600()
    {
        var leds = new[] { new RgbColor(255, 255, 255) };
        var buf = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        Assert.True(buf[0] + buf[1] + buf[2] <= 600);
    }

    [Fact]
    public void BuildEffectIndex_is_big_endian()
    {
        var bytes = Slv3RgbFrame.BuildEffectIndex(0x01020304);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, bytes);
    }

    [Fact]
    public void BuildPackets_header_packet_carries_common_fields_and_metadata_and_no_data()
    {
        var raw = Slv3RgbFrame.BuildFrameBuffer(new[] { new RgbColor(10, 20, 30) }, 100);
        var compressed = TinyUz.Compress(raw);

        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 40, totalFrames: 30, intervalMs: 100);

        var header = packets[0];
        Assert.Equal(Slv3Protocol.RfPayloadSize, header.Length);
        Assert.Equal(Slv3Protocol.RfFrameType, header[0]);
        Assert.Equal(Slv3Protocol.RfRgbSync, header[1]);
        Assert.Equal(FanMac, header.AsSpan(2, 6).ToArray());
        Assert.Equal(MasterMac, header.AsSpan(8, 6).ToArray());
        Assert.Equal(EffectIndex, header.AsSpan(14, 4).ToArray());
        Assert.Equal(0, header[18]);
        Assert.Equal(packets.Length, header[19]);

        var complen = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        Assert.Equal(compressed.Length, complen);
        Assert.Equal(0, header[24]);
        Assert.Equal(30, (header[25] << 8) | header[26]);
        Assert.Equal(40, header[27]);
        Assert.Equal(100, (header[32] << 8) | header[33]);
        Assert.Equal(0, header[34]);                                  // whole-ms interval: no centi-fraction
        Assert.Equal(100, (header[35] << 8) | header[36]);            // sub-ring interval mirrors the main one
        Assert.Equal(0, header[37]);
        Assert.Equal(30, (header[38] << 8) | header[39]);             // sub-ring frame count mirrors total frames
        // L-Connect's part 0 is header-only: the stream starts in part 1.
        Assert.All(header.AsSpan(40).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildPackets_reassembles_compressed_stream_across_data_parts()
    {
        var leds = new RgbColor[160]; // 4 fans * 40 LEDs
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new RgbColor((byte)(i % 256), (byte)((i * 3) % 256), (byte)((i * 7) % 256));
        }
        var raw = Slv3RgbFrame.BuildFrameBuffer(leds, 100);
        var compressed = TinyUz.Compress(raw);
        Assert.True(compressed.Length > Slv3RgbFrame.DataPacketChunk, "test needs a multi-packet payload");

        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 160, totalFrames: 1, intervalMs: 50);

        var dataParts = (compressed.Length + Slv3RgbFrame.DataPacketChunk - 1) / Slv3RgbFrame.DataPacketChunk;
        Assert.Equal(dataParts + 1, packets.Length);
        var collected = new System.Collections.Generic.List<byte>();
        for (var i = 1; i < packets.Length; i++)
        {
            var remaining = compressed.Length - collected.Count;
            var take = Math.Min(Slv3RgbFrame.DataPacketChunk, remaining);
            collected.AddRange(packets[i].AsSpan(Slv3RgbFrame.DataPacketOffset, take).ToArray());
        }
        Assert.Equal(compressed, collected.ToArray());
    }

    [Fact]
    public void BuildPackets_small_stream_is_one_header_plus_one_data_part()
    {
        // The Y70 captures: an 11-byte stream ships as part 0 (header) + part 1
        // (data), [19] = 2 on both - never a trailing empty part.
        var compressed = TinyUz.Compress(new byte[30 * 120 * 3]);
        var packets = Slv3RgbFrame.BuildPackets(FanMac, MasterMac, EffectIndex, compressed, ledCount: 120, totalFrames: 30, intervalMs: 100);

        Assert.Equal(2, packets.Length);
        for (var i = 0; i < packets.Length; i++)
        {
            Assert.Equal(i, packets[i][18]);
            Assert.Equal(2, packets[i][19]);
        }
        Assert.Equal(compressed, packets[1].AsSpan(Slv3RgbFrame.DataPacketOffset, compressed.Length).ToArray());
    }
}
