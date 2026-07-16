using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class PercentToBrightnessByteTests
{
    [Fact]
    public void Zero_maps_to_zero()
    {
        Assert.Equal(0, QSeriesPortWatcher.PercentToBrightnessByte(0));
    }

    [Fact]
    public void Full_maps_to_255()
    {
        Assert.Equal(255, QSeriesPortWatcher.PercentToBrightnessByte(100));
    }

    [Fact]
    public void Rounds_half_away_from_zero()
    {
        // 30 / 100 * 255 = 76.5, and 76 is even: this only passes under
        // AwayFromZero (77), not the default ToEven (76).
        Assert.Equal(77, QSeriesPortWatcher.PercentToBrightnessByte(30));
    }

    [Fact]
    public void Rounds_down_below_midpoint()
    {
        // 99 / 100 * 255 = 252.45
        Assert.Equal(252, QSeriesPortWatcher.PercentToBrightnessByte(99));
    }

    [Fact]
    public void Rounds_up_above_midpoint()
    {
        // 1 / 100 * 255 = 2.55
        Assert.Equal(3, QSeriesPortWatcher.PercentToBrightnessByte(1));
    }

    [Fact]
    public void Out_of_range_input_is_clamped()
    {
        Assert.Equal(0, QSeriesPortWatcher.PercentToBrightnessByte(-10));
        Assert.Equal(255, QSeriesPortWatcher.PercentToBrightnessByte(150));
    }
}

public class PanelBrightnessTookTests
{
    [Fact]
    public void Exact_match_took()
    {
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("132", 132));
    }

    [Fact]
    public void Trims_whitespace_and_trailing_newline()
    {
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("132\n", 132));
        Assert.True(QSeriesPortWatcher.PanelBrightnessTook("  132  ", 132));
    }

    [Fact]
    public void Mismatch_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelBrightnessTook("100", 132));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelBrightnessTook("", 132));
    }
}

public class PanelScreenPowerTookTests
{
    [Fact]
    public void Awake_output_matches_awake_expectation()
    {
        Assert.True(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Awake", true));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Asleep", true));
    }

    [Fact]
    public void Asleep_output_matches_asleep_expectation()
    {
        Assert.True(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Asleep", false));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("  mWakefulness=Awake", false));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("", true));
        Assert.False(QSeriesPortWatcher.PanelScreenPowerTook("", false));
    }
}
