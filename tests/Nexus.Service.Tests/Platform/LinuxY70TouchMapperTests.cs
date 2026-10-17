using System.Collections.Generic;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Platform;

/// <summary>
/// Device selection and apply for Y70 touch mapping. Fixtures follow the
/// <c>busctl --user</c> output captured from KWin 6.7 on the Bazzite Y70 box.
/// </summary>
public class LinuxY70TouchMapperTests
{
    private const string Tree = """
/org/kde/KWin/InputDevice
/org/kde/KWin/InputDevice/event3
/org/kde/KWin/InputDevice/event7
/org/kde/KWin/InputDevice/event8
""";

    private sealed class FakeBus
    {
        public readonly Dictionary<string, (string Name, int Vendor, int Product, bool Touch, string Output)> Devices = new();
        public readonly List<string> Calls = new();
        public bool RejectSet;
        public int TreeExit;

        public (int Exit, string Output) Run(string[] args)
        {
            Calls.Add(string.Join(' ', args));
            if (args[0] == "tree")
            {
                return (TreeExit, TreeExit == 0 ? Tree : "");
            }
            var path = args[2];
            var d = Devices[path];
            if (args[0] == "set-property")
            {
                if (RejectSet)
                {
                    return (1, "denied");
                }
                Devices[path] = d with { Output = args[6] };
                return (0, "");
            }
            return args.Length == 5
                ? (0, $"s \"{d.Output}\"\n")
                : (0, $"s \"{d.Name}\"\nu {d.Vendor}\nu {d.Product}\nb {(d.Touch ? "true" : "false")}\ns \"{d.Output}\"\n");
        }
    }

    private static FakeBus Box()
    {
        var bus = new FakeBus();
        bus.Devices["/org/kde/KWin/InputDevice/event3"] = ("Brio 301", 1133, 2373, false, "");
        bus.Devices["/org/kde/KWin/InputDevice/event7"] = ("wch.cn TouchScreen", 0x27c0, 0x0859, true, "");
        bus.Devices["/org/kde/KWin/InputDevice/event8"] = ("wch.cn TouchScreen", 0x27c0, 0x0859, false, "");
        return bus;
    }

    [Fact]
    public void ParseDevicePaths_keeps_only_event_nodes()
    {
        Assert.Equal(
            new[] { "/org/kde/KWin/InputDevice/event3", "/org/kde/KWin/InputDevice/event7", "/org/kde/KWin/InputDevice/event8" },
            LinuxY70TouchMapper.ParseDevicePaths(Tree));
    }

    [Fact]
    public void ParseDevice_reads_the_five_properties()
    {
        var d = LinuxY70TouchMapper.ParseDevice("s \"wch.cn TouchScreen\"\nu 10176\nu 2137\nb true\ns \"\"\n");
        Assert.Equal(new LinuxY70TouchMapper.InputDeviceInfo("wch.cn TouchScreen", 0x27c0, 0x0859, true, ""), d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("s \"x\"\nu 1\n")]
    [InlineData("s \"x\"\nu a\nu 2\nb true\ns \"\"\n")]
    public void ParseDevice_rejects_malformed_output(string text)
    {
        Assert.Null(LinuxY70TouchMapper.ParseDevice(text));
    }

    [Theory]
    [InlineData(0x27c0, 0x0859, true, true)]
    [InlineData(0x222a, 0x0001, true, true)]
    [InlineData(0x27c0, 0x0859, false, false)]
    [InlineData(1133, 2373, true, false)]
    public void IsY70Touch_requires_touch_capability_and_a_known_digitizer(int vendor, int product, bool touch, bool expected)
    {
        Assert.Equal(expected, LinuxY70TouchMapper.IsY70Touch(new("d", vendor, product, touch, "")));
    }

    [Fact]
    public void Apply_maps_only_the_touch_node_to_the_connector()
    {
        var bus = Box();

        var (ok, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run);

        Assert.True(ok, detail);
        Assert.Equal("DP-1", bus.Devices["/org/kde/KWin/InputDevice/event7"].Output);
        Assert.Equal("", bus.Devices["/org/kde/KWin/InputDevice/event8"].Output);
        Assert.Equal("", bus.Devices["/org/kde/KWin/InputDevice/event3"].Output);
    }

    [Fact]
    public void Apply_is_idempotent_and_writes_nothing_when_already_mapped()
    {
        var bus = Box();
        LinuxY70TouchMapper.Apply("DP-1", bus.Run);
        bus.Calls.Clear();

        Assert.True(LinuxY70TouchMapper.Apply("DP-1", bus.Run).Ok);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("set-property"));
    }

    [Fact]
    public void Apply_remaps_when_the_connector_changes()
    {
        var bus = Box();
        LinuxY70TouchMapper.Apply("DP-1", bus.Run);

        Assert.True(LinuxY70TouchMapper.Apply("DP-2", bus.Run).Ok);
        Assert.Equal("DP-2", bus.Devices["/org/kde/KWin/InputDevice/event7"].Output);
    }

    [Fact]
    public void Apply_fails_with_a_reason_when_no_digitizer_is_present()
    {
        var bus = Box();
        bus.Devices.Remove("/org/kde/KWin/InputDevice/event7");
        bus.Devices["/org/kde/KWin/InputDevice/event7"] = ("Other", 1, 2, true, "");

        var (ok, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run);

        Assert.False(ok);
        Assert.Contains("no Y70 touch digitizer", detail);
    }

    [Fact]
    public void Apply_fails_when_kwin_rejects_the_write()
    {
        var bus = Box();
        bus.RejectSet = true;

        var (ok, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run);

        Assert.False(ok);
        Assert.Contains("denied", detail);
    }

    [Fact]
    public void Apply_fails_with_a_kde_reason_when_the_session_bus_has_no_kwin()
    {
        var bus = Box();
        bus.TreeExit = 1;

        var (ok, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run);

        Assert.False(ok);
        Assert.Contains("KDE Plasma", detail);
        Assert.Single(bus.Calls);
    }

    [Fact]
    public void SetY70Orientation_maps_touch_even_when_the_rotation_is_already_right()
    {
        var bus = Box();
        var provider = new LinuxDisplayOrientationProvider(
            () => "DP-1",
            _ => (0, "Output: 2 DP-1 u\n\tRotation: 8\n"),
            bus.Run);

        provider.SetY70Orientation("PortraitFlipped");

        Assert.Equal("DP-1", bus.Devices["/org/kde/KWin/InputDevice/event7"].Output);
    }
}
