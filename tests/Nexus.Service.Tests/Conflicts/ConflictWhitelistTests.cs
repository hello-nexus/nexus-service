using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictWhitelistTests
{
    private const string Elgato = "elgato-stream-deck";
    private const string Kanali = "tryx-kanali";
    private const string LConnect = "lian-li-l-connect";
    private const string ICue = "icue";
    private const string SignalRgb = "signalrgb";
    private const string Nexus2 = "hyte-nexus-2";

    private sealed class FakeDetector : IConflictDetector
    {
        public IReadOnlyList<DetectedConflict> Conflicts = Array.Empty<DetectedConflict>();
        public bool IsAppRunning(string appId) => Conflicts.Any(c => c.Id == appId);
        public IReadOnlyList<DetectedConflict> GetConflicts() => Conflicts;
        public bool DetectionReady => true;
    }

    private static DetectedConflict App(string id, int pid = 100) => new() { Id = id, DisplayName = id, Pid = pid };

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

    [Theory]
    [InlineData("streamdeck")]
    [InlineData("tryx")]
    [InlineData("nzxt-kraken")]
    [InlineData("zmatrices-lcd")]
    public void ADeviceWhoseAppStartsWhitelistedDefaultsOff(string handlerId)
    {
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
    }

    [Fact]
    public void AFreshInstallStartsWithTheCatalogDefaultsWhitelisted()
    {
        var whitelist = new UiSettings().ConflictAutoKillExclusions;

        Assert.Contains(Elgato, whitelist);
        Assert.Contains(Kanali, whitelist);
        Assert.Contains("msi-super-charger", whitelist);
        Assert.Contains(LConnect, whitelist);
        Assert.Contains(ICue, whitelist);
        Assert.Contains("nzxt-cam", whitelist);
        Assert.Contains("zmatrices", whitelist);
        Assert.DoesNotContain("signalrgb", whitelist);
    }

    [Theory]
    [InlineData("corsair-link-lcd", ICue)]
    [InlineData("lianli-screen88", LConnect)]
    [InlineData("lianli-galahad2-lcd", LConnect)]
    public void EveryThirdPartyDeviceNamesItsVendorApp(string handlerId, string appId)
    {
        Assert.Equal(appId, DeviceControlPolicy.ConflictAppFor(handlerId));
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
    }

    [Fact]
    public void UpgradeKeepsAKrakenThatNeverChoseOn()
    {
        var doc = new NexusSettings();

        ConflictWhitelistMigration.KeepThirdPartyDevicesOn(doc);

        Assert.Contains("nzxt-kraken", doc.Devices.NexusControlEnabled);
        Assert.Contains("zmatrices-lcd", doc.Devices.NexusControlEnabled);
    }

    [Fact]
    public void AnInstallOlderThanV19KeepsEndingTheAppsWhitelistedSince()
    {
        var doc = new NexusSettings();
        doc.Ui.ConflictAutoKillExclusions = new List<string>();

        ConflictWhitelistMigration.Apply(doc);
        ConflictWhitelistMigration.KeepThirdPartyDevicesOn(doc);

        Assert.Contains("nzxt-kraken", doc.Devices.NexusControlEnabled);
        Assert.DoesNotContain("nzxt-cam", doc.Ui.ConflictAutoKillExclusions);
        Assert.DoesNotContain("zmatrices", doc.Ui.ConflictAutoKillExclusions);
        Assert.DoesNotContain(ICue, doc.Ui.ConflictAutoKillExclusions);
        Assert.DoesNotContain(LConnect, doc.Ui.ConflictAutoKillExclusions);
        Assert.Contains("msi-super-charger", doc.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public async Task AWindowOpenedBeforeOnboardingNeverRunsTheFullSweep()
    {
        var store = new InMemoryConfigStore();
        var shutdown = new ConflictStartupShutdown(store, new FakeDetector(), NullLogger<ConflictStartupShutdown>.Instance,
            _ => { }, _ => false);

        await shutdown.StartAsync(CancellationToken.None);
        try
        {
            store.Update(s =>
            {
                s.OnboardingCompleted = true;
                s.FeaturesOnboardingCompleted = true;
                s.LightingOnboardingCompleted = true;
            });

            Assert.True(shutdown.LaunchWindowOpen);
            Assert.False(shutdown.InLaunchKillWindow);
        }
        finally
        {
            await shutdown.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void UpgradeLeavesAKrakenTheUserTurnedOffOff()
    {
        var doc = new NexusSettings();
        doc.Devices.NexusControlDisabled = new List<string> { "nzxt-kraken" };

        ConflictWhitelistMigration.KeepThirdPartyDevicesOn(doc);

        Assert.DoesNotContain("nzxt-kraken", doc.Devices.NexusControlEnabled);
    }

    [Fact]
    public void TurningNexusControlOnTakesTheAppOffTheWhitelist()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("tryx", true);

        Assert.DoesNotContain(Kanali, store.Load().Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void TurningNexusControlOffPutsTheAppOnTheWhitelist()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("tryx", true);

        gate.SetEnabled("tryx", false);

        Assert.Contains(Kanali, store.Load().Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void AnAppStaysOffTheWhitelistWhileNexusStillDrivesAnotherOfItsDevices()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("lianli", true);
        gate.SetEnabled("lianli-wireless", true);

        gate.SetEnabled("lianli", false);
        Assert.DoesNotContain(LConnect, store.Load().Ui.ConflictAutoKillExclusions);

        gate.SetEnabled("lianli-wireless", false);
        Assert.Contains(LConnect, store.Load().Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void UpgradeKeepsAUsedStreamDeckAndTryxOnAndLeavesTheirAppsOffTheWhitelist()
    {
        var doc = new NexusSettings();
        doc.Ui.ConflictAutoKillExclusions = new List<string> { "gcc" };
        doc.StreamDeck.Decks["SERIAL"] = new PhysicalDeckSettings();

        ConflictWhitelistMigration.Apply(doc);

        Assert.Contains("streamdeck", doc.Devices.NexusControlEnabled);
        Assert.Contains("tryx", doc.Devices.NexusControlEnabled);
        Assert.DoesNotContain(Elgato, doc.Ui.ConflictAutoKillExclusions);
        Assert.DoesNotContain(Kanali, doc.Ui.ConflictAutoKillExclusions);
        Assert.Contains("gcc", doc.Ui.ConflictAutoKillExclusions);
        Assert.Contains("msi-super-charger", doc.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void UpgradeWithoutADeckWhitelistsElgatoAndRespectsAnExplicitOff()
    {
        var doc = new NexusSettings();
        doc.Ui.ConflictAutoKillExclusions = new List<string>();
        doc.Devices.NexusControlDisabled = new List<string> { "tryx" };

        ConflictWhitelistMigration.Apply(doc);

        Assert.DoesNotContain("streamdeck", doc.Devices.NexusControlEnabled);
        Assert.DoesNotContain("tryx", doc.Devices.NexusControlEnabled);
        Assert.Contains(Elgato, doc.Ui.ConflictAutoKillExclusions);
        Assert.Contains(Kanali, doc.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void TheShutdownEndsOnlyAppsOffTheWhitelistAndReportsThem()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { Elgato });
        var detector = new FakeDetector { Conflicts = new[] { App(Elgato), App(LConnect) } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);
        var reported = new List<string>();
        shutdown.AppsTerminated += names => reported.AddRange(names);

        shutdown.EndRunningApps();

        Assert.Equal(new[] { LConnect }, killed);
        Assert.Equal(new[] { "Lian Li L-Connect" }, reported);
    }

    [Fact]
    public void EndOnLaunchIsOffByDefault()
    {
        Assert.False(new UiSettings().EndConflictsOnLaunch);
    }

    [Fact]
    public void EndOnLaunchEndsOnlyAppsOffTheWhitelist()
    {
        var store = OnboardedStore();
        store.Update(s =>
        {
            s.Ui.EndConflictsOnLaunch = true;
            s.Ui.ConflictAutoKillExclusions = new List<string> { Elgato };
        });
        var detector = new FakeDetector { Conflicts = new[] { App(Elgato), App(ICue) } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndOnLaunchTick();

        Assert.Equal(new[] { ICue }, killed);
        Assert.True(shutdown.InLaunchKillWindow);
    }

    [Fact]
    public void EndOnLaunchEndsARelaunchAgainButReportsItOnce()
    {
        var store = OnboardedStore();
        store.Update(s => s.Ui.EndConflictsOnLaunch = true);
        var detector = new FakeDetector { Conflicts = new[] { App(SignalRgb, pid: 1) } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);
        var notices = 0;
        shutdown.AppsTerminated += _ => notices++;

        shutdown.EndOnLaunchTick();
        detector.Conflicts = new[] { App(SignalRgb, pid: 2) };
        shutdown.EndOnLaunchTick();

        Assert.Equal(new[] { SignalRgb, SignalRgb }, killed);
        Assert.Equal(1, notices);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EndOnLaunchNeedsTheSwitchAndOnboarding(bool switchOn, bool onboarded)
    {
        var store = onboarded ? OnboardedStore() : new InMemoryConfigStore();
        store.Update(s => s.Ui.EndConflictsOnLaunch = switchOn);
        var detector = new FakeDetector { Conflicts = new[] { App(SignalRgb) } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndOnLaunchTick();

        Assert.Empty(killed);
        Assert.False(shutdown.InLaunchKillWindow);
    }

    [Fact]
    public void Nexus2IsEndedWithTheSweepOffAndOnTheWhitelist()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Ui.AutoKillConflictsAtStartup = false;
            s.Ui.ConflictAutoKillExclusions = new List<string> { Nexus2 };
        });
        var detector = new FakeDetector { Conflicts = new[] { App(Nexus2), App(SignalRgb) } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndRunningApps(ConflictStartupShutdown.SweepAllowed(store.Load()));

        Assert.Equal(new[] { Nexus2 }, killed);
    }

    [Fact]
    public async Task Nexus2IsEndedAtServiceStartBeforeOnboarding()
    {
        var store = new InMemoryConfigStore();
        var detector = new FakeDetector { Conflicts = new[] { App(Nexus2), App(SignalRgb) } };
        var killed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.TrySetResult(def.Id), _ => false);

        await shutdown.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(Nexus2, await killed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(shutdown.LaunchWindowOpen);
            Assert.False(shutdown.InLaunchKillWindow);
        }
        finally
        {
            await shutdown.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void TheShutdownRetriesAnAppOnlyWhenItComesBackUnderANewPid()
    {
        var store = new InMemoryConfigStore();
        var detector = new FakeDetector { Conflicts = new[] { App(SignalRgb, pid: 100) } };
        var killed = 0;
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            _ => killed++, _ => true);

        shutdown.EndRunningApps();
        shutdown.EndRunningApps();
        Assert.Equal(1, killed);

        detector.Conflicts = new[] { App(SignalRgb, pid: 200) };
        shutdown.EndRunningApps();
        Assert.Equal(2, killed);
    }

    [Fact]
    public async Task TheLaunchNoticeStaysQuietWhileTheShutdownOwnsLaunches()
    {
        var store = OnboardedStore();
        var detector = new FakeDetector();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            _ => { }, _ => true);
        var notifier = new ConflictLaunchNotifier(detector, store, shutdown);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        await shutdown.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(shutdown.InLaunchKillWindow);
            notifier.Tick(0);
            detector.Conflicts = new[] { App(SignalRgb) };
            notifier.Tick(6_000);

            Assert.Empty(launched);
        }
        finally
        {
            await shutdown.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ASilencedLaunchDoesNotStartTheCooldown()
    {
        var tracker = new ConflictLaunchTracker();
        tracker.Observe(Array.Empty<DetectedConflict>(), 0);

        Assert.Empty(tracker.Observe(new[] { App(LConnect) }, 1_000, _ => true));
        tracker.Observe(Array.Empty<DetectedConflict>(), 2_000);

        Assert.Single(tracker.Observe(new[] { App(LConnect) }, 3_000));
    }

    [Fact]
    public async Task TheWindowStaysClosedBeforeOnboarding()
    {
        var shutdown = new ConflictStartupShutdown(new InMemoryConfigStore(), new FakeDetector(),
            NullLogger<ConflictStartupShutdown>.Instance, _ => { }, _ => true);

        await shutdown.StartAsync(CancellationToken.None);

        Assert.False(shutdown.InLaunchKillWindow);
    }
}
