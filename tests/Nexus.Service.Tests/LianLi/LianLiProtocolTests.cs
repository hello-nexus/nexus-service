using System;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Tests.LianLi;

public class LianLiProtocolTests
{
    // ── Constants ──

    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x0CF2, LianLiProtocol.VendorId);
        Assert.Equal(0xA102, LianLiProtocol.ProductId);
        Assert.Equal(0xFF72, LianLiProtocol.VendorUsagePage);
        Assert.Equal(0xA1, LianLiProtocol.VendorUsage);
        Assert.Equal(4, LianLiProtocol.PortCount);
        // Hardware-confirmed per-fan counts (camera, fw 1.4): inner index 0/8/16
        // land on fans 1/2/3, outer index 12 on fan 2.
        Assert.Equal(8, LianLiProtocol.InnerLedsPerFan);
        Assert.Equal(12, LianLiProtocol.OuterLedsPerFan);
        LianLiFanProfiles.TryGet(0xA102, out var sli);
        Assert.Equal(8, sli.LedsPerFanForChannel(0));
        Assert.Equal(12, sli.LedsPerFanForChannel(1));
        Assert.Equal(8, sli.LedsPerFanForChannel(2));
        Assert.Equal(12, sli.LedsPerFanForChannel(3));
        // SL v1: one channel per port, 16 LEDs per fan.
        Assert.Equal(16, LianLiProtocol.SlLedsPerFan);
        LianLiFanProfiles.TryGet(0xA100, out var sl);
        Assert.Equal(16, sl.LedsPerFanForChannel(0));
        Assert.Equal(16, sl.LedsPerFanForChannel(1));
        Assert.Equal(16, LianLiProtocol.MaxLedsPerFanPerChannel);
        Assert.Equal(4, LianLiProtocol.MaxFansPerPort);
        Assert.Equal(353, LianLiProtocol.OutputReportSize);
        Assert.Equal(65, LianLiProtocol.InputReportSize);
        Assert.Equal(0xE0, LianLiProtocol.ReportId);
    }

    // ── BuildSetQuantity ──

    private static LianLiFanProfile Profile(int pid)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var p));
        return p;
    }

    [Theory]
    [InlineData(0, 3, new byte[] { 0xE0, 0x10, 0x60, 0x01, 0x03, 0x00, 0x00 })]
    [InlineData(1, 2, new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x02, 0x00, 0x00 })]
    [InlineData(3, 0, new byte[] { 0xE0, 0x10, 0x60, 0x04, 0x00, 0x00, 0x00 })]
    public void BuildSetQuantity_emits_correct_bytes(int group, int qty, byte[] expected)
    {
        Assert.Equal(expected, LianLiProtocol.BuildSetQuantity(Profile(0xA102), group, qty));
    }

    // SL v1: E0 10 32 ((g << 4) | qty).
    [Theory]
    [InlineData(0, 3, new byte[] { 0xE0, 0x10, 0x32, 0x03, 0x00, 0x00, 0x00 })]
    [InlineData(1, 2, new byte[] { 0xE0, 0x10, 0x32, 0x12, 0x00, 0x00, 0x00 })]
    [InlineData(3, 4, new byte[] { 0xE0, 0x10, 0x32, 0x34, 0x00, 0x00, 0x00 })]
    [InlineData(2, 0, new byte[] { 0xE0, 0x10, 0x32, 0x20, 0x00, 0x00, 0x00 })]
    public void BuildSetQuantity_sl_v1_packs_port_and_count(int group, int qty, byte[] expected)
    {
        Assert.Equal(expected, LianLiProtocol.BuildSetQuantity(Profile(0xA100), group, qty));
        Assert.Equal(expected, LianLiProtocol.BuildSetQuantity(Profile(0xA106), group, qty));
    }

    // AL: E0 10 40 (g+1) qty.
    [Fact]
    public void BuildSetQuantity_al_uses_register_0x40()
    {
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x40, 0x02, 0x03, 0x00, 0x00 },
            LianLiProtocol.BuildSetQuantity(Profile(0xA101), 1, 3));
    }

    [Fact]
    public void BuildSetQuantity_clamps_qty_to_four()
    {
        var report = LianLiProtocol.BuildSetQuantity(Profile(0xA102), 0, 99);
        Assert.Equal(4, report[4]);
        var packed = LianLiProtocol.BuildSetQuantity(Profile(0xA100), 1, 99);
        Assert.Equal(0x14, packed[3]);
    }

    // ── BuildEffectCommit ──

    [Fact]
    public void BuildEffectCommit_encodes_channel_in_nibble()
    {
        var report = LianLiProtocol.BuildEffectCommit(3, 0x01, 0x00, 0x00, 0x00);
        Assert.Equal(7, report.Length);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x13, report[1]); // 0x10 | ch
        Assert.Equal(0x01, report[2]); // STATIC_COLOR
    }

    // ── BuildManualMode ──

    [Theory]
    [InlineData(0, 0x10)]
    [InlineData(1, 0x20)]
    [InlineData(2, 0x40)]
    [InlineData(3, 0x80)]
    public void BuildManualMode_selector_byte(int ch, int expected)
    {
        var report = LianLiProtocol.BuildManualMode(ch, 0x62); // SL-Infinity register
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x10, report[1]);
        Assert.Equal(0x62, report[2]);
        Assert.Equal((byte)expected, report[3]);
    }

    // 49=0x31 (Sl family), 66=0x42 (Al family), 98=0x62 (SlInfinity/SlV2/AlV2)
    [Theory]
    [InlineData(49, 0x31)]
    [InlineData(66, 0x42)]
    [InlineData(98, 0x62)]
    public void BuildManualMode_register_byte_is_written_to_byte2(int manualReg, int expectedByte2)
    {
        var report = LianLiProtocol.BuildManualMode(0, (byte)manualReg);
        Assert.Equal((byte)expectedByte2, report[2]);
    }

    // ── BuildReleaseMode ──

    [Theory]
    [InlineData(0, 0x11)]
    [InlineData(1, 0x22)]
    [InlineData(2, 0x44)]
    [InlineData(3, 0x88)]
    public void BuildReleaseMode_selector_byte(int ch, int expected)
    {
        var report = LianLiProtocol.BuildReleaseMode(ch, 0x62); // SlInfinity register
        Assert.Equal((byte)expected, report[3]);
    }

    // Sl raw (reg 49=0x31), Al raw (reg 66=0x42), SlInfinity floored (reg 98=0x62).
    [Theory]
    [InlineData(49, 0x31)]
    [InlineData(66, 0x42)]
    [InlineData(98, 0x62)]
    public void BuildReleaseMode_register_byte_matches_family_register(int manualReg, int expectedByte2)
    {
        var report = LianLiProtocol.BuildReleaseMode(0, (byte)manualReg);
        Assert.Equal((byte)expectedByte2, report[2]);
        Assert.Equal(0x11, report[3]); // ch=0: 0x11<<0 = 0x11 (sync bit set)
    }

    // ── BuildSetSpeed ──

    [Fact]
    public void BuildSetSpeed_port_in_second_byte()
    {
        var report = LianLiProtocol.BuildSetSpeed(2, 50, false);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x22, report[1]); // 0x20 | 2
        Assert.Equal(0x00, report[2]);
    }

    [Theory]
    [InlineData(0,    0)]
    [InlineData(5,    5)]
    [InlineData(10,  10)]
    [InlineData(50,  50)]
    [InlineData(100, 100)]
    public void BuildSetSpeed_raw_duty_byte(int duty, int expectedByte)
    {
        var report = LianLiProtocol.BuildSetSpeed(0, duty, false);
        Assert.Equal((byte)expectedByte, report[3]);
    }

    [Theory]
    [InlineData(0,    1)]
    [InlineData(5,   10)]
    [InlineData(10,  10)]
    [InlineData(50,  50)]
    [InlineData(100, 100)]
    public void BuildSetSpeed_floored_duty_byte(int duty, int expectedByte)
    {
        var report = LianLiProtocol.BuildSetSpeed(0, duty, true);
        Assert.Equal((byte)expectedByte, report[3]);
    }

    // ── BuildRpmPrimer ──

    [Fact]
    public void BuildRpmPrimer_is_E0_50_00_00_00_00_00()
    {
        Assert.Equal(
            new byte[] { 0xE0, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00 },
            LianLiProtocol.BuildRpmPrimer());
    }

    // ── DutyByte ──

    [Theory]
    [InlineData(0,    0)]
    [InlineData(-5,   0)]
    [InlineData(5,    5)]
    [InlineData(10,  10)]
    [InlineData(50,  50)]
    [InlineData(100, 100)]
    public void DutyByte_raw_mapping(int duty, int expected)
    {
        Assert.Equal((byte)expected, LianLiProtocol.DutyByte(duty, false));
    }

    [Theory]
    [InlineData(0,    1)]
    [InlineData(-5,   1)]
    [InlineData(5,   10)]
    [InlineData(10,  10)]
    [InlineData(50,  50)]
    [InlineData(100, 100)]
    public void DutyByte_floored_mapping(int duty, int expected)
    {
        Assert.Equal((byte)expected, LianLiProtocol.DutyByte(duty, true));
    }

    // ── DecodeRpm ──

    [Fact]
    public void DecodeRpm_big_endian_two_bytes_per_channel()
    {
        // ch=0, rpmOffset=1: buf[1]=high, buf[2]=low -> rpm=0x04B0=1200
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[1] = 0x04;
        buf[2] = 0xB0;
        Assert.Equal(1200, LianLiProtocol.DecodeRpm(buf, 0, 1));
    }

    [Fact]
    public void DecodeRpm_channel_offset()
    {
        // ch=2, rpmOffset=1: buf[1+4]=buf[5]=high, buf[6]=low -> rpm=0x0546=1350
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[5] = 0x05;
        buf[6] = 0x46;
        Assert.Equal(1350, LianLiProtocol.DecodeRpm(buf, 2, 1));
    }

    [Fact]
    public void DecodeRpm_offset_two_reads_from_correct_position()
    {
        // ch=0, rpmOffset=2: buf[2+0*2]=buf[2] high, buf[3] low -> rpm=0x04B0=1200
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[2] = 0x04;
        buf[3] = 0xB0;
        Assert.Equal(1200, LianLiProtocol.DecodeRpm(buf, 0, 2));
    }

    [Fact]
    public void DecodeRpm_returns_negative_one_for_out_of_range()
    {
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        buf[1] = 0xFF;
        buf[2] = 0xFF;
        Assert.Equal(-1, LianLiProtocol.DecodeRpm(buf, 0, 1));
    }

    [Fact]
    public void DecodeRpm_accepts_zero()
    {
        Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
        Assert.Equal(0, LianLiProtocol.DecodeRpm(buf, 0, 1));
    }

    // ── WriteColorData ──

    [Fact]
    public void WriteColorData_report_id_and_channel_nibble()
    {
        var report = new byte[LianLiProtocol.OutputReportSize];
        LianLiProtocol.WriteColorData(report, 5, ReadOnlySpan<byte>.Empty);
        Assert.Equal(0xE0, report[0]);
        Assert.Equal(0x35, report[1]);
    }

    [Fact]
    public void WriteColorData_swaps_green_and_blue()
    {
        // Input: R=0x10, G=0x20, B=0x30. Wire order: R, B, G.
        byte[] leds = { 0x10, 0x20, 0x30 };
        var report = new byte[LianLiProtocol.OutputReportSize];
        LianLiProtocol.WriteColorData(report, 0, leds);
        Assert.Equal(0x10, report[2]); // R unchanged
        Assert.Equal(0x30, report[3]); // B in wire-G slot
        Assert.Equal(0x20, report[4]); // G in wire-B slot
    }

    [Fact]
    public void WriteColorData_energy_cap_scales_proportionally()
    {
        // R=200, G=200, B=200 -> sum=600 > 460; should be scaled down.
        byte[] leds = { 200, 200, 200 };
        var report = new byte[LianLiProtocol.OutputReportSize];
        LianLiProtocol.WriteColorData(report, 0, leds);
        Assert.Equal(report[2], report[3]);
        Assert.Equal(report[2], report[4]);
        Assert.True(report[2] < 200);
    }

    [Fact]
    public void WriteColorData_no_cap_below_limit()
    {
        // R=100, G=100, B=100 -> sum=300 < 460; no capping.
        byte[] leds = { 100, 100, 100 };
        var report = new byte[LianLiProtocol.OutputReportSize];
        LianLiProtocol.WriteColorData(report, 0, leds);
        Assert.Equal(100, report[2]);
        Assert.Equal(100, report[3]);
        Assert.Equal(100, report[4]);
    }

    // ── LianLiFanProfiles ──

    [Theory]
    [InlineData(0x7750, false, 49, 48, 1)]
    [InlineData(0xA100, false, 49, 48, 1)]
    [InlineData(0xA101, false, 66, 65, 1)]
    [InlineData(0xA102, true,  98, 97, 1)]
    [InlineData(0xA103, true,  98, 97, 2)]
    [InlineData(0xA104, true,  98, 97, 2)]
    [InlineData(0xA105, true,  98, 97, 2)]
    [InlineData(0xA106, false, 49, 48, 1)]
    public void LianLiFanProfiles_TryGet_returns_correct_profile(
        int pid, bool floored, int manualReg, int argbReg, int rpmOffset)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var profile));
        Assert.Equal(pid, profile.ProductId);
        Assert.Equal(floored, profile.FlooredDuty);
        Assert.Equal((byte)manualReg, profile.ManualRegister);
        Assert.Equal((byte)argbReg, profile.ArgbRegister);
        Assert.Equal(rpmOffset, profile.RpmOffset);
    }

    // Lighting layout per family. SL v1 = one 16-LED ring per fan on one channel
    // per port, colours over interrupt-OUT; the rest keep the SL-Infinity layout.
    [Theory]
    [InlineData(0xA100, 1, 16, 0,  true,  0x32, true)]
    [InlineData(0xA106, 1, 16, 0,  true,  0x32, true)]
    [InlineData(0xA101, 2, 8,  12, false, 0x40, false)]
    [InlineData(0xA102, 2, 8,  12, false, 0x60, false)]
    [InlineData(0xA103, 2, 8,  12, false, 0x60, false)]
    public void LianLiFanProfiles_lighting_layout(
        int pid, int channelsPerPort, int inner, int outer, bool packed, int quantityReg, bool slV1)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var profile));
        Assert.Equal(channelsPerPort, profile.ChannelsPerPort);
        Assert.Equal(inner, profile.InnerLedsPerFan);
        Assert.Equal(outer, profile.OuterLedsPerFan);
        Assert.Equal(packed, profile.PackedQuantity);
        Assert.Equal((byte)quantityReg, profile.QuantityRegister);
        // SL v1: interrupt-OUT colours, quantity once on attach, merge cleared, per-fan static palette.
        Assert.Equal(slV1, profile.ColorViaInterruptOut);
        Assert.Equal(!slV1, profile.StartActionPerFrame);
        Assert.Equal(slV1, profile.ClearMergeOnAttach);
        Assert.Equal(slV1, profile.PerFanStaticPalette);
    }

    [Fact]
    public void BuildStopMerge_emits_sl_v1_merge_off()
    {
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 }, LianLiProtocol.BuildStopMerge());
    }

    [Fact]
    public void LianLiFanProfiles_Default_is_the_sl_infinity_row()
    {
        Assert.Equal(0xA102, LianLiFanProfiles.Default.ProductId);
        Assert.Equal(2, LianLiFanProfiles.Default.ChannelsPerPort);
    }

    [Fact]
    public void LianLiFanProfiles_TryGet_unknown_pid_returns_false()
    {
        Assert.False(LianLiFanProfiles.TryGet(0x9999, out _));
    }

    [Fact]
    public void LianLiFanProfiles_AllProductIds_contains_all_eight_pids()
    {
        var ids = LianLiFanProfiles.AllProductIds;
        Assert.Equal(8, ids.Length);
        Assert.Contains(0x7750, ids);
        Assert.Contains(0xA100, ids);
        Assert.Contains(0xA101, ids);
        Assert.Contains(0xA102, ids);
        Assert.Contains(0xA103, ids);
        Assert.Contains(0xA104, ids);
        Assert.Contains(0xA105, ids);
        Assert.Contains(0xA106, ids);
    }

    // ── LianLiSettings ──

    [Fact]
    public void LianLiSettings_GetFans_reads_per_port()
    {
        var s = new Nexus.Service.Persistence.LianLiSettings
        {
            Port0Fans = 3,
            Port1Fans = 2,
            Port2Fans = 0,
            Port3Fans = 1,
        };
        Assert.Equal(3, s.GetFans(0));
        Assert.Equal(2, s.GetFans(1));
        Assert.Equal(0, s.GetFans(2));
        Assert.Equal(1, s.GetFans(3));
        Assert.Equal(0, s.GetFans(99));
    }

    [Fact]
    public void LianLiSettings_default_fan_count_is_4_per_port()
    {
        var s = new Nexus.Service.Persistence.LianLiSettings();
        Assert.Equal(4, s.Port0Fans);
        Assert.Equal(4, s.Port1Fans);
        Assert.Equal(4, s.Port2Fans);
        Assert.Equal(4, s.Port3Fans);
    }

    [Fact]
    public void LianLiSettings_SetFans_writes_per_port()
    {
        var s = new Nexus.Service.Persistence.LianLiSettings();
        s.SetFans(0, 4);
        s.SetFans(2, 3);
        Assert.Equal(4, s.Port0Fans);
        Assert.Equal(4, s.Port1Fans);
        Assert.Equal(3, s.Port2Fans);
        Assert.Equal(4, s.Port3Fans);
    }
}
