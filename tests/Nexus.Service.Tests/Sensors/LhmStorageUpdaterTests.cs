using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmStorageUpdaterTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_stuck_drive_does_not_block_the_requesting_thread()
    {
        using var updater = new LhmStorageUpdater();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();

        var queued = updater.Request("/hdd/0", () => { started.Set(); release.Wait(Wait); });

        Assert.True(queued);
        Assert.True(started.Wait(Wait));
        Assert.True(updater.IsBusy("/hdd/0"));
        release.Set();
        Assert.True(updater.WaitIdle(Wait));
    }

    [Fact]
    public void A_drive_with_an_update_in_flight_is_not_requested_again()
    {
        using var updater = new LhmStorageUpdater();
        using var release = new ManualResetEventSlim();
        var runs = 0;

        Assert.True(updater.Request("/hdd/0", () => { Interlocked.Increment(ref runs); release.Wait(Wait); }));
        Assert.False(updater.Request("/hdd/0", () => Interlocked.Increment(ref runs)));

        release.Set();
        Assert.True(updater.WaitIdle(Wait));
        Assert.Equal(1, runs);
        Assert.True(updater.Request("/hdd/0", () => Interlocked.Increment(ref runs)));
        Assert.True(updater.WaitIdle(Wait));
        Assert.Equal(2, runs);
    }

    [Fact]
    public void WaitIdle_times_out_while_a_drive_is_stuck()
    {
        using var updater = new LhmStorageUpdater();
        using var release = new ManualResetEventSlim();
        updater.Request("/hdd/0", () => release.Wait(Wait));

        Assert.False(updater.WaitIdle(TimeSpan.FromMilliseconds(50)));

        release.Set();
        Assert.True(updater.WaitIdle(Wait));
    }

    [Fact]
    public void A_throwing_update_does_not_kill_the_worker()
    {
        using var updater = new LhmStorageUpdater();
        var ran = false;

        updater.Request("/hdd/0", () => throw new InvalidOperationException("drive gone"));
        Assert.True(updater.WaitIdle(Wait));
        updater.Request("/hdd/1", () => ran = true);
        Assert.True(updater.WaitIdle(Wait));

        Assert.True(ran);
    }

    [Fact]
    public void Updates_run_one_at_a_time_in_request_order()
    {
        using var updater = new LhmStorageUpdater();
        var order = new List<string>();

        updater.Request("a", () => { lock (order) { order.Add("a"); } });
        updater.Request("b", () => { lock (order) { order.Add("b"); } });
        Assert.True(updater.WaitIdle(Wait));

        Assert.Equal(new[] { "a", "b" }, order);
    }
}
