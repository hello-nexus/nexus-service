using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Tests.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

// A customer with two TL receivers on USB and four TL screens: one receiver fronts a 3-fan chain (three screens), the other a single fan.
public class FlexCustomerTopologyTests
{
    private const string Guid = "#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
    private const string Rx1 = @"\\?\usb#vid_43a8&pid_0102#9&aaaa&0&4" + Guid;
    private const string Rx2 = @"\\?\usb#vid_43a8&pid_0102#9&bbbb&0&4" + Guid;
    private static readonly string[] Screens =
    {
        @"\\?\usb#vid_1cbe&pid_a018#2B324029848A0703" + Guid,
        @"\\?\usb#vid_1cbe&pid_a018#331964888482C702" + Guid,
        @"\\?\usb#vid_1cbe&pid_a018#5D30175F64A28700" + Guid,
        @"\\?\usb#vid_1cbe&pid_a018#47449BCE76D28303" + Guid,
    };

    private static string ResolvedHub(string instance) => instance switch
    {
        @"USB\VID_43A8&PID_0102\9&aaaa&0&4" => "HUB-A",
        @"USB\VID_43A8&PID_0102\9&bbbb&0&4" => "HUB-B",
        @"USB\VID_1CBE&PID_A018\2B324029848A0703" => "HUB-A",
        @"USB\VID_1CBE&PID_A018\331964888482C702" => "HUB-A",
        @"USB\VID_1CBE&PID_A018\5D30175F64A28700" => "HUB-A",
        @"USB\VID_1CBE&PID_A018\47449BCE76D28303" => "HUB-B",
        _ => "",
    };

    [Fact]
    public void Three_screens_behind_one_receiver_with_resolved_hubs_leave_two_screen_only()
    {
        var snapshot = FlexDiscoveryParser.Snapshot(new[] { Rx1, Screens[0], Screens[1], Screens[2] }, ResolvedHub);
        var unit = Assert.Single(snapshot.Units);
        Assert.Equal("2B324029848A0703", unit.ScreenSerial);
        Assert.Equal(new[] { "331964888482C702", "5D30175F64A28700" }, snapshot.ScreenOnly.Select(s => s.ScreenSerial).ToArray());
        Assert.All(snapshot.ScreenOnly, s => Assert.Equal("", s.ReceiverPath));
    }

    [Fact]
    public void Three_screens_with_unresolved_hubs_are_all_screen_only_and_never_guessed()
    {
        var snapshot = FlexDiscoveryParser.Snapshot(new[] { Rx1, Screens[0], Screens[1], Screens[2] }, _ => "");
        Assert.Equal("", Assert.Single(snapshot.Units).ScreenSerial);
        Assert.Equal(3, snapshot.ScreenOnly.Count);
    }

    [Fact]
    public void A_screen_is_in_exactly_one_list_and_a_repeated_path_serial_is_listed_once()
    {
        var snapshot = FlexDiscoveryParser.Snapshot(new[] { Rx1, Rx2 }.Concat(Screens).Append(Screens[3]), ResolvedHub);
        var serials = snapshot.Units.Select(u => u.ScreenSerial).Where(s => s.Length > 0).Concat(snapshot.ScreenOnly.Select(s => s.ScreenSerial)).ToList();
        Assert.Equal(4, serials.Count);
        Assert.Equal(4, serials.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private sealed class SnapshotDiscovery : IFlexDiscovery
    {
        private readonly FlexDiscoverySnapshot _snapshot;

        public SnapshotDiscovery(FlexDiscoverySnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public IReadOnlyList<FlexUnitPort> Discover() => _snapshot.Units;

        public FlexDiscoverySnapshot Snapshot() => _snapshot;
    }

    private sealed class NoLcd : ISlv3LcdTransport
    {
        public bool IsOpen => true;
        public string PortName => "p";
        public bool PushImage(byte[] jpeg) => true;
        public bool SetBrightness(byte value) => true;
        public bool SetRotation(byte value) => true;
        public bool TryGetPosition(out byte groupIndex) { groupIndex = 0; return false; }
        public void Dispose() { }
    }

    [Fact]
    public void Two_receivers_and_four_screens_all_reach_the_state_and_the_screen_routes()
    {
        var snapshot = FlexDiscoveryParser.Snapshot(new[] { Rx1, Rx2 }.Concat(Screens), ResolvedHub);
        var discovery = new SnapshotDiscovery(snapshot);
        var macs = new Dictionary<string, string> { [Rx1] = "210807F517E1", [Rx2] = "A41F07F517E1" };
        var hub = new FlexHub(discovery, port => new FakeFlexTransport(macs[port.ReceiverPath]), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        Assert.Equal(2, hub.Units.Length);

        var screenDiscovery = new FlexScreenDiscovery(discovery);
        var lcd = new FlexLcdHub(new Slv3LcdHub(screenDiscovery, _ => new NoLcd()), screenDiscovery);
        var ports = lcd.Hub.Discover();
        Assert.Equal(4, ports.Count);
        lcd.Hub.UpdateScreens(ports.Select(p => new Slv3LcdScreenInfo { Serial = p.Serial }).ToList());

        var state = LianLiFlexRoutes.BuildState(hub, new NexusSettings(), null, null, lcd);
        Assert.True(state.IsConnected);
        Assert.Equal(2, state.ScreenOnlySerials.Length);
        Assert.Equal(new[] { "331964888482C702", "5D30175F64A28700" }, state.ScreenOnlySerials.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(Screens.Select(p => FlexDiscoveryParser.TryParse(p, out var f) ? f.Serial : ""), serial => Assert.True(LianLiFlexRoutes.IsKnownScreen(hub, lcd, serial)));
        Assert.False(LianLiFlexRoutes.IsKnownScreen(hub, lcd, "NOT-A-SCREEN"));

        var screens = new FlexScreens(lcd, hub, new FlexClock().Func);
        var names = new[] { "2B324029848A0703", "47449BCE76D28303", "331964888482C702", "5D30175F64A28700" }.Select(screens.PanelName).ToArray();
        Assert.Equal(new[] { "TL Flex", "TL Flex 2", "TL Flex 3", "TL Flex 4" }, names.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("TL Flex 3", screens.PanelName("331964888482C702"));
        Assert.Equal("TL Flex 4", screens.PanelName("5D30175F64A28700"));
    }

    [Theory]
    [InlineData(43, 3)]
    [InlineData(51, 1)]
    public void The_wireless_structure_and_fan_count_follow_the_chain_length(int fansType, int fans)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString("112233445566"), MasterMac = net.MasterMac, RxType = 1, FanCount = (byte)fans, FansType = (byte)fansType });
        Assert.True(hub.DriveTick());
        var provider = new Slv3LightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        var structure = Assert.Single(provider.GetStructures());
        Assert.Equal(fans, Slv3LightingDeviceProvider.FanCountOf(structure, hub.State.Fans[0]));
        var family = Slv3LightingDeviceProvider.FlexFamilyOf(hub.State.Fans[0])!.Value;
        Assert.Equal(fans * FlexProtocol.LedsPerFan(family), structure.Segments.Sum(s => s.LedCount));
    }

    [Fact]
    public void Scatter_refuses_buffers_that_do_not_match_the_fan_count()
    {
        var family = FlexFamily.SlInfFlex;
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(family);
        var buffers = zones.Select(z => new RgbColor[FlexProtocol.ZoneWireIndexes(family, z.Key).Count]).ToArray();
        Assert.True(Slv3LightingFrameWriter.ScatterFlexZones(family, 1, buffers, new RgbColor[44]));
        Assert.False(Slv3LightingFrameWriter.ScatterFlexZones(family, 3, buffers, new RgbColor[132]));
        Assert.False(Slv3LightingFrameWriter.ScatterFlexZones(family, 1, buffers, new RgbColor[10]));
        Assert.False(Slv3LightingFrameWriter.ScatterFlexZones(family, 1, buffers.Take(2).ToArray(), new RgbColor[44]));
    }

    [Fact]
    public void Receivers_on_a_different_hub_than_the_screens_leave_every_screen_listed_screen_only()
    {
        string Hub(string instance) => instance.Contains("PID_0102") ? "HUB-RX-" + instance[^4..] : "HUB-SCREENS";
        var paths = new[] { Rx1, Rx2 }.Concat(Screens).ToArray();
        var snapshot = FlexDiscoveryParser.Snapshot(paths, Hub);
        Assert.Equal(2, snapshot.Units.Count);
        Assert.All(snapshot.Units, u => Assert.Equal("", u.ScreenSerial));
        Assert.Equal(4, snapshot.ScreenOnly.Count);
    }

    [Fact]
    public void The_topology_line_names_receivers_screens_hubs_pairing_and_screen_only_serials()
    {
        var paths = new[] { Rx1, Rx2 }.Concat(Screens).ToArray();
        var snapshot = FlexDiscoveryParser.Snapshot(paths, ResolvedHub);
        var line = FlexDiscoveryParser.Describe(paths, ResolvedHub, snapshot);
        Assert.Contains("0102:9&aaaa&0&4 hub=HUB-A", line);
        Assert.Contains("A018:2B324029848A0703 hub=HUB-A", line);
        Assert.Contains("paired=[0102->2B324029848A0703; 0102->47449BCE76D28303]", line);
        Assert.Contains("screenOnly=[331964888482C702; 5D30175F64A28700]", line);
        Assert.Contains("hub=?", FlexDiscoveryParser.Describe(paths, _ => "", FlexDiscoveryParser.Snapshot(paths, _ => "")));
    }

    // Records the customer's dongle reported: right after an RF unbind (slot 0, master zero) and after a resume (slot 254).
    [Theory]
    [InlineData("210807F517E10000000000000800017E20D3")]
    [InlineData("210807F517E1000000000000" + "08FE0000231500030000000034343400300000001000A00000000000001C")]
    public void The_customers_unbound_records_read_as_unbound_at_once(string hex)
    {
        var bytes = Convert.FromHexString(hex).ToList();
        while (bytes.Count < Slv3Protocol.RecordLength - 1) bytes.Add(0);
        if (bytes.Count < Slv3Protocol.RecordLength) bytes.Add(Slv3Protocol.RecordValidator);
        Assert.True(Slv3Protocol.TryParseRecord(bytes.ToArray(), 0, out var record));
        Assert.True(Slv3Protocol.MacIsZero(record.MasterMac));
        Assert.False(Slv3Protocol.IsBoundSlot(record.RxType));
        var info = new byte[64];
        info[0] = FlexProtocol.CmdGetInfo;
        Convert.FromHexString("210807F517E1").CopyTo(info, 1);
        info[13] = 8;
        info[14] = record.RxType;
        Assert.False(FlexProtocol.ParseInfo(info)!.RfBound);
    }
}
