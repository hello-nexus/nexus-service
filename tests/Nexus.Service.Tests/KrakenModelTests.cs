using System;
using System.Linq;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Peripherals.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers what varies between Kraken models. The wire facts here were reconstructed from
/// third-party documentation; only the Elite V2 rows have been run against hardware, so
/// these tests pin the reconstruction rather than proving the devices accept it.
/// </summary>
public class KrakenModelTests
{
    [Theory]
    [InlineData(0x3012, 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.ChannelReport)]
    [InlineData(0x3014, 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.ChannelReport)]
    [InlineData(0x300C, 640, 640, KrakenLcdFormat.Q565, KrakenLightingProtocol.None)]
    [InlineData(0x300E, 240, 240, KrakenLcdFormat.Rgb565, KrakenLightingProtocol.None)]
    [InlineData(0x3008, 320, 320, KrakenLcdFormat.Rgba8888, KrakenLightingProtocol.StreamedTables)]
    [InlineData(0x2007, 0, 0, KrakenLcdFormat.None, KrakenLightingProtocol.StreamedTables)]
    [InlineData(0x2014, 0, 0, KrakenLcdFormat.None, KrakenLightingProtocol.StreamedTables)]
    public void Each_supported_model_declares_its_panel_and_lighting(
        int productId, int width, int height, KrakenLcdFormat format, KrakenLightingProtocol lighting)
    {
        var model = KrakenModel.Find(productId);

        Assert.NotNull(model);
        Assert.Equal(width, model!.LcdWidth);
        Assert.Equal(height, model.LcdHeight);
        Assert.Equal(format, model.LcdFormat);
        Assert.Equal(lighting, model.Lighting);
        Assert.Equal(format != KrakenLcdFormat.None, model.HasLcd);
    }

    [Fact]
    public void Product_ids_are_unique_and_match_the_model_table()
    {
        var ids = KrakenModel.All.Select(m => m.ProductId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids, KrakenProtocol.ProductIds.ToArray());
    }

    [Fact]
    public void Only_the_2023_Elite_streams_on_the_direct_path()
    {
        Assert.Equal(new[] { 0x300C }, KrakenModel.All.Where(m => m.DirectStream).Select(m => m.ProductId));
    }

    [Fact]
    public void Unknown_product_id_has_no_model()
    {
        Assert.Null(KrakenModel.Find(0x170E));
    }

    /// <summary>
    /// Only the 2023 Kraken and Kraken Elite ever moved their 0x72 tuples. The Elite V2
    /// ships firmware 1.x, which would read as "old" on that version test, so it must not
    /// be gated on the version at all.
    /// </summary>
    [Fact]
    public void Only_the_2023_coolers_pick_their_speed_channels_by_firmware()
    {
        Assert.True(KrakenModel.Find(0x300C)!.SpeedChannelsFollowFirmware);
        Assert.True(KrakenModel.Find(0x300E)!.SpeedChannelsFollowFirmware);
        Assert.False(KrakenModel.Find(0x3012)!.SpeedChannelsFollowFirmware);
        Assert.False(KrakenModel.Find(0x3008)!.SpeedChannelsFollowFirmware);
    }

    [Theory]
    [InlineData(2, 1, 1, true)]
    [InlineData(2, 2, 0, true)]
    [InlineData(3, 0, 0, true)]
    [InlineData(2, 1, 0, false)]
    [InlineData(2, 0, 9, false)]
    [InlineData(1, 2, 0, false)]
    public void New_speed_channels_start_at_firmware_2_1_1(int major, int minor, int patch, bool expected)
    {
        Assert.Equal(expected, KrakenProtocol.UsesNewSpeedChannels(new KrakenFirmware(major, minor, patch)));
    }

    [Fact]
    public void No_firmware_reply_keeps_the_older_speed_channels()
    {
        Assert.False(KrakenProtocol.UsesNewSpeedChannels(null));
    }

    [Fact]
    public void Legacy_speed_channels_differ_from_the_current_pair()
    {
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00 }, KrakenProtocol.LegacyPumpChannel.ToArray());
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, KrakenProtocol.LegacyFanChannel.ToArray());
        Assert.NotEqual(KrakenProtocol.PumpChannel.ToArray(), KrakenProtocol.LegacyPumpChannel.ToArray());
        Assert.NotEqual(KrakenProtocol.FanChannel.ToArray(), KrakenProtocol.LegacyFanChannel.ToArray());
    }

    // ── 0x22 streamed colour tables (Kraken X3 / Z3) ──

    [Fact]
    public void Streamed_colors_write_grb_into_the_first_table()
    {
        var reports = KrakenProtocol.EncodeStreamedColors(0x02, new byte[] { 10, 20, 30, 40, 50, 60 });

        Assert.Equal(2, reports.Length);
        Assert.Equal(0x22, reports[0][0]);
        Assert.Equal(0x10, reports[0][1]);
        Assert.Equal(0x02, reports[0][2]);
        Assert.Equal(0x00, reports[0][3]);
        // GRB, not RGB.
        Assert.Equal(new byte[] { 20, 10, 30, 50, 40, 60 }, reports[0].Skip(4).Take(6).ToArray());
    }

    [Fact]
    public void Streamed_colors_spill_into_the_second_table_after_twenty_leds()
    {
        // 21 LEDs: the 21st is the first byte of table 1.
        var rgb = new byte[21 * 3];
        rgb[20 * 3] = 1;      // R
        rgb[(20 * 3) + 1] = 2; // G
        rgb[(20 * 3) + 2] = 3; // B
        var reports = KrakenProtocol.EncodeStreamedColors(0x01, rgb);

        Assert.Equal(0x11, reports[1][1]);
        Assert.Equal(new byte[] { 2, 1, 3 }, reports[1].Skip(4).Take(3).ToArray());
        // Table 0 is full at 60 bytes and nothing spilled past its end.
        Assert.Equal(0, reports[0][4 + 60]);
    }

    [Fact]
    public void Streamed_colors_stop_at_the_forty_led_ceiling()
    {
        var rgb = new byte[60 * 3];
        rgb.AsSpan().Fill(0xAB);

        var reports = KrakenProtocol.EncodeStreamedColors(0x01, rgb);

        Assert.Equal(40, KrakenProtocol.MaxStreamedColors);
        Assert.All(reports, r => Assert.All(r.Skip(4).Take(60), b => Assert.Equal(0xAB, b)));
        // Nothing past the two tables.
        Assert.Equal(0, reports[1][4 + 60]);
    }

    [Fact]
    public void Both_tables_are_always_emitted_so_a_stale_one_cannot_survive()
    {
        var reports = KrakenProtocol.EncodeStreamedColors(0x01, new byte[] { 1, 2, 3 });

        Assert.Equal(2, reports.Length);
        Assert.Equal(0x11, reports[1][1]);
        Assert.All(reports[1].Skip(4), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Submit_carries_the_constant_latch_tail()
    {
        var report = KrakenProtocol.EncodeSubmitColors(0x02);

        Assert.Equal(
            new byte[] { 0x22, 0xA0, 0x02, 0x00, 0x01, 0x00, 0x00, 0x28, 0x00, 0x00, 0x80, 0x00, 0x32, 0x00, 0x00, 0x01 },
            report.Take(16).ToArray());
    }

    /// <summary>The 0x22 path has to fit a 64-byte report: 4 header bytes plus 60 of colour.</summary>
    [Fact]
    public void Streamed_reports_fit_a_64_byte_report()
    {
        var reports = KrakenProtocol.EncodeStreamedColors(0x01, new byte[40 * 3]);

        Assert.All(reports, r => Assert.All(r.Skip(64), b => Assert.Equal(0, b)));
        Assert.All(KrakenProtocol.EncodeSubmitColors(0x01).Skip(64), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0x10, true)]  // X3 pump ring
    [InlineData(0x11, true)]  // X3 logo
    [InlineData(0x1E, true)]  // Elite pump ring
    [InlineData(0x1B, false)] // F240 fan chain
    [InlineData(0x1D, false)] // F360 fan chain
    public void Pump_ring_accessories_are_named_apart_from_fan_chains(byte accessoryId, bool expected)
    {
        Assert.Equal(expected, KrakenProtocol.IsPumpRingAccessory(accessoryId));
    }

    // ── RGB565 (the 2023 Kraken's only format) ──

    [Fact]
    public void Rgb565_packs_two_little_endian_bytes_per_pixel()
    {
        var dest = new byte[Rgb565Encoder.EncodedLength(1, 1)];

        // Pure red: 0b11111 000000 00000 = 0xF800.
        var written = Rgb565Encoder.Encode(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, 1, 1, 0, dest);

        Assert.Equal(2, written);
        Assert.Equal(new byte[] { 0x00, 0xF8 }, dest);
    }

    [Fact]
    public void Rgb565_reads_a_bgra_source_in_the_other_order()
    {
        var dest = new byte[2];

        Rgb565Encoder.Encode(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, 1, 1, 0, dest, sourceIsBgra: true);

        // The same bytes now mean pure blue: 0x001F.
        Assert.Equal(new byte[] { 0x1F, 0x00 }, dest);
    }

    [Fact]
    public void Rgb565_rotation_moves_the_top_left_pixel_to_the_top_right()
    {
        // 2x2, only the top-left pixel is white.
        var src = new byte[2 * 2 * 4];
        src[0] = src[1] = src[2] = 0xFF;
        var dest = new byte[Rgb565Encoder.EncodedLength(2, 2)];

        Rgb565Encoder.Encode(src, 2, 2, 1, dest);

        // Destination (1,0) - index 1 - is the lit one; every other pixel is black.
        Assert.Equal(new byte[] { 0x00, 0x00 }, dest.Take(2).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, dest.Skip(2).Take(2).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, dest.Skip(4).ToArray());
    }

    [Fact]
    public void Rgb565_length_is_two_bytes_a_pixel()
    {
        Assert.Equal(240 * 240 * 2, Rgb565Encoder.EncodedLength(240, 240));
    }

    // ── raw RGBA wire conversion (the Z3's format) ──

    [Fact]
    public void Wire_rgba_swaps_a_bgra_source_and_zeroes_alpha()
    {
        var wire = KrakenProtocol.ToWireRgba(new byte[] { 0x11, 0x22, 0x33, 0xFF }, 1, 1, 0, sourceIsBgra: true);

        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0x00 }, wire);
    }

    [Fact]
    public void Wire_rgba_keeps_an_rgba_source_in_order()
    {
        var wire = KrakenProtocol.ToWireRgba(new byte[] { 0x11, 0x22, 0x33, 0xFF }, 1, 1, 0, sourceIsBgra: false);

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x00 }, wire);
    }

    [Fact]
    public void Wire_rgba_rotation_matches_the_compressed_encoders()
    {
        var src = new byte[2 * 2 * 4];
        src[0] = src[1] = src[2] = 0xFF;

        var wire = KrakenProtocol.ToWireRgba(src, 2, 2, 1, sourceIsBgra: false);

        // Same destination as the RGB565 case: top-left goes to top-right.
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x00 }, wire.Skip(4).Take(4).ToArray());
        Assert.Equal(0, wire[0]);
    }
}
