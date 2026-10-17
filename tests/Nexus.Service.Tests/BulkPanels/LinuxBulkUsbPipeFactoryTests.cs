using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class LinuxBulkUsbPipeFactoryTests
{
    private static void WriteDevice(string root, string name, string vid, string pid, int bus, int dev)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(dir, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(dir, "devnum"), dev + "\n");
    }

    private static void WriteInterface(string root, string device, int config, int number, params string[] endpoints)
    {
        var dir = Path.Combine(root, device, $"{device}:{config}.{number}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bInterfaceNumber"), $"{number:x2}\n");
        foreach (var ep in endpoints)
        {
            Directory.CreateDirectory(Path.Combine(dir, ep));
        }
    }

    [Fact]
    public void Picks_the_interface_that_owns_the_write_endpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nexus-bulk-test-{Path.GetRandomFileName()}");
        try
        {
            WriteDevice(root, "1-3", "1cbe", "a088", 1, 12);
            WriteInterface(root, "1-3", 1, 0, "ep_83");
            WriteInterface(root, "1-3", 1, 1, "ep_01", "ep_81");
            // The bezel LEDs and an unrelated device must not match.
            WriteDevice(root, "1-4", "0416", "8050", 1, 13);
            WriteInterface(root, "1-4", 1, 0, "ep_01", "ep_81");
            WriteDevice(root, "usb1", "1cbe", "a088", 1, 1);

            var found = LinuxBulkUsbPipeFactory.FindInterfaces(root, 0x1CBE, 0xA088, 0x01).ToList();

            var match = Assert.Single(found);
            Assert.Equal("/dev/bus/usb/001/012", match.Path);
            Assert.Equal(1u, match.Interface);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Finds_nothing_when_the_device_or_endpoint_is_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nexus-bulk-test-{Path.GetRandomFileName()}");
        try
        {
            WriteDevice(root, "1-3", "1cbe", "a088", 1, 12);
            WriteInterface(root, "1-3", 1, 0, "ep_02");

            Assert.Empty(LinuxBulkUsbPipeFactory.FindInterfaces(root, 0x1CBE, 0xA088, 0x01));
            Assert.Empty(LinuxBulkUsbPipeFactory.FindInterfaces(root, 0xFFFF, 0xFFFF, 0x01));
            Assert.Empty(LinuxBulkUsbPipeFactory.FindInterfaces(Path.Combine(root, "missing"), 0x1CBE, 0xA088, 0x01));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Open_returns_null_when_no_device_matches()
    {
        Assert.Null(new LinuxBulkUsbPipeFactory().Open(0xFFFF, 0xFFFF, 0x01, 0x81));
    }
}
