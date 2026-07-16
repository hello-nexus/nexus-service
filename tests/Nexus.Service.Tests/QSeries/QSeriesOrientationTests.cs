using Nexus.Service.Models.Displays;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class ToUserRotationTests
{
    [Fact]
    public void Portrait_maps_to_zero()
    {
        Assert.Equal(0, QSeriesPortWatcher.ToUserRotation(DisplayOrientations.Portrait));
    }

    [Fact]
    public void PortraitFlipped_maps_to_two()
    {
        Assert.Equal(2, QSeriesPortWatcher.ToUserRotation(DisplayOrientations.PortraitFlipped));
    }

    [Fact]
    public void Unknown_value_defaults_to_zero()
    {
        Assert.Equal(0, QSeriesPortWatcher.ToUserRotation("Landscape"));
        Assert.Equal(0, QSeriesPortWatcher.ToUserRotation(""));
    }
}

public class PanelOrientationTookTests
{
    [Fact]
    public void Rotation_zero_matches_only_rotation_zero_output()
    {
        Assert.True(QSeriesPortWatcher.PanelOrientationTook("  mCurrentRotation=ROTATION_0", 0));
        Assert.False(QSeriesPortWatcher.PanelOrientationTook("  mCurrentRotation=ROTATION_180", 0));
    }

    [Fact]
    public void Rotation_180_matches_only_rotation_180_output()
    {
        Assert.True(QSeriesPortWatcher.PanelOrientationTook("  mCurrentRotation=ROTATION_180", 2));
        Assert.False(QSeriesPortWatcher.PanelOrientationTook("  mCurrentRotation=ROTATION_0", 2));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelOrientationTook("", 0));
        Assert.False(QSeriesPortWatcher.PanelOrientationTook("", 2));
    }
}
