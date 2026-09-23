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
}
