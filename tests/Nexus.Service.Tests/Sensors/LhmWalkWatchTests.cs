using Nexus.Service.Sensors;
using Nexus.Service.Tests.Common;

namespace Nexus.Service.Tests.Sensors;

public class LhmWalkWatchTests
{
    private long _nowMs = 10_000;

    private readonly FakeOneShot.Factory _deadlines = new();

    private LhmWalkWatch NewWatch() => new(() => Volatile.Read(ref _nowMs), _deadlines.Create);

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

    [Fact]
    public async Task Close_is_skipped_while_a_walk_holds_the_gate_past_the_wait()
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
        var ran = false;

        var result = LhmWalkWatch.TryRunWhenIdle(gate, TimeSpan.FromMilliseconds(50), () => ran = true);

        Assert.False(result);
        Assert.False(ran);
        release.Set();
        await walker;
    }

    [Fact]
    public void Close_runs_when_no_walk_is_running()
    {
        var ran = false;

        Assert.True(LhmWalkWatch.TryRunWhenIdle(new object(), TimeSpan.FromMilliseconds(50), () => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void The_current_item_is_reported_while_a_walk_runs_and_cleared_when_it_ends()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.SetItem("/memory/dimm/1");
        Advance(TimeSpan.FromSeconds(3));

        var at = watch.CurrentItem;
        Assert.Equal("/memory/dimm/1", at!.Value.Item);
        Assert.Equal(TimeSpan.FromSeconds(3), at.Value.Age);

        watch.EndWalk();
        Assert.Null(watch.CurrentItem);
    }

    [Fact]
    public void Our_dimm_reads_are_paused_for_the_requested_duration_only()
    {
        var watch = NewWatch();
        Assert.False(watch.SpdQuiesced);

        watch.QuiesceSpdFor(TimeSpan.FromSeconds(10));
        Advance(TimeSpan.FromSeconds(9));
        Assert.True(watch.SpdQuiesced);

        Advance(TimeSpan.FromSeconds(2));
        Assert.False(watch.SpdQuiesced);
    }

    [Theory]
    [InlineData("/memory/dimm/3", false)]
    [InlineData("spd-gate", false)]
    [InlineData("/intelcpu/0", true)]
    public void Only_memory_items_and_the_spd_gate_count_as_inside_an_smbus_item(string item, bool outside)
    {
        var watch = NewWatch();
        Assert.True(watch.WaitUntilOutsideSmbusItem(TimeSpan.Zero));
        watch.BeginWalk();
        watch.SetItem(item);

        Assert.Equal(outside, watch.WaitUntilOutsideSmbusItem(TimeSpan.Zero));

        watch.EndWalk();
        Assert.True(watch.WaitUntilOutsideSmbusItem(TimeSpan.Zero));
    }

    [Fact]
    public void A_walk_arms_one_deadline_at_the_stall_limit_and_ending_it_disarms()
    {
        var watch = NewWatch();
        Assert.Empty(_deadlines.Created);

        watch.BeginWalk();
        var deadline = _deadlines.Single!;
        Assert.True(deadline.Armed);
        Assert.Equal(LhmWalkWatch.StallLimit + TimeSpan.FromMilliseconds(1), deadline.Due);

        watch.EndWalk();
        Assert.False(deadline.Armed);

        watch.BeginWalk();
        Assert.Same(deadline, _deadlines.Single);
        Assert.True(deadline.Armed);
    }

    [Fact]
    public void The_deadline_firing_on_a_wedged_walk_flags_the_stall_once()
    {
        var watch = NewWatch();
        var raised = new List<TimeSpan>();
        watch.Stalled += raised.Add;
        watch.BeginWalk();
        Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(1));

        _deadlines.Single!.Fire();
        _deadlines.Single!.Fire();

        Assert.Single(raised);
        Assert.True(watch.IsStale);
        Assert.False(_deadlines.Single!.Armed);
    }

    [Fact]
    public void A_deadline_that_fires_before_the_limit_re_arms_for_the_remainder()
    {
        var watch = NewWatch();
        var raised = 0;
        watch.Stalled += _ => raised++;
        watch.BeginWalk();
        Advance(TimeSpan.FromSeconds(40));

        _deadlines.Single!.Fire();

        Assert.Equal(0, raised);
        Assert.False(watch.IsStale);
        Assert.True(_deadlines.Single!.Armed);
        Assert.Equal(LhmWalkWatch.StallLimit - TimeSpan.FromSeconds(40) + TimeSpan.FromMilliseconds(1), _deadlines.Single!.Due);
    }

    [Fact]
    public void A_deadline_that_fires_after_the_walk_ended_does_nothing()
    {
        var watch = NewWatch();
        var raised = 0;
        watch.Stalled += _ => raised++;
        watch.BeginWalk();
        Advance(LhmWalkWatch.StallLimit + TimeSpan.FromSeconds(5));
        watch.EndWalk();

        _deadlines.Single!.Fire();

        Assert.Equal(0, raised);
        Assert.False(watch.IsStale);
    }

    [Fact]
    public void Waiting_outside_the_smbus_items_returns_at_once_when_no_memory_item_runs()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.SetItem("/intelcpu/0");

        Assert.True(watch.WaitUntilOutsideSmbusItem(TimeSpan.Zero));
    }

    [Fact]
    public void Waiting_outside_the_smbus_items_times_out_while_a_memory_item_runs()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.SetItem("/memory/dimm/1");

        Assert.False(watch.WaitUntilOutsideSmbusItem(TimeSpan.FromMilliseconds(50)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/intelcpu/0")]
    public async Task Leaving_the_memory_item_releases_a_waiter(string? next)
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.SetItem("/memory/dimm/1");
        var waiter = Task.Run(() => watch.WaitUntilOutsideSmbusItem(TimeSpan.FromSeconds(30)));

        watch.SetItem(next);

        Assert.True(await waiter);
    }

    [Fact]
    public async Task Ending_the_walk_releases_a_waiter()
    {
        var watch = NewWatch();
        watch.BeginWalk();
        watch.SetItem("spd-gate");
        var waiter = Task.Run(() => watch.WaitUntilOutsideSmbusItem(TimeSpan.FromSeconds(30)));

        watch.EndWalk();

        Assert.True(await waiter);
    }
}
