using System.Collections.Generic;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests.Platform;

/// <summary>
/// xrandr parsing and output selection for the panel kiosk. Fixtures are real
/// <c>xrandr --query</c> output: a rotated Y70 strip beside a landscape
/// desktop monitor, which is the layout that put the kiosk on the wrong screen
/// on every non-KDE X11 desktop.
/// </summary>
public class LinuxX11OutputsTests
{
    private const string Query = """
Screen 0: minimum 320 x 200, current 5360 x 1920, maximum 16384 x 16384
DP-3 connected primary 3440x1440+0+480 (normal left inverted right x axis y axis) 800mm x 335mm
   3440x1440     59.97*+  49.99
   2560x1080     60.00
DP-5 connected 1100x3840+3440+0 left (normal left inverted right x axis y axis) 74mm x 259mm
   1920x1080     60.00*+
HDMI-1 disconnected (normal left inverted right x axis y axis)
""";

    [Fact]
    public void Parse_ReadsConnectedOutputsAndSkipsDisconnectedAndModeLines()
    {
        var outputs = LinuxX11Outputs.Parse(Query);
        Assert.Equal(2, outputs.Count);
        Assert.Equal(new LinuxX11Outputs.Output("DP-3", 3440, 1440, 0, 480), outputs[0]);
        // Rotated: xrandr reports the post-rotation geometry, which is what a
        // window position needs.
        Assert.Equal(new LinuxX11Outputs.Output("DP-5", 1100, 3840, 3440, 0), outputs[1]);
    }

    [Fact]
    public void Select_PrefersTheNamedConnector()
    {
        var outputs = LinuxX11Outputs.Parse(Query);
        Assert.Equal("DP-3", LinuxX11Outputs.Select(outputs, "DP-3", allowPortraitFallback: true)?.Name);
        Assert.Equal("DP-5", LinuxX11Outputs.Select(outputs, "dp-5", allowPortraitFallback: false)?.Name);
    }

    [Fact]
    public void Select_FallsBackToThePortraitStripOnlyForTheY70()
    {
        var outputs = LinuxX11Outputs.Parse(Query);
        Assert.Equal("DP-5", LinuxX11Outputs.Select(outputs, "DP-9", allowPortraitFallback: true)?.Name);
        // A promoted monitor gets no shape guess: landing it on the wrong
        // desktop screen is worse than leaving it where the WM put it.
        Assert.Null(LinuxX11Outputs.Select(outputs, "DP-9", allowPortraitFallback: false));
    }

    [Fact]
    public void Select_HasNoPortraitFallbackWhenNothingIsTallEnough()
    {
        var outputs = new List<LinuxX11Outputs.Output>
        {
            new("DP-1", 3840, 2160, 0, 0),
            // 16:10 rotated is portrait but nowhere near the Y70's 3:1 strip.
            new("DP-2", 1200, 1920, 3840, 0),
        };
        Assert.Null(LinuxX11Outputs.Select(outputs, "DP-9", allowPortraitFallback: true));
    }

    // Provider ids are "{Mfg}{Product:X4}-{connector}" with an EDID and the
    // bare connector without one; connector names carry dashes of their own.
    [Theory]
    [InlineData("DELA0B8-DP-5", "DP-5")]
    [InlineData("HYT1234-HDMI-A-1", "HDMI-A-1")]
    [InlineData("DP-5", "DP-5")]
    [InlineData("HDMI-A-1", "HDMI-A-1")]
    [InlineData("", "")]
    public void ConnectorFromDisplayId_StripsOnlyAnEdidPrefix(string displayId, string expected)
        => Assert.Equal(expected, LinuxX11Outputs.ConnectorFromDisplayId(displayId));
}
