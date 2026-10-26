using System;
using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Peripherals.LinuxUsb;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform.Linux;
using Nexus.Service.Platform.Linux.Render;
using Xunit;

namespace Nexus.Service.Tests.LinuxUsb;

public sealed class LinuxUsbSysfsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-sysfs-" + Guid.NewGuid().ToString("N"));

    public LinuxUsbSysfsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AddDevice(string name, string vid, string pid, int bus, int dev, string? serial = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(dir, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(dir, "devnum"), dev + "\n");
        if (serial is not null)
        {
            File.WriteAllText(Path.Combine(dir, "serial"), serial + "\n");
        }
        return dir;
    }

    [Fact]
    public void Find_MatchesVidPidAndBuildsDevNode()
    {
        AddDevice("1-2", "1cbe", "0006", 1, 7, "ABC");
        AddDevice("1-3", "1cbe", "9999", 1, 8);
        AddDevice("1-4", "046d", "0006", 1, 9);

        var nodes = LinuxUsbSysfs.Find(_root, 0x1CBE, new[] { 0x0006 });

        var node = Assert.Single(nodes);
        Assert.Equal("/dev/bus/usb/001/007", node.DevNode);
        Assert.Equal("ABC", node.Serial);
    }

    [Fact]
    public void Find_MissingRootIsEmpty()
    {
        Assert.Empty(LinuxUsbSysfs.Find(Path.Combine(_root, "absent"), 0x1CBE, new[] { 0x0006 }));
    }

    [NonWindowsFact]
    public void FindInterfaceWithEndpoint_ReturnsInterfaceNumber()
    {
        var dir = AddDevice("1-2", "1cbe", "a088", 1, 7);
        var iface = Path.Combine(dir, "1-2:1.1");
        Directory.CreateDirectory(Path.Combine(iface, "ep_02"));
        File.WriteAllText(Path.Combine(iface, "bInterfaceNumber"), "01\n");

        Assert.Equal(1, LinuxUsbSysfs.FindInterfaceWithEndpoint(_root, "1-2", 0x02));
        Assert.Null(LinuxUsbSysfs.FindInterfaceWithEndpoint(_root, "1-2", 0x05));
    }

    [Fact]
    public void Slv3LcdDiscovery_FallsBackToSysNameWithoutSerial()
    {
        AddDevice("1-2", "1cbe", "0006", 2, 11);
        AddDevice("1-3", "1cbe", "0006", 2, 12, "SER");

        var ports = new LinuxSlv3LcdDiscovery(_root).Discover();

        Assert.Equal(2, ports.Count);
        Assert.Contains(ports, p => p.Serial == "1-2" && p.PortName == "/dev/bus/usb/002/011");
        Assert.Contains(ports, p => p.Serial == "SER");
    }

    [Fact]
    public void BulkFactory_TargetsDefaultToInterfaceZero()
    {
        AddDevice("1-2", "1cbe", "a088", 1, 7);

        var targets = new LinuxBulkUsbPipeFactory(_root).Targets(0x1CBE, 0xA088, 0x01);

        Assert.Equal(("/dev/bus/usb/001/007", 0), Assert.Single(targets));
    }

    [Fact]
    public void HeadlessBrowserArguments_UsePipeAndProfileDir()
    {
        var args = HeadlessBrowser.Arguments("/tmp/p");

        Assert.Contains("--remote-debugging-pipe", args);
        Assert.Contains("--user-data-dir=/tmp/p", args);
        Assert.DoesNotContain("--no-sandbox", args);
        Assert.Equal("about:blank", args.Last());
    }

    [Theory]
    [InlineData(101, 1.0, 100)]
    [InlineData(101, 1.5, 152)]
    [InlineData(480, 1.0, 480)]
    [InlineData(1, 1.0, 2)]
    public void RenderSessionPixels_RoundDownLikeTheService(int css, double dpr, int expected)
    {
        var session = new PanelRenderSession(null!, "s", "http://x", css, css, dpr, 30);

        Assert.Equal(expected, session.PixelWidth);
        Assert.Equal(StreamedPanelProfile.FramePixels(css, dpr), session.PixelHeight);
    }

    [Theory]
    [InlineData(0x1CBE, 0xA088, true)]
    [InlineData(0x1CBE, 0xA034, false)]
    [InlineData(0x0416, 0xA088, false)]
    [InlineData(0x0416, 0x5302, false)]
    public void BulkFactory_OnlyClaimsTheUniversalScreen(int vid, int pid, bool expected)
    {
        Assert.Equal(expected, LinuxBulkUsbPipeFactory.Supports(vid, pid));
    }

    [NonWindowsTheory]
    [InlineData(null, "/home/u", "/root", "/root/nexus-panel-render")]
    [InlineData(1000u, "/home/u", "/root", "/home/u/nexus-panel-render")]
    [InlineData(1000u, null, "/root", null)]
    [InlineData(null, null, "", null)]
    public void RenderProfileDir_UsesTheAdoptedSessionHome(uint? uid, string? sessionHome, string userProfile, string? expected)
    {
        Assert.Equal(expected, LinuxStreamRenderHost.ProfileDir(uid, sessionHome, userProfile));
    }
}
