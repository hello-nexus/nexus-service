using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class AdlxFanCurveTests
{
    private static readonly AdlxFanCurve.Range Percent = new(0, 100, 1);

    [Theory]
    [InlineData(45, 45)]
    [InlineData(-5, 0)]
    [InlineData(130, 100)]
    public void Speed_clamps_to_the_range(int duty, int expected) =>
        Assert.Equal(expected, AdlxFanCurve.Speed(duty, Percent));

    [Fact]
    public void Speed_raises_a_duty_below_the_card_minimum() =>
        Assert.Equal(20, AdlxFanCurve.Speed(5, new AdlxFanCurve.Range(20, 100, 1)));

    [Theory]
    [InlineData(42, 40)]
    [InlineData(43, 45)]
    [InlineData(99, 100)]
    public void Speed_snaps_to_the_step(int duty, int expected) =>
        Assert.Equal(expected, AdlxFanCurve.Speed(duty, new AdlxFanCurve.Range(0, 100, 5)));

    [Fact]
    public void Flat_holds_one_speed_on_increasing_temperatures_inside_the_range()
    {
        var points = AdlxFanCurve.Flat(35, new AdlxFanCurve.Range(25, 100, 1), 5);

        Assert.Equal(5, points.Length);
        Assert.All(points, p => Assert.Equal(35, p.Speed));
        for (var i = 1; i < points.Length; i++)
            Assert.True(points[i].Temp > points[i - 1].Temp);
        Assert.Equal(25, points[0].Temp);
        Assert.True(points[^1].Temp <= 100);
    }

    [Fact]
    public void Flat_keeps_temperatures_on_the_step()
    {
        var points = AdlxFanCurve.Flat(50, new AdlxFanCurve.Range(20, 110, 4), 5);
        Assert.All(points, p => Assert.Equal(0, (p.Temp - 20) % 4));
    }
}
