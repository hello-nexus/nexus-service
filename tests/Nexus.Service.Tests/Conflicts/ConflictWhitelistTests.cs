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
    public void ADeviceWhoseAppStartsWhitelistedDefaultsOff(string handlerId)
    {
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
        Assert.Null(DeviceControlPolicy.AdoptionConflictAppFor(handlerId));
    }

    [Fact]
    public void AFreshInstallStartsWithTheCatalogDefaultsWhitelisted()
    {
        var whitelist = new UiSettings().ConflictAutoKillExclusions;

        Assert.Contains(Elgato, whitelist);
        Assert.Contains(Kanali, whitelist);
        Assert.Contains("msi-super-charger", whitelist);
        Assert.DoesNotContain(LConnect, whitelist);
        Assert.DoesNotContain("signalrgb", whitelist);
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
    public void AdoptionLeavesTheDeviceOfAWhitelistedAppAlone()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string> { LConnect });
        var gate = new DeviceControlGate(store);

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, new FakeDetector()));
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
    public void TheShutdownRetriesAnAppOnlyWhenItComesBackUnderANewPid()
    {
        var store = new InMemoryConfigStore();
        var detector = new FakeDetector { Conflicts = new[] { App(LConnect, pid: 100) } };
        var killed = 0;
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            _ => killed++, _ => true);

        shutdown.EndRunningApps();
        shutdown.EndRunningApps();
        Assert.Equal(1, killed);

        detector.Conflicts = new[] { App(LConnect, pid: 200) };
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
            detector.Conflicts = new[] { App(LConnect) };
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
