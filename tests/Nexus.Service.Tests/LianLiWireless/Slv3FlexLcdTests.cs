using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

// The golden bytes are USBPcap of L-Connect 3 driving a TL Flex LCD (rx 4) on the Y70.
public class Slv3FlexLcdTests
{
    private static readonly byte[] Master = Convert.FromHexString("82FBF3E566E4");
    private static readonly byte[] TlMac = Convert.FromHexString("6D1007F517E1");

    [Fact]
    public void Default_theme_colors_match_the_captured_send_colors_slot()
    {
        var buffer = Slv3FlexLcd.BuildColorBuffer(0, 0, null, 0x39);
        Assert.Equal("A932FF" + new string('F', 30) + "39", Convert.ToHexString(buffer, 0, 19));
        Assert.All(buffer.Skip(19), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Custom_five_color_theme_repeats_the_unit_color_like_l_connect()
    {
        var buffer = Slv3FlexLcd.BuildColorBuffer(0, 0, new uint[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF }, 0x0E);
        Assert.Equal("FF000000FF000000FFFFFF00FF00FFFF00FF0E", Convert.ToHexString(buffer, 0, 19));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 4)]
    [InlineData(13, 0)]
    [InlineData(18, 0)]
    [InlineData(19, 3)]
    [InlineData(28, 3)]
    public void Color_counts_follow_the_send_path(int theme, int count)
    {
        Assert.Equal(count, Slv3FlexLcd.ColorCount(theme));
        Assert.Equal(count, Slv3FlexLcd.DefaultColors(theme).Length);
    }

    [Fact]
    public void Three_color_themes_take_their_shadow_colors_from_the_defaults()
    {
        var buffer = Slv3FlexLcd.BuildColorBuffer(0, 20, null, 1);
        Assert.Equal("333333333333333333FFFFFFFFFFFFFFFFFF", Convert.ToHexString(buffer, 0, 18));
    }

    [Fact]
    public void The_fixed_block_matches_the_captured_reading_layout()
    {
        var sensors = new Slv3FlexLcdSensors(37, 8, 50, 17, 4400, 1815, 57, 2, 16192, 7379, 33, 45, 0, 1170, 0, 1.09, 0.96, 151, 9);
        var block = Slv3FlexLcd.BuildFixedBlock(sensors, new DateTime(2026, 10, 20, 21, 30, 10));
        Assert.Equal("250832111130071739023F401CD3014A01C200000492" + "00006D6000009700000907EA0A14151E0A0000000000000000000000", Convert.ToHexString(block));
    }

    [Fact]
    public void The_table_row_sits_at_its_slot_with_direction_source_and_brightness()
    {
        var themes = new byte[] { 22, 0, 0, 0 };
        var block = Slv3FlexLcd.BuildLcdBlock(
            new[] { new Slv3FlexLcd.Row(4, themes, new byte[4], Slv3FlexLcd.Direction(3), 60, 1) },
            default, new DateTime(2026, 10, 20, 21, 30, 10));
        Assert.Equal(218, block.Length);
        Assert.Equal("16000000" + "40000000" + "3C000000", Convert.ToHexString(block, 50 + (3 * 12), 12));
        Assert.All(block.Skip(50 + 4 * 12), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void Direction_follows_the_stored_rotation(int rotation, int expected)
    {
        Assert.Equal((byte)expected, Slv3FlexLcd.Direction(rotation));
    }

    [Theory]
    [InlineData(false, false, 3, 0, 2)]
    [InlineData(false, false, 3, 2, 0)]
    [InlineData(false, false, 1, 0, 0)]
    [InlineData(true, false, 3, 0, 0)]
    [InlineData(true, true, 3, 0, 2)]
    [InlineData(true, false, 4, 3, 3)]
    public void Cache_index_reverses_for_tl_and_right_attached_sl_inf(bool slInf, bool right, int fans, int service, int expected)
    {
        Assert.Equal(expected, Slv3FlexLcd.CacheIndex(slInf, right, fans, service));
    }

    [Fact]
    public void Clock_sync_with_a_block_carries_it_whole_and_without_one_stays_the_filler_frame()
    {
        var now = new DateTime(2026, 10, 20, 21, 30, 10);
        var plain = Slv3Protocol.BuildClockSync(Master, now);
        Assert.Equal(0x14, plain[14]);
        Assert.All(plain.Skip(64), b => Assert.Equal(0, b));
        var block = Slv3FlexLcd.BuildLcdBlock(Array.Empty<Slv3FlexLcd.Row>(), default, now);
        var withBlock = Slv3Protocol.BuildClockSync(Master, now, block);
        Assert.Equal(block, withBlock.AsSpan(14, 218).ToArray());
        Assert.Equal(Convert.ToHexString(Master), Convert.ToHexString(withBlock, 8, 6));
    }

    [Fact]
    public void Send_colors_and_switch_frames_match_the_captured_layout()
    {
        var data = Slv3FlexLcd.BuildColorBuffer(0, 0, null, 0x39);
        var colors = Slv3Protocol.BuildSequencedCommand(Slv3Protocol.RfSendColors, TlMac, Master, 4, 8, 3, 0, 0, data);
        Assert.Equal("12286D1007F517E182FBF3E566E4" + "04080003" + "0000" + "A932FF", Convert.ToHexString(colors, 0, 23));
        var sw = Slv3Protocol.BuildSequencedCommand(Slv3Protocol.RfSwitchWirelessTheme, TlMac, Master, 4, 8, 4, 0, 1);
        Assert.Equal("12296D1007F517E182FBF3E566E4" + "04080004" + "0000" + "01", Convert.ToHexString(sw, 0, 21));
    }

    [Fact]
    public void The_record_reports_the_theme_switch_nibble()
    {
        var rec = new byte[Slv3Protocol.RecordLength];
        TlMac.CopyTo(rec, 0);
        Master.CopyTo(rec, 6);
        rec[13] = 4;
        rec[30] = 0x10;
        rec[41] = Slv3Protocol.RecordValidator;
        Assert.True(Slv3Protocol.TryParseRecord(rec, 0, out var record));
        Assert.Equal(1, record.WirelessThemeSwitch);
        Assert.Equal(1, record.RxType == 4 ? 1 : 0);
    }

    // ---- hub ----

    private static long _clock;

    private static void Ticks(Slv3Hub hub, int n)
    {
        for (var i = 0; i < n; i++)
        {
            _clock += 1500;
            Assert.True(hub.DriveTick());
        }
    }

    private static (Slv3Hub Hub, Slv3TestHub.FakeSlv3Network Net, Slv3TestHub.FakeTxTransport Tx, Slv3TestHub.SimulatedFan Fan) Rig(int fansType = 51, int fans = 1)
    {
        _clock = 1_000_000;
        var (hub, net, tx) = Slv3TestHub.CreateConnected(() => _clock);
        var fan = new Slv3TestHub.SimulatedFan { Mac = TlMac, MasterMac = net.MasterMac, RxType = 1, FanCount = (byte)fans, FansType = (byte)fansType };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        return (hub, net, tx, fan);
    }

    private static Slv3FlexLcdFrame Frame(bool enabled, int theme = 3) => new(
        new Dictionary<string, Slv3FlexLcdChain>
        {
            [Convert.ToHexString(TlMac)] = new(Convert.ToHexString(TlMac), enabled, 60, 3, new[] { new Slv3FlexLcdFan(theme, 0, null) }),
        },
        default);

    // Whole RF payloads of one command, rebuilt from the 60-byte USB parts.
    private static List<byte[]> Frames(Slv3TestHub.FakeTxTransport tx, byte cmd)
    {
        var result = new List<byte[]>();
        List<byte>? current = null;
        foreach (var f in tx.SentFrames)
        {
            if (f.Length < 64 || f[0] != Slv3Protocol.UsbSendRf)
            {
                continue;
            }
            if (f[1] == 0)
            {
                current = f[4] == Slv3Protocol.RfFrameType && f[5] == cmd ? new List<byte>() : null;
                if (current is not null)
                {
                    result.Add(Array.Empty<byte>());
                }
            }
            if (current is not null)
            {
                current.AddRange(f.AsSpan(4, 60).ToArray());
                result[^1] = current.ToArray();
            }
        }
        return result;
    }

    [Fact]
    public void A_chain_that_is_not_switched_on_gets_the_plain_clock_frame_and_no_theme_commands()
    {
        var (hub, _, tx, _) = Rig();
        hub.FlexLcd = () => null;
        tx.SentFrames.Clear();
        Ticks(hub, 6);
        Assert.Empty(Frames(tx, Slv3Protocol.RfSendColors));
        Assert.Empty(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme));
        Assert.All(Frames(tx, Slv3Protocol.RfClockSync), f => Assert.Equal(0x14, f[14]));
    }

    [Fact]
    public void A_switched_on_chain_gets_the_table_colors_and_the_switch_once_each()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        tx.SentFrames.Clear();
        Ticks(hub, 20);
        var clock = Frames(tx, Slv3Protocol.RfClockSync).Last();
        // The first table slot (rx 1) starts after the 50-byte block at RF [14..].
        Assert.Equal(3, clock[14 + 50]);
        Assert.Equal(60, clock[14 + 50 + 8]);
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSendColors));
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme));
        Assert.Equal(1, fan.ThemeSwitch & 1);
        var colorsSent = Frames(tx, Slv3Protocol.RfSendColors).Count;
        var switchSent = Frames(tx, Slv3Protocol.RfSwitchWirelessTheme).Count;
        Ticks(hub, 10);
        Assert.Equal(colorsSent, Frames(tx, Slv3Protocol.RfSendColors).Count);
        Assert.Equal(switchSent, Frames(tx, Slv3Protocol.RfSwitchWirelessTheme).Count);
    }

    [Fact]
    public void Changing_the_theme_sends_new_colors_and_switching_off_clears_the_switch_bit()
    {
        var (hub, _, tx, fan) = Rig();
        var current = Frame(true, 3);
        hub.FlexLcd = () => current;
        Ticks(hub, 20);
        var before = Frames(tx, Slv3Protocol.RfSendColors).Count;
        current = Frame(true, 7);
        Ticks(hub, 20);
        Assert.True(Frames(tx, Slv3Protocol.RfSendColors).Count > before);
        Assert.Equal(7, Frames(tx, Slv3Protocol.RfClockSync).Last()[14 + 50]);
        current = Frame(false);
        Ticks(hub, 20);
        Assert.Equal(0, fan.ThemeSwitch & 1);
        Assert.Equal(0x14, Frames(tx, Slv3Protocol.RfClockSync).Last()[14]);
    }

    [Fact]
    public void A_chain_that_is_not_an_lcd_flex_never_gets_theme_traffic()
    {
        var (hub, _, tx, _) = Rig(fansType: 52 + 1);
        hub.FlexLcd = () => Frame(true);
        tx.SentFrames.Clear();
        Ticks(hub, 10);
        Assert.Empty(Frames(tx, Slv3Protocol.RfSendColors));
        Assert.Empty(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme));
    }

    // ---- settings and routes ----

    [Fact]
    public void The_frame_is_only_built_when_a_chain_is_switched_on()
    {
        var s = new NexusSettings();
        s.Devices.LianLiWireless.FlexLcd["6D1007F517E1"] = new LianLiFlexWirelessLcdSettings { Enabled = false };
        Assert.Null(Slv3FlexLcdRoutes.BuildFrame(s, _ => 1f));
        s.Devices.LianLiWireless.FlexLcd["6D1007F517E1"].Enabled = true;
        s.Devices.LianLiWireless.FlexLcd["6D1007F517E1"].Fans.Add(new LianLiFlexWirelessLcdFan { Theme = 0, Colors = new() { "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF" } });
        var frame = Slv3FlexLcdRoutes.BuildFrame(s, kind => kind == SummarySensorKind.CpuTemp ? 41f : 0f)!;
        Assert.Equal(41, frame.Sensors.CpuTemp);
        Assert.Equal(new uint[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF }, frame.Chains["6D1007F517E1"].Fans[0].Colors);
    }

    [Fact]
    public void A_patch_validates_and_applies_and_a_theme_change_resets_the_colors()
    {
        var all = new Dictionary<string, LianLiFlexWirelessLcdSettings>();
        Assert.Equal("fan index out of range", Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Fans = new[] { new Slv3FlexLcdFanPatch { Index = 1 } } }, 1));
        Assert.Equal("theme out of range", Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Fans = new[] { new Slv3FlexLcdFanPatch { Index = 0, Theme = 29 } } }, 1));
        Assert.Equal("colors must be #RRGGBB", Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Fans = new[] { new Slv3FlexLcdFanPatch { Index = 0, Colors = new[] { "red" } } } }, 1));
        Assert.Null(Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Brightness = 40, Rotation = 1 }, 1));
        Slv3FlexLcdRoutes.Apply(all, "AA", 2, new Slv3FlexLcdRequest { Enabled = true, Brightness = 40, Fans = new[] { new Slv3FlexLcdFanPatch { Index = 1, Theme = 0, Colors = new[] { "#ff0000", "#00ff00", "#0000ff", "#ffff00", "#ff00ff" } } } });
        var chain = all["AA"];
        Assert.True(chain.Enabled);
        Assert.Equal(40, chain.Brightness);
        Assert.Equal(2, chain.Fans.Count);
        Assert.Equal("#FF0000", chain.Fans[1].Colors![0]);
        Slv3FlexLcdRoutes.Apply(all, "AA", 2, new Slv3FlexLcdRequest { Fans = new[] { new Slv3FlexLcdFanPatch { Index = 1, Theme = 14 } } });
        Assert.Null(chain.Fans[1].Colors);
    }

    [Fact]
    public void The_response_lists_every_theme_with_its_color_count_and_only_bound_lcd_flex_chains()
    {
        var (hub, _, _, _) = Rig();
        var response = Slv3FlexLcdRoutes.BuildResponse(hub.State, new NexusSettings());
        Assert.Equal(29, response.Themes.Length);
        Assert.Equal(new[] { "#A932FF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" }, response.Themes[0].Defaults);
        var chain = Assert.Single(response.Chains);
        Assert.Equal("tlFlex", chain.Family);
        Assert.Equal(1, chain.FanCount);
        Assert.False(chain.Enabled);
        Assert.Equal(25, response.SourceCount);
    }
}
