using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingRoutesTests
{
    private const string StrimerMac = "64F271E566E1";
    private const string FanMac = "998D1DE566E1";

    private static string? ValidateStrimer(Slv3ChainLightingRequest body) =>
        Slv3Routes.ValidateLightingRequest(body, Slv3StrimerEffects.Catalog, perLane: true);

    private static string? ValidateFans(Slv3ChainLightingRequest body) =>
        Slv3Routes.ValidateLightingRequest(body, Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Lcd), perLane: false);

    [Theory]
    [InlineData("custom")]
    [InlineData("perLane")]
    [InlineData("rainbow")]
    [InlineData("meteor")]
    public void Validate_accepts_known_strimer_modes(string mode) =>
        Assert.Null(ValidateStrimer(new Slv3ChainLightingRequest { Mode = mode }));

    [Fact]
    public void Validate_rejects_an_unknown_mode() =>
        Assert.Equal("unknown mode", ValidateStrimer(new Slv3ChainLightingRequest { Mode = "sparkles" }));

    [Fact]
    public void Validate_rejects_per_lane_on_a_fan_chain()
    {
        Assert.Equal("unknown mode", ValidateFans(new Slv3ChainLightingRequest { Mode = LianLiWirelessChainLighting.ModePerLane }));
        Assert.Equal("lanes not supported", ValidateFans(new Slv3ChainLightingRequest
        {
            LaneSettings = new[] { new Slv3LaneDto { Mode = "rainbow", Color = "#FF0000" } },
        }));
    }

    [Fact]
    public void Validate_accepts_a_fan_effect_on_a_fan_chain() =>
        Assert.Null(ValidateFans(new Slv3ChainLightingRequest { Mode = "rainbow" }));

    [Fact]
    public void Validate_rejects_a_malformed_color() =>
        Assert.Equal("invalid color", ValidateStrimer(new Slv3ChainLightingRequest { Colors = new[] { "#12345" } }));

    [Fact]
    public void Validate_rejects_more_colors_than_a_chain_takes() =>
        Assert.Equal("too many colors", ValidateStrimer(new Slv3ChainLightingRequest
        {
            Colors = new[] { "#000000", "#000000", "#000000", "#000000", "#000000", "#000000", "#000000" },
        }));

    [Fact]
    public void Validate_rejects_a_lane_with_an_effect_that_has_no_per_lane_form() =>
        Assert.Equal("unknown lane mode", ValidateStrimer(new Slv3ChainLightingRequest
        {
            LaneSettings = new[] { new Slv3LaneDto { Mode = "meteor", Color = "#FF0000" } },
        }));

    [Fact]
    public void Validate_accepts_per_lane_settings() =>
        Assert.Null(ValidateStrimer(new Slv3ChainLightingRequest
        {
            Mode = LianLiWirelessChainLighting.ModePerLane,
            LaneSettings = new[] { new Slv3LaneDto { Mode = "rainbow", Color = "#FF0000" } },
        }));

    [Theory]
    [InlineData("meteor", null, "meteor")]
    [InlineData("custom", "tide", "tide")]
    [InlineData("custom", null, "rainbow")]
    public void Chains_report_the_animation_to_return_to(string mode, string? effectMode, string expected)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString(StrimerMac), MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());
        var settings = new NexusSettings();
        settings.Devices.LianLiWireless.Chains[StrimerMac] = new LianLiWirelessChainLighting { Mode = mode, EffectMode = effectMode };

        var dto = Assert.Single(Slv3Routes.BuildLightingResponse(hub, settings).Chains);

        Assert.Equal(mode, dto.Mode);
        Assert.Equal(expected, dto.EffectMode);
    }

    [Fact]
    public void Lighting_lists_a_bound_fan_chain_with_its_family_catalog()
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan
        {
            Mac = Convert.FromHexString(StrimerMac), MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0,
        });
        net.Fans.Add(new Slv3TestHub.SimulatedFan
        {
            Mac = Convert.FromHexString(FanMac), MasterMac = net.MasterMac, RxType = 2, DevType = 0, FanCount = 3, FansType = 24,
        });
        Assert.True(hub.DriveTick());

        var chains = Slv3Routes.BuildLightingResponse(hub, new NexusSettings()).Chains;

        var fans = Assert.Single(chains, c => c.Kind == "fans");
        Assert.Equal(3, fans.FanCount);
        Assert.False(fans.SupportsPerLane);
        Assert.Equal(Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Lcd).Count, fans.Modes.Length);
        var strimer = Assert.Single(chains, c => c.Kind == "strimer");
        Assert.True(strimer.SupportsPerLane);
    }
}
