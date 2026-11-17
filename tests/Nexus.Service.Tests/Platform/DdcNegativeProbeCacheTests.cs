using System;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class DdcNegativeProbeCacheTests
{
    private static readonly long SpacingMs = (long)DdcNegativeProbeCache.FailureSpacing.TotalMilliseconds;
    private static readonly long RetryMs = (long)DdcNegativeProbeCache.RetryInterval.TotalMilliseconds;

    private long _now;
    private DdcNegativeProbeCache Cache() => new(() => _now);

    // One poll that is allowed to probe and fails: waits out the spacing first, as a real poll would.
    private void Fail(DdcNegativeProbeCache c, string id)
    {
        _now += SpacingMs;
        Assert.False(c.ShouldSkip(id));
        c.RecordFailure(id);
    }

    [Fact]
    public void UnknownDisplay_IsProbed()
    {
        Assert.False(Cache().ShouldSkip("a"));
    }

    [Fact]
    public void FirstFailures_StillProbeOncePerSpacing_ThenSkipUntilRetry()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += SpacingMs;
        Assert.True(c.ShouldSkip("a"));
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
        _now += SpacingMs;
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void FailureInsideSpacing_SkipsProbing_UntilSpacingPasses()
    {
        var c = Cache();
        c.RecordFailure("a");
        Assert.True(c.ShouldSkip("a"));
        _now += SpacingMs - 1;
        Assert.True(c.ShouldSkip("a"));
        _now += 1;
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void DroppedFailure_DoesNotMoveTheCountedTime()
    {
        var c = Cache();
        c.RecordFailure("a");
        _now += SpacingMs / 2;
        c.RecordFailure("a");
        _now += SpacingMs - SpacingMs / 2;
        // Exactly one spacing after the first counted failure: this one counts (the dropped one did not reset the clock).
        c.RecordFailure("a");
        for (var i = 2; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += SpacingMs;
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Skips_UntilRetryInterval_ThenProbesAgain()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += RetryMs - 1;
        Assert.True(c.ShouldSkip("a"));
        _now += 1;
        Assert.False(c.ShouldSkip("a"));
    }

    [Fact]
    public void AtCap_RetryIntervalRearmsOneProbe_ThenSkipsAgain()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        _now += RetryMs;
        Assert.False(c.ShouldSkip("a"));
        c.RecordFailure("a");
        Assert.True(c.ShouldSkip("a"));
        _now += SpacingMs;
        Assert.True(c.ShouldSkip("a"));
    }

    [Fact]
    public void Success_ForgetsTheDisplay()
    {
        var c = Cache();
        for (var i = 0; i < DdcNegativeProbeCache.MaxAttempts; i++) Fail(c, "a");
        c.RecordSuccess("a");
        Assert.False(c.ShouldSkip("a"));
        c.RecordFailure("a");
        c.RecordSuccess("a");
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
