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

    [Fact]
    public void A_check_after_the_walk_ended_does_not_mark_stale()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(1));
        watch.EndWalk();

        Assert.False(watch.CheckStalled(out _));
        Assert.False(watch.IsStale);
    }

    [Fact]
    public async Task Racing_checks_and_walk_ends_never_leave_the_flag_stale_when_idle()
    {
        var watch = NewWatch();
        for (var i = 0; i < 2000; i++)
        {
            watch.BeginWalk();
            Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(1));
            var check = Task.Run(() => watch.CheckStalled(out _));
            var end = Task.Run(() => watch.EndWalk());
            var stalled = await check;
            var recovered = await end;
            if (stalled)
            {
                Assert.True(recovered);
            }
            Assert.False(watch.IsStale);
        }
    }

    [Fact]
    public async Task A_reader_that_must_not_wait_returns_at_once_while_a_walk_holds_the_gate()
    {
        var gate = new object();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var walker = Task.Run(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        });
        held.Wait();

        Assert.False(LhmWalkWatch.Acquire(gate, neverWait: true));

        release.Set();
        await walker;
        Assert.True(LhmWalkWatch.Acquire(gate, neverWait: true));
        Monitor.Exit(gate);
    }

    [Fact]
    public async Task A_reader_that_needs_fresh_data_waits_for_the_walk()
    {
        var gate = new object();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var walker = Task.Run(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        });
        held.Wait();

        var got = false;
        var waiterThread = new Thread(() =>
        {
            got = LhmWalkWatch.Acquire(gate, neverWait: false);
            Monitor.Exit(gate);
        });
        waiterThread.Start();
        Assert.True(SpinWait.SpinUntil(() => waiterThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(5)));
        Assert.False(got);

        release.Set();
        await walker;
        waiterThread.Join();
        Assert.True(got);
    }

    [Fact]
    public void Readings_stop_counting_as_fresh_once_the_running_walk_passes_the_fresh_limit()
    {
        var watch = NewWatch();
        Assert.True(watch.IsFresh);
        watch.BeginWalk();
        Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(watch.IsFresh);

        Advance(LhmWalkWatch.FreshLimit);
        Assert.False(watch.IsFresh);

        watch.EndWalk();
        Assert.True(watch.IsFresh);
    }
}
