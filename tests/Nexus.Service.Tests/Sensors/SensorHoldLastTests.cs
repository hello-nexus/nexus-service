using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class SensorHoldLastTests
{
    [Fact]
    public void Null_within_window_returns_last_good()
    {
        var hold = new SensorHoldLast();
        hold.Resolve("a", 61f, 1000, out _);
        Assert.Equal(61f, hold.Resolve("a", null, 1000 + SensorHoldLast.HoldWindowMs, out var missed));
        Assert.True(missed);
    }

    [Fact]
    public void Null_after_window_returns_zero()
    {
        var hold = new SensorHoldLast();
        hold.Resolve("a", 61f, 1000, out _);
        Assert.Equal(0f, hold.Resolve("a", null, 1001 + SensorHoldLast.HoldWindowMs, out _));
    }

    [Fact]
    public void Null_with_no_history_returns_zero()
    {
        Assert.Equal(0f, new SensorHoldLast().Resolve("a", null, 1000, out _));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Non_finite_is_treated_like_null(float raw)
    {
        var hold = new SensorHoldLast();
        hold.Resolve("a", 42f, 1000, out _);
        Assert.Equal(42f, hold.Resolve("a", raw, 2000, out var missed));
        Assert.True(missed);
    }

    [Fact]
    public void Fresh_value_replaces_held_and_restarts_window()
    {
        var hold = new SensorHoldLast();
        hold.Resolve("a", 10f, 1000, out _);
        Assert.Equal(20f, hold.Resolve("a", 20f, 3000, out var missed));
        Assert.False(missed);
        Assert.Equal(20f, hold.Resolve("a", null, 3000 + SensorHoldLast.HoldWindowMs, out _));
    }

    [Fact]
    public void Sensors_are_held_independently()
    {
        var hold = new SensorHoldLast();
        hold.Resolve("a", 10f, 1000, out _);
        Assert.Equal(0f, hold.Resolve("b", null, 1500, out _));
    }

    [Fact]
    public void Miss_log_is_rate_limited_and_reports_accumulated_count()
    {
        var hold = new SensorHoldLast();
        Assert.Equal(1, hold.NoteMiss("a", 0));
        Assert.Equal(0, hold.NoteMiss("a", 1000));
        Assert.Equal(0, hold.NoteMiss("a", 2000));
        Assert.Equal(3, hold.NoteMiss("a", SensorHoldLast.MissLogIntervalMs));
        Assert.Equal(0, hold.NoteMiss("a", SensorHoldLast.MissLogIntervalMs + 1));
    }
}
