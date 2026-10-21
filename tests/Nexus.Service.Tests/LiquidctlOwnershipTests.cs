using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

public class LiquidctlOwnershipTests
{
    private const string ListJson = """
    [
      {"bus":"hid","address":"/dev/hidraw14","description":"Corsair Commander ST (broken)","vendor_id":6940,"product_id":3122},
      {"bus":"hid","address":"/dev/hidraw3","description":"NZXT Kraken X53","vendor_id":7793,"product_id":8199}
    ]
    """;

    private static string StatusFor(string address, string desc) => $$"""
    [{"bus":"hid","address":"{{address}}","description":"{{desc}}","status":[{"key":"Pump speed","value":1800,"unit":"rpm"}]}]
    """;

    private static LinuxLiquidctlProvider Make(Func<OpenRgbOwnership> ownership, List<string> statusCalls)
        => new(() => ListJson, e =>
        {
            statusCalls.Add(string.Join(' ', LiquidctlOwnership.StatusArgs(e)));
            return StatusFor(e.Address, e.Description);
        }, (_, _, _) => true, null, ownership);

    private static OpenRgbOwnership Known(params string[] locations)
        => OpenRgbOwnership.FromView(true, true, locations);

    [Theory]
    [InlineData("HID: /dev/hidraw14", "hidraw14")]
    [InlineData("/dev/hidraw14", "hidraw14")]
    [InlineData("HID: /dev/HIDRAW7", "hidraw7")]
    [InlineData("/dev/hidraw", null)]
    [InlineData("I2C: /dev/i2c-3", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeHidraw_ExtractsNode(string? raw, string? expected)
        => Assert.Equal(expected, LiquidctlOwnership.NormalizeHidraw(raw));

    [Fact]
    public void Hidraw14_DoesNotMatchHidraw1()
    {
        var entry = new LiquidctlListEntry("/dev/hidraw1", "hid", null, null, "x");
        Assert.Equal(LiquidctlSkip.None, LiquidctlOwnership.Decide(entry, Known("HID: /dev/hidraw14")));
    }

    [Fact]
    public void Known_SkipsOnlyOwnedDevice()
    {
        var calls = new List<string>();
        var p = Make(() => Known("HID: /dev/hidraw14"), calls);
        var comps = p.GetAll();
        Assert.Single(comps);
        Assert.Contains("hidraw3", comps[0].Id);
        Assert.Single(calls);
        Assert.Equal("--json --address /dev/hidraw3 status", calls[0]);
    }

    [Fact]
    public void OpenRgbOff_PollsEverything()
    {
        var calls = new List<string>();
        var p = Make(() => OpenRgbOwnership.Off, calls);
        Assert.Equal(2, p.GetAll().Count);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void Unknown_BlocksOnlyCommanderCoreFamily()
    {
        var calls = new List<string>();
        var p = Make(() => OpenRgbOwnership.FromView(true, false, new string[0]), calls);
        var comps = p.GetAll();
        Assert.Single(comps);
        Assert.Contains("hidraw3", comps[0].Id);
    }

    [Theory]
    [InlineData(0x1B1C, 0x0C1C)]
    [InlineData(0x1B1C, 0x0C2A)]
    [InlineData(0x1B1C, 0x0C32)]
    public void CommanderCoreIds_AreFamily(int vid, int pid)
        => Assert.True(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", "hid", vid, pid, "")));

    [Fact]
    public void OwnershipChanges_AreReevaluatedWithoutRestart()
    {
        var state = OpenRgbOwnership.Off;
        var calls = new List<string>();
        var p = Make(() => state, calls);
        Assert.Equal(2, p.GetAll().Count);

        state = Known("HID: /dev/hidraw14");
        System.Threading.Thread.Sleep(1600); // outlasts the snapshot ttl
        Assert.Single(p.GetAll());

        state = Known();
        System.Threading.Thread.Sleep(1600);
        Assert.Equal(2, p.GetAll().Count);
    }

    [Fact]
    public void StatusArgs_PinsBusOnlyForNonPathAddresses()
    {
        Assert.Equal(new[] { "--json", "--address", "/dev/hidraw3", "status" },
            LiquidctlOwnership.StatusArgs(new LiquidctlListEntry("/dev/hidraw3", "hid", null, null, "")));
        Assert.Equal(new[] { "--json", "--bus", "usb", "--address", "1:4", "status" },
            LiquidctlOwnership.StatusArgs(new LiquidctlListEntry("1:4", "usb", null, null, "")));
    }

    [Fact]
    public void ParseList_ReadsIdsAsNumbersOrHexStrings()
    {
        var l = LiquidctlOwnership.ParseList("""[{"address":"/dev/hidraw1","vendor_id":"0x1b1c","product_id":3122}]""");
        Assert.Equal(0x1B1C, l.Single().VendorId);
        Assert.Equal(0x0C32, l.Single().ProductId);
    }
}
