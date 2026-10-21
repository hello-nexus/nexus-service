using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexRoutesTests
{
    [Theory]
    [InlineData("rainbow", null, true)]
    [InlineData("custom", null, true)]
    [InlineData("perZone", null, true)]
    [InlineData("nope", null, false)]
    public void ValidateLighting_checks_the_mode(string mode, string[]? colors, bool ok)
    {
        var error = LianLiFlexRoutes.ValidateLighting(new LianLiFlexChainLightingRequest { Mode = mode, Colors = colors }, FlexFamily.TlFlex);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void ValidateLighting_checks_colors_zones_and_swap()
    {
        Assert.NotNull(LianLiFlexRoutes.ValidateLighting(new() { Colors = new[] { "red" } }, FlexFamily.TlFlex));
        Assert.NotNull(LianLiFlexRoutes.ValidateLighting(new() { Colors = new[] { "#000000", "#111111", "#222222", "#333333", "#444444" } }, FlexFamily.TlFlex));
        Assert.NotNull(LianLiFlexRoutes.ValidateLighting(new() { Zones = new[] { new LianLiFlexZonePatch { Id = "center" } } }, FlexFamily.TlFlex));
        Assert.Null(LianLiFlexRoutes.ValidateLighting(new() { Zones = new[] { new LianLiFlexZonePatch { Id = "center", Mode = "meteor" } } }, FlexFamily.SlInfFlex));
        Assert.NotNull(LianLiFlexRoutes.ValidateLighting(new() { UpDownSwap = true }, FlexFamily.SlInfFlex));
        Assert.Null(LianLiFlexRoutes.ValidateLighting(new() { UpDownSwap = true }, FlexFamily.TlFlex));
    }

    [Fact]
    public void Patch_changes_only_the_named_fields_and_clamps()
    {
        var old = new LianLiFlexUnitSettings { Speed = 1, Brightness = 2, PackageSignature = "x", SortIndex = 3 };
        var next = LianLiFlexRoutes.Patch(old, new LianLiFlexChainLightingRequest
        {
            Speed = 9,
            Mode = "meteor",
            Zones = new[] { new LianLiFlexZonePatch { Id = "down", Mode = "static", Brightness = 9 } },
        }, FlexFamily.TlFlex);
        Assert.Equal(4, next.Speed);
        Assert.Equal(2, next.Brightness);
        Assert.Equal("meteor", next.Mode);
        Assert.Equal("meteor", next.EffectMode);
        Assert.Equal(3, next.SortIndex);
        Assert.Equal(2, next.Zones.Count);
        Assert.Equal("static", next.Zones[1].Mode);
        Assert.Equal(4, next.Zones[1].Brightness);
        Assert.NotSame(old, next);
    }

    [Fact]
    public void Modes_carry_the_catalog_for_each_family()
    {
        Assert.Equal(29, LianLiFlexRoutes.ModesOf(FlexFamily.TlFlex).Length);
        Assert.Equal(26, LianLiFlexRoutes.ModesOf(FlexFamily.SlInfFlex).Length);
        Assert.All(LianLiFlexRoutes.ModesOf(FlexFamily.TlFlex), m => Assert.True(m.ColorsMax <= 4));
    }

    [Fact]
    public void State_and_lighting_responses_serialize_with_the_source_generated_context()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => new FakeFlexTransport(), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var settings = new NexusSettings();
        var state = LianLiFlexRoutes.BuildState(hub, settings);
        Assert.False(Assert.Single(state.Fans).RadioBound);
        Assert.True(Assert.Single(LianLiFlexRoutes.BuildState(hub, settings, null, null, null, mac => mac == "6D1007F517E1").Fans).RadioBound);
        var json = System.Text.Json.JsonSerializer.Serialize(state, Nexus.Service.Serialization.AppJsonContext.Default.LianLiFlexStateResponse);
        Assert.Contains("\"family\":\"tlFlex\"", json);
        Assert.Contains("\"mac\":\"6D1007F517E1\"", json);
        var lighting = LianLiFlexRoutes.BuildLighting(hub, settings);
        var chain = Assert.Single(lighting.Chains);
        Assert.Equal(new[] { "up", "down" }, chain.Zones.Select(z => z.Id).ToArray());
        Assert.True(chain.UpDownSwap is false);
        var lj = System.Text.Json.JsonSerializer.Serialize(lighting, Nexus.Service.Serialization.AppJsonContext.Default.LianLiFlexLightingResponse);
        Assert.Contains("\"supportsZones\":true", lj);
    }

    [Fact]
    public void Modes_carry_the_palette_the_fan_renders_without_user_colors()
    {
        var tl = LianLiFlexRoutes.ModesOf(FlexFamily.TlFlex).ToDictionary(m => m.Key);
        Assert.Equal(new[] { "#AA00FF", "#00D7FF", "#00FF00", "#FFD800" }, tl["static"].DefaultColors);
        Assert.Empty(tl["rainbow"].DefaultColors);
        Assert.NotEmpty(tl["staggered"].DefaultColors);
        Assert.All(tl.Values, m => Assert.True(m.ColorsMax >= m.DefaultColors.Length || m.ColorsMax == 4));
    }

    [Fact]
    public void Per_zone_is_remembered_as_the_effect_mode()
    {
        var next = LianLiFlexRoutes.Patch(new LianLiFlexUnitSettings(), new LianLiFlexChainLightingRequest { Mode = "perZone" }, FlexFamily.TlFlex);
        Assert.Equal("perZone", next.EffectMode);
        var back = LianLiFlexRoutes.Patch(next, new LianLiFlexChainLightingRequest { Mode = "custom" }, FlexFamily.TlFlex);
        Assert.Equal("perZone", back.EffectMode);
        var response = LianLiFlexRoutes.BuildLighting(
            HubOf(new FakeFlexTransport()), SettingsWith(back));
        Assert.Equal("perZone", response.Chains[0].EffectMode);
        Assert.NotEmpty(response.Chains[0].Zones[0].Modes);
    }

    private static FlexHub HubOf(FakeFlexTransport tx)
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => tx, new FlexClock().Func, null, _ => { });
        hub.Refresh();
        return hub;
    }

    private static NexusSettings SettingsWith(LianLiFlexUnitSettings look)
    {
        var s = new NexusSettings();
        s.Devices.FlexUnits["6D1007F517E1"] = look;
        return s;
    }

    [Fact]
    public void Sort_indexes_become_unique_including_rf_bound_units()
    {
        var tx1 = new FakeFlexTransport("AAAAAAAAAAAA");
        var tx2 = new FakeFlexTransport("BBBBBBBBBBBB", rxType: 3);
        var tx3 = new FakeFlexTransport("CCCCCCCCCCCC");
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("a"));
        discovery.Ports.Add(FakeFlexDiscovery.Tl("b"));
        discovery.Ports.Add(FakeFlexDiscovery.Tl("c"));
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "a" ? tx1 : p.ReceiverPath == "b" ? tx2 : tx3, new FlexClock().Func, null, _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.FlexUnits["CCCCCCCCCCCC"] = new LianLiFlexUnitSettings { SortIndex = 0 };
            s.Devices.FlexUnits["AAAAAAAAAAAA"] = new LianLiFlexUnitSettings { SortIndex = 0 };
        });
        var provider = new Nexus.Service.Lighting.FlexLightingDeviceProvider(hub, store, new Nexus.Service.Lighting.Np50IdentifyTracker());
        provider.EnsureUniqueSortIndexes();
        var units = store.Load().Devices.FlexUnits;
        Assert.Equal(3, units.Count);
        Assert.Equal(3, units.Values.Select(u => u.SortIndex).Distinct().Count());
        Assert.True(units["AAAAAAAAAAAA"].SortIndex < units["CCCCCCCCCCCC"].SortIndex);
        Assert.Equal(2, units["BBBBBBBBBBBB"].SortIndex);
    }
}
