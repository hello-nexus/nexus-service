using System.IO;
using Nexus.Service.Peripherals.Hyte;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Drives LinuxSerialDiscovery.FindIn against a fake /dev + /sys/class/tty tree,
/// so VID/PID matching, serial parsing, and the interface→device symlink walk
/// are tested on any OS without real hardware.
/// </summary>
public class LinuxSerialDiscoveryTests
{
    private static string Dev(TempDir t) => t.At("dev");
    private static string SysTty(TempDir t) => t.At("sys/class/tty");

    /// <summary>
    /// /dev/&lt;node&gt; + USB device tree (idVendor/idProduct/serial on the bus dir,
    /// an interface child), with /sys/class/tty/&lt;node&gt;/device → the interface.
    /// </summary>
    private static void AddDevice(TempDir t, string node, string busId, string vid, string pid, string? serial)
    {
        t.Write($"dev/{node}", "");
        var iface = t.Dir($"sys/devices/usb1/{busId}/{busId}:1.0");
        t.Write($"sys/devices/usb1/{busId}/idVendor", vid + "\n");
        t.Write($"sys/devices/usb1/{busId}/idProduct", pid + "\n");
        if (serial is not null)
            t.Write($"sys/devices/usb1/{busId}/serial", serial + "\n");
        t.Symlink($"sys/class/tty/{node}/device", iface);
    }

    [NonWindowsFact]
    public void FindIn_MatchesVidPid_AndParsesSerialFromUsbParent()
    {
        using var t = new TempDir();
        AddDevice(t, "ttyACM0", "1-1", "3402", "0901", "NP50ABC123"); // NP50
        AddDevice(t, "ttyUSB0", "1-2", "1234", "5678", "OTHER");      // unrelated vendor

        var m = Assert.Single(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0901));
        Assert.Equal(Path.Combine(Dev(t), "ttyACM0"), m.PortName);
        Assert.Equal("NP50ABC123", m.Serial);
        Assert.Equal(0x0901, m.ProductId);
    }

    [NonWindowsFact]
    public void FindIn_FiltersByProductId()
    {
        using var t = new TempDir();
        AddDevice(t, "ttyACM0", "1-1", "3402", "0901", "A"); // NP50 PID
        AddDevice(t, "ttyACM1", "1-3", "3402", "0900", "B"); // MiniHub PID (same vendor)

        Assert.Equal(0x0900, Assert.Single(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0900)).ProductId);

        var both = LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0901, 0x0900);
        Assert.Equal(2, both.Count);
    }

    [NonWindowsFact]
    public void FindIn_NoVendorMatch_ReturnsEmpty()
    {
        using var t = new TempDir();
        AddDevice(t, "ttyACM0", "1-1", "3402", "0901", "A");
        Assert.Empty(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x9999, 0x0901));
    }

    [NonWindowsFact]
    public void FindIn_MissingSerial_StillMatchesWithEmptySerial()
    {
        using var t = new TempDir();
        AddDevice(t, "ttyACM0", "1-1", "3402", "0901", null);
        Assert.Equal("", Assert.Single(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0901)).Serial);
    }

    [NonWindowsFact]
    public void FindIn_EmptyDevRoot_ReturnsEmpty()
    {
        using var t = new TempDir();
        t.Dir("dev");
        Assert.Empty(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0901));
    }

    /// <summary>
    /// Real sysfs shape, which <see cref="AddDevice"/> does not reproduce:
    /// <c>/sys/class/tty/&lt;node&gt;</c> is itself a symlink into
    /// <c>/sys/devices/…</c>, and its <c>device</c> entry is a RELATIVE link.
    /// Resolving that target against the literal class path lands outside the
    /// device tree and drops every port.
    /// </summary>
    [NonWindowsFact]
    public void FindIn_RelativeDeviceLink_UnderSymlinkedClassEntry_StillMatches()
    {
        using var t = new TempDir();
        t.Write("dev/ttyACM0", "");
        t.Write("sys/devices/usb1/1-13/idVendor", "3402\n");
        t.Write("sys/devices/usb1/1-13/idProduct", "0400\n");
        t.Write("sys/devices/usb1/1-13/serial", "Q60SERIAL\n");
        t.Dir("sys/devices/usb1/1-13/1-13:1.0");
        var ttyNode = t.Dir("sys/devices/usb1/1-13/1-13:1.0/tty/ttyACM0");
        t.Symlink("sys/devices/usb1/1-13/1-13:1.0/tty/ttyACM0/device", "../../../1-13:1.0");
        t.Symlink("sys/class/tty/ttyACM0", ttyNode);

        var m = Assert.Single(LinuxSerialDiscovery.FindIn(Dev(t), SysTty(t), 0x3402, 0x0400));
        Assert.Equal("Q60SERIAL", m.Serial);
        Assert.Equal(0x0400, m.ProductId);
    }
}
