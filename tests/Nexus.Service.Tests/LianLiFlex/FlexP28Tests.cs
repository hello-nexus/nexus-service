using System;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexP28Tests
{
    private const string Mac = "6D1007F517E1";

    [Fact]
    public void P28_receiver_pid_is_recognized_and_maps_to_its_own_family()
    {
        Assert.True(FlexProtocol.IsReceiverProduct(0x43A8, 0x0105));
        Assert.False(FlexProtocol.IsReceiverProduct(0x43A8, 0x0106));
        Assert.False(FlexProtocol.IsScreenProduct(0x43A8, 0x0105));
        Assert.Equal(FlexFamily.P28V2, FlexProtocol.FamilyOfReceiver(0x0105));
        Assert.Equal(FlexFamily.TlFlex, FlexProtocol.FamilyOfReceiver(0x0102));
        Assert.Equal(FlexFamily.SlInfFlex, FlexProtocol.FamilyOfReceiver(0x0104));
        Assert.Equal(9, FlexProtocol.LedsPerFan(FlexFamily.P28V2));
    }

    [Theory]
    [InlineData(1, 9, 1)]
    [InlineData(2, 18, 1)]
    [InlineData(3, 27, 2)]
    [InlineData(4, 36, 2)]
    public void Live_packets_for_one_to_four_p28_fans_split_twenty_then_the_rest(int fans, int leds, int packets)
    {
        Assert.Equal(leds, fans * FlexProtocol.LedsPerFan(FlexFamily.P28V2));
        var built = FlexProtocol.BuildLivePackets(new byte[leds * 3]);
        Assert.Equal(packets, built.Length);
        Assert.Equal(2, built[^1][3]);
        if (packets == 2)
        {
            Assert.Equal(20, built[0][2]);
            Assert.Equal(leds - 20, built[1][2]);
            Assert.Equal(0, built[0][3]);
        }
    }

    [Fact]
    public void ParseInfo_keeps_p28_flag_bits_out_of_the_rpm()
    {
        var f = FakeFlexTransport.InfoFrame(Mac, FlexProtocol.P28FansType, FlexProtocol.UnboundRxType, 0, 0, fans: 12);
        // Byte 31: bit 7 clear (sync line on), gear 5, RPM high nibble 0x9; byte 32 the low byte.
        f[31] = 0x50 | 0x9;
        f[32] = 0xC4;
        var info = FlexProtocol.ParseInfo(f)!;
        Assert.True(info.IsP28);
        Assert.Equal(2, info.Fans);
        Assert.Equal((0x9 << 8) | 0xC4, info.Rpm[1]);
        Assert.True(info.P28SyncLineOn);
        Assert.Equal(5, info.P28Gear);

        f[31] = 0x80 | 0x9;
        info = FlexProtocol.ParseInfo(f)!;
        Assert.False(info.P28SyncLineOn);
        Assert.Equal((0x9 << 8) | 0xC4, info.Rpm[1]);
    }

    [Fact]
    public void Non_p28_units_report_no_p28_flags()
    {
        var f = FakeFlexTransport.InfoFrame(Mac, 0x33, FlexProtocol.UnboundRxType, 500, 0);
        f[31] = 0x50;
        var info = FlexProtocol.ParseInfo(f)!;
        Assert.False(info.P28SyncLineOn);
        Assert.Equal(0, info.P28Gear);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 8)]
    [InlineData(8, 8)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void P28_duty_floor_is_the_vendor_floor_with_zero_parked(int percent, int expected)
    {
        Assert.Equal(expected, FlexProtocol.ClampDutyPercent(FlexFamily.P28V2, percent));
    }

    [Fact]
    public void P28_duty_never_encodes_the_follow_motherboard_value()
    {
        Assert.Equal(1, FlexProtocol.EncodeDuty(FlexFamily.P28V2, 0));
        Assert.Equal(20, FlexProtocol.EncodeDuty(FlexFamily.P28V2, 8));
        Assert.Equal(255, FlexProtocol.EncodeDuty(FlexFamily.P28V2, 100));
        for (var p = -5; p <= 120; p++)
        {
            foreach (var family in Enum.GetValues<FlexFamily>())
            {
                Assert.NotEqual(FlexProtocol.PwmFollowMotherboard, FlexProtocol.EncodeDuty(family, p));
            }
        }
    }

    [Fact]
    public void Existing_family_duty_encoding_is_unchanged()
    {
        Assert.Equal(5, FlexProtocol.ClampDutyPercent(FlexFamily.TlFlex, 0));
        Assert.Equal(11, FlexProtocol.ClampDutyPercent(FlexFamily.TlFlex, 5));
        Assert.Equal(10, FlexProtocol.ClampDutyPercent(FlexFamily.SlInfFlex, 5));
    }

    private static FakeFlexDiscovery P28Discovery()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "p28-receiver", ReceiverPid = FlexProtocol.P28ReceiverProductId });
        return discovery;
    }

    private static (FlexHub Hub, FakeFlexTransport Tx, FlexClock Clock) P28Hub(int fans = 2)
    {
        var tx = new FakeFlexTransport(Mac, FlexProtocol.P28FansType, FlexProtocol.UnboundRxType, 1200, 0x3B, fans);
        var clock = new FlexClock();
        var hub = new FlexHub(P28Discovery(), _ => tx, clock.Func, null, _ => { });
        hub.Refresh();
        return (hub, tx, clock);
    }

    [Fact]
    public void A_screenless_p28_receiver_becomes_a_controllable_unit()
    {
        var (hub, _, _) = P28Hub(3);
        var unit = Assert.Single(hub.Units);
        Assert.Equal(FlexFamily.P28V2, unit.Family);
        Assert.Equal("", unit.ScreenSerial);
        Assert.True(unit.Controllable);
        Assert.Equal(27, unit.LedCount);
    }

    [Fact]
    public void Pairing_never_attaches_a_screen_to_a_p28_receiver()
    {
        var paths = new[]
        {
            @"\\?\USB#VID_43A8&PID_0105#SER1#{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}",
            @"\\?\USB#VID_1CBE&PID_A018#SCREEN1#{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}",
        };
        var units = FlexDiscoveryParser.Pair(paths, _ => "");
        var p28 = Assert.Single(units);
        Assert.Equal(FlexFamily.P28V2, p28.Family);
        Assert.Equal("", p28.ScreenPath);
        var snapshot = FlexDiscoveryParser.Snapshot(paths, _ => "");
        Assert.Single(snapshot.ScreenOnly);
    }

    [Fact]
    public void P28_cooling_channel_uses_the_vendor_floor_and_never_follow_value()
    {
        var (hub, tx, clock) = P28Hub();
        var provider = new FlexCoolingProvider(hub);
        var channel = Assert.Single(provider.GetFanChannels());
        Assert.Equal(FlexProtocol.P28MinDutyPercent, channel.MinDuty);
        provider.SetFanSpeed(channel.Id, 2);
        clock.Now += 1000;
        hub.Tick();
        Assert.Equal(20, tx.Of(FlexProtocol.CmdSetPwm).Single()[1]);
    }

    [Fact]
    public void P28_structure_is_one_nine_led_zone_per_fan()
    {
        var structure = FlexLightingDeviceProvider.BuildStructure(Mac, FlexFamily.P28V2, 3);
        var segment = Assert.Single(structure.Segments);
        Assert.Equal(27, segment.LedCount);
        Assert.Equal(27, segment.DefaultU!.Length);
        var zone = Assert.Single(structure.DefaultZones);
        Assert.Equal($"lianli-flex:{Mac}:fan", zone.Id);
        Assert.Equal("Lian Li P28 V2 Fan", structure.Name);
        Assert.Equal(new[] { "fan" }, FlexLightingDeviceProvider.ZonesOf(FlexFamily.P28V2));
    }

    [Fact]
    public void P28_has_no_stored_effects_and_never_merges()
    {
        Assert.Empty(FlexAnimationComposer.Keys(FlexFamily.P28V2));
        Assert.False(FlexMergeEffects.IsMergeable(FlexFamily.P28V2, "rainbow"));
        var (hub, _, _) = P28Hub();
        var unit = hub.Units.Single();
        Assert.Null(FlexStoredLook.Build(unit, new Nexus.Service.Persistence.LianLiFlexUnitSettings()));
    }

    [Fact]
    public void P28_release_sends_follow_motherboard_six_and_a_computed_duty_never_does()
    {
        var (hub, tx, clock) = P28Hub();
        var provider = new FlexCoolingProvider(hub);
        var id = provider.GetFanChannels().Single().Id;
        provider.SetFanSpeed(id, 1);
        clock.Now += 1000;
        hub.Tick();
        provider.ReleaseFan(id);
        clock.Now += 1000;
        hub.Tick();
        var sent = tx.Of(FlexProtocol.CmdSetPwm).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(20, sent[0][1]);
        Assert.Equal(new byte[] { 6, 6, 6, 6 }, sent[1][1..5]);
    }

    [Fact]
    public void P28_cooling_reads_rpm_from_the_first_slot_and_ignores_byte_31_flags()
    {
        var (hub, tx, _) = P28Hub(2);
        tx.Info[29] = 0x20 | 0x04;
        tx.Info[30] = 0xB0;
        tx.Info[31] = 0x70 | 0x02;
        tx.Info[32] = 0x10;
        hub.Tick();
        var provider = new FlexCoolingProvider(hub);
        Assert.Equal((0x04 << 8) | 0xB0, provider.GetFanChannels().Single().Rpm);
    }

    [Fact]
    public void P28_never_plays_or_requests_the_motherboard_input()
    {
        var f = FakeFlexTransport.InfoFrame(Mac, FlexProtocol.P28FansType, FlexProtocol.UnboundRxType, 100, 0);
        f[29] |= 0x40;
        f[31] = 0x00;
        var info = FlexProtocol.ParseInfo(f)!;
        Assert.False(info.PlayingMotherboardArgb);
        Assert.True(info.ArgbCableConnected);
        f[31] = 0x80;
        Assert.False(FlexProtocol.ParseInfo(f)!.ArgbCableConnected);

        var (hub, tx, _) = P28Hub();
        tx.Info[29] |= 0x40;
        hub.Tick();
        var unit = hub.Units.Single();
        var stored = new System.Collections.Generic.Dictionary<string, Nexus.Service.Persistence.LianLiFlexUnitSettings>
        {
            [unit.Mac] = new() { MotherboardArgb = true },
        };
        Assert.False(FlexLightingDeviceProvider.PlaysMotherboardArgb(unit, stored));
        Assert.False(hub.SetMotherboardArgb(unit.Mac, true));
        Assert.Empty(tx.Of(FlexProtocol.CmdSyncBoard));
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(9, 4)]
    [InlineData(3, 3)]
    public void P28_fan_count_is_capped_at_four(int reported, int expected)
    {
        var (hub, _, _) = P28Hub(reported);
        Assert.Equal(expected, hub.Units.Single().Fans);
    }

    [Fact]
    public void P28_lighting_validation_rejects_per_zone_merge_and_effect_modes()
    {
        var family = FlexFamily.P28V2;
        Assert.NotNull(Nexus.Service.Routes.LianLiFlexRoutes.ValidateLighting(new() { Mode = Nexus.Service.Persistence.LianLiFlexUnitSettings.ModePerZone }, family));
        Assert.NotNull(Nexus.Service.Routes.LianLiFlexRoutes.ValidateLighting(new() { Merge = true }, family));
        Assert.NotNull(Nexus.Service.Routes.LianLiFlexRoutes.ValidateLighting(new() { Mode = "rainbow" }, family));
    }

    [Fact]
    public void Catalog_lists_the_p28_v2_fan_without_a_screen()
    {
        var row = LightingDevicesCatalog.All.Single(d => d.Source == "nexus" && d.Model == "P28 V2 Fan");
        Assert.Equal("Lian Li", row.Vendor);
        Assert.Equal("0x43A8", row.VendorId);
        Assert.Equal("0x0105", row.ProductId);
    }
}
