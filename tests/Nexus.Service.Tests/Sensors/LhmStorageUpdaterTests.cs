using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmStorageUpdaterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(60);

    private static LhmStorageUpdater New(
        TimeSpan? overdue = null,
        Action<string, string, TimeSpan>? onOverdue = null,
        Action<string, string>? onRecovered = null) =>
        new(Grace, overdue ?? TimeSpan.FromSeconds(30), onOverdue, onRecovered);

    private static readonly Action Nothing = () => { };

    [Fact]
    public void A_stuck_drive_does_not_block_the_requesting_thread_or_other_drives()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var healthyRan = false;

        var stuck = updater.Request("/hdd/0", "stuck", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.NotNull(stuck);
        Assert.True(started.Wait(Wait));
        var healthy = updater.Request("/nvme/0", "healthy", Nothing, () => healthyRan = true);

        Assert.NotNull(healthy);
        Assert.True(healthy.Wait(Wait));
        Assert.True(healthyRan);
        Assert.True(updater.IsBusy("/hdd/0"));
        release.Set();
        Assert.True(stuck.Wait(Wait));
    }

    [Fact]
    public void A_drive_with_an_update_in_flight_is_not_requested_again()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        var runs = 0;

        var first = updater.Request("/hdd/0", "d", Nothing, () => { Interlocked.Increment(ref runs); release.Wait(Wait); });
        Assert.NotNull(first);
        Assert.Null(updater.Request("/hdd/0", "d", Nothing, () => Interlocked.Increment(ref runs)));

        release.Set();
        Assert.True(first.Wait(Wait));
        Assert.True(updater.WaitIdle("/hdd/0", Wait));
        Assert.Equal(1, runs);
        var again = updater.Request("/hdd/0", "d", Nothing, () => Interlocked.Increment(ref runs));
        Assert.NotNull(again);
        Assert.True(again.Wait(Wait));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Each_update_reads_its_own_cycle_setting_even_while_another_drive_is_stuck()
    {
        using var updater = New();
        using var release = new ManualResetEventSlim();
        var current = 0;
        var seenByStuck = 0;
        var seenByOther = 0;
        using var stuckRead = new ManualResetEventSlim();

        var stuck = updater.Request("/hdd/0", "a", () => current = 1, () => { seenByStuck = current; stuckRead.Set(); release.Wait(Wait); });
        var other = updater.Request("/hdd/1", "b", () => current = 2, () => seenByOther = current);

        Assert.NotNull(stuck);
        Assert.NotNull(other);
        Assert.True(stuckRead.Wait(Wait));
        Assert.True(other.Wait(Wait));
        Assert.Equal(1, seenByStuck);
        Assert.Equal(2, seenByOther);
        release.Set();
        Assert.True(stuck.Wait(Wait));
    }

    [Fact]
    public void A_drive_past_the_overdue_limit_is_reported_once_and_again_on_recovery()
    {
        var overdue = new List<string>();
        var recovered = new List<string>();
        using var overdueSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var updater = New(
            TimeSpan.FromMilliseconds(80),
            (id, name, _) => { lock (overdue) { overdue.Add($"{id}|{name}"); } overdueSeen.Set(); },
            (id, name) => { lock (recovered) { recovered.Add($"{id}|{name}"); } });

        var job = updater.Request("/hdd/0", "ST4000", Nothing, () => release.Wait(Wait));
        Assert.NotNull(job);
        Assert.True(overdueSeen.Wait(Wait));
        Thread.Sleep(250);
        release.Set();
        Assert.True(job.Wait(Wait));
        Assert.True(updater.WaitIdle("/hdd/0", Wait));

        lock (overdue) { Assert.Equal(new[] { "/hdd/0|ST4000" }, overdue); }
        lock (recovered) { Assert.Equal(new[] { "/hdd/0|ST4000" }, recovered); }
    }

    [Fact]
    public void A_fast_update_never_reports_overdue()
    {
        var calls = 0;
        using var updater = New(TimeSpan.FromMilliseconds(500), (_, _, _) => Interlocked.Increment(ref calls), (_, _) => Interlocked.Increment(ref calls));

        var job = updater.Request("/hdd/0", "d", Nothing, Nothing);
        Assert.NotNull(job);
        Assert.True(job.Wait(Wait));
        Thread.Sleep(700);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_throwing_update_does_not_kill_the_worker()
    {
        using var updater = New();
        var ran = false;

        var first = updater.Request("/hdd/0", "d", Nothing, () => throw new InvalidOperationException("drive gone"));
        Assert.NotNull(first);
        Assert.True(first.Wait(Wait));
        Assert.True(updater.WaitIdle("/hdd/0", Wait));
        var second = updater.Request("/hdd/0", "d", Nothing, () => ran = true);
        Assert.NotNull(second);
        Assert.True(second.Wait(Wait));

        Assert.True(ran);
    }

    [Fact]
    public void Shutdown_waits_a_bounded_time_for_a_stuck_update_and_refuses_new_work()
    {
        var updater = New();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        updater.Request("/hdd/0", "d", Nothing, () => { started.Set(); release.Wait(Wait); });
        Assert.True(started.Wait(Wait));

        Assert.False(updater.Shutdown(TimeSpan.FromMilliseconds(80)));
        Assert.Null(updater.Request("/hdd/1", "e", Nothing, Nothing));

        release.Set();
        Assert.True(updater.Shutdown(Wait));
    }

    [Fact]
    public void Shutdown_returns_at_once_when_idle()
    {
        var updater = New();
        var job = updater.Request("/hdd/0", "d", Nothing, Nothing);
        Assert.NotNull(job);
        Assert.True(job.Wait(Wait));
        Assert.True(updater.WaitIdle("/hdd/0", Wait));

        Assert.True(updater.Shutdown(TimeSpan.Zero));
    }
}
