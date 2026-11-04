using System;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class DdcNegativeProbeCacheTests
{
    private long _now;
    private DdcNegativeProbeCache Cache() => new(() => _now);

    private void Fail(DdcNegativeProbeCache c, string id)
    {
        _now += (long)DdcNegativeProbeCache.FailureSpacing.TotalMilliseconds;
        c.RecordFailure(id);
    }

    [Fact]
    public void BurstOfFailures_CountsOnce_SoItCannotTripTheThreshold()
    {
        var c = Cache();
        for (var i = 0; i < 50; i++)
        {
            c.RecordFailure("a");
            _now += 100;
        }
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void FailuresSpreadOverTime_TripTheThreshold()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++)
        {
            Fail(c, "a");
        }
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void UnknownDisplay_IsProbed()
    {
        Assert.False(Cache().ShouldSkip("a"));
    }

    [Fact]
    public void FirstFailures_StillProbe_ThenSkip()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts - 1; i++)
        {
            Fail(c, "a");
            Assert.False(c.ShouldSkip("a"));
        }
        Fail(c, "a");
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Skips_UntilRetryInterval_ThenProbesAgain()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += (long)DdcNegativeProbeCache.RetryInterval.TotalMilliseconds - 1;
        Assert.True(c.ShouldSkip("a"));
        _now += 1;
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void FailureAfterInterval_RestartsTheSkipWindow()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += (long)DdcNegativeProbeCache.RetryInterval.TotalMilliseconds;
        Assert.False(c.ShouldSkip("a"));
        Fail(c, "a");
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Success_ForgetsTheDisplay()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        c.RecordSuccess("a");
        Assert.False(c.ShouldSkip("a"));
        Fail(c, "a");
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void Clear_ResetsEveryDisplay_AndDisplaysAreIndependent()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        Assert.False(c.ShouldSkip("b"));
        c.Clear();
        Assert.False(c.ShouldSkip("a"));
    }
}
