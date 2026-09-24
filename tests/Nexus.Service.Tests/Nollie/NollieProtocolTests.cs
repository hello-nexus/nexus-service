using System;
using System.Linq;
using Nexus.Service.Peripherals.Nollie;

namespace Nexus.Service.Tests.Nollie;

/// <summary>
/// Byte layout and device-table facts, checked against OpenRGB's
/// NollieController (the reference driver these were ported from) and against
/// the HID report descriptors observed on real hardware.
/// </summary>
public class NollieProtocolTests
{
    private static NollieDevice Nollie1Os2_1 => NollieProtocol.Lookup(0x16D5, 0x2A01)!;
    private static NollieDevice Nollie16Os2_1 => NollieProtocol.Lookup(0x16D5, 0x2A16)!;
    private static NollieDevice Nollie32 => NollieProtocol.Lookup(0x3061, 0x4714)!;

    // ── Device table ──

    [Fact]
    public void Lookup_finds_every_table_entry_and_nothing_else()
    {
        foreach (var d in NollieProtocol.Devices)
        {
            Assert.Same(d, NollieProtocol.Lookup(d.VendorId, d.ProductId));
        }
        Assert.Null(NollieProtocol.Lookup(0x16D5, 0x9999));
    }

    [Fact]
    public void Device_table_has_no_duplicate_vid_pid()
    {
        var keys = NollieProtocol.Devices.Select(d => (d.VendorId, d.ProductId)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    /// <summary>Report sizes observed on the user's hardware: 2A01 reports 65/65, 2A16 reports 1025/1025.</summary>
    [Fact]
    public void Transport_matches_observed_report_sizes()
    {
        Assert.Equal(NollieTransport.Chunked, Nollie1Os2_1.Transport);
        Assert.Equal(NollieTransport.Wide, Nollie16Os2_1.Transport);
    }

    [Fact]
    public void Channel_counts_match_reference_driver()
    {
        Assert.Equal(1, Nollie1Os2_1.Channels);
        Assert.Equal(16, Nollie16Os2_1.Channels);
        Assert.Equal(32, Nollie32.Channels);
        Assert.Equal(630, Nollie1Os2_1.MaxLedsPerChannel);
        Assert.Equal(256, Nollie16Os2_1.MaxLedsPerChannel);
    }

    [Fact]
    public void Only_legacy_1ch_takes_the_led_count_handshake()
    {
        foreach (var d in NollieProtocol.Devices)
        {
            var expected = d.VendorId == 0x16D2 && d.ProductId == 0x1F11;
            Assert.Equal(expected, d.WantsLedCountHandshake);
        }
    }

    // ── Channel mapping ──

    /// <summary>The OS2_1 16-channel map (n16 upstream); card 0 drives hardware channel 3.</summary>
    [Fact]
    public void Nollie16_os2_1_channel_map_matches_reference()
    {
        int[] expected = { 3, 2, 1, 0, 8, 9, 10, 11, 4, 5, 6, 7, 15, 14, 13, 12 };
        for (var card = 0; card < expected.Length; card++)
        {
            Assert.Equal(expected[card], Nollie16Os2_1.HardwareChannel(card));
        }
    }

    [Fact]
    public void Single_channel_device_maps_identity()
    {
        Assert.Equal(0, Nollie1Os2_1.HardwareChannel(0));
    }

    [Fact]
    public void Channel_names_follow_the_board_silkscreen_groups()
    {
        Assert.Equal("Channel 1", NollieProtocol.ChannelName(0));
        Assert.Equal("Channel 16", NollieProtocol.ChannelName(15));
        Assert.Equal("Channel ATX 1", NollieProtocol.ChannelName(16));
        Assert.Equal("Channel GPU 1", NollieProtocol.ChannelName(22));
        Assert.Equal("Channel EXT 1", NollieProtocol.ChannelName(28));
    }

    /// <summary>
    /// The 32-channel board's Strimer connectors are six channels each behind
    /// one physical plug, so they present as one port apiece between the
    /// headers and the EXT channels, sized per the vendor's own OpenRGB guidance.
    /// </summary>
    [Theory]
    [InlineData(0x3061, 0x4714)]
    [InlineData(0x16D5, 0x4714)]
    [InlineData(0x16D5, 0x2A32)]
    public void Thirty_two_channel_boards_bundle_the_strimer_channels_into_two_ports(int vid, int pid)
    {
        var ports = NollieProtocol.Lookup(vid, pid)!.Ports;
        Assert.Equal(22, ports.Count);

        var atx = ports[16];
        Assert.Equal("strimer-atx", atx.Slug);
        Assert.Equal("Strimer ATX", atx.Name);
        Assert.Equal(16, atx.FirstChannel);
        Assert.Equal(6, atx.Lanes);
        Assert.Equal(20, atx.LaneLedCount);
        Assert.Equal(120, atx.MaxLedCount);
        Assert.Equal(NollieProtocol.StrimerAtxProductKey, atx.DefaultProductKey);

        var gpu = ports[17];
        Assert.Equal("strimer-gpu", gpu.Slug);
        Assert.Equal("Strimer GPU", gpu.Name);
        Assert.Equal(22, gpu.FirstChannel);
        Assert.Equal(6, gpu.Lanes);
        Assert.Equal(27, gpu.LaneLedCount);
        Assert.Equal(162, gpu.MaxLedCount);
        Assert.Equal(NollieProtocol.StrimerGpuProductKey, gpu.DefaultProductKey);

        Assert.Equal("ch15", ports[15].Slug);
        Assert.Equal("ch28", ports[18].Slug);
        Assert.Equal("Channel EXT 1", ports[18].Name);
        Assert.Equal("Channel EXT 4", ports[21].Name);
        Assert.All(ports.Where(p => p.Lanes == 1), p =>
        {
            Assert.Equal(256, p.MaxLedCount);
            Assert.Null(p.DefaultProductKey);
        });
    }

    [Theory]
    [InlineData(0x16D5, 0x2A16, 16)]
    [InlineData(0x16D5, 0x2A08, 8)]
    [InlineData(0x16D5, 0x2A01, 1)]
    [InlineData(0x16D2, 0x1617, 8)]
    public void Other_boards_present_one_port_per_channel(int vid, int pid, int channels)
    {
        var ports = NollieProtocol.Lookup(vid, pid)!.Ports;
        Assert.Equal(channels, ports.Count);
        for (var i = 0; i < ports.Count; i++)
        {
            Assert.Equal($"ch{i}", ports[i].Slug);
            Assert.Equal(i, ports[i].FirstChannel);
            Assert.Equal(1, ports[i].Lanes);
        }
    }

    /// <summary>A dual 8-pin harness is four full lanes; the vendor's guide leaves GPU 5 and 6 at 0.</summary>
    [Fact]
    public void Lane_counts_fill_lanes_in_order_and_leave_the_tail_empty()
    {
        var gpu = NollieProtocol.Lookup(0x16D5, 0x2A32)!.Ports[17];
        Assert.Equal(new[] { 27, 27, 27, 27, 0, 0 }, Enumerable.Range(0, 6).Select(l => gpu.LaneLeds(108, l)).ToArray());
        Assert.Equal(new[] { 27, 27, 27, 27, 27, 27 }, Enumerable.Range(0, 6).Select(l => gpu.LaneLeds(162, l)).ToArray());
        Assert.Equal(new[] { 27, 3, 0, 0, 0, 0 }, Enumerable.Range(0, 6).Select(l => gpu.LaneLeds(30, l)).ToArray());
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, Enumerable.Range(0, 6).Select(l => gpu.LaneLeds(0, l)).ToArray());
    }

    /// <summary>Regression: nollie16_os2 (0x16D5/0x4716) uses the n16 map, not the legacy 16CH map (NollieDevices.cpp:97-104).</summary>
    [Fact]
    public void Nollie16_os2_uses_the_same_map_as_the_os2_1_variant()
    {
        var os2 = NollieProtocol.Lookup(0x16D5, 0x4716)!;
        for (var card = 0; card < 16; card++)
        {
            Assert.Equal(Nollie16Os2_1.HardwareChannel(card), os2.HardwareChannel(card));
        }
        // The legacy 16CH part on the other VID keeps its own map.
        Assert.NotEqual(os2.HardwareChannel(0), NollieProtocol.Lookup(0x3061, 0x4716)!.HardwareChannel(0));
    }

    /// <summary>Regression: 28 L2 is NOLLIE_FS_CH_LED_NUM (525), same as L1 (NollieDevices.cpp:79-86).</summary>
    [Fact]
    public void Nollie28_L2_allows_the_full_strip_length()
    {
        Assert.Equal(525, NollieProtocol.Lookup(0x16D2, 0x1618)!.MaxLedsPerChannel);
        Assert.Equal(525, NollieProtocol.Lookup(0x16D2, 0x1617)!.MaxLedsPerChannel);
    }

    /// <summary>Regression: the reference keys flag channels on the hardware channel value, so a 16-channel board's hw 15 qualifies.</summary>
    [Fact]
    public void Sixteen_channel_board_still_has_a_flag_channel()
    {
        // n16 maps card 12 to hardware channel 15.
        Assert.Equal(15, Nollie16Os2_1.HardwareChannel(12));
        Assert.True(NollieProtocol.IsFlagChannel(Nollie16Os2_1, 15));
        Assert.Equal(1, NollieProtocol.FlagMarker(Nollie16Os2_1, 15));

        var report = new byte[NollieProtocol.WideReportSize];
        NollieProtocol.WriteWide(report, Nollie16Os2_1, 15, new byte[] { 1, 2, 3 });
        Assert.Equal(1, report[2]);
    }

    [Fact]
    public void Chunked_devices_never_have_flag_channels()
    {
        Assert.False(NollieProtocol.IsFlagChannel(Nollie1Os2_1, 15));
        Assert.Equal(0, NollieProtocol.FlagMarker(Nollie1Os2_1, 15));
    }

    // ── Wide transport ──

    [Fact]
    public void WriteWide_places_channel_count_and_grb()
    {
        var report = new byte[NollieProtocol.WideReportSize];
        // One LED: R=0x10 G=0x20 B=0x30.
        byte[] rgb = { 0x10, 0x20, 0x30 };
        NollieProtocol.WriteWide(report, Nollie16Os2_1, hardwareChannel: 3, rgb);

        Assert.Equal(0x00, report[0]);
        Assert.Equal(0x03, report[1]);
        Assert.Equal(0x00, report[2]);
        Assert.Equal(0x00, report[3]); // count high
        Assert.Equal(0x01, report[4]); // count low
        Assert.Equal(0x20, report[5]); // G
        Assert.Equal(0x10, report[6]); // R
        Assert.Equal(0x30, report[7]); // B
    }

    [Fact]
    public void WriteWide_splits_count_across_high_and_low_bytes()
    {
        var report = new byte[NollieProtocol.WideReportSize];
        var rgb = new byte[300 * 3];
        NollieProtocol.WriteWide(report, Nollie16Os2_1, 0, rgb);
        Assert.Equal(1, report[3]);  // 300 / 256
        Assert.Equal(44, report[4]); // 300 % 256
    }

    [Fact]
    public void WriteWide_clears_stale_bytes_between_calls()
    {
        var report = new byte[NollieProtocol.WideReportSize];
        NollieProtocol.WriteWide(report, Nollie16Os2_1, 0, new byte[] { 0xFF, 0xFF, 0xFF });
        NollieProtocol.WriteWide(report, Nollie16Os2_1, 0, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, report[5]);
        Assert.Equal(0, report[6]);
        Assert.Equal(0, report[7]);
    }

    [Fact]
    public void WriteWide_never_overruns_the_report()
    {
        var report = new byte[NollieProtocol.WideReportSize];
        var rgb = new byte[NollieProtocol.WideReportSize * 2];
        var ex = Record.Exception(() => NollieProtocol.WriteWide(report, Nollie32, 0, rgb));
        Assert.Null(ex);
    }

    // ── Flag channels ──

    [Fact]
    public void Flag_channels_are_keyed_on_the_hardware_channel_value()
    {
        Assert.True(NollieProtocol.IsFlagChannel(Nollie32, 15));
        Assert.True(NollieProtocol.IsFlagChannel(Nollie32, 31));
        Assert.False(NollieProtocol.IsFlagChannel(Nollie32, 0));
    }

    [Fact]
    public void Flag_marker_is_1_and_2_and_lands_in_byte_2()
    {
        Assert.Equal(1, NollieProtocol.FlagMarker(Nollie32, 15));
        Assert.Equal(2, NollieProtocol.FlagMarker(Nollie32, 31));
        Assert.Equal(0, NollieProtocol.FlagMarker(Nollie32, 4));

        var report = new byte[NollieProtocol.WideReportSize];
        NollieProtocol.WriteWide(report, Nollie32, 31, new byte[] { 1, 2, 3 });
        Assert.Equal(2, report[2]);
    }

    // ── Chunked transport ──

    /// <summary>report[1] packs chunk and channel as chunk + channel * stride; the 1CH part uses stride 30.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 2, 60)]
    [InlineData(3, 2, 63)]
    public void WriteChunk_packs_chunk_and_channel_into_report_id(int chunk, int channel, int expected)
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        NollieProtocol.WriteChunk(report, Nollie1Os2_1, channel, chunk, ReadOnlySpan<byte>.Empty);
        Assert.Equal((byte)expected, report[1]);
    }

    [Fact]
    public void WriteChunk_uses_grb_on_the_1ch_part()
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        NollieProtocol.WriteChunk(report, Nollie1Os2_1, 0, 0, new byte[] { 0x10, 0x20, 0x30 });
        Assert.Equal(0x20, report[2]); // G
        Assert.Equal(0x10, report[3]); // R
        Assert.Equal(0x30, report[4]); // B
    }

    [Fact]
    public void WriteChunk_uses_rgb_on_the_28_series()
    {
        var n28 = NollieProtocol.Lookup(0x16D2, 0x1617)!;
        Assert.False(n28.ChunkedIsGrb);
        var report = new byte[NollieProtocol.ChunkedReportSize];
        NollieProtocol.WriteChunk(report, n28, 0, 0, new byte[] { 0x10, 0x20, 0x30 });
        Assert.Equal(0x10, report[2]); // R
        Assert.Equal(0x20, report[3]); // G
        Assert.Equal(0x30, report[4]); // B
    }

    [Fact]
    public void WriteChunk_never_overruns_the_report()
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        var rgb = new byte[NollieProtocol.ChunkedReportSize * 4];
        var ex = Record.Exception(() => NollieProtocol.WriteChunk(report, Nollie1Os2_1, 0, 0, rgb));
        Assert.Null(ex);
    }

    // ── Latch + LED-count handshake ──

    [Fact]
    public void WriteLatch_sets_0xFF_in_byte_1()
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        report[5] = 0x77;
        NollieProtocol.WriteLatch(report);
        Assert.Equal(0xFF, report[1]);
        Assert.Equal(0, report[5]);
    }

    [Fact]
    public void WriteLedCounts_uses_little_endian_pairs_after_the_FE_03_header()
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        NollieProtocol.WriteLedCounts(report, new[] { 300, 1 });
        Assert.Equal(0xFE, report[1]);
        Assert.Equal(0x03, report[2]);
        Assert.Equal(300 & 0xFF, report[3]);
        Assert.Equal(300 >> 8, report[4]);
        Assert.Equal(1, report[5]);
        Assert.Equal(0, report[6]);
    }

    [Fact]
    public void WriteLedCounts_never_overruns_for_a_32_channel_board()
    {
        var report = new byte[NollieProtocol.ChunkedReportSize];
        var ex = Record.Exception(() => NollieProtocol.WriteLedCounts(report, new int[32]));
        Assert.Null(ex);
    }
}
