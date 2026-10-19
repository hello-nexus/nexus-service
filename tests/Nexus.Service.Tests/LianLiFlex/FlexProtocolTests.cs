using System;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexProtocolTests
{
    [Theory]
    [InlineData(FlexFamily.TlFlex, 0, 5)]
    [InlineData(FlexFamily.TlFlex, 5, 11)]
    [InlineData(FlexFamily.TlFlex, 40, 40)]
    [InlineData(FlexFamily.SlInfFlex, 0, 5)]
    [InlineData(FlexFamily.SlInfFlex, 5, 10)]
    [InlineData(FlexFamily.SlInfFlex, 10, 10)]
    public void ClampDutyPercent_applies_the_family_floor(FlexFamily family, int percent, int expected)
    {
        Assert.Equal(expected, FlexProtocol.ClampDutyPercent(family, percent));
    }

    [Theory]
    [InlineData(FlexFamily.TlFlex, 100, 255)]
    [InlineData(FlexFamily.TlFlex, 50, 128)]
    [InlineData(FlexFamily.SlInfFlex, 10, 26)]
    public void EncodeDuty_rounds_percent_onto_the_byte_scale(FlexFamily family, int percent, int expected)
    {
        Assert.Equal(expected, FlexProtocol.EncodeDuty(family, percent));
    }

    [Fact]
    public void SetPwm_and_follow_motherboard_fill_all_four_ports()
    {
        var pwm = FlexProtocol.BuildSetPwm(0x54);
        Assert.Equal(new byte[] { 0x13, 0x54, 0x54, 0x54, 0x54 }, pwm[..5]);
        var follow = FlexProtocol.BuildFollowMotherboard();
        Assert.Equal(new byte[] { 0x13, 6, 6, 6, 6 }, follow[..5]);
    }

    [Fact]
    public void LivePackets_for_a_tl_fan_are_two_with_only_the_last_committing()
    {
        var rgb = new byte[26 * 3];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(i + 1);
        }
        var packets = FlexProtocol.BuildLivePackets(rgb);
        Assert.Equal(2, packets.Length);
        Assert.Equal(new byte[] { 0x11, 0, 20, 0 }, packets[0][..4]);
        Assert.Equal(new byte[] { 0x11, 20, 6, 2 }, packets[1][..4]);
        Assert.Equal(rgb[60], packets[1][4]);
    }

    [Fact]
    public void LivePackets_for_an_sl_inf_fan_are_three()
    {
        var packets = FlexProtocol.BuildLivePackets(new byte[44 * 3], commitFlag: 1);
        Assert.Equal(3, packets.Length);
        Assert.Equal(0, packets[0][3]);
        Assert.Equal(0, packets[1][3]);
        Assert.Equal(1, packets[2][3]);
        Assert.Equal(4, packets[2][2]);
        Assert.Equal(40, packets[2][1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void LivePackets_refuse_a_commit_flag_that_does_not_display(byte flag)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FlexProtocol.BuildLivePackets(new byte[3], flag));
    }

    [Fact]
    public void PackagePackets_carry_the_header_fields_and_60_byte_parts()
    {
        var compressed = new byte[130];
        compressed[0] = 0xAA;
        compressed[129] = 0xBB;
        var packets = FlexProtocol.BuildPackagePackets(new byte[] { 1, 2, 3, 4 }, compressed, 13, 26, 44.0);
        Assert.Equal(4, packets.Length);
        var h = packets[0];
        Assert.Equal(0x18, h[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, h[16..20]);
        Assert.Equal(130, (h[22] << 24) | (h[23] << 16) | (h[24] << 8) | h[25]);
        Assert.Equal(13, (h[27] << 8) | h[28]);
        Assert.Equal(26, h[29]);
        Assert.Equal(44, (h[34] << 8) | h[35]);
        Assert.Equal(0, h[36]);
        Assert.Equal(1, packets[1][1]);
        Assert.Equal(0xAA, packets[1][4]);
        Assert.Equal(3, packets[3][1]);
        Assert.Equal(0xBB, packets[3][4 + 9]);
    }

    [Fact]
    public void PackagePackets_split_a_fractional_interval_into_whole_and_hundredths()
    {
        var h = FlexProtocol.BuildPackagePackets(new byte[4], new byte[10], 10, 44, 65.45)[0];
        Assert.Equal(65, (h[34] << 8) | h[35]);
        Assert.Equal(45, h[36]);
    }

    [Fact]
    public void ParseInfo_reads_identity_rpm_and_pwm()
    {
        var info = FlexProtocol.ParseInfo(FakeFlexTransport.InfoFrame("6D1007F517E1", 0x33, FlexProtocol.UnboundRxType, 661, 0x3B));
        Assert.NotNull(info);
        Assert.Equal("6D1007F517E1", info!.Mac);
        Assert.Equal(0x33, info.FansType);
        Assert.Equal(661, info.Rpm[0]);
        Assert.Equal(0x3B, info.Pwm[0]);
        Assert.True(info.PwmLinePresent);
        Assert.False(info.RfBound);
        Assert.Equal(1, info.Fans);
    }

    [Fact]
    public void ParseInfo_treats_fan_num_over_ten_as_right_attach()
    {
        var f = FakeFlexTransport.InfoFrame("112233445566", 0x2B, 0, 0, 0);
        f[20] = 11;
        var info = FlexProtocol.ParseInfo(f)!;
        Assert.Equal(1, info.Fans);
        Assert.True(info.RfBound);
    }

    [Fact]
    public void ParseInfo_rejects_other_replies()
    {
        Assert.Null(FlexProtocol.ParseInfo(new byte[64]));
    }

    [Theory]
    [InlineData(0x08, 0, true)]
    [InlineData(0x10, 0, true)]
    [InlineData(0x11, 0, true)]
    [InlineData(0x12, 0, true)]
    [InlineData(0x13, 0, true)]
    [InlineData(0x15, 1, true)]
    [InlineData(0x17, 0, true)]
    [InlineData(0x18, 0, true)]
    [InlineData(0x19, 0, true)]
    [InlineData(0x15, 0, false)]
    [InlineData(0x14, 0, false)]
    [InlineData(0x16, 0, false)]
    [InlineData(0x20, 0, false)]
    [InlineData(0x21, 0, false)]
    [InlineData(0x22, 0, false)]
    [InlineData(0x23, 0, false)]
    [InlineData(0x25, 0, false)]
    [InlineData(0x26, 0, false)]
    [InlineData(0x27, 0, false)]
    [InlineData(0x29, 0, false)]
    public void IsAllowed_admits_only_the_commands_this_module_uses(byte cmd, byte arg, bool allowed)
    {
        var f = new byte[64];
        f[0] = cmd;
        f[1] = arg;
        Assert.Equal(allowed, FlexProtocol.IsAllowed(f));
    }

    [Fact]
    public void ZoneWireIndexes_follow_the_vendor_zone_layout()
    {
        Assert.Equal(13, FlexProtocol.ZoneWireIndexes(FlexFamily.TlFlex, "up").Count);
        Assert.Equal(13, FlexProtocol.ZoneWireIndexes(FlexFamily.TlFlex, "down")[0]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, FlexProtocol.ZoneWireIndexes(FlexFamily.SlInfFlex, "center"));
        var inner = FlexProtocol.ZoneWireIndexes(FlexFamily.SlInfFlex, "inner");
        Assert.Equal(20, inner.Count);
        Assert.Equal(8, inner[0]);
        Assert.Equal(17, inner[9]);
        Assert.Equal(26, inner[10]);
        Assert.Equal(35, inner[19]);
        var outer = FlexProtocol.ZoneWireIndexes(FlexFamily.SlInfFlex, "outer");
        Assert.Equal(16, outer.Count);
        Assert.Equal(18, outer[0]);
        Assert.Equal(36, outer[8]);
        Assert.Equal(43, outer[15]);
    }

    [Fact]
    public void ZoneWireIndexes_cover_every_led_of_a_fan_exactly_once()
    {
        foreach (var (family, zones, leds) in new[]
        {
            (FlexFamily.TlFlex, new[] { "up", "down" }, 26),
            (FlexFamily.SlInfFlex, new[] { "center", "inner", "outer" }, 44),
        })
        {
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var z in zones)
            {
                foreach (var i in FlexProtocol.ZoneWireIndexes(family, z))
                {
                    Assert.True(seen.Add(i));
                }
            }
            Assert.Equal(leds, seen.Count);
        }
    }

    [Fact]
    public void SyncClock_encodes_seconds_times_1600_big_endian()
    {
        var f = FlexProtocol.BuildSyncClock(3.0);
        Assert.Equal(4800, (f[1] << 24) | (f[2] << 16) | (f[3] << 8) | f[4]);
    }
}
