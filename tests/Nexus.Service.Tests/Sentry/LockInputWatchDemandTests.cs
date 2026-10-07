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
        var demand = new LockInputWatchDemand(applied.Add);
        var blackout = demand.Consumer(LockInputWatchConsumer.Blackout);
        var sentry = demand.Consumer(LockInputWatchConsumer.Sentry);

        blackout(true);
        sentry(true);
        blackout(false);
        Assert.True(demand.Armed);
        sentry(false);

        Assert.False(demand.Armed);
        Assert.Equal(new[] { true, true, true, false }, applied);
    }

    [Fact]
    public async Task Blackout_and_Sentry_both_hold_the_watch_through_an_unlock()
    {
        var store = new InMemoryConfigStore();
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        var blackout = new SleepBlackoutCoordinator(engine, store);
        var sentry = new Nexus.Service.Sentry.SentryCoordinator(
            store,
            new PanelPhonePairingService(store, new MultiplexHub()),
            new FakeCloudApiClient(),
            new FakePowerProvider(),
            readLockState: () => false);
        var applied = new List<bool>();
        var demand = new LockInputWatchDemand(applied.Add);
        blackout.LockInputWatch = demand.Consumer(LockInputWatchConsumer.Blackout);
        sentry.LockInputWatch = demand.Consumer(LockInputWatchConsumer.Sentry);

        blackout.OnSessionLocked();
        sentry.OnLockChanged(true);
        await sentry.ArmAsync(lockFirst: false);
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
    public async Task The_blackout_unlock_does_not_stop_a_watch_Sentry_still_holds()
    {
        var store = new InMemoryConfigStore();
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        var blackout = new SleepBlackoutCoordinator(engine, store);
        var sentry = new Nexus.Service.Sentry.SentryCoordinator(
            store,
            new PanelPhonePairingService(store, new MultiplexHub()),
            new FakeCloudApiClient(),
            new FakePowerProvider(),
            readLockState: () => false);
        var applied = new List<bool>();
        var demand = new LockInputWatchDemand(applied.Add);
        blackout.LockInputWatch = demand.Consumer(LockInputWatchConsumer.Blackout);
        sentry.LockInputWatch = demand.Consumer(LockInputWatchConsumer.Sentry);

        blackout.OnSessionLocked();
        sentry.OnLockChanged(true);
        await sentry.ArmAsync(lockFirst: false);

        blackout.OnSessionUnlocked();

        Assert.True(demand.Armed);
        Assert.True(applied[^1]);
        sentry.OnLockChanged(false);
        Assert.False(demand.Armed);
    }
}
