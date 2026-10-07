using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

public class KrakenProtocolTests
{
    private static readonly KrakenModel EliteV2 = KrakenModel.Find(0x3012)!;

    // Reply frames captured from the physical Kraken Elite V2 (1E71:3012, firmware
    // 1.2.0). Any change to the decoders must be re-verified against real hardware.

    private static byte[] Frame(params byte[] head)
    {
        var buf = new byte[KrakenProtocol.ReportLength];
        head.CopyTo(buf, 0);
        return buf;
    }

    [Fact]
    public void DecodeStatus_matches_captured_frame()
    {
        var report = Frame(
            0x75, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x1b, 0x01, 0xb5, 0x05, 0x14, 0x19, 0x01, 0x02, 0xee, 0x01, 0x14, 0x14);

        var reading = KrakenProtocol.DecodeStatus(report);

        Assert.NotNull(reading);
        Assert.Equal(27.1, reading!.Value.LiquidTempC, 3);
        Assert.Equal(1461, reading.Value.PumpRpm);
        Assert.Equal(20, reading.Value.PumpDuty);
        Assert.Equal(494, reading.Value.FanRpm);
        Assert.Equal(20, reading.Value.FanDuty);
    }

    [Fact]
    public void DecodeStatus_rejects_the_firmware_fault_marker()
    {
        var report = Frame(0x75, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0xFF, 0xFF);
        Assert.Null(KrakenProtocol.DecodeStatus(report));
    }

    [Fact]
    public void DecodeStatus_accepts_the_unsolicited_push_subcommand()
    {
        // The cooler pushes 0x75 0x02 about once a second without being asked.
        var report = Frame(
            0x75, 0x02, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x1b, 0x01, 0xb5, 0x05, 0x14, 0x19, 0x01, 0x02, 0xee, 0x01, 0x14, 0x14);

        Assert.NotNull(KrakenProtocol.DecodeStatus(report));
    }

    [Fact]
    public void DecodeLcdInfo_reads_geometry_and_backlight_from_the_device()
    {
        var report = Frame(
            0x31, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x80, 0x00, 0x00, 0x10, 0x80, 0x02, 0x80, 0x02, 0x50, 0x01, 0x00, 0xff);

        var info = KrakenProtocol.DecodeLcdInfo(report);

        Assert.NotNull(info);
        Assert.Equal(640, info!.Value.Width);
        Assert.Equal(640, info.Value.Height);
        Assert.Equal(80, info.Value.BrightnessPercent);
        Assert.Equal(0, info.Value.OrientationQuarterTurns);
    }

    [Fact]
    public void DecodeFirmware_reads_1_2_0()
    {
        var report = Frame(
            0x11, 0x01, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x12, 0x30, 0x01, 0x01, 0x02, 0x00);

        var fw = KrakenProtocol.DecodeFirmware(report);

        Assert.NotNull(fw);
        Assert.Equal("1.2.0", fw!.Value.ToString());
    }

    [Fact]
    public void DecodeAccessories_finds_the_ring_and_the_fan_chain()
    {
        // Two channels: slot 0 of channel 0 is the Elite ring, slot 0 of channel 1 the fans.
        var report = Frame(
            0x21, 0x03, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00,
            0x02, 0x1e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1b);

        Assert.Equal(2, KrakenProtocol.DecodeChannelCount(report));
        Assert.Equal(0x1E, KrakenProtocol.DecodeAccessory(report, 0, 0));
        Assert.Equal(0x1B, KrakenProtocol.DecodeAccessory(report, 1, 0));
        Assert.Equal(24, KrakenProtocol.LedCountForAccessory(0x1E));
        Assert.Equal(16, KrakenProtocol.LedCountForAccessory(0x1B));
        Assert.Equal(0, KrakenProtocol.LedCountForAccessory(0x99));
    }

    [Fact]
    public void DecodeDisplayMode_reads_the_active_mode()
    {
        var report = Frame(
            0x31, 0x03, 0x28, 0x63, 0x94, 0x82, 0x0e, 0xc0, 0x2d, 0x39, 0x00, 0x00, 0x00, 0x00, 0x04);

        Assert.Equal(KrakenDisplayMode.Bucket, KrakenProtocol.DecodeDisplayMode(report));
    }

    [Fact]
    public void Every_command_fills_a_full_report()
    {
        // A short write is rejected by this firmware, so no encoder may return a stub.
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeStatusRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeFirmwareRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeLcdInfoRequest().Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetBacklight(50, 1).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeDeleteBucket(3).Length);
        Assert.Equal(KrakenProtocol.ReportLength, KrakenProtocol.EncodeSetupBucket(0, 0, 1601).Length);
        Assert.Equal(
            KrakenProtocol.ReportLength,
            KrakenProtocol.EncodeColors(0b001, KrakenColorMode.Fixed, KrakenAnimationSpeed.Normal, new byte[] { 1, 2, 3 }, forward: true).Length);
        Assert.Equal(
            KrakenProtocol.ReportLength,
            KrakenProtocol.EncodeChannelColors(0b010, new byte[] { 1, 2, 3 }).Length);
    }

    [Fact]
    public void EncodeSetBacklight_carries_brightness_and_rotation_together()
    {
        var report = KrakenProtocol.EncodeSetBacklight(15, 2);

        Assert.Equal(0x30, report[0]);
        Assert.Equal(0x02, report[1]);
        Assert.Equal(0x01, report[2]);
        Assert.Equal(15, report[3]);
        Assert.Equal(0x01, report[6]);
        Assert.Equal(2, report[7]);
    }

    [Fact]
    public void EncodeSetBacklight_clamps_out_of_range_brightness()
    {
        Assert.Equal(100, KrakenProtocol.EncodeSetBacklight(250, 0)[3]);
        Assert.Equal(0, KrakenProtocol.EncodeSetBacklight(-5, 0)[3]);
    }

    [Fact]
    public void Direct_transfer_start_matches_the_cam_capture()
    {
        Assert.Equal(new byte[] { 0x36, 0x01, 0x00, 0x01, 0x09 }, KrakenProtocol.EncodeStartDirectTransfer()[..5]);
        Assert.Equal(new byte[] { 0x36, 0x03 }, KrakenProtocol.EncodeCancelTransfers()[..2]);
    }

    [Fact]
    public void ToWireBgr_writes_three_bytes_a_pixel_in_bgr_order()
    {
        var bgra = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 0 };
        var dst = new byte[12];
        Assert.Equal(12, KrakenProtocol.ToWireBgr(bgra, 2, 2, 0, sourceIsBgra: true, dst));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, dst);

        var one = new byte[3];
        KrakenProtocol.ToWireBgr(new byte[] { 3, 2, 1, 0 }, 1, 1, 0, sourceIsBgra: false, one);
        Assert.Equal(new byte[] { 1, 2, 3 }, one);
    }

    [Fact]
    public void ToWireBgr_rotates_like_the_rgba_path()
    {
        var bgra = Enumerable.Range(0, 3 * 3 * 4).Select(i => (byte)i).ToArray();
        for (int turns = 0; turns < 4; turns++)
        {
            var rgba = KrakenProtocol.ToWireRgba(bgra, 3, 3, turns, sourceIsBgra: true);
            var bgr = new byte[27];
            KrakenProtocol.ToWireBgr(bgra, 3, 3, turns, sourceIsBgra: true, bgr);
            for (int p = 0; p < 9; p++)
            {
                Assert.Equal(rgba[(p * 4) + 2], bgr[p * 3]);
                Assert.Equal(rgba[(p * 4) + 1], bgr[(p * 3) + 1]);
                Assert.Equal(rgba[p * 4], bgr[(p * 3) + 2]);
            }
        }
    }

    [Fact]
    public void EncodeSpeedCurve_uses_the_verified_channel_tuples()
    {
        var duties = Enumerable.Repeat((byte)55, KrakenProtocol.CurvePointCount).ToArray();

        var pump = KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.PumpChannel, duties);
        Assert.Equal(0x72, pump[0]);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00 }, pump[1..4]);
        Assert.Equal(55, pump[4]);
        Assert.Equal(55, pump[43]);
        Assert.Equal(0, pump[44]);

        var fan = KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.FanChannel, duties);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x01 }, fan[1..4]);
    }

    [Fact]
    public void EncodeSpeedCurve_rejects_a_wrong_length_curve()
    {
        Assert.Throws<ArgumentException>(() =>
            KrakenProtocol.EncodeSpeedCurve(KrakenProtocol.PumpChannel, new byte[10]));
    }

    [Fact]
    public void EncodeColors_writes_grb_not_rgb()
    {
        var report = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.Fixed, KrakenAnimationSpeed.Normal,
            new byte[] { 0x11, 0x22, 0x33 }, forward: true);

        Assert.Equal(0x2A, report[0]);
        Assert.Equal(0x04, report[1]);
        Assert.Equal(KrakenProtocol.ColorChannelRing, report[2]);
        Assert.Equal(KrakenProtocol.ColorChannelRing, report[3]);
        Assert.Equal(0x22, report[7]);  // G
        Assert.Equal(0x11, report[8]);  // R
        Assert.Equal(0x33, report[9]);  // B
        Assert.Equal(1, report[7 + (16 * 3) + 1]); // colour count
    }

    [Fact]
    public void EncodeColors_adds_two_to_the_direction_byte_when_backward()
    {
        const int footer = 7 + (16 * 3);
        var forward = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.SpectrumWave, KrakenAnimationSpeed.Normal, ReadOnlySpan<byte>.Empty, forward: true);
        var backward = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.SpectrumWave, KrakenAnimationSpeed.Normal, ReadOnlySpan<byte>.Empty, forward: false);

        Assert.Equal(0x00, forward[footer]);
        Assert.Equal(0x02, backward[footer]);

        // Marquee carries a non-zero base even running forward.
        var marquee = KrakenProtocol.EncodeColors(
            0b001, KrakenColorMode.CoveringMarquee, KrakenAnimationSpeed.Normal, new byte[] { 1, 2, 3 }, forward: true);
        Assert.Equal(0x04, marquee[footer]);
    }

    [Fact]
    public void EncodeChannelColors_addresses_the_channel_and_swaps_to_grb()
    {
        var rgb = new byte[] { 0xAA, 0xBB, 0xCC, 0x10, 0x20, 0x30 };

        var report = KrakenProtocol.EncodeChannelColors(KrakenProtocol.ColorChannelFans, rgb);

        Assert.Equal(0x26, report[0]);
        Assert.Equal(0x14, report[1]);
        // The channel id rides both bytes.
        Assert.Equal(KrakenProtocol.ColorChannelFans, report[2]);
        Assert.Equal(KrakenProtocol.ColorChannelFans, report[3]);
        Assert.Equal(new byte[] { 0xBB, 0xAA, 0xCC, 0x20, 0x10, 0x30 }, report[4..10]);
        // Unused slots stay zero so stale colours cannot linger.
        Assert.Equal(0, report[10]);
    }

    [Fact]
    public void EncodeChannelColors_keeps_the_whole_ring_in_one_report()
    {
        // The tail of the ring used to ride a second report and came out dark; LED 23
        // sits at 11 o'clock, which is where that showed.
        var rgb = new byte[24 * 3];
        rgb[23 * 3] = 0x11;       // R
        rgb[(23 * 3) + 1] = 0x22; // G
        rgb[(23 * 3) + 2] = 0x33; // B

        var report = KrakenProtocol.EncodeChannelColors(KrakenProtocol.ColorChannelRing, rgb);

        var slot = 4 + (23 * 3);
        Assert.Equal(new byte[] { 0x22, 0x11, 0x33 }, report[slot..(slot + 3)]);
    }

    [Fact]
    public void AccessoryRings_gives_one_circle_per_fan_on_a_multi_fan_radiator()
    {
        Assert.Equal((1, 24), KrakenProtocol.AccessoryRings(0x1E)); // Kraken Elite ring
        Assert.Equal((2, 8), KrakenProtocol.AccessoryRings(0x1B));  // F240: two fans
        Assert.Equal((3, 8), KrakenProtocol.AccessoryRings(0x1D));  // F360: three fans
        // Ring counts must agree with the LED counts they are derived from.
        foreach (byte id in new byte[] { 0x10, 0x11, 0x17, 0x18, 0x19, 0x1B, 0x1D, 0x1E, 0x1F })
        {
            var (rings, perRing) = KrakenProtocol.AccessoryRings(id);
            Assert.Equal(KrakenProtocol.LedCountForAccessory(id), rings * perRing);
        }
        // Unmeasured accessory: no geometry rather than a guess.
        Assert.Equal((0, 0), KrakenProtocol.AccessoryRings(0x13));
    }

    [Fact]
    public void EncodeChannelColors_drops_colours_past_the_channel_limit()
    {
        var rgb = new byte[(KrakenProtocol.MaxDirectColors + 5) * 3];
        rgb.AsSpan().Fill(0x7F);

        var report = KrakenProtocol.EncodeChannelColors(0b001, rgb);

        var lastSlot = 4 + ((KrakenProtocol.MaxDirectColors - 1) * 3);
        Assert.Equal(0x7F, report[lastSlot]);
        Assert.Equal(0, report[lastSlot + 3]);
    }

    [Fact]
    public void EncodeBulkHeader_is_the_magic_plus_format_and_little_endian_length()
    {
        var header = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgba8888, 1_638_400);

        Assert.Equal(20, header.Length);
        Assert.Equal(
            new byte[] { 0x12, 0xFA, 0x01, 0xE8, 0xAB, 0xCD, 0xEF, 0x98, 0x76, 0x54, 0x32, 0x10 },
            header[..12]);
        Assert.Equal(0x02, header[12]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x19, 0x00 }, header[16..20]);
    }

    [Fact]
    public void PagesFor_rounds_the_frame_up_to_whole_pages()
    {
        // A full RGBA frame plus the 20-byte header spans 1601 KiB pages.
        Assert.Equal(1601, KrakenProtocol.PagesFor(EliteV2.LcdFrameBytes));
        Assert.Equal(1, KrakenProtocol.PagesFor(1));
    }

    [Fact]
    public void LcdFrameBytes_is_one_full_rgba_panel()
    {
        Assert.Equal(640 * 640 * 4, EliteV2.LcdFrameBytes);
    }

    [Fact]
    public void IsBucketEmpty_ignores_the_echoed_index()
    {
        var empty = Frame(0x31, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x07);
        Assert.True(KrakenProtocol.IsBucketEmpty(empty));

        // An occupied bucket reports its asset index, start page and size.
        var occupied = Frame(
            0x31, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x00, 0x01, 0x02, 0x00, 0x00, 0x41, 0x06, 0x01, 0x01);
        Assert.False(KrakenProtocol.IsBucketEmpty(occupied));
    }

    [Fact]
    public void RotateRgba_moves_a_corner_pixel_a_quarter_turn_at_a_time()
    {
        // 2x2 frame, one marked pixel in the top-left.
        const int n = 2;
        var src = new byte[n * n * 4];
        src[0] = 0xAA; src[1] = 0xBB; src[2] = 0xCC; src[3] = 0x00;

        int MarkedIndex(byte[] f)
        {
            for (int i = 0; i < f.Length; i += 4)
            {
                if (f[i] == 0xAA && f[i + 1] == 0xBB && f[i + 2] == 0xCC) return i / 4;
            }
            return -1;
        }

        Assert.Equal(0, MarkedIndex(KrakenProtocol.RotateRgba(src, n, n, 0)));
        // Top-left travels to top-right, then bottom-right, then bottom-left.
        Assert.Equal(1, MarkedIndex(KrakenProtocol.RotateRgba(src, n, n, 1)));
        Assert.Equal(3, MarkedIndex(KrakenProtocol.RotateRgba(src, n, n, 2)));
        Assert.Equal(2, MarkedIndex(KrakenProtocol.RotateRgba(src, n, n, 3)));
    }

    [Fact]
    public void RotateRgba_four_quarter_turns_is_the_identity()
    {
        const int n = 4;
        var src = new byte[n * n * 4];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i * 7);

        var once = KrakenProtocol.RotateRgba(src, n, n, 1);
        var twice = KrakenProtocol.RotateRgba(once, n, n, 1);
        var thrice = KrakenProtocol.RotateRgba(twice, n, n, 1);
        var full = KrakenProtocol.RotateRgba(thrice, n, n, 1);

        Assert.Equal(src, full);
        Assert.Equal(KrakenProtocol.RotateRgba(src, n, n, 2), twice);
    }

    [Fact]
    public void RotateRgba_normalises_out_of_range_turns()
    {
        const int n = 2;
        var src = new byte[n * n * 4];
        src[4] = 0x42;

        Assert.Equal(KrakenProtocol.RotateRgba(src, n, n, 1), KrakenProtocol.RotateRgba(src, n, n, 5));
        Assert.Equal(KrakenProtocol.RotateRgba(src, n, n, 3), KrakenProtocol.RotateRgba(src, n, n, -1));
    }

    [Fact]
    public void EncodeColors_reads_the_speed_row_the_animation_names()
    {
        // Spectrum wave is speed scale 2; liquidctl's row runs 0x015E slowest to 0x0050
        // fastest, and the two bytes sit at 5 and 6 little-endian.
        var slowest = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.SpectrumWave,
            KrakenAnimationSpeed.Slowest, ReadOnlySpan<byte>.Empty, forward: true);
        var fastest = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.SpectrumWave,
            KrakenAnimationSpeed.Fastest, ReadOnlySpan<byte>.Empty, forward: true);

        Assert.Equal(0x5E, slowest[5]);
        Assert.Equal(0x01, slowest[6]);
        Assert.Equal(0x50, fastest[5]);
        Assert.Equal(0x00, fastest[6]);
    }

    [Fact]
    public void EncodeColors_reads_the_high_speed_rows_too()
    {
        // Scales 3, 4, 7 and 8 were missing from the table; tai chi is scale 7 and
        // loading is scale 8, whose row is flat at 0x0014 for every speed.
        var taiChi = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.TaiChi,
            KrakenAnimationSpeed.Slowest, new byte[] { 1, 2, 3 }, forward: true);
        var loadingSlow = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.Loading,
            KrakenAnimationSpeed.Slowest, new byte[] { 1, 2, 3 }, forward: true);
        var loadingFast = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.Loading,
            KrakenAnimationSpeed.Fastest, new byte[] { 1, 2, 3 }, forward: true);

        Assert.Equal(0x32, taiChi[5]);
        Assert.Equal(0x00, taiChi[6]);
        Assert.Equal(0x14, loadingSlow[5]);
        Assert.Equal(0x14, loadingFast[5]);
    }

    [Fact]
    public void EncodeColors_pins_the_water_cooler_colour_count_to_one()
    {
        const int footer = 7 + (16 * 3);
        // Water cooler is handed two colours but the count byte must still read 1.
        var report = KrakenProtocol.EncodeColors(
            KrakenProtocol.ColorChannelRing, KrakenColorMode.WaterCooler,
            KrakenAnimationSpeed.Normal, new byte[] { 1, 2, 3, 4, 5, 6 }, forward: true);

        Assert.Equal(1, report[footer + 1]);
        // Both colours still reach the report; only the count is pinned.
        Assert.Equal(2, report[7]);
        Assert.Equal(5, report[10]);
    }

    [Fact]
    public void Effects_catalogue_ids_are_unique_and_resolvable()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in KrakenEffects.All)
        {
            Assert.True(ids.Add(effect.Id), $"duplicate effect id {effect.Id}");
            Assert.Same(effect, KrakenEffects.Find(effect.Id));
            Assert.True(effect.MinColors <= effect.MaxColors);
        }
        Assert.Null(KrakenEffects.Find("nope"));
        Assert.Null(KrakenEffects.Find(null));
    }

    [Fact]
    public void IsAck_reads_byte_fourteen()
    {
        Assert.True(KrakenProtocol.IsAck(Frame(0x33, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01)));
        // 0x05 is the overlap failure the device returns for a stale bucket allocation.
        Assert.False(KrakenProtocol.IsAck(Frame(0x33, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05)));
    }
}
