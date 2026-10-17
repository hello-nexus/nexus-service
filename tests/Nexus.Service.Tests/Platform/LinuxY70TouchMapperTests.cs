using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    private static IReadOnlyList<string> NoCompanions(string sysName) => Array.Empty<string>();

    private sealed class FakeBus
    {
        public readonly Dictionary<string, (string Name, int Vendor, int Product, bool Touch, string Output)> Devices = new();
        public readonly List<string> Calls = new();
        public bool RejectSet;
        public string ExtraTree = "";
        public readonly HashSet<string> Unreadable = new();
        public int TreeExit;

        public (int Exit, string Output) Run(string[] args)
        {
            Calls.Add(string.Join(' ', args));
            if (args[0] == "tree")
            {
                return (TreeExit, TreeExit == 0 ? Tree + ExtraTree : "");
            }
            var path = args[2];
            if (Unreadable.Contains(path))
            {
                return (1, "unreadable");
            }
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
        Assert.Equal(expected, LinuxY70TouchMapper.IsY70Touch(new("d", vendor, product, touch, ""), () => NoCompanions("event7")));
    }

    [Fact]
    public void Apply_maps_only_the_touch_node_to_the_connector()
    {
        var bus = Box();

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Mapped, status);
        Assert.Equal("DP-1", bus.Devices["/org/kde/KWin/InputDevice/event7"].Output);
        Assert.Equal("", bus.Devices["/org/kde/KWin/InputDevice/event8"].Output);
        Assert.Equal("", bus.Devices["/org/kde/KWin/InputDevice/event3"].Output);
    }

    [Fact]
    public void Apply_is_idempotent_and_writes_nothing_when_already_mapped()
    {
        var bus = Box();
        LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);
        bus.Calls.Clear();

        Assert.True(LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions).Ok);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("set-property"));
    }

    [Fact]
    public void Apply_remaps_when_the_connector_changes()
    {
        var bus = Box();
        LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.True(LinuxY70TouchMapper.Apply("DP-2", bus.Run, NoCompanions).Ok);
        Assert.Equal("DP-2", bus.Devices["/org/kde/KWin/InputDevice/event7"].Output);
    }

    [Fact]
    public void Apply_fails_with_a_reason_when_no_digitizer_is_present()
    {
        var bus = Box();
        bus.Devices.Remove("/org/kde/KWin/InputDevice/event7");
        bus.Devices["/org/kde/KWin/InputDevice/event7"] = ("Other", 1, 2, true, "");

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Final, status);
        Assert.Contains("no Y70 touch digitizer", detail);
    }

    [Fact]
    public void Apply_fails_when_kwin_rejects_the_write()
    {
        var bus = Box();
        bus.RejectSet = true;

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Retry, status);
        Assert.Contains("denied", detail);
    }

    [Fact]
    public void Apply_fails_with_a_kde_reason_when_the_session_bus_has_no_kwin()
    {
        var bus = Box();
        bus.TreeExit = 1;

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Retry, status);
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

        Assert.True(WaitFor(() => bus.Devices["/org/kde/KWin/InputDevice/event7"].Output == "DP-1"));
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void Apply_skips_as_ambiguous_when_two_touch_nodes_match()
    {
        var bus = Box();
        bus.ExtraTree = "\n/org/kde/KWin/InputDevice/event9";
        bus.Devices["/org/kde/KWin/InputDevice/event9"] = ("wch.cn TouchScreen", 0x27c0, 0x0859, true, "");

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Final, status);
        Assert.Contains("ambiguous", detail);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("set-property"));
    }

    [Fact]
    public void Apply_ignores_a_digitizer_whose_hub_carries_the_xeneon_edge_endpoint()
    {
        var bus = Box();

        var (status, _) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, _ => new[] { "VID_1B1C&PID_1D0D" });

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Final, status);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("set-property"));
    }

    [Theory]
    [InlineData(-1, "", 2)]
    [InlineData(1, "Failed to introspect object / of service org.kde.KWin: The name is not activatable", 2)]
    [InlineData(1, "Failed to connect to bus", 1)]
    [InlineData(-2, "", 1)]
    [InlineData(-3, "no graphical session yet", 1)]
    [InlineData(-4, "no Wayland session", 1)]
    public void Apply_classifies_bus_failures_as_final_or_retryable(int exit, string output, int expected)
    {
        var result = LinuxY70TouchMapper.Apply("DP-1", _ => (exit, output), NoCompanions);

        Assert.Equal((LinuxY70TouchMapper.TouchMapStatus)expected, result.Status);
    }

    [Fact]
    public void Apply_keeps_going_to_report_an_unreadable_node_as_retryable()
    {
        var bus = Box();
        bus.Devices.Remove("/org/kde/KWin/InputDevice/event7");
        bus.Unreadable.Add("/org/kde/KWin/InputDevice/event7");

        var (status, detail) = LinuxY70TouchMapper.Apply("DP-1", bus.Run, NoCompanions);

        Assert.Equal(LinuxY70TouchMapper.TouchMapStatus.Retry, status);
        Assert.Contains("unreadable", detail);
    }

    [Fact]
    public void ReadCompanions_lists_the_other_usb_devices_under_the_same_hub()
    {
        var root = Path.Combine(Path.GetTempPath(), "sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = Path.Combine(root, "devices", "1-2");
            var digitizer = Path.Combine(hub, "1-2.1");
            var sibling = Path.Combine(hub, "1-2.3");
            var node = Path.Combine(digitizer, "1-2.1:1.0", "input", "input9", "event7");
            Directory.CreateDirectory(node);
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(digitizer, "idVendor"), "27c0\n");
            File.WriteAllText(Path.Combine(digitizer, "idProduct"), "0859\n");
            File.WriteAllText(Path.Combine(sibling, "idVendor"), "1b1c\n");
            File.WriteAllText(Path.Combine(sibling, "idProduct"), "1d0d\n");
            Directory.CreateDirectory(Path.Combine(root, "class", "input"));
            // Real sysfs links are relative to the link's own directory.
            Directory.CreateSymbolicLink(Path.Combine(root, "class", "input", "event7"), Path.GetRelativePath(Path.Combine(root, "class", "input"), node));

            Assert.Equal(new[] { "VID_1B1C&PID_1D0D" }, LinuxY70TouchMapper.ReadCompanions("event7", root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReadCompanions_is_empty_for_a_node_without_a_usb_ancestor()
    {
        Assert.Empty(LinuxY70TouchMapper.ReadCompanions("event404", Path.GetTempPath()));
    }

    private static int CountSets(FakeBus bus) => bus.Calls.FindAll(c => c.StartsWith("set-property")).Count;

    [Fact]
    public void Final_verdict_is_not_retried()
    {
        var bus = Box();
        bus.Devices["/org/kde/KWin/InputDevice/event7"] = ("Other", 1, 2, true, "");
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", _ => (0, ""), bus.Run, NoCompanions);

        provider.MapTouch("DP-1");

        Assert.False(provider.TouchRetryPending);
    }

    [Fact]
    public void Session_not_ready_is_retried()
    {
        var bus = Box();
        bus.TreeExit = 1;
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", _ => (0, ""), bus.Run, NoCompanions);

        provider.MapTouch("DP-1");

        Assert.True(provider.TouchRetryPending);
    }

    [Fact]
    public void SnapshotDigitizers_lists_only_known_digitizer_nodes()
    {
        var root = Path.Combine(Path.GetTempPath(), "sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Node(string name, string vid, string pid)
            {
                var id = Path.Combine(root, "class", "input", name, "device", "id");
                Directory.CreateDirectory(id);
                File.WriteAllText(Path.Combine(id, "vendor"), vid + "\n");
                File.WriteAllText(Path.Combine(id, "product"), pid + "\n");
            }
            Node("event7", "27c0", "0859");
            Node("event3", "046d", "0945");
            Node("event12", "222a", "0001");

            Assert.Equal("event12,event7", LinuxY70TouchMapper.SnapshotDigitizers(root));
            Assert.Equal("", LinuxY70TouchMapper.SnapshotDigitizers(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Digitizer_set_change_remaps_a_replugged_digitizer()
    {
        var bus = Box();
        var snapshot = "event7";
        var provider = new LinuxDisplayOrientationProvider(
            () => "DP-1", _ => (0, "Output: 2 DP-1 u\n\tRotation: 8\n"), bus.Run, NoCompanions, () => snapshot)
        { TopologyDebounceMs = 20 };
        provider.OnDigitizerPoll();
        provider.MapTouch("DP-1");
        // Unchanged set: no work.
        provider.OnDigitizerPoll();
        Assert.Equal(1, CountSets(bus));

        // Replug: KWin recreates the node with no mapping and the node number changes.
        bus.Devices["/org/kde/KWin/InputDevice/event7"] = ("wch.cn TouchScreen", 0x27c0, 0x0859, true, "");
        snapshot = "event11";
        provider.OnDigitizerPoll();
        provider.OnDigitizerPoll();

        Assert.True(WaitFor(() => bus.Devices["/org/kde/KWin/InputDevice/event7"].Output == "DP-1"));
        Assert.Equal(2, CountSets(bus));
    }
}
