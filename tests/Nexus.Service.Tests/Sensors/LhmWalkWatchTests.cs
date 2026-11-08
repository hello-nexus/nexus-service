using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmWalkWatchTests
{
    private long _nowMs = 10_000;

    private LhmWalkWatch NewWatch() => new(() => Volatile.Read(ref _nowMs));

    private void Advance(TimeSpan by) => Interlocked.Add(ref _nowMs, (long)by.TotalMilliseconds);

    [Fact]
    public void A_healthy_walk_never_stalls_and_readings_pass_through()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        Advance(TimeSpan.FromMilliseconds(120));

        Assert.False(watch.CheckStalled(out _));
        Assert.False(watch.EndWalk());
        Assert.False(watch.IsStale);
        Assert.Equal(1800f, watch.Read(1800f));
    }

    [Fact]
    public void A_walk_past_the_limit_fires_once_and_marks_readings_missing()
    {
        var watch = NewWatch();
        var raised = 0;
        watch.Stalled += _ => raised++;
        watch.BeginWalk();
        Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(1));

        Assert.True(watch.CheckStalled(out var age));
        watch.RaiseStalled(age);
        Assert.False(watch.CheckStalled(out _));

        Assert.Equal(1, raised);
        Assert.True(watch.IsStale);
        Assert.Null(watch.Read(1800f));
    }

    [Fact]
    public void A_completed_walk_ends_the_stale_period_and_reports_it()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(1));
        Assert.True(watch.CheckStalled(out _));

        Assert.True(watch.EndWalk());

        Assert.False(watch.IsStale);
        Assert.Equal(1800f, watch.Read(1800f));
        Assert.Null(watch.CurrentWalkAge);
    }

    [Fact]
    public void Idle_time_between_walks_is_not_a_stall()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.EndWalk();
        Advance(TimeSpan.FromHours(4));

        Assert.False(watch.CheckStalled(out _));
        Assert.Equal(TimeSpan.FromHours(4), watch.SinceLastWalk);
    }
}
