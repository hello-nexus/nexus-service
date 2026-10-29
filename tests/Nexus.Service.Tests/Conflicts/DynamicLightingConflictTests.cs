using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>Windows Dynamic Lighting as a conflict: detection, the user's whitelist, the startup/logon off-switch and its log decision.</summary>
public class DynamicLightingConflictTests
{
    private const string Id = WindowsDynamicLighting.ConflictId;

    private static WindowsDynamicLightingState State(bool available = true, bool enabled = true, int devices = 1) =>
        new() { Available = available, Enabled = enabled, DeviceCount = devices };

    private static ConflictWatcher Watcher(WindowsDynamicLightingState state, InMemoryConfigStore? store = null, Action? onProbe = null)
    {
        store ??= new InMemoryConfigStore();
        return new(new MultiplexHub(), null, new FeatureGates(store), null, () => new(), null, null, () => { onProbe?.Invoke(); return state; })
        {
            HandlerCacheMs = 0,
        };
    }

    private static InMemoryConfigStore OnboardedStore()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.OnboardingCompleted = true;
            s.FeaturesOnboardingCompleted = true;
            s.LightingOnboardingCompleted = true;
        });
        return store;
    }

    private sealed class FakeDetector : IConflictDetector
    {
        public IReadOnlyList<DetectedConflict> Conflicts = Array.Empty<DetectedConflict>();
        public bool IsAppRunning(string appId) => Conflicts.Any(c => c.Id == appId);
        public IReadOnlyList<DetectedConflict> GetConflicts() => Conflicts;
        public bool DetectionReady => true;
    }

    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(true, true, 3, true)]
    [InlineData(true, false, 1, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(false, true, 1, false)]
    public void ItIsAConflictOnlyWhileEnabledWithALampArrayPresent(bool available, bool enabled, int devices, bool conflict)
    {
        var watcher = Watcher(State(available, enabled, devices));

        var found = watcher.GetConflicts().Where(c => c.Id == Id).ToList();

        Assert.Equal(conflict, found.Count == 1);
        if (conflict)
        {
            Assert.True(found[0].IsSetting);
            Assert.Equal("Windows Dynamic Lighting", found[0].DisplayName);
            Assert.Equal(0, found[0].Pid);
        }
    }

    [Fact]
    public void ItCompetesOnlyWhileLightingIsOn()
    {
        var store = new InMemoryConfigStore();
        var watcher = Watcher(State(), store);
        store.Update(s => s.Features.Lighting = false);

        Assert.DoesNotContain(watcher.GetConflicts(), c => c.Id == Id);

        store.Update(s => s.Features.Lighting = true);
        // The probe is skipped while Lighting is off, so the pillar change rescans, as in production.
        watcher.PillarsChanged();

        Assert.Contains(watcher.GetConflicts(), c => c.Id == Id);
    }

    [Fact]
    public void TheCatalogEntryIsASettingWithNoProcessOrService()
    {
        var def = ConflictWatcher.FindById(Id)!;

        Assert.True(def.IsSetting);
        Assert.Empty(def.ProcessNames);
        Assert.Empty(def.WindowsServiceNames);
        Assert.False(def.DefaultWhitelisted);
    }

    [Fact]
    public void AWhitelistedEntryIsNeverEndedByTheSweep()
    {
        var store = OnboardedStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { Id });
        var detector = new FakeDetector { Conflicts = new[] { new DetectedConflict { Id = Id, DisplayName = "Windows Dynamic Lighting", IsSetting = true } } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndRunningApps();

        Assert.Empty(killed);
    }

    [Fact]
    public void ARunningEntryOffTheWhitelistIsEndedByTheSweepLikeAnApp()
    {
        var store = OnboardedStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        var detector = new FakeDetector { Conflicts = new[] { new DetectedConflict { Id = Id, DisplayName = "Windows Dynamic Lighting", IsSetting = true } } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndRunningApps();

        Assert.Equal(new[] { Id }, killed);
    }

    [Fact]
    public void ItJoinsEndOnLaunchWhenSwitchedOnWhileNexusRuns()
    {
        var store = OnboardedStore();
        store.Update(s => s.Ui.EndConflictsOnLaunch = true);
        var detector = new FakeDetector { Conflicts = new[] { new DetectedConflict { Id = Id, DisplayName = "x", IsSetting = true } } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndOnLaunchTick();

        Assert.Equal(new[] { Id }, killed);
    }

    [Fact]
    public void TheNotifierAnnouncesItSwitchingOnLikeAnAppLaunch()
    {
        var detector = new FakeDetector();
        var notifier = new ConflictLaunchNotifier(detector, new InMemoryConfigStore());
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        detector.Conflicts = new[] { new DetectedConflict { Id = Id, DisplayName = "x", IsSetting = true } };
        notifier.Tick(6_000);

        Assert.Equal(new[] { Id }, launched);
    }

    [Fact]
    public void TheNotifierStaysQuietWhenItIsWhitelisted()
    {
        var detector = new FakeDetector();
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { Id });
        var notifier = new ConflictLaunchNotifier(detector, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        detector.Conflicts = new[] { new DetectedConflict { Id = Id, DisplayName = "x", IsSetting = true } };
        notifier.Tick(6_000);

        Assert.Empty(launched);
    }

    [Fact]
    public void ItDoesNotRestartTheOpenRgbDaemonWhenItGoesAway()
    {
        Assert.False(ConflictExitTracker.CompetesForOpenRgb(Id));
    }

    [Theory]
    [InlineData(true, false, true, false, "switched")]
    [InlineData(false, false, false, false, "nothing to turn off")]
    [InlineData(false, true, false, false, "nothing to turn off")]
    [InlineData(true, true, false, true, "still on")]
    public void TheLogSaysSwitchedOffOnlyWhenTheReReadSaysOff_AndWarnsOnlyForAWriteThatDidNotTake(
        bool available, bool enabled, bool switchedOff, bool warn, string fragment)
    {
        var (ok, shouldWarn, message) = ConflictStartupShutdown.DescribeDynamicLightingOff(State(available, enabled), "start");

        Assert.Equal(switchedOff, ok);
        Assert.Equal(warn, shouldWarn);
        Assert.Contains(fragment, message);
        Assert.Contains("(start)", message);
    }

    private static ConflictStartupShutdown Shutdown(InMemoryConfigStore store, Func<WindowsDynamicLightingState?> off, FeatureGates? gates = null) =>
        new(store, new FakeDetector(), NullLogger<ConflictStartupShutdown>.Instance, _ => { }, _ => false, null, gates, off);

    [Fact]
    public void TheOffSwitchWritesOnceAllowed()
    {
        var writes = 0;
        var shutdown = Shutdown(OnboardedStore(), () => { writes++; return State(enabled: false); });

        shutdown.TurnOffWindowsDynamicLighting("start");

        Assert.Equal(1, writes);
    }

    [Fact]
    public void TheOffSwitchSkipsAWhitelistedEntry()
    {
        var store = OnboardedStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { Id });
        var writes = 0;
        var shutdown = Shutdown(store, () => { writes++; return State(enabled: false); });

        shutdown.TurnOffWindowsDynamicLighting("start");

        Assert.Equal(0, writes);
    }

    [Fact]
    public void TheOffSwitchWaitsForOnboardingAndTheSwitch()
    {
        var writes = 0;
        var fresh = Shutdown(new InMemoryConfigStore(), () => { writes++; return State(enabled: false); });
        var switchedOff = OnboardedStore();
        switchedOff.Update(s => s.Ui.AutoKillConflictsAtStartup = false);
        var disabled = Shutdown(switchedOff, () => { writes++; return State(enabled: false); });

        fresh.TurnOffWindowsDynamicLighting("start");
        disabled.TurnOffWindowsDynamicLighting("start");

        Assert.Equal(0, writes);
    }

    [Fact]
    public void TheOffSwitchSkipsWhenLightingIsOff()
    {
        var store = OnboardedStore();
        store.Update(s => s.Features.Lighting = false);
        var writes = 0;
        var shutdown = Shutdown(store, () => { writes++; return State(enabled: false); }, new FeatureGates(store));

        shutdown.TurnOffWindowsDynamicLighting("start");

        Assert.Equal(0, writes);
    }

    [Fact]
    public void AnUnsupportedPlatformReportsNothingAndDoesNotThrow()
    {
        var calls = 0;
        var shutdown = Shutdown(OnboardedStore(), () => { calls++; return null; });

        var ex = Record.Exception(() => shutdown.TurnOffWindowsDynamicLighting("start"));

        Assert.Null(ex);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ItStaysInTheUnfilteredSetWhileLightingIsOff()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var watcher = Watcher(State(), store);

        Assert.DoesNotContain(watcher.GetConflicts(), c => c.Id == Id);
        Assert.Contains(Id, watcher.RunningIds);
    }

    [Fact]
    public void LightingComingOnWithItAlreadyOnIsARevealNotALaunch()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Features.Lighting = false);
        var watcher = Watcher(State(), store);
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        store.Update(s => s.Features.Lighting = true);
        watcher.PillarsChanged();
        notifier.Tick(6_000);

        Assert.Contains(watcher.GetConflicts(), c => c.Id == Id);
        Assert.Empty(launched);
    }

    [Fact]
    public void EndOnLaunchLeavesItAloneWhileLightingIsOff()
    {
        var store = OnboardedStore();
        store.Update(s =>
        {
            s.Features.Lighting = false;
            s.Ui.EndConflictsOnLaunch = true;
        });
        var watcher = Watcher(State(), store);
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, watcher, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndOnLaunchTick();

        Assert.Empty(killed);
    }

    [Fact]
    public void UnWhitelistingItWithItAlreadyOnIsARevealNotALaunch()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { Id });
        var watcher = Watcher(State(), store);
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        watcher.PillarsChanged();
        notifier.Tick(6_000);

        Assert.Empty(launched);
    }

    [Fact]
    public void ItsTransitionsReadAsTurnedOnAndOffWithoutAPid()
    {
        var on = new DetectedConflict { Id = Id, DisplayName = "Windows Dynamic Lighting", IsSetting = true };

        var opened = ConflictWatcher.TransitionLines(Array.Empty<string>(), new[] { on });
        var closed = ConflictWatcher.TransitionLines(new[] { Id }, Array.Empty<DetectedConflict>());

        Assert.Contains("turned on", opened.Single().Message);
        Assert.DoesNotContain("pid", opened.Single().Message);
        Assert.Contains("turned off", closed.Single().Message);
    }

    [Theory]
    [InlineData(Id, false, false)]
    [InlineData(Id, true, true)]
    [InlineData("signalrgb", false, true)]
    [InlineData("signalrgb", true, true)]
    public void OnlyTheSettingEntryNeedsALoopbackKill(string id, bool loopback, bool allowed)
    {
        Assert.Equal(allowed, ConflictRoutes.KillAllowed(ConflictWatcher.FindById(id), loopback));
    }

    [Fact]
    public void TurningItOnWhitelistsTheEntryAndTurningItOffRemovesIt()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { "icue" });

        ConflictRoutes.SetDynamicLightingWhitelisted(store, null, whitelisted: true);
        ConflictRoutes.SetDynamicLightingWhitelisted(store, null, whitelisted: true);
        Assert.Equal(new[] { "icue", Id }, store.Load().Ui.ConflictAutoKillExclusions);

        ConflictRoutes.SetDynamicLightingWhitelisted(store, null, whitelisted: false);
        Assert.Equal(new[] { "icue" }, store.Load().Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public async Task ALogonReappliesTheOffSwitch()
    {
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = Shutdown(OnboardedStore(), () => { applied.TrySetResult(true); return State(enabled: false); });

        shutdown.OnSessionLogon();

        try { Assert.True(await applied.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { await shutdown.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ALogonBeforeOnboardingWritesNothing()
    {
        var writes = 0;
        var shutdown = Shutdown(new InMemoryConfigStore(), () => { writes++; return State(enabled: false); });

        shutdown.OnSessionLogon();
        await shutdown.StopAsync(CancellationToken.None);

        Assert.Equal(0, writes);
    }
}
