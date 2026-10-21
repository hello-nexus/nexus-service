using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;
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
        public bool BridgeActive;
        public long Now;
        public string List = ListJson;
        public int ListCalls;
        public int AllCalls;
        public Func<LiquidctlListEntry, string>? One;
        public readonly List<string> OneCalls = new();
        public readonly InMemoryConfigStore Store = new();
        public LinuxLiquidctlProvider Provider;

        public Rig()
        {
            Provider = new LinuxLiquidctlProvider(
                () => { ListCalls++; return List; },
                () => { AllCalls++; return StatusAll; },
                e =>
                {
                    lock (OneCalls)
                        OneCalls.Add(string.Join(' ', LiquidctlOwnership.StatusArgs(e)));
                    return One is not null ? One(e) : "[" + StatusFor(e.Address, e.Description) + "]";
                },
                (_, _, _) => true, Store, () => BridgeActive, () => Now);
        }

        public string[] Ids()
        {
            Now += 2000;
            return Provider.GetAll().Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
    }

    [Fact]
    public void BridgeInactive_ReadsEverythingWithOneAllDevicesCall()
    {
        var r = new Rig();
        Assert.Equal(2, r.Ids().Length);
        Assert.Equal(0, r.ListCalls);
        Assert.Equal(1, r.AllCalls);
        Assert.Empty(r.OneCalls);
    }

    [Fact]
    public void BridgeActive_SkipsFamilyAndReadsOthersPerDevice()
    {
        var r = new Rig { BridgeActive = true };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw3", ids[0]);
        Assert.Equal(0, r.AllCalls);
        Assert.Equal(new[] { "--json --address /dev/hidraw3 status" }, r.OneCalls);
    }

    [Fact]
    public void BridgeActive_ThenInactive_ReleasesTheFamily()
    {
        var r = new Rig { BridgeActive = true };
        Assert.Single(r.Ids());
        r.BridgeActive = false;
        Assert.Equal(2, r.Ids().Length);
    }

    private static void Exclude(Rig r, params string[] detectors)
    {
        foreach (var d in detectors)
            r.Store.Update(s => s.Devices.OpenRgbDetectorExclusions["openrgb-s-" + d] =
                new OpenRgbDetectorExclusion { DetectorName = d, DeviceName = d });
    }

    private const string TwoFamilyList = """
    [{"address":"/dev/hidraw14","description":"Corsair Commander Core","vendor_id":6940,"product_id":3122},
     {"address":"/dev/hidraw15","description":"Corsair Commander Core XT","vendor_id":6940,"product_id":3114},
     {"address":"/dev/hidraw3","description":"NZXT Kraken X53"}]
    """;

    [Fact]
    public void BothDetectorsExcluded_ReadsEverythingWithOneCall()
    {
        var r = new Rig { BridgeActive = true, List = TwoFamilyList };
        Exclude(r, "Corsair Commander Core", "Corsair Commander Core XT");
        Assert.Equal(2, r.Ids().Length);
        Assert.Equal(0, r.ListCalls);
    }

    [Fact]
    public void OnlyXtExcluded_ReleasesOnlyTheXt()
    {
        var r = new Rig { BridgeActive = true, List = TwoFamilyList };
        Exclude(r, "Corsair Commander Core XT");
        var ids = r.Ids();
        Assert.Equal(new[] { "liquidctl:dev-hidraw15", "liquidctl:dev-hidraw3" }, ids);
    }

    [Fact]
    public void OnlyCoreExcluded_ReleasesOnlyTheNonXtIds()
    {
        var r = new Rig { BridgeActive = true, List = TwoFamilyList };
        Exclude(r, "Corsair Commander Core");
        var ids = r.Ids();
        Assert.Equal(new[] { "liquidctl:dev-hidraw14", "liquidctl:dev-hidraw3" }, ids);
    }

    [Fact]
    public void DescriptionOnlyEntry_StaysWithheldUnlessBothDetectorsExcluded()
    {
        var list = """[{"address":"/dev/hidraw14","description":"Corsair Commander ST (broken)"},{"address":"/dev/hidraw3","description":"NZXT Kraken X53"}]""";
        var r = new Rig { BridgeActive = true, List = list };
        Exclude(r, "Corsair Commander Core");
        Assert.Single(r.Ids());
        Exclude(r, "Corsair Commander Core XT");
        Assert.Equal(2, r.Ids().Length);
    }

    [Fact]
    public void OtherDetectorExcluded_StillWithholds()
    {
        var r = new Rig { BridgeActive = true };
        Exclude(r, "Some Mouse");
        Assert.Single(r.Ids());
    }

    [Fact]
    public void FlippingToWithheld_DropsTheCachedSnapshot()
    {
        var r = new Rig();
        Assert.Equal(2, r.Provider.GetAll().Count);
        r.BridgeActive = true;
        // No clock advance: the cached snapshot would still be fresh.
        Assert.Single(r.Provider.GetAll());
    }

    [Fact]
    public void Drive_RefusesAWithheldAddress()
    {
        var calls = new List<string>();
        var r = new Rig();
        var p = new LinuxLiquidctlProvider(() => ListJson, () => StatusAll,
            e => "[" + StatusFor(e.Address, e.Description) + "]",
            (a, c, d) => { calls.Add(a); return true; }, null, () => r.BridgeActive, () => r.Now);
        var channels = p.GetFanChannels();
        Assert.Equal(2, channels.Count);
        var familyId = channels.Single(c => c.DeviceId!.Contains("hidraw14")).Id;
        var otherId = channels.Single(c => c.DeviceId!.Contains("hidraw3")).Id;

        p.DriveFanSpeed(familyId, 50);
        Assert.Equal(new[] { "/dev/hidraw14" }, calls);

        r.BridgeActive = true;
        r.Now += 2000;
        p.GetFanChannels();
        calls.Clear();
        p.DriveFanSpeed(familyId, 50);
        p.DriveFanSpeed(otherId, 50);
        Assert.Equal(new[] { "/dev/hidraw3" }, calls);
    }

    [Theory]
    [InlineData(0x1B1C, 0x0C1C)]
    [InlineData(0x1B1C, 0x0C1D)]
    [InlineData(0x1B1C, 0x0C2A)]
    [InlineData(0x1B1C, 0x0C32)]
    [InlineData(0x1B1C, 0x0C3C)]
    [InlineData(0x1B1C, 0x0C3D)]
    [InlineData(0x1B1C, 0x0C3E)]
    public void FamilyIsMatchedById(int vid, int pid)
        => Assert.True(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", vid, pid, "")));

    [Theory]
    [InlineData("Corsair Commander Core XT")]
    [InlineData("Corsair Commander ST (broken)")]
    public void FamilyIsMatchedByDescription(string desc)
        => Assert.True(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", null, null, desc)));

    [Fact]
    public void NonFamilyDevices_AreNotMatched()
    {
        Assert.False(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", 0x1B1C, 0x0C10, "Corsair Commander Pro")));
        Assert.False(LiquidctlOwnership.IsCommanderCoreFamily(new LiquidctlListEntry("/dev/hidraw1", 7793, 8199, "NZXT Kraken X53")));
    }

    [Fact]
    public void EmptyList_NeverFallsBackToAllDevices()
    {
        var r = new Rig { BridgeActive = true, List = "" };
        Assert.Empty(r.Ids());
        Assert.Equal(0, r.AllCalls);
        r.List = ListJson;
        Assert.Single(r.Ids());
        Assert.Equal(2, r.ListCalls);
    }

    [Fact]
    public void Withholding_AlwaysReadsPerDevice_EvenWithoutAFamilyDevice()
    {
        var r = new Rig { BridgeActive = true, List = """[{"bus":"hid","address":"/dev/hidraw3","description":"NZXT Kraken X53"}]""" };
        Assert.Single(r.Ids());
        Assert.Equal(0, r.AllCalls);
        Assert.Equal(new[] { "--json --address /dev/hidraw3 status" }, r.OneCalls);
    }

    [Fact]
    public void PerDeviceFailure_DoesNotDropOtherDevices()
    {
        var r = new Rig
        {
            BridgeActive = true,
            List = """
            [{"address":"/dev/hidraw14","description":"Corsair Commander Core"},
             {"address":"/dev/hidraw3","description":"A"},{"address":"/dev/hidraw4","description":"B"},{"address":"/dev/hidraw5","description":"C"}]
            """,
        };
        r.One = e => e.Address switch
        {
            "/dev/hidraw3" => throw new InvalidOperationException("boom"),
            "/dev/hidraw4" => "",
            _ => "[" + StatusFor(e.Address, e.Description) + "]",
        };
        var ids = r.Ids();
        Assert.Single(ids);
        Assert.Contains("hidraw5", ids[0]);
    }

    [Fact]
    public void PerDeviceReads_RunConcurrently()
    {
        var r = new Rig
        {
            BridgeActive = true,
            List = """
            [{"address":"/dev/hidraw14","description":"Corsair Commander Core"},
             {"address":"/dev/hidraw3","description":"A"},{"address":"/dev/hidraw4","description":"B"},{"address":"/dev/hidraw5","description":"C"}]
            """,
        };
        using var barrier = new Barrier(3);
        r.One = e =>
        {
            // Only completes when all three reads are in flight together.
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return "[" + StatusFor(e.Address, e.Description) + "]";
        };
        Assert.Equal(3, r.Ids().Length);
    }

    [Fact]
    public void SkipLog_RearmsWhenTheFamilyIsReleased()
    {
        var r = new Rig { BridgeActive = true };
        Assert.Single(r.Ids());
        Assert.Single(r.Ids());
        r.BridgeActive = false;
        Assert.Equal(2, r.Ids().Length);
        r.BridgeActive = true;
        Assert.Single(r.Ids());
    }

    [Fact]
    public void ParseList_ReadsIdsAsNumbersOrHexStrings()
    {
        var l = LiquidctlOwnership.ParseList("""
        [{"bus":"hid","address":"/dev/hidraw1","vendor_id":6940,"product_id":3122,"description":"a"},
         {"address":"/dev/hidraw2","vendor_id":"0x1b1c","product_id":"0c3e","description":"b"}]
        """);
        Assert.Equal(0x0C32, l[0].ProductId);
        Assert.Equal(0x1B1C, l[1].VendorId);
        Assert.Equal(0x0C3E, l[1].ProductId);
    }
}
