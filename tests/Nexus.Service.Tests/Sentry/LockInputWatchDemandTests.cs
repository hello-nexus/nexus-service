using Nexus.Service.Lighting;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Sentry;

public sealed class LockInputWatchDemandTests
{
    [Fact]
    public void The_watch_runs_while_any_consumer_wants_it()
    {
        var applied = new List<bool>();
        var demand = new LockInputWatchDemand(3, applied.Add);
        var blackout = demand.Consumer(0);
        var sentry = demand.Consumer(2);

        blackout(true);
        sentry(true);
        blackout(false);
        Assert.True(demand.Armed);
        sentry(false);

        Assert.False(demand.Armed);
        Assert.Equal(new[] { true, true, true, false }, applied);
    }

    [Fact]
    public void Blackout_and_Sentry_both_hold_the_watch_through_an_unlock()
    {
        var store = new InMemoryConfigStore();
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        var blackout = new SleepBlackoutCoordinator(engine, store);
        var sentry = new Nexus.Service.Sentry.SentryCoordinator(
            store,
            new PanelPhonePairingService(store, new MultiplexHub()),
            new FakeCloudApiClient(),
            new FakePowerProvider());
        var applied = new List<bool>();
        var demand = new LockInputWatchDemand(2, applied.Add);
        blackout.LockInputWatch = demand.Consumer(0);
        sentry.LockInputWatch = demand.Consumer(1);

        blackout.OnSessionLocked();
        sentry.OnLockChanged(true);
        sentry.Arm(lockFirst: false);
        Assert.True(demand.Armed);

        // Sentry disarms by itself: the blackout still holds the watch.
        sentry.Disarm();
        Assert.True(demand.Armed);
        Assert.True(applied[^1]);

        // The unlock releases the blackout; with Sentry already off the watch stops.
        blackout.OnSessionUnlocked();
        Assert.False(demand.Armed);
    }

    [Fact]
    public void The_blackout_unlock_does_not_stop_a_watch_Sentry_still_holds()
    {
        var store = new InMemoryConfigStore();
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        var blackout = new SleepBlackoutCoordinator(engine, store);
        var sentry = new Nexus.Service.Sentry.SentryCoordinator(
            store,
            new PanelPhonePairingService(store, new MultiplexHub()),
            new FakeCloudApiClient(),
            new FakePowerProvider());
        var applied = new List<bool>();
        var demand = new LockInputWatchDemand(2, applied.Add);
        blackout.LockInputWatch = demand.Consumer(0);
        sentry.LockInputWatch = demand.Consumer(1);

        blackout.OnSessionLocked();
        sentry.OnLockChanged(true);
        sentry.Arm(lockFirst: false);

        blackout.OnSessionUnlocked();

        Assert.True(demand.Armed);
        Assert.True(applied[^1]);
        sentry.OnLockChanged(false);
        Assert.False(demand.Armed);
    }
}
