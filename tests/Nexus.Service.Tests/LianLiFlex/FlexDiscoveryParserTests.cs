using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexDiscoveryParserTests
{
    private const string Guid = "#{1d4b2365-4749-48ea-b38a-7c6fdddd7e26}";
    private const string TlRx = @"\\?\usb#vid_43a8&pid_0102#9&14888ba7&0&4" + Guid;
    private const string InfRx = @"\\?\usb#vid_43a8&pid_0104#9&10ead5a3&0&4" + Guid;
    private const string TlScreen = @"\\?\usb#vid_1cbe&pid_a018#5C00C251854D2B79" + Guid;
    private const string InfScreen = @"\\?\usb#vid_1cbe&pid_a019#7810C01384CE1788" + Guid;
    private const string Dongle = @"\\?\usb#vid_0416&pid_8040#0123456789" + Guid;
    private const string Hub = @"\\?\usb#vid_1a86&pid_8091#5&abc&0&1" + Guid;

    private static string HubOf(string instance) => instance switch
    {
        @"USB\VID_43A8&PID_0102\9&14888ba7&0&4" => "HUB-A",
        @"USB\VID_1CBE&PID_A018\5C00C251854D2B79" => "HUB-A",
        @"USB\VID_43A8&PID_0104\9&10ead5a3&0&4" => "HUB-B",
        @"USB\VID_1CBE&PID_A019\7810C01384CE1788" => "HUB-B",
        _ => "",
    };

    [Fact]
    public void Pair_keeps_only_the_four_flex_devices_and_pairs_by_parent_hub()
    {
        var units = FlexDiscoveryParser.Pair(new[] { Dongle, InfScreen, TlRx, Hub, InfRx, TlScreen }, HubOf);
        Assert.Equal(2, units.Count);
        var tl = units.Single(u => u.Family == FlexFamily.TlFlex);
        Assert.Equal(TlScreen, tl.ScreenPath);
        Assert.Equal("5C00C251854D2B79", tl.ScreenSerial);
        var inf = units.Single(u => u.Family == FlexFamily.SlInfFlex);
        Assert.Equal(InfScreen, inf.ScreenPath);
        Assert.Equal("HUB-B", inf.HubId);
    }

    [Fact]
    public void Pair_falls_back_to_family_when_the_hub_is_unknown()
    {
        var units = FlexDiscoveryParser.Pair(new[] { TlRx, TlScreen }, _ => "");
        Assert.Equal(TlScreen, Assert.Single(units).ScreenPath);
    }

    [Fact]
    public void Pair_does_not_guess_between_two_units_of_one_family_without_a_hub()
    {
        var second = @"\\?\usb#vid_43a8&pid_0102#9&other&0&2" + Guid;
        var units = FlexDiscoveryParser.Pair(new[] { TlRx, second, TlScreen }, _ => "");
        Assert.All(units, u => Assert.Equal("", u.ScreenPath));
    }

    [Fact]
    public void Pair_never_pairs_across_families()
    {
        var units = FlexDiscoveryParser.Pair(new[] { TlRx, InfScreen }, _ => "");
        Assert.Equal("", Assert.Single(units).ScreenPath);
    }

    [Fact]
    public void The_dongle_screen_and_unrelated_paths_are_ignored()
    {
        Assert.Empty(FlexDiscoveryParser.Pair(new[] { Dongle, Hub, "garbage" }, _ => ""));
    }

    [Fact]
    public void ScreenOnly_lists_screens_whose_fan_has_no_receiver()
    {
        var screens = FlexDiscoveryParser.Snapshot(new[] { Dongle, InfScreen, TlScreen, Hub }, HubOf).ScreenOnly;
        Assert.Equal(2, screens.Count);
        Assert.All(screens, s => Assert.Equal("", s.ReceiverPath));
        Assert.Equal("5C00C251854D2B79", screens.Single(s => s.Family == FlexFamily.TlFlex).ScreenSerial);
        Assert.Equal(FlexProtocol.SlInfScreenProductId, screens.Single(s => s.ScreenPid == FlexProtocol.SlInfScreenProductId).ScreenPid);
    }

    [Fact]
    public void ScreenOnly_skips_screens_a_receiver_already_owns()
    {
        var screens = FlexDiscoveryParser.Snapshot(new[] { TlRx, TlScreen, InfScreen }, HubOf).ScreenOnly;
        Assert.Equal(InfScreen, Assert.Single(screens).ScreenPath);
    }
}
