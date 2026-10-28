using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

public class FanCalibratorGuardTests
{
    private static Task<Nexus.Service.Models.Cooling.FanCalibration> Run(Func<int?> read, int priorRpm)
        => FanCalibrator.CalibrateOneAsync("fan/0", _ => { }, read, priorRpm, null, CancellationToken.None, sampleDelayMs: 1);

    [Fact]
    public async Task SpinningFan_WithAllZeroSweep_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<CalibrationRejectedException>(() => Run(() => 0, priorRpm: 826));
        Assert.Equal(826, ex.PriorRpm);
        Assert.Equal(0, ex.MaxRpm);
    }

    [Fact]
    public async Task NullReads_AreFailedSamples_NotZeroRpm()
    {
        var ex = await Assert.ThrowsAsync<CalibrationRejectedException>(() => Run(() => null, priorRpm: 1912));
        Assert.Equal(ex.TotalSamples, ex.FailedSamples);
        Assert.True(ex.TotalSamples > 0);
    }

    [Fact]
    public async Task FanDeadBeforeAndDuring_IsStillUnresponsive()
    {
        var result = await Run(() => 0, priorRpm: 0);
        Assert.Equal("Unresponsive", result.Classification);
    }

    [Fact]
    public async Task NormalFan_IsSaved()
    {
        var duty = 100;
        var result = await FanCalibrator.CalibrateOneAsync(
            "fan/0", d => duty = d, () => 200 + duty * 15, 1700, null, CancellationToken.None, sampleDelayMs: 1);
        Assert.Equal("Controllable", result.Classification);
        Assert.Equal(1700, result.MaxRpm);
    }

    [Theory]
    [InlineData(826, 0, 0, 10, true)]
    [InlineData(826, 2000, 0, 10, false)]
    [InlineData(826, 2000, 10, 10, true)]
    [InlineData(100, 0, 0, 10, false)]
    [InlineData(0, 0, 0, 10, false)]
    public void IsImplausibleSweep_Cases(int prior, int max, int failed, int total, bool expected)
        => Assert.Equal(expected, FanCalibrationLogic.IsImplausibleSweep(prior, max, failed, total));
}
