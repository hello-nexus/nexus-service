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

    private void AddInterface(string device, int number, string cls, bool endpoint02, bool hidChild = false)
    {
        var dir = Path.Combine(_root, device, $"{device}:1.{number}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bInterfaceNumber"), $"{number:x2}\n");
        File.WriteAllText(Path.Combine(dir, "bInterfaceClass"), cls + "\n");
        if (endpoint02)
        {
            Directory.CreateDirectory(Path.Combine(dir, "ep_02"));
        }
        if (hidChild)
        {
            Directory.CreateDirectory(Path.Combine(dir, "0003:1E71:3012.0001"));
        }
    }

    [Fact]
    public void Candidates_OnlyTheSerialMatchWhenOneExists()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, "ff", true);
        AddDevice("1-3", "1e71", "3012", 1, 6, "BBB");
        AddInterface("1-3", 0, "ff", true);
        AddDevice("1-4", "046d", "3012", 1, 7, "BBB");

        var nodes = new LinuxKrakenLcdTransportFactory(_root).Candidates("bbb");

        Assert.Equal(new[] { ("/dev/bus/usb/001/006", 0) }, nodes);
    }

    [Fact]
    public void Candidates_FallsBackToEveryKrakenWhenNoSerialMatches()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, "ff", true);

        Assert.Single(new LinuxKrakenLcdTransportFactory(_root).Candidates("ZZZ"));
        Assert.Single(new LinuxKrakenLcdTransportFactory(_root).Candidates(null));
    }

    [Fact]
    public void Candidates_UsesTheInterfaceThatOwnsEndpoint02()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, "03", false);
        AddInterface("1-2", 1, "ff", true);

        var target = Assert.Single(new LinuxKrakenLcdTransportFactory(_root).Candidates("AAA"));

        Assert.Equal(1, target.Interface);
    }

    [Fact]
    public void Candidates_SkipsDeviceWithoutEndpoint02()
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, "ff", false);

        Assert.Empty(new LinuxKrakenLcdTransportFactory(_root).Candidates("AAA"));
    }

    [Theory]
    [InlineData("03", false)]
    [InlineData("ff", true)]
    public void Candidates_RefusesHidClassInterface(string cls, bool hidChild)
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, cls, true, hidChild);

        Assert.Empty(new LinuxKrakenLcdTransportFactory(_root).Candidates("AAA"));
    }

    [Theory]
    [InlineData("03", true)]
    [InlineData("ff", false)]
    public void Candidates_SerialMatchWithRefusedInterfaceDoesNotFallBack(string cls, bool endpoint02)
    {
        AddDevice("1-2", "1e71", "3012", 1, 5, "AAA");
        AddInterface("1-2", 0, cls, endpoint02);
        AddDevice("1-3", "1e71", "3012", 1, 6, "BBB");
        AddInterface("1-3", 0, "ff", true);

        var factory = new LinuxKrakenLcdTransportFactory(_root);

        Assert.Empty(factory.Candidates("AAA"));
        Assert.Null(factory.Open("AAA"));
    }

    [Fact]
    public void Open_ReturnsNullWhenNoKrakenIsPresent()
    {
        Assert.Null(new LinuxKrakenLcdTransportFactory(_root).Open("AAA"));
    }
}
