using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LightingDeviceNamesTests
{
    private static List<LightingDevice> Cards() => new()
    {
        new LightingDevice { Id = "openrgb-0-0", Name = "B650E - ARGB header 1", ParentDeviceId = "openrgb-0" },
        new LightingDevice { Id = "np50:AABB:1", Name = "HYTE NP50 Port 1" },
    };

    [Fact]
    public void Apply_RenamesTheCardAndKeepsTheHardwareName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "Top intake" });

        Assert.Equal("Top intake", cards[1].Name);
        Assert.Equal("HYTE NP50 Port 1", cards[1].OriginalName);
    }

    [Fact]
    public void Apply_LeavesUnrenamedCardsWithoutAnOriginalName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "Top intake" });

        Assert.Equal("B650E - ARGB header 1", cards[0].Name);
        Assert.Null(cards[0].OriginalName);
    }

    [Fact]
    public void Apply_IgnoresABlankStoredName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "   " });

        Assert.Equal("HYTE NP50 Port 1", cards[1].Name);
        Assert.Null(cards[1].OriginalName);
    }

    [Fact]
    public void Apply_HandsAGroupRenameToEveryMemberCard()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["openrgb-0"] = "Motherboard" });

        Assert.Equal("Motherboard", cards[0].ParentName);
        Assert.Equal("B650E - ARGB header 1", cards[0].Name);
        Assert.Null(cards[0].OriginalName);
    }

    [Fact]
    public void Apply_LeavesParentNameUnsetOnACardWithNoRenamedGroup()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["openrgb-0-0"] = "Top strip" });

        Assert.Null(cards[0].ParentName);
        Assert.Null(cards[1].ParentName);
    }

    private const string Chain = "lianli-wireless:998D1DE566E1";

    [Fact]
    public void Merge_FallsBackToTheCoolingNameOfTheChain()
    {
        var merged = LightingDeviceNames.Merge(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { [Chain] = "Rear stack" });

        var cards = new List<LightingDevice>
        {
            new LightingDevice { Id = Chain + ":fan1", Name = "Lian Li Wireless - Fan", DeviceId = Chain },
        };
        LightingDeviceNames.Apply(cards, merged);

        Assert.Equal("Rear stack", cards[0].DeviceName);
    }

    [Fact]
    public void Merge_AnOlderLightingEntryWinsOverCooling()
    {
        var merged = LightingDeviceNames.Merge(
            new Dictionary<string, string> { [Chain] = "Lighting name" },
            new Dictionary<string, string> { [Chain] = "Rear stack" });

        Assert.Equal("Lighting name", merged[Chain]);
    }

    [Fact]
    public void Merge_IgnoresOtherFanNamesAndPortNames()
    {
        var merged = LightingDeviceNames.Merge(new Dictionary<string, string>(), new Dictionary<string, string>
        {
            [Chain + ":port0"] = "Port",
            ["np50:AABB"] = "Hub",
            ["lianli-wireless:112233445566"] = "   ",
        });

        Assert.Empty(merged);
    }

    [Fact]
    public void SetName_RoutesAChainToTheCoolingStoreAndDropsTheStaleLightingEntry()
    {
        var s = new Nexus.Service.Persistence.NexusSettings();
        s.Lighting.DeviceNames[Chain] = "Old";

        Assert.True(LightingDeviceNames.SetName(s, Chain, "  New  "));

        Assert.Equal("New", s.Cooling.FanNames[Chain]);
        Assert.False(s.Lighting.DeviceNames.ContainsKey(Chain));
    }

    [Fact]
    public void SetName_EmptyResetsAChainInBothStores()
    {
        var s = new Nexus.Service.Persistence.NexusSettings();
        s.Lighting.DeviceNames[Chain] = "Old";
        s.Cooling.FanNames[Chain] = "New";

        LightingDeviceNames.SetName(s, Chain, "");

        Assert.False(s.Cooling.FanNames.ContainsKey(Chain));
        Assert.False(s.Lighting.DeviceNames.ContainsKey(Chain));
    }

    [Fact]
    public void SetName_KeepsEveryOtherIdInTheLightingStore()
    {
        var s = new Nexus.Service.Persistence.NexusSettings();

        Assert.False(LightingDeviceNames.SetName(s, Chain + ":fan1", "Zone"));
        Assert.False(LightingDeviceNames.SetName(s, "np50:AABB:1", "Port"));

        Assert.Equal("Zone", s.Lighting.DeviceNames[Chain + ":fan1"]);
        Assert.Empty(s.Cooling.FanNames);
    }
}
