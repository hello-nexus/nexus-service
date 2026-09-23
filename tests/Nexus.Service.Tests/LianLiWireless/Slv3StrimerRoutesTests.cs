using System;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3StrimerRoutesTests
{
    private const string Mac = "64F271E566E1";

    [Theory]
    [InlineData("custom")]
    [InlineData("perLane")]
    [InlineData("rainbow")]
    [InlineData("meteor")]
    public void Validate_accepts_known_modes(string mode) =>
        Assert.Null(Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest { Mode = mode }));

    [Fact]
    public void Validate_rejects_an_unknown_mode() =>
        Assert.Equal("unknown mode", Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest { Mode = "sparkles" }));

    [Theory]
    [InlineData("64F271E566")]
    [InlineData("64F271E566E1FF")]
    [InlineData("zzF271E566E1")]
    public void Validate_rejects_a_malformed_mac(string mac) =>
        Assert.Equal("invalid mac", Slv3Routes.ValidateStrimerRequest(mac, new Slv3StrimerLightingRequest()));

    [Fact]
    public void Validate_rejects_a_malformed_color() =>
        Assert.Equal("invalid color", Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest { Colors = new[] { "#12345" } }));

    [Fact]
    public void Validate_rejects_more_colors_than_a_cable_takes() =>
        Assert.Equal("too many colors", Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest
        {
            Colors = new[] { "#000000", "#000000", "#000000", "#000000", "#000000", "#000000", "#000000" },
        }));

    [Fact]
    public void Validate_rejects_a_lane_with_an_effect_that_has_no_per_lane_form() =>
        Assert.Equal("unknown lane mode", Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest
        {
            LaneSettings = new[] { new Slv3StrimerLaneDto { Mode = "meteor", Color = "#FF0000" } },
        }));

    [Fact]
    public void Validate_accepts_per_lane_settings() =>
        Assert.Null(Slv3Routes.ValidateStrimerRequest(Mac, new Slv3StrimerLightingRequest
        {
            Mode = LianLiWirelessStrimerSettings.ModePerLane,
            LaneSettings = new[] { new Slv3StrimerLaneDto { Mode = "rainbow", Color = "#FF0000" } },
        }));

    [Theory]
    [InlineData("meteor", null, "meteor")]
    [InlineData("custom", "tide", "tide")]
    [InlineData("custom", null, "rainbow")]
    public void Strimers_report_the_animation_to_return_to(string mode, string? effectMode, string expected)
    {
        var (hub, net, _) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString(Mac), MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());
        var settings = new NexusSettings();
        settings.Devices.LianLiWireless.Strimers[Mac] = new LianLiWirelessStrimerSettings { Mode = mode, EffectMode = effectMode };

        var dto = Assert.Single(Slv3Routes.BuildStrimersResponse(hub, settings).Strimers);

        Assert.Equal(mode, dto.Mode);
        Assert.Equal(expected, dto.EffectMode);
    }
}
