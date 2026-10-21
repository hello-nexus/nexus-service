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

    private static Slv3FlexLcdFrame FrameFor(params (byte[] Mac, bool Enabled, int Theme)[] chains) => new(
        chains.ToDictionary(
            c => Convert.ToHexString(c.Mac),
            c => new Slv3FlexLcdChain(Convert.ToHexString(c.Mac), c.Enabled, 60, 3, new[] { new Slv3FlexLcdFan(c.Theme, 0, null) })),
        default);

    [Fact]
    public void The_frames_the_hub_sends_carry_l_connects_zero_based_ordinal_in_the_captured_report_order()
    {
        _clock = 1_000_000;
        var (hub, net, tx) = Slv3TestHub.CreateConnected(() => _clock);
        var strimer = Convert.FromHexString("64F271E566E1");
        var sl = Convert.FromHexString("C5D212506CE1");
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = TlMac, MasterMac = net.MasterMac, RxType = 4, FanCount = 1, FansType = 51 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = sl, MasterMac = net.MasterMac, RxType = 5, FanCount = 1, FansType = 43 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = strimer, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Ticks(hub, 1);
        hub.FlexLcd = () => FrameFor((TlMac, true, 3), (sl, true, 9));
        Ticks(hub, 12);
        string Ordinal(byte[] mac, byte cmd) => Frames(tx, cmd).Where(f => f.AsSpan(2, 6).SequenceEqual(mac)).Select(f => f[16]).Distinct().Single().ToString();
        Assert.Equal("0", Ordinal(TlMac, Slv3Protocol.RfSendColors));
        Assert.Equal("1", Ordinal(sl, Slv3Protocol.RfSendColors));
    }

    [Fact]
    public void A_failed_send_does_not_count_as_delivered_and_the_colors_go_out_when_the_link_returns()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        tx.FailSends = true;
        Ticks(hub, 5);
        tx.FailSends = false;
        tx.SentFrames.Clear();
        Ticks(hub, 12);
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSendColors));
        Assert.Equal(1, fan.ThemeSwitch & 1);
    }

    [Fact]
    public void Colors_overwritten_by_another_command_or_never_acknowledged_are_sent_again()
    {
        var (hub, _, tx, fan) = Rig();
        fan.IgnoreCommands = true;
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 2);
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSendColors));
        // Another command takes the chain's single pending slot before the colors were acknowledged.
        Assert.True(hub.Identify(Convert.ToHexString(TlMac)));
        tx.SentFrames.Clear();
        fan.IgnoreCommands = false;
        Ticks(hub, 20);
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSendColors));
        var settled = Frames(tx, Slv3Protocol.RfSendColors).Count;
        Ticks(hub, 10);
        Assert.Equal(settled, Frames(tx, Slv3Protocol.RfSendColors).Count);
    }

    [Fact]
    public void A_chain_that_never_answers_is_asked_a_bounded_number_of_times_for_colors_and_the_switch()
    {
        var (hub, _, tx, fan) = Rig();
        fan.IgnoreCommands = true;
        hub.FlexLcd = () => Frame(true, 3);
        tx.SentFrames.Clear();
        Ticks(hub, 400);
        var colorSeqs = Frames(tx, Slv3Protocol.RfSendColors).Select(f => f[17]).Distinct().Count();
        var switchSeqs = Frames(tx, Slv3Protocol.RfSwitchWirelessTheme).Select(f => f[17]).Distinct().Count();
        Assert.InRange(colorSeqs, 1, Slv3Hub.FlexLcdSwitchAttempts);
        Assert.InRange(switchSeqs, 1, Slv3Hub.FlexLcdSwitchAttempts);
        int Sent() => tx.SentFrames.Count(f => f.Length > 5 && (f[5] == Slv3Protocol.RfSendColors || f[5] == Slv3Protocol.RfSwitchWirelessTheme));
        var before = Sent();
        Ticks(hub, 100);
        Assert.Equal(before, Sent());
        // A changed theme asks again.
        hub.FlexLcd = () => Frame(true, 7);
        Ticks(hub, 5);
        Assert.True(Sent() > before);
    }

    [Fact]
    public void A_pending_switch_is_not_queued_twice()
    {
        var (hub, _, tx, fan) = Rig();
        fan.IgnoreCommands = true;
        hub.FlexLcd = () => Frame(true, 3);
        tx.SentFrames.Clear();
        Ticks(hub, 3);
        Assert.True(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme).Select(f => f[17]).Distinct().Count() <= 1);
    }

    private static int FirstFrame(Slv3TestHub.FakeTxTransport tx, byte cmd) =>
        tx.SentFrames.FindIndex(f => f.Length > 24 && f[4] == Slv3Protocol.RfFrameType && f[5] == cmd);

    private static void ShortTicks(Slv3Hub hub, int n, long stepMs = 500)
    {
        for (var i = 0; i < n; i++)
        {
            _clock += stepMs;
            Assert.True(hub.DriveTick());
        }
    }

    private static bool Released(Slv3TestHub.FakeTxTransport tx) =>
        tx.SentFrames.Exists(f => f.Length > 17 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfBind && f.AsSpan(12, 6).ToArray().All(b => b == 0));

    [Fact]
    public void Unbinding_a_settled_chain_with_themes_on_sends_no_release_before_the_switch_off_is_echoed()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        Assert.Equal(1, fan.ThemeSwitch & 1);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        fan.DropCommands = 2;
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out var error), error);
        Assert.True(FirstFrame(tx, Slv3Protocol.RfSwitchWirelessTheme) >= 0, "no switch-off frame");
        var ticks = 0;
        while ((fan.ThemeSwitch & 1) != 0 && ticks++ < 7)
        {
            ShortTicks(hub, 1);
            Assert.False(Released(tx), "the release went out while the theme bits were still set");
        }
        Assert.Equal(0, fan.ThemeSwitch & 1);
        ShortTicks(hub, 8);
        Assert.True(Released(tx), "no release after the echo");
        var afterSwitch = FirstFrame(tx, Slv3Protocol.RfSwitchWirelessTheme);
        var save = tx.SentFrames.FindIndex(afterSwitch + 1, f => f.Length > 5 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSaveCfg);
        Assert.True(save > afterSwitch && save < tx.SentFrames.FindIndex(f => f.Length > 17 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfBind && f.AsSpan(12, 6).ToArray().All(x => x == 0)), "the cleared switch was not saved before the release");
        Assert.DoesNotContain(tx.SentFrames, f => f.Length > 24 && f[5] == Slv3Protocol.RfSwitchWirelessTheme && f[24] != 0);
    }

    [Fact]
    public void The_release_goes_out_only_after_the_hold_when_the_switch_off_is_never_heard()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        fan.IgnoreCommands = true;
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        var steps = (int)(Slv3Hub.FlexLcdUnbindHoldMs / 500) - 1;
        ShortTicks(hub, steps);
        Assert.False(Released(tx), "released before the hold ran out");
        ShortTicks(hub, 4);
        Assert.True(Released(tx), "no release after the hold");
    }

    [Fact]
    public void A_bind_during_the_hold_cancels_the_unbind()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        fan.IgnoreCommands = true;
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        ShortTicks(hub, 2);
        Assert.True(hub.Bind(Convert.ToHexString(TlMac), out var error), error);
        tx.SentFrames.Clear();
        ShortTicks(hub, 16);
        Assert.False(Released(tx), "the cancelled unbind still fired");
    }

    [Fact]
    public void Turning_the_lock_on_during_the_hold_cancels_the_unbind()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        fan.IgnoreCommands = true;
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        hub.SetLockBoundDevices(true);
        tx.SentFrames.Clear();
        ShortTicks(hub, 16);
        Assert.False(Released(tx));
    }

    private static int SaveAfter(Slv3TestHub.FakeTxTransport tx, int index) =>
        tx.SentFrames.FindIndex(index + 1, f => f.Length > 5 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSaveCfg);

    private static int ReleaseIndex(Slv3TestHub.FakeTxTransport tx) =>
        tx.SentFrames.FindIndex(f => f.Length > 17 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfBind && f.AsSpan(12, 6).ToArray().All(x => x == 0));

    private static void TickUntilCleared(Slv3Hub hub, Slv3TestHub.SimulatedFan fan)
    {
        for (var i = 0; i < 40 && (fan.ThemeSwitch & 1) != 0; i++)
        {
            ShortTicks(hub, 1, 250);
        }
        Assert.True((fan.ThemeSwitch & 1) == 0, "the theme bits were never cleared");
    }

    [Fact]
    public void Theme_bits_that_return_after_a_clear_are_cleared_and_saved_again_before_the_release()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        TickUntilCleared(hub, fan);
        // First clear: let both of its saves go out, short of the release.
        var guard = 0;
        while (SaveFramesSince(tx, 0) < 2 && guard++ < 20)
        {
            ShortTicks(hub, 1, 250);
        }
        Assert.True(SaveFramesSince(tx, 0) >= 2, "the clear was not saved twice");
        Assert.False(Released(tx));
        fan.ThemeSwitch = 1;
        var mark = tx.SentFrames.Count;
        var cleared = false;
        for (var i = 0; i < 40 && !Released(tx); i++)
        {
            ShortTicks(hub, 1, 250);
            cleared |= (fan.ThemeSwitch & 1) == 0;
        }
        Assert.True(cleared, "the returning bits were not cleared again");
        var secondSwitch = tx.SentFrames.FindIndex(mark, f => f.Length > 24 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSwitchWirelessTheme);
        Assert.True(secondSwitch >= 0);
        var release = ReleaseIndex(tx);
        Assert.True(release > secondSwitch, "no release");
        var save = SaveAfter(tx, secondSwitch);
        Assert.True(save > secondSwitch && save < release, "the second clear was not saved before the release");
    }

    private static int SaveFramesSince(Slv3TestHub.FakeTxTransport tx, int index) =>
        tx.SentFrames.Skip(index).Count(f => f.Length > 5 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSaveCfg);

    [Fact]
    public void Theme_bits_that_return_late_get_one_more_hold_before_the_release()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        TickUntilCleared(hub, fan);
        ShortTicks(hub, 1, 250);
        // The bits return just as the hold runs out; the clear then needs the extension.
        _clock += Slv3Hub.FlexLcdUnbindHoldMs;
        fan.ThemeSwitch = 1;
        fan.DropCommands = 1;
        var mark = tx.SentFrames.Count;
        ShortTicks(hub, 1, 250);
        Assert.False(Released(tx), "released with the bits set");
        for (var i = 0; i < 40 && !Released(tx); i++)
        {
            ShortTicks(hub, 1, 250);
        }
        Assert.Equal(0, fan.ThemeSwitch & 1);
        var secondSwitch = tx.SentFrames.FindIndex(mark, f => f.Length > 24 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSwitchWirelessTheme);
        Assert.True(secondSwitch >= 0 && ReleaseIndex(tx) > secondSwitch, "the extension did not let the bits clear first");
    }

    [Fact]
    public void A_chain_that_leaves_the_list_during_the_hold_is_released_when_it_returns()
    {
        var (hub, net, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        hub.SetLockBoundDevices(false);
        ShortTicks(hub, 3);
        fan.IgnoreCommands = true;
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out _));
        net.Fans.Remove(fan);
        _clock += 40_000;
        Assert.True(hub.DriveTick());
        Assert.False(Released(tx), "a gone chain was sent a release");
        net.Fans.Add(fan);
        ShortTicks(hub, 8);
        Assert.True(Released(tx), "the returning chain stayed bound");
    }

    [Fact]
    public void An_unbind_right_after_unlocking_still_waits_for_the_switch_off()
    {
        var (hub, _, tx, fan) = Rig();
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 20);
        fan.DropCommands = 2;
        hub.SetLockBoundDevices(false);
        tx.SentFrames.Clear();
        Assert.True(hub.Unbind(Convert.ToHexString(TlMac), out var error), error);
        var ticks = 0;
        while ((fan.ThemeSwitch & 1) != 0 && ticks++ < 7)
        {
            ShortTicks(hub, 1);
            Assert.False(Released(tx), "released while the theme bits were still set");
        }
        ShortTicks(hub, 8);
        Assert.True(Released(tx));
    }

    private sealed class CountingStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public int Updates { get; private set; }
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); Updates++; OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    [Fact]
    public void A_rejected_put_leaves_the_store_untouched()
    {
        var (hub, _, _, _) = Rig();
        var store = new CountingStore();
        var (status, error, _) = Slv3FlexLcdRoutes.Put("6D1007F517E1", new Slv3FlexLcdRequest { Brightness = 0 }, hub.State, store);
        Assert.Equal(400, status);
        Assert.NotNull(error);
        Assert.Equal(0, store.Updates);
        Assert.Empty(store.Load().Devices.LianLiWireless.FlexLcd);
        Assert.Equal(404, Slv3FlexLcdRoutes.Put("AABBCCDDEEFF", new Slv3FlexLcdRequest { Enabled = true }, hub.State, store).Status);
        Assert.Equal(0, store.Updates);
    }

    [Fact]
    public void The_up_down_swap_is_refused_for_an_sl_inf_chain_and_stored_for_a_tl_chain()
    {
        var (slHub, _, _, _) = Rig(fansType: 43);
        var store = new CountingStore();
        Assert.Equal(400, Slv3FlexLcdRoutes.Put("6D1007F517E1", new Slv3FlexLcdRequest { UpDownSwap = true }, slHub.State, store).Status);
        Assert.Equal(0, store.Updates);
        Assert.Empty(store.Load().Devices.FlexUnits);

        var (tlHub, _, _, _) = Rig();
        var (status, _, changed) = Slv3FlexLcdRoutes.Put("6D1007F517E1", new Slv3FlexLcdRequest { UpDownSwap = true }, tlHub.State, store);
        Assert.Equal(200, status);
        Assert.True(changed);
        Assert.True(store.Load().Devices.FlexUnits["6D1007F517E1"].UpDownSwap);
        Assert.False(Slv3FlexLcdRoutes.Put("6D1007F517E1", new Slv3FlexLcdRequest { UpDownSwap = true }, tlHub.State, store).SwapChanged);
    }

    [Fact]
    public void Theme_frames_are_never_resent_to_a_chain_that_is_no_longer_bound_to_us()
    {
        var (hub, _, tx, fan) = Rig();
        fan.DropCommands = 1000;
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 2);
        Assert.NotEmpty(Frames(tx, Slv3Protocol.RfSendColors));
        fan.MasterMac = new byte[6];
        fan.RxType = 254;
        Ticks(hub, 1);
        tx.SentFrames.Clear();
        Ticks(hub, 10);
        Assert.Empty(Frames(tx, Slv3Protocol.RfSendColors));
        Assert.Empty(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme));
    }

    [Fact]
    public void A_chain_that_is_unheard_is_not_asked_and_asked_again_when_it_returns()
    {
        var (hub, net, tx, fan) = Rig();
        fan.IgnoreCommands = true;
        hub.FlexLcd = () => Frame(true, 3);
        Ticks(hub, 1);
        net.Fans.Clear();
        Ticks(hub, 3);
        var during = Frames(tx, Slv3Protocol.RfSendColors).Select(f => f[17]).Distinct().Count();
        Ticks(hub, 8);
        Assert.Equal(during, Frames(tx, Slv3Protocol.RfSendColors).Select(f => f[17]).Distinct().Count());
        fan.IgnoreCommands = false;
        net.Fans.Add(fan);
        Ticks(hub, 20);
        Assert.Equal(1, fan.ThemeSwitch & 1);
    }

    [Fact]
    public void The_ordinal_follows_the_dongles_report_order_and_counts_every_bound_chain()
    {
        _clock = 1_000_000;
        var (hub, net, tx) = Slv3TestHub.CreateConnected(() => _clock);
        var aio = Convert.FromHexString("EE305E6232E1");
        var other = Convert.FromHexString("998D1DE566E1");
        var strimer = Convert.FromHexString("64F271E566E1");
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = aio, MasterMac = net.MasterMac, RxType = 8, DevType = 10, FanCount = 0 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = other, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 36 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = TlMac, MasterMac = net.MasterMac, RxType = 4, FanCount = 1, FansType = 51 });
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = strimer, MasterMac = net.MasterMac, RxType = 2, DevType = 2, FanCount = 0 });
        Ticks(hub, 1);
        hub.FlexLcd = () => FrameFor((TlMac, true, 3));
        Ticks(hub, 8);
        // Two chains precede the TL fan in the report; the Strimer behind it does not count.
        Assert.Equal(2, Frames(tx, Slv3Protocol.RfSendColors).Select(f => (int)f[16]).Distinct().Single());
    }

    [Fact]
    public void A_failed_settings_read_keeps_the_last_settings_and_releases_nothing()
    {
        var (hub, _, tx, fan) = Rig();
        var fail = false;
        hub.FlexLcd = () => fail ? throw new InvalidOperationException("collection was modified") : Frame(true, 3);
        Ticks(hub, 20);
        Assert.Equal(1, fan.ThemeSwitch & 1);
        fail = true;
        tx.SentFrames.Clear();
        Ticks(hub, 10);
        Assert.Equal(1, fan.ThemeSwitch & 1);
        Assert.Empty(Frames(tx, Slv3Protocol.RfSwitchWirelessTheme));
        Assert.Equal(3, Frames(tx, Slv3Protocol.RfClockSync).Last()[14 + 50]);
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
        Assert.Equal("brightness must be 1-100", Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Brightness = 0 }, 1));
        Assert.Equal("wrong number of colors for the theme", Slv3FlexLcdRoutes.Validate(new Slv3FlexLcdRequest { Fans = new[] { new Slv3FlexLcdFanPatch { Index = 0, Theme = 20, Colors = new[] { "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF" } } } }, 1));
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

    [Fact]
    public void A_tl_chain_reports_its_stored_up_down_swap()
    {
        var (hub, _, _, _) = Rig();
        var settings = new NexusSettings();
        Assert.False(Assert.Single(Slv3FlexLcdRoutes.BuildResponse(hub.State, settings).Chains).UpDownSwap);
        settings.Devices.FlexUnits["6D1007F517E1"] = new LianLiFlexUnitSettings { UpDownSwap = true };
        Assert.True(Assert.Single(Slv3FlexLcdRoutes.BuildResponse(hub.State, settings).Chains).UpDownSwap);
    }

    [Fact]
    public void The_store_read_builds_the_frame_and_the_new_settings_survive_a_round_trip()
    {
        var store = new InMemoryConfigStore();
        store.Update(c => c.Devices.LianLiWireless.FlexLcd["6D1007F517E1"] = new LianLiFlexWirelessLcdSettings
        {
            Enabled = true,
            Fans = { new LianLiFlexWirelessLcdFan { Theme = 3, Source = 4, Colors = new() { "#112233", "#445566", "#778899", "#AABBCC" } } },
        });
        Assert.NotNull(Slv3FlexLcdRoutes.BuildFrame(store, _ => 0f));
        var json = System.Text.Json.JsonSerializer.Serialize(store.Load(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        var fan = back.Devices.LianLiWireless.FlexLcd["6D1007F517E1"].Fans[0];
        Assert.Equal((3, 4), (fan.Theme, fan.Source));
        Assert.Equal(new[] { "#112233", "#445566", "#778899", "#AABBCC" }, fan.Colors);
    }
}
