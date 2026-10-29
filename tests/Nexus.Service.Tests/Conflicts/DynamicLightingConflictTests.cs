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
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>Windows Dynamic Lighting as a conflict: detection, the user's whitelist, the startup/logon off-switch and its log decision.</summary>
public class DynamicLightingConflictTests
{
    private const string Id = WindowsDynamicLighting.ConflictId;

    private static WindowsDynamicLightingState State(bool available = true, bool enabled = true, int devices = 1) =>
        new() { Available = available, Enabled = enabled, DeviceCount = devices };

    private static ConflictWatcher Watcher(WindowsDynamicLightingState state, InMemoryConfigStore? store = null) =>
        new(new MultiplexHub(), null, new FeatureGates(store ?? new InMemoryConfigStore()), null, () => new(), null, null, () => state)
        {
            HandlerCacheMs = 0,
        };

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
    [InlineData(true, false, true, "switched")]
    [InlineData(false, false, false, "no console user")]
    [InlineData(true, true, false, "still on")]
    public void TheLogSaysSwitchedOffOnlyWhenTheReReadSaysOff(bool available, bool enabled, bool switchedOff, string fragment)
    {
        var (ok, message) = ConflictStartupShutdown.DescribeDynamicLightingOff(State(available, enabled), "start");

        Assert.Equal(switchedOff, ok);
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
    public void AnUnsupportedPlatformWritesNothing()
    {
        var shutdown = Shutdown(OnboardedStore(), () => null);

        shutdown.TurnOffWindowsDynamicLighting("start");
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
