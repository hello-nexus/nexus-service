using System;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class DdcNegativeProbeCacheTests
{
    private long _now;
    private DdcNegativeProbeCache Cache() => new(() => _now);

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
            c.RecordFailure("a");
            Assert.False(c.ShouldSkip("a"));
        }
        c.RecordFailure("a");
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Skips_UntilRetryInterval_ThenProbesAgain()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) c.RecordFailure("a");
        _now += (long)DdcNegativeProbeCache.RetryInterval.TotalMilliseconds - 1;
        Assert.True(c.ShouldSkip("a"));
        _now += 1;
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void FailureAfterInterval_RestartsTheSkipWindow()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) c.RecordFailure("a");
        _now += (long)DdcNegativeProbeCache.RetryInterval.TotalMilliseconds;
        Assert.False(c.ShouldSkip("a"));
        c.RecordFailure("a");
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Success_ForgetsTheDisplay()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) c.RecordFailure("a");
        c.RecordSuccess("a");
        Assert.False(c.ShouldSkip("a"));
        c.RecordFailure("a");
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void Clear_ResetsEveryDisplay_AndDisplaysAreIndependent()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) c.RecordFailure("a");
        Assert.False(c.ShouldSkip("b"));
        c.Clear();
        Assert.False(c.ShouldSkip("a"));
    }
}
