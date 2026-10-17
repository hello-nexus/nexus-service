using System.Collections.Generic;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Platform;

/// <summary>
/// kscreen-doctor parsing, output selection and orientation mapping for the
/// Linux Y70 rotation. The fixture is real <c>kscreen-doctor -o</c> output
/// from a Y70 PC: a landscape desktop monitor plus the Y70 strip rotated right.
/// </summary>
public class LinuxDisplayOrientationProviderTests
{
    private const string Esc = "\u001b[";

    private static string Listing(int y70Rotation) => $"""
{Esc}01;32mOutput: {Esc}0;0m1 HDMI-A-2 ed3df781-5e98-4a1f-ba1c-2bf5d33cde2a
	enabled
	connected
	Modes:  1:3840x1080@59.97!  8:2560x1440@120.00*
	Geometry: 2743,0 2560x1440
	Scale: 1
	Rotation: 1
	Overscan: 0
{Esc}01;32mOutput: {Esc}0;0m2 DP-1 4baec945-04db-4e83-ad20-86841037ae68
	enabled
	connected
	Modes:  36:3840x1100@60.00*!
	Geometry: 0,0 786x2743
	Scale: 1.4
	Rotation: {y70Rotation}
	Overscan: 0
""";

    private sealed class FakeKScreen
    {
        public int Rotation;
        public bool IgnoreSet;
        public readonly List<string> Calls = new();

        public (int Exit, string Output) Run(string[] args)
        {
            Calls.Add(string.Join(' ', args));
            if (args[0] == "-o")
            {
                return (0, Listing(Rotation));
            }
            if (!IgnoreSet)
            {
                Rotation = args[0].EndsWith(".rotation.normal") ? 1
                    : args[0].EndsWith(".rotation.left") ? 2
                    : args[0].EndsWith(".rotation.inverted") ? 4
                    : 8;
            }
            return (0, "");
        }
    }

    [Theory]
    [InlineData("Landscape", "normal", 1)]
    [InlineData("Portrait", "left", 2)]
    [InlineData("LandscapeFlipped", "inverted", 4)]
    [InlineData("PortraitFlipped", "right", 8)]
    public void ToKScreenRotation_maps_each_canonical_orientation(string orientation, string name, int code)
    {
        Assert.Equal((name, code), LinuxDisplayOrientationProvider.ToKScreenRotation(orientation));
    }

    [Fact]
    public void ToKScreenRotation_rejects_unknown_orientation()
    {
        Assert.Null(LinuxDisplayOrientationProvider.ToKScreenRotation("Sideways"));
    }

    [Fact]
    public void ParseOutputs_reads_names_and_rotation_through_ansi_colors()
    {
        var outputs = LinuxDisplayOrientationProvider.ParseOutputs(Listing(8));
        Assert.Equal(2, outputs.Count);
        Assert.Equal(new LinuxDisplayOrientationProvider.KScreenOutput("HDMI-A-2", 1), outputs[0]);
        Assert.Equal(new LinuxDisplayOrientationProvider.KScreenOutput("DP-1", 8), outputs[1]);
    }

    [Theory]
    [InlineData("DP-1", 8)]
    [InlineData("HDMI-A-2", 1)]
    public void SelectOutput_matches_the_connector_exactly(string connector, int rotation)
    {
        var outputs = LinuxDisplayOrientationProvider.ParseOutputs(Listing(8));
        Assert.Equal(rotation, LinuxDisplayOrientationProvider.SelectOutput(outputs, connector)?.Rotation);
    }

    [Fact]
    public void SelectOutput_returns_null_for_an_absent_connector()
    {
        var outputs = LinuxDisplayOrientationProvider.ParseOutputs(Listing(8));
        Assert.Null(LinuxDisplayOrientationProvider.SelectOutput(outputs, "DP-2"));
    }

    [Fact]
    public void SetY70Orientation_rotates_the_selected_output_and_verifies()
    {
        var kscreen = new FakeKScreen { Rotation = 8 };
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", kscreen.Run);

        var (ok, detail) = provider.SetY70Orientation("Landscape");

        Assert.True(ok, detail);
        Assert.Contains("output.DP-1.rotation.normal", kscreen.Calls);
        Assert.Equal(1, kscreen.Rotation);
    }

    [Fact]
    public void SetY70Orientation_skips_the_write_when_already_rotated()
    {
        var kscreen = new FakeKScreen { Rotation = 8 };
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", kscreen.Run);

        var (ok, _) = provider.SetY70Orientation("PortraitFlipped");

        Assert.True(ok);
        Assert.Equal(new[] { "-o" }, kscreen.Calls);
    }

    [Fact]
    public void SetY70Orientation_fails_when_the_rotation_does_not_stick()
    {
        var kscreen = new FakeKScreen { Rotation = 8, IgnoreSet = true };
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", kscreen.Run);

        Assert.False(provider.SetY70Orientation("Landscape").Ok);
    }

    [Fact]
    public void SetY70Orientation_is_not_an_error_when_no_y70_is_attached()
    {
        var kscreen = new FakeKScreen();
        var provider = new LinuxDisplayOrientationProvider(() => "", kscreen.Run);

        var (ok, detail) = provider.SetY70Orientation("Portrait");

        Assert.True(ok);
        Assert.Contains("not attached", detail);
        Assert.Empty(kscreen.Calls);
    }

    [Theory]
    [InlineData(-1, "not found")]
    [InlineData(134, "KDE Plasma Wayland required")]
    [InlineData(-2, "timed out")]
    public void SetY70Orientation_fails_with_a_reason_when_the_tool_is_unusable(int exit, string reason)
    {
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", _ => (exit, ""));

        var (ok, detail) = provider.SetY70Orientation("Portrait");

        Assert.False(ok);
        Assert.Contains(reason, detail);
    }

    [Fact]
    public void SetY70Orientation_fails_when_the_connector_is_not_in_kscreen_output()
    {
        var provider = new LinuxDisplayOrientationProvider(() => "DP-9", new FakeKScreen { Rotation = 8 }.Run);

        var (ok, detail) = provider.SetY70Orientation("Portrait");

        Assert.False(ok);
        Assert.Contains("DP-9", detail);
    }

    [Fact]
    public void SetY70Orientation_rejects_an_unknown_orientation_without_running_the_tool()
    {
        var kscreen = new FakeKScreen();
        var provider = new LinuxDisplayOrientationProvider(() => "DP-1", kscreen.Run);

        Assert.False(provider.SetY70Orientation("Sideways").Ok);
        Assert.Empty(kscreen.Calls);
    }

    [Fact]
    public void NoopProvider_reports_failure_instead_of_faking_success()
    {
        var (ok, detail) = new NoopDisplayOrientationProvider().SetY70Orientation("Portrait");

        Assert.False(ok);
        Assert.NotEmpty(detail);
    }
}
