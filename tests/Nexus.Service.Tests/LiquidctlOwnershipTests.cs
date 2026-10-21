using System;
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
    {"bus":"hid","address":"{{address}}","description":"{{desc}}","status":[{"key":"Pump speed","value":1800,"unit":"rpm"}]}
    """;

    private static readonly string StatusAll = "[" + StatusFor("/dev/hidraw14", "Corsair Commander ST (broken)") + ","
        + StatusFor("/dev/hidraw3", "NZXT Kraken X53") + "]";

    private sealed class Rig
    {
        public OpenRgbOwnership Ownership = OpenRgbOwnership.Off;
        public long Now;
        public string List = ListJson;
        public int ListCalls;
        public int AllCalls;
        public readonly List<string> OneCalls = new();
        public LinuxLiquidctlProvider Provider;

        public Rig()
        {
            Provider = new LinuxLiquidctlProvider(
                () => { ListCalls++; return List; },
                () => { AllCalls++; return StatusAll; },
                e =>
                {
                    OneCalls.Add(string.Join(' ', LiquidctlOwnership.StatusArgs(e)));
                    return "[" + StatusFor(e.Address, e.Description) + "]";
                },
                (_, _, _) => true, null, () => Ownership, () => Now);
        }

        public string[] Ids()
        {
            Now += 2000;
            return Provider.GetAll().Select(c => c.Id).ToArray();
        }
    }

    private static OpenRgbOwnership Known(params (string Name, string Location)[] controllers)
        => OpenRgbOwnership.FromView(true, true, controllers);

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
    public void Off_UsesTheSingleAllDevicesRead()
    {
        var r = new Rig();
        Assert.Equal(2, r.Ids().Length);
        Assert.Equal(0, r.ListCalls);
        Assert.Equal(1, r.AllCalls);
        Assert.Empty(r.OneCalls);
    }

    [Fact]
    public void Known_WithoutCommanderCore_UsesTheSingleAllDevicesRead()
    {
        var r = new Rig { Ownership = Known(("Some Mouse", "HID: /dev/hidraw3")) };
        Assert.Equal(2, r.Ids().Length);
        Assert.Equal(0, r.ListCalls);
        Assert.Empty(r.OneCalls);
    }

    [Fact]
    public void Known_OwnedCommanderCore_IsSkippedAndOthersReadPerDevice()
    {
        var r = new Rig { Ownership = Known(("Corsair Commander Core", "HID: /dev/hidraw14")) };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw3", ids[0]);
        Assert.Equal(new[] { "--json --address /dev/hidraw3 status" }, r.OneCalls);
    }

    [Fact]
    public void Known_OwnedNonCommanderDevice_KeepsItsData()
    {
        var r = new Rig { Ownership = Known(("Kraken X53", "HID: /dev/hidraw3"), ("Corsair Commander Core", "HID: /dev/hidraw14")) };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw3", ids[0]);
    }

    [Fact]
    public void Known_CommanderCoreOnNonHidrawLocation_FailsClosedForTheFamily()
    {
        var r = new Rig { Ownership = Known(("Corsair Commander Core", "HID: 1b1c:0c32 libusb")) };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw3", ids[0]);
    }

    [Fact]
    public void Known_CommanderCoreGone_ReleasesTheFamily()
    {
        var r = new Rig { Ownership = Known(("Corsair Commander Core", "HID: /dev/hidraw14")) };
        Assert.Single(r.Ids());
        r.Ownership = Known(("Some Mouse", "HID: /dev/hidraw9"));
        Assert.Equal(2, r.Ids().Length);
        r.Ownership = OpenRgbOwnership.Off;
        Assert.Equal(2, r.Ids().Length);
    }

    [Fact]
    public void Unknown_BlocksOnlyTheFamily_UntilTheDeadline()
    {
        var r = new Rig { Ownership = OpenRgbOwnership.Unknown };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw3", ids[0]);

        r.Now += 55_000;
        Assert.Single(r.Ids());

        r.Now += 2_000;
        Assert.Equal(2, r.Ids().Length);
    }

    [Fact]
    public void Unknown_DeadlineRestartsAfterTheListBecomesKnown()
    {
        var r = new Rig { Ownership = OpenRgbOwnership.Unknown };
        r.Ids();
        r.Ownership = Known(("Some Mouse", "HID: /dev/hidraw9"));
        r.Ids();
        r.Ownership = OpenRgbOwnership.Unknown;
        r.Now += 59_000;
        Assert.Single(r.Ids());
    }

    [Fact]
    public void EmptyListRead_IsNotCachedPastTheSnapshot()
    {
        var r = new Rig { Ownership = OpenRgbOwnership.Unknown, List = "" };
        Assert.Empty(r.Ids());
        r.List = ListJson;
        Assert.Single(r.Ids());
        Assert.Equal(2, r.ListCalls);
    }

    [Fact]
    public void NonEmptyList_IsCachedAcrossSnapshots()
    {
        var r = new Rig { Ownership = OpenRgbOwnership.Unknown };
        r.Ids();
        r.Ids();
        Assert.Equal(1, r.ListCalls);
    }

    [Fact]
    public void SkipLog_RearmsWhenTheDeviceIsReleased()
    {
        var r = new Rig { Ownership = Known(("Corsair Commander Core", "HID: /dev/hidraw14")) };
        Assert.Single(r.Ids());
        Assert.Single(r.Ids());
        r.Ownership = Known(("Other", "HID: /dev/hidraw9"));
        Assert.Equal(2, r.Ids().Length);
        r.Ownership = Known(("Corsair Commander Core", "HID: /dev/hidraw14"));
        Assert.Single(r.Ids());
    }

    [Theory]
    [InlineData(0x1B1C, 0x0C1C)]
    [InlineData(0x1B1C, 0x0C2A)]
    [InlineData(0x1B1C, 0x0C32)]
    public void CommanderCoreIds_AreFamily(int vid, int pid)
        => Assert.True(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", "hid", vid, pid, "")));

    [Fact]
    public void StatusArgs_PinsBusOnlyForPyUsbDevices()
    {
        Assert.Equal(new[] { "--json", "--address", "/dev/hidraw3", "status" },
            LiquidctlOwnership.StatusArgs(new LiquidctlListEntry("/dev/hidraw3", "hid", null, null, "")));
        Assert.Equal(new[] { "--json", "--bus", "usb1", "--address", "4", "status" },
            LiquidctlOwnership.StatusArgs(new LiquidctlListEntry("4", "usb1", null, null, "")));
    }

    [Fact]
    public void ParseList_ReadsRealShapes()
    {
        var l = LiquidctlOwnership.ParseList("""
        [{"bus":"hid","address":"/dev/hidraw1","vendor_id":6940,"product_id":3122,"description":"a"},
         {"bus":"usb1","address":4,"vendor_id":7793,"product_id":8199,"description":"b"}]
        """);
        Assert.Equal(0x1B1C, l[0].VendorId);
        Assert.Equal(0x0C32, l[0].ProductId);
        Assert.Equal("4", l[1].Address);
        Assert.Equal("usb1", l[1].Bus);
    }
}
