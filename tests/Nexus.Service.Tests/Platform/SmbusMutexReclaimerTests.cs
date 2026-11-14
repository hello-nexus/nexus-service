using Nexus.Service.Platform;

namespace Nexus.Service.Tests.Platform;

public class SmbusMutexReclaimerTests
{
    private static string UniqueName() => "Nexus.Test.Smbus." + Guid.NewGuid().ToString("N");

    // Takes the mutex on a thread that exits without releasing it, which is what a killed owner leaves behind.
    private static Mutex Abandon(string name)
    {
        var mutex = new Mutex(false, name);
        var t = new Thread(() => mutex.WaitOne());
        t.Start();
        t.Join();
        return mutex;
    }

    [Fact]
    public void A_missing_mutex_is_left_alone()
    {
        Assert.Equal(SmbusMutexReclaimer.Outcome.NotPresent, SmbusMutexReclaimer.Reclaim(UniqueName(), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void A_free_mutex_is_acquired_and_released()
    {
        var name = UniqueName();
        using var keep = new Mutex(false, name);
        Assert.Equal(SmbusMutexReclaimer.Outcome.Clean, SmbusMutexReclaimer.Reclaim(name, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task An_abandoned_mutex_is_reclaimed_and_a_later_waiter_acquires_it()
    {
        var name = UniqueName();
        using var keep = Abandon(name);
        Assert.Equal(SmbusMutexReclaimer.Outcome.Reclaimed, SmbusMutexReclaimer.Reclaim(name, TimeSpan.FromMilliseconds(500)));
        var acquired = await Task.Factory.StartNew(() =>
        {
            using var m = Mutex.OpenExisting(name);
            var got = m.WaitOne(TimeSpan.FromMilliseconds(500));
            if (got)
            {
                m.ReleaseMutex();
            }
            return got;
        }, TaskCreationOptions.LongRunning);
        Assert.True(acquired);
    }

    [Fact]
    public void A_mutex_held_by_a_live_thread_times_out_without_being_taken()
    {
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var keep = new Mutex(false, name);
        var owner = new Thread(() =>
        {
            keep.WaitOne();
            held.Set();
            release.Wait();
            keep.ReleaseMutex();
        })
        {
            IsBackground = true,
        };
        owner.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(SmbusMutexReclaimer.Outcome.HeldByOther, SmbusMutexReclaimer.Reclaim(name, TimeSpan.FromMilliseconds(100)));
        release.Set();
        owner.Join();
    }
}
