using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Conflicts;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>Which scans count as a conflict app launching: the baseline, the per-app cooldown, and re-seeding after a reset.</summary>
public class ConflictLaunchTrackerTests
{
    private static readonly long Cooldown = (long)ConflictLaunchTracker.Cooldown.TotalMilliseconds;

    private static IReadOnlyList<DetectedConflict> Running(params string[] ids) =>
        ids.Select(id => new DetectedConflict { Id = id, DisplayName = id, Pid = 100 }).ToList();

    private static string[] Launched(ConflictLaunchTracker tracker, IReadOnlyList<DetectedConflict> running, long nowMs) =>
        tracker.Observe(running, nowMs).Select(a => a.Id).ToArray();

    [Fact]
    public void AppsRunningAtTheFirstScanAreTheBaseline()
    {
        var tracker = new ConflictLaunchTracker();
        Assert.Empty(Launched(tracker, Running("gcc", "icue"), 0));
        Assert.Empty(Launched(tracker, Running("gcc", "icue"), 6_000));
    }

    [Fact]
    public void AnAppAppearingAfterTheBaselineIsALaunch()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        Assert.Equal(new[] { "gcc" }, Launched(tracker, Running("gcc"), 6_000));
        Assert.Empty(Launched(tracker, Running("gcc"), 12_000));
    }

    [Fact]
    public void ARelaunchInsideTheCooldownIsNotAnnouncedAgain()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        Launched(tracker, Running("gcc"), 1_000);
        Launched(tracker, Running(), 2_000);
        Assert.Empty(Launched(tracker, Running("gcc"), 1_000 + Cooldown - 1));
    }

    [Fact]
    public void ARelaunchAfterTheCooldownIsAnnounced()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        Launched(tracker, Running("gcc"), 1_000);
        Launched(tracker, Running(), 2_000);
        Assert.Equal(new[] { "gcc" }, Launched(tracker, Running("gcc"), 1_000 + Cooldown));
    }

    [Fact]
    public void TheCooldownIsPerApp()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        Launched(tracker, Running("gcc"), 1_000);
        Assert.Equal(new[] { "icue" }, Launched(tracker, Running("gcc", "icue"), 2_000));
    }

    [Fact]
    public void ADuplicateIdInOneScanIsOneLaunch()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        Assert.Equal(new[] { "gcc" }, Launched(tracker, Running("gcc", "gcc"), 1_000));
    }

    [Fact]
    public void AResetReseedsTheBaseline()
    {
        var tracker = new ConflictLaunchTracker();
        Launched(tracker, Running(), 0);
        tracker.Reset();
        Assert.Empty(Launched(tracker, Running("gcc"), 1_000));
    }
}

/// <summary>The hosted service's tick: the setting gates both the scan and the event.</summary>
public class ConflictLaunchNotifierTickTests
{
    private sealed class FakeDetector : IConflictDetector
    {
        public IReadOnlyList<DetectedConflict> Conflicts = Array.Empty<DetectedConflict>();
        public int Scans;
        public bool IsAppRunning(string appId) => Conflicts.Any(c => c.Id == appId);
        public IReadOnlyList<DetectedConflict> GetConflicts() { Scans++; return Conflicts; }
        public bool DetectionReady => true;
    }

    private sealed class MemoryConfigStore : IConfigStore
    {
        public readonly NexusSettings Settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) { mutator(Settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private static DetectedConflict App(string id) => new() { Id = id, DisplayName = id, Pid = 100 };

    [Fact]
    public void ALaunchRaisesTheEvent()
    {
        var detector = new FakeDetector();
        var notifier = new ConflictLaunchNotifier(detector, new MemoryConfigStore());
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        detector.Conflicts = new[] { App("gcc") };
        notifier.Tick(6_000);

        Assert.Equal(new[] { "gcc" }, launched);
    }

    [Fact]
    public void AnAppExcludedFromTheStartupShutdownIsNotAnnounced()
    {
        var detector = new FakeDetector();
        var store = new MemoryConfigStore();
        store.Settings.Ui.ConflictAutoKillExclusions = new List<string> { "GCC" };
        var notifier = new ConflictLaunchNotifier(detector, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        detector.Conflicts = new[] { App("gcc"), App("icue") };
        notifier.Tick(6_000);

        Assert.Equal(new[] { "icue" }, launched);
    }

    [Fact]
    public void SwitchedOffItNeitherScansNorNotifies()
    {
        var detector = new FakeDetector();
        var store = new MemoryConfigStore();
        store.Settings.Ui.NotifyConflictLaunches = false;
        var notifier = new ConflictLaunchNotifier(detector, store);
        var launched = 0;
        notifier.AppLaunched += _ => launched++;

        notifier.Tick(0);
        detector.Conflicts = new[] { App("gcc") };
        notifier.Tick(6_000);

        Assert.Equal(0, detector.Scans);
        Assert.Equal(0, launched);
    }

    [Fact]
    public void SwitchingOnDoesNotAnnounceAppsAlreadyRunning()
    {
        var detector = new FakeDetector();
        var store = new MemoryConfigStore();
        var notifier = new ConflictLaunchNotifier(detector, store);
        var launched = 0;
        notifier.AppLaunched += _ => launched++;

        notifier.Tick(0);
        store.Settings.Ui.NotifyConflictLaunches = false;
        notifier.Tick(6_000);
        detector.Conflicts = new[] { App("gcc") };
        store.Settings.Ui.NotifyConflictLaunches = true;
        notifier.Tick(12_000);

        Assert.Equal(0, launched);
    }
}

/// <summary>The launch notice is on by default, including for a settings file written before the field existed.</summary>
public class ConflictLaunchNotifySettingsTests
{
    [Fact]
    public void TheLaunchNoticeIsOnByDefault()
    {
        Assert.True(new UiSettings().NotifyConflictLaunches);
    }

    [Fact]
    public void ASettingsFileWithoutTheFieldReadsAsOn()
    {
        var ui = JsonSerializer.Deserialize("{\"autoKillConflictsAtStartup\":false}", AppJsonContext.Default.UiSettings);
        Assert.NotNull(ui);
        Assert.True(ui!.NotifyConflictLaunches);
    }

    [Fact]
    public void ResettingTheDashboardCategoryRestoresTheDefault()
    {
        var settings = new NexusSettings { Ui = { NotifyConflictLaunches = false } };
        ProfileSharing.ResetCategory(settings, "dashboard");
        Assert.True(settings.Ui.NotifyConflictLaunches);
    }
}
