using Nexus.Service.Platform.Mac;
using Xunit;

namespace Nexus.Service.Tests;

// Pure geometry of the titlebar drag strip's click-through columns, in
// lockstep with the layout constants mirrored from TopBar.module.scss. The
// left cluster holds the sidebar-collapse toggle AND the Focus toggle; a
// carve-out spanning only one button leaves the second click-dead under the
// drag strip (its clicks start a window drag instead).
public class MacAppWindowTests
{
    private const double BarWidth = 1260;
    // .topBar padding-left + --mac-traffic-light-inset.
    private const double ClusterLeft = 8 + 96;
    // Search pill right edge (bar center + half --search-w) + pill->gear gap (0.4rem).
    private const double PageSettingsLeft = BarWidth / 2 + 680 / 2.0 + 7;
    // .pageSettingsButton width - the gear is the whole cluster.
    private const double PageSettingsClusterWidth = 34;

    [Theory]
    [InlineData(ClusterLeft + 16)]              // collapse toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 16)]   // Focus toggle center
    [InlineData(ClusterLeft + 32 + 2.4 + 30)]   // Focus toggle right edge
    [InlineData(BarWidth / 2)]                  // search pill center
    [InlineData(PageSettingsLeft + 17)]         // gear center
    [InlineData(PageSettingsLeft + PageSettingsClusterWidth)] // gear right edge, within margin
    [InlineData(BarWidth - 8 - 40)]             // right cluster
    public void ControlColumns_FallThroughToTheWebView(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));

    [Theory]
    [InlineData(ClusterLeft + 64 + 2.4 + 4 + 2)] // right of the cluster + margin
    [InlineData(195)]                            // open bar between the cluster and the history arrows
    [InlineData(PageSettingsLeft + PageSettingsClusterWidth + 4 + 2)] // right of the gear + margin
    // The page-mode control moved into the pages' own tab strip, so nothing
    // widens this cluster any more: the bar drags from just past the gear.
    [InlineData(PageSettingsLeft + 100)]
    public void OpenBar_OutsideControlColumns_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, BarWidth));

    // The gear column is a fixed width now, so a narrower window carves out
    // exactly the same cluster.
    private const double NarrowWidth = 1040;
    private const double NarrowPageSettingsLeft = NarrowWidth / 2 + 476 / 2.0 + 7;

    [Theory]
    [InlineData(NarrowPageSettingsLeft + 10)]  // gear button
    public void NarrowWidth_GearFallsThrough(double x)
        => Assert.True(MacAppWindow.IsTopBarButtonColumn(x, NarrowWidth));

    [Theory]
    [InlineData(NarrowPageSettingsLeft + 100)] // right of the gear: drags
    public void NarrowWidth_BeyondGear_Drags(double x)
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(x, NarrowWidth));

    // --search-w: clamped, growing with window width past the floor width; centered.
    [Theory]
    [InlineData(1024, 460)]
    [InlineData(1100, 536)]
    [InlineData(1244, 680)]
    [InlineData(2000, 680)]
    public void SearchPill_FollowsClampedWidth(double w, double pill)
    {
        double left = w / 2 - pill / 2;
        double right = w / 2 + pill / 2;
        Assert.True(MacAppWindow.IsTopBarButtonColumn(left + 1, w));
        Assert.True(MacAppWindow.IsTopBarButtonColumn(right - 1, w));
    }

    [Theory]
    [InlineData(1100, 536)]
    [InlineData(2000, 680)]
    public void SearchPill_PastGear_Drags(double w, double pill)
        // pill edge + gap + gear + margin + slack.
        => Assert.False(MacAppWindow.IsTopBarButtonColumn(w / 2 + pill / 2 + 7 + 34 + 4 + 2, w));
}
