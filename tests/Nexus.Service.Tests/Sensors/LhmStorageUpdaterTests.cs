using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmStorageUpdaterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan Never = TimeSpan.FromMinutes(30);

    private static LhmStorageUpdater New(
        TimeSpan? grace = null,
        TimeSpan? overdue = null,
        Action<string, string, TimeSpan>? onOverdue = null,
        Action<string, string>? onRecovered = null) =>
        new(grace ?? Grace, overdue ?? Never, onOverdue, onRecovered);

    private static readonly Action Nothing = () => { };

    [Fact]
    public void A_stuck_drive_does_not_block_the_requesting_thread_or_other_drives()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object stuckDrive = new(), healthyDrive = new();
        var healthyRan = false;

        var stuck = updater.Request(stuckDrive, "/hdd/0", "stuck", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.NotNull(stuck);
        Assert.True(started.Wait(Wait));
        var healthy = updater.Request(healthyDrive, "/nvme/0", "healthy", Nothing, () => healthyRan = true);

        Assert.NotNull(healthy);
        Assert.True(healthy.Wait(Wait));
        Assert.True(healthyRan);
        Assert.True(updater.IsBusy(stuckDrive));
        release.Set();
        Assert.True(stuck.Wait(Wait));
    }

    [Fact]
    public void Drives_sharing_an_identifier_each_get_their_own_worker()
    {
        using var updater = New();
        object first = new(), second = new();
        var ran = 0;

        var a = updater.Request(first, "/ata/id_error", "a", Nothing, () => Interlocked.Increment(ref ran));
        var b = updater.Request(second, "/nvme/id_error", "b", Nothing, () => Interlocked.Increment(ref ran));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a.Wait(Wait));
        Assert.True(b.Wait(Wait));
        Assert.Equal(2, ran);
    }

    [Fact]
    public void A_drive_with_an_update_in_flight_is_not_requested_again()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        object drive = new();
        var runs = 0;

        var first = updater.Request(drive, "/hdd/0", "d", Nothing, () => { Interlocked.Increment(ref runs); release.Wait(Wait); });
        Assert.NotNull(first);
        Assert.Null(updater.Request(drive, "/hdd/0", "d", Nothing, () => Interlocked.Increment(ref runs)));

        release.Set();
        Assert.True(first.Wait(Wait));
        Assert.True(updater.WaitIdle(drive, Wait));
        Assert.Equal(1, runs);
        var again = updater.Request(drive, "/hdd/0", "d", Nothing, () => Interlocked.Increment(ref runs));
        Assert.NotNull(again);
        Assert.True(again.Wait(Wait));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_second_drive_sets_its_cycle_only_after_the_first_drive_update_has_read_its_own()
    {
        // The gate is held for the whole first update here (the grace never expires), so the order is by construction.
        using var updater = New(grace: Never);
        object a = new(), b = new();
        var current = 0;
        var seenByA = 0;
        var seenByB = 0;
        var order = new List<string>();

        var first = updater.Request(a, "/hdd/0", "a", () => { current = 1; lock (order) { order.Add("a-before"); } }, () => { seenByA = current; lock (order) { order.Add("a-work"); } });
        var second = updater.Request(b, "/hdd/1", "b", () => { current = 2; lock (order) { order.Add("b-before"); } }, () => seenByB = current);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Wait(Wait));
        Assert.True(second.Wait(Wait));
        Assert.Equal(1, seenByA);
        Assert.Equal(2, seenByB);
        lock (order) { Assert.True(order.IndexOf("a-work") < order.IndexOf("b-before") || order.IndexOf("b-before") < order.IndexOf("a-before")); }
    }

    [Fact]
    public void A_drive_past_the_overdue_limit_is_reported_once_and_again_on_recovery()
    {
        var order = new List<string>();
        using var overdueSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        object drive = new();
        using var updater = New(
            overdue: TimeSpan.FromMilliseconds(20),
            onOverdue: (id, name, _) => { lock (order) { order.Add($"overdue {id} {name}"); } overdueSeen.Set(); },
            onRecovered: (id, name) => { lock (order) { order.Add($"recovered {id} {name}"); } });

        var job = updater.Request(drive, "/hdd/0", "ST4000", Nothing, () => release.Wait(Wait));
        Assert.NotNull(job);
        Assert.True(overdueSeen.Wait(Wait));
        release.Set();
        Assert.True(job.Wait(Wait));

        lock (order) { Assert.Equal(new[] { "overdue /hdd/0 ST4000", "recovered /hdd/0 ST4000" }, order); }
    }

    [Fact]
    public void A_recovery_never_lands_before_an_overdue_report_that_is_still_running()
    {
        var order = new List<string>();
        using var inOverdue = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        object drive = new();
        using var updater = New(
            overdue: TimeSpan.FromMilliseconds(20),
            onOverdue: (_, _, _) => { inOverdue.Set(); proceed.Wait(Wait); lock (order) { order.Add("overdue"); } },
            onRecovered: (_, _) => { lock (order) { order.Add("recovered"); } });

        var job = updater.Request(drive, "/hdd/0", "d", Nothing, () => release.Wait(Wait));
        Assert.NotNull(job);
        Assert.True(inOverdue.Wait(Wait));
        release.Set();
        proceed.Set();
        Assert.True(job.Wait(Wait));

        lock (order) { Assert.Equal(new[] { "overdue", "recovered" }, order); }
    }

    [Fact]
    public void A_completed_update_never_reports_overdue()
    {
        var calls = 0;
        object drive = new();
        using var updater = New(onOverdue: (_, _, _) => Interlocked.Increment(ref calls), onRecovered: (_, _) => Interlocked.Increment(ref calls));

        var job = updater.Request(drive, "/hdd/0", "d", Nothing, Nothing);
        Assert.NotNull(job);
        Assert.True(job.Wait(Wait));
        Assert.True(updater.WaitIdle(drive, Wait));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_throwing_update_does_not_kill_the_worker()
    {
        using var updater = New();
        object drive = new();
        var ran = false;

        var first = updater.Request(drive, "/hdd/0", "d", Nothing, () => throw new InvalidOperationException("drive gone"));
        Assert.NotNull(first);
        Assert.True(first.Wait(Wait));
        Assert.True(updater.WaitIdle(drive, Wait));
        var second = updater.Request(drive, "/hdd/0", "d", Nothing, () => ran = true);
        Assert.NotNull(second);
        Assert.True(second.Wait(Wait));

        Assert.True(ran);
    }

    [Fact]
    public void A_read_runs_after_the_drives_update_and_never_beside_it()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object drive = new();
        var order = new List<string>();

        var update = updater.Request(drive, "/hdd/0", "d", Nothing, () => { started.Set(); release.Wait(Wait); lock (order) { order.Add("update-end"); } });
        Assert.NotNull(update);
        Assert.True(started.Wait(Wait));
        var read = updater.RequestRead(drive, () => { lock (order) { order.Add("read"); } });
        Assert.NotNull(read);
        Assert.Null(updater.RequestRead(drive, Nothing));

        Assert.False(read.IsDone);
        release.Set();
        Assert.True(read.Wait(Wait));
        lock (order) { Assert.Equal(new[] { "update-end", "read" }, order); }
    }

    [Fact]
    public void A_read_of_a_stuck_drive_times_out_without_blocking_other_drives_reads()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object stuckDrive = new(), healthyDrive = new();
        updater.Request(stuckDrive, "/hdd/0", "d", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));

        var stuckRead = updater.RequestRead(stuckDrive, Nothing);
        var healthyRead = updater.RequestRead(healthyDrive, Nothing);

        Assert.NotNull(stuckRead);
        Assert.NotNull(healthyRead);
        Assert.True(healthyRead.Wait(Wait));
        Assert.False(stuckRead.Wait(TimeSpan.Zero));
        release.Set();
        Assert.True(stuckRead.Wait(Wait));
    }

    [Fact]
    public void Retain_drops_a_departed_drive_so_a_reused_slot_does_not_inherit_its_stuck_update()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object drive = new();
        updater.Request(drive, "/hdd/0", "old", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));

        updater.Retain(new List<object>());
        var fresh = updater.Request(drive, "/hdd/0", "new", Nothing, Nothing);

        Assert.NotNull(fresh);
        Assert.True(fresh.Wait(Wait));
        release.Set();
    }

    [Fact]
    public void Shutdown_waits_a_bounded_time_for_a_stuck_update_and_refuses_new_work()
    {
        var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        updater.Request(new object(), "/hdd/0", "d", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));

        Assert.False(updater.Shutdown(TimeSpan.FromMilliseconds(80)));
        Assert.Null(updater.Request(new object(), "/hdd/1", "e", Nothing, Nothing));

        release.Set();
        Assert.True(updater.Shutdown(Wait));
    }

    [Fact]
    public void Shutdown_returns_at_once_when_idle()
    {
        var updater = New();
        object drive = new();
        var job = updater.Request(drive, "/hdd/0", "d", Nothing, Nothing);
        Assert.NotNull(job);
        Assert.True(job.Wait(Wait));
        Assert.True(updater.WaitIdle(drive, Wait));

        Assert.True(updater.Shutdown(TimeSpan.Zero));
    }

    [Fact]
    public void Shutdown_still_waits_for_a_stuck_update_whose_drive_was_retired()
    {
        var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        updater.Request(new object(), "/hdd/0", "d", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));

        updater.Retain(new List<object>());

        Assert.False(updater.Shutdown(TimeSpan.FromMilliseconds(80)));
        release.Set();
        Assert.True(updater.Shutdown(Wait));
    }

    [Fact]
    public void A_stuck_power_monitor_is_overdue_without_blocking_and_recovers_when_it_returns()
    {
        var overdue = new ManualResetEventSlim();
        var recovered = new ManualResetEventSlim();
        using var updater = new LhmStorageUpdater(
            Grace,
            TimeSpan.FromMilliseconds(50),
            (_, _, _) => overdue.Set(),
            (_, _) => recovered.Set(),
            "lhm-powermonitor");
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object monitor = new(), other = new();
        var otherRan = 0;

        var stuck = updater.Request(monitor, "/gpu-powermonitor/COM4", "WireView", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.NotNull(stuck);
        Assert.True(started.Wait(Wait));
        Assert.True(overdue.Wait(Wait));
        // Every later walk asks again; none queues behind the stuck one.
        for (var i = 0; i < 20; i++)
        {
            Assert.Null(updater.Request(monitor, "/gpu-powermonitor/COM4", "WireView", Nothing, Nothing));
        }
        var healthy = updater.Request(other, "/gpu-powermonitor/COM5", "Other", Nothing, () => Interlocked.Increment(ref otherRan));
        Assert.NotNull(healthy);
        Assert.True(healthy.Wait(Wait));
        Assert.Equal(1, otherRan);

        release.Set();
        Assert.True(stuck.Wait(Wait));
        Assert.True(recovered.Wait(Wait));
        var again = updater.Request(monitor, "/gpu-powermonitor/COM4", "WireView", Nothing, Nothing);
        Assert.NotNull(again);
        Assert.True(again.Wait(Wait));
    }

    [Fact]
    public void Requests_against_a_stuck_device_never_add_threads()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        object monitor = new();
        var job = updater.Request(monitor, "/gpu-powermonitor/COM4", "WireView", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));
        var before = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;

        for (var i = 0; i < 200; i++)
        {
            updater.Request(monitor, "/gpu-powermonitor/COM4", "WireView", Nothing, Nothing);
        }

        Assert.True(System.Diagnostics.Process.GetCurrentProcess().Threads.Count <= before + 2);
        release.Set();
        Assert.True(job!.Wait(Wait));
    }
}
