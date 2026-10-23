using System;
using System.IO;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests.Kraken;

public sealed class LinuxKrakenLcdTransportFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-kraken-sysfs-" + Guid.NewGuid().ToString("N"));

    public LinuxKrakenLcdTransportFactoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddDevice(string name, string vid, string pid, int bus, int dev, string serial)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(dir, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(dir, "devnum"), dev + "\n");
        File.WriteAllText(Path.Combine(dir, "serial"), serial + "\n");
    }

    [Fact]
    public void Candidates_PutsTheSerialMatchFirstAndSkipsOtherDevices()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddDevice("1-3", "1e71", "3012", 1, 6, "BBB");
        AddDevice("1-4", "046d", "3012", 1, 7, "BBB");

        var nodes = new LinuxKrakenLcdTransportFactory(_root).Candidates("bbb");

        Assert.Equal(new[] { "/dev/bus/usb/001/006", "/dev/bus/usb/001/005" }, nodes);
    }

    [Fact]
    public void Candidates_WithoutSerialReturnsEveryKraken()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");

        Assert.Single(new LinuxKrakenLcdTransportFactory(_root).Candidates(null));
    }

    [Fact]
    public void Open_ReturnsNullWhenNoKrakenIsPresent()
    {
        Assert.Null(new LinuxKrakenLcdTransportFactory(_root).Open("AAA"));
    }

    [Fact]
    public void BulkTargetIsInterfaceZeroNeverTheHidInterface()
    {
        Assert.Equal(0, LinuxKrakenLcdTransport.BulkInterface);
        Assert.Equal(0x02, LinuxKrakenLcdTransport.BulkOutEndpoint);
    }
}
