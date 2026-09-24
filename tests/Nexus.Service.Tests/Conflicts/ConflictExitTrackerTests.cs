using System;
using System.Collections.Generic;
using Nexus.Service.Conflicts;
using Nexus.Service.Models.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The scan-to-scan decision behind the conflict-exit daemon restart: which
/// apps count, the relaunch window that cancels it, the floor between two, and
/// the per-app cap.
/// </summary>
public class ConflictExitTrackerTests
{
    private static readonly long Debounce = (long)ConflictExitTracker.Debounce.TotalMilliseconds;
    private static readonly long MinInterval = (long)ConflictExitTracker.MinInterval.TotalMilliseconds;
    private static readonly long CapWindow = (long)ConflictExitTracker.CapWindow.TotalMilliseconds;

    private static DetectedConflict App(string id) => new() { Id = id, DisplayName = id, Pid = 100 };

    private static IReadOnlyList<DetectedConflict> Running(params string[] ids)
    {
        var list = new List<DetectedConflict>(ids.Length);
        foreach (var id in ids) list.Add(App(id));
        return list;
    }

    private static IReadOnlyList<string>? Observe(ConflictExitTracker tracker, IReadOnlyList<DetectedConflict> detected, long nowMs) =>
        tracker.Observe(detected, nowMs, out _);

    [Fact]
    public void AnExitRestartsAfterTheDebounce()
    {
        var tracker = new ConflictExitTracker();
        Assert.Null(Observe(tracker, Running("icue"), 0));
        Assert.Null(Observe(tracker, Running(), 1_000));
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce - 1));
        var names = Observe(tracker, Running(), 1_000 + Debounce);
        Assert.Equal(new[] { "Corsair iCUE" }, names);
    }

    [Fact]
    public void TheFirstScanIsABaselineNotAnExit()
    {
        var tracker = new ConflictExitTracker();
        Assert.Null(Observe(tracker, Running(), 0));
        Assert.Null(Observe(tracker, Running(), Debounce * 10));
    }

    [Fact]
    public void ARelaunchInsideTheDebounceCancelsTheRestart()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("icue"), 0);
        Observe(tracker, Running(), 1_000);
        Assert.Null(Observe(tracker, Running("icue"), 2_000));
        Assert.Null(Observe(tracker, Running("icue"), 2_000 + Debounce * 2));
    }

    [Fact]
    public void ARelaunchOfOneExitedAppKeepsTheRestartForTheOther()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("icue", "signalrgb"), 0);
        Observe(tracker, Running(), 1_000);
        Assert.Null(Observe(tracker, Running("icue"), 2_000));
        var names = Observe(tracker, Running("icue"), 2_000 + Debounce);
        Assert.Equal(new[] { "SignalRGB" }, names);
    }

    [Fact]
    public void ExitsCloseTogetherCoalesceIntoOneRestart()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("icue", "signalrgb"), 0);
        Observe(tracker, Running("signalrgb"), 1_000);
        Assert.Null(Observe(tracker, Running(), 3_000));
        // The second exit re-arms the window, so the first alone never fires.
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce));
        var names = Observe(tracker, Running(), 3_000 + Debounce);
        Assert.Equal(new[] { "Corsair iCUE", "SignalRGB" }, names);
        Assert.Null(Observe(tracker, Running(), 3_000 + Debounce + 1_000));
    }

    [Fact]
    public void ASecondRestartWaitsOutTheFloor()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("icue"), 0);
        Observe(tracker, Running(), 1_000);
        Assert.NotNull(Observe(tracker, Running(), 1_000 + Debounce));
        var firstRestart = 1_000 + Debounce;

        Observe(tracker, Running("icue"), firstRestart + 1_000);
        Observe(tracker, Running(), firstRestart + 2_000);
        Assert.Null(Observe(tracker, Running(), firstRestart + 2_000 + Debounce));
        Assert.Null(Observe(tracker, Running(), firstRestart + MinInterval - 1));
        Assert.NotNull(Observe(tracker, Running(), firstRestart + MinInterval));
    }

    [Fact]
    public void AnAppPastTheCapIsIgnoredUntilTheWindowRolls()
    {
        var tracker = new ConflictExitTracker();
        long now = 0;
        Observe(tracker, Running("icue"), now);
        for (var i = 0; i < ConflictExitTracker.MaxRestartsPerWindow; i++)
        {
            now += MinInterval;
            Observe(tracker, Running(), now);
            now += Debounce;
            Assert.NotNull(Observe(tracker, Running(), now));
            Observe(tracker, Running("icue"), now + 1_000);
            now += 1_000;
        }

        // Exit number MaxRestartsPerWindow + 1: named once as capped, then silent.
        now += MinInterval;
        Assert.Null(tracker.Observe(Running(), now, out var capped));
        Assert.Equal(new[] { "Corsair iCUE" }, capped);
        Assert.Null(tracker.Observe(Running(), now + Debounce * 2, out capped));
        Assert.Null(capped);
        tracker.Observe(Running("icue"), now + Debounce * 3, out _);
        Assert.Null(tracker.Observe(Running(), now + Debounce * 4, out capped));
        Assert.Null(capped);

        // A fresh window forgets the count.
        now += CapWindow + 1;
        Observe(tracker, Running("icue"), now);
        Observe(tracker, Running(), now + MinInterval);
        Assert.NotNull(Observe(tracker, Running(), now + MinInterval + Debounce));
    }

    [Fact]
    public void TheCapIsPerApp()
    {
        var tracker = new ConflictExitTracker();
        long now = 0;
        Observe(tracker, Running("icue", "signalrgb"), now);
        for (var i = 0; i < ConflictExitTracker.MaxRestartsPerWindow; i++)
        {
            now += MinInterval;
            Observe(tracker, Running("signalrgb"), now);
            now += Debounce;
            Assert.NotNull(Observe(tracker, Running("signalrgb"), now));
            now += 1_000;
            Observe(tracker, Running("icue", "signalrgb"), now);
        }

        now += MinInterval;
        Assert.Null(tracker.Observe(Running(), now, out var capped));
        Assert.Equal(new[] { "Corsair iCUE" }, capped);
        Assert.Equal(new[] { "SignalRGB" }, tracker.Observe(Running(), now + Debounce, out _));
    }

    [Fact]
    public void APeripheralSuiteIsIgnored()
    {
        // Deck, mouse and keyboard tools hold no OpenRGB controller; a restart would blink the lighting for nothing.
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("elgato-stream-deck", "razer-synapse", "fan-control"), 0);
        Observe(tracker, Running(), 1_000);
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce * 2));
    }

    [Fact]
    public void AnUnknownIdIsIgnored()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("not-in-catalog"), 0);
        Observe(tracker, Running(), 1_000);
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce * 2));
    }

    [Fact]
    public void ResetDropsThePendingRestartAndTheBaseline()
    {
        var tracker = new ConflictExitTracker();
        Observe(tracker, Running("icue"), 0);
        Observe(tracker, Running(), 1_000);
        tracker.Reset();
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce));
        Assert.Null(Observe(tracker, Running(), 1_000 + Debounce * 3));
    }

    [Theory]
    [InlineData("hyte-nexus-2", true)]
    [InlineData("signalrgb", true)]
    [InlineData("openrgb", true)]
    [InlineData("icue", true)]
    [InlineData("razer-chroma-sdk", true)]
    [InlineData("razer-synapse", false)]
    [InlineData("elgato-stream-deck", false)]
    [InlineData("nzxt-kraken", false)]
    [InlineData("msi-afterburner", false)]
    [InlineData("", false)]
    public void CompetesForOpenRgbFollowsTheCatalog(string id, bool expected)
    {
        Assert.Equal(expected, ConflictExitTracker.CompetesForOpenRgb(id));
    }
}

/// <summary>The hosted service's tick against the tracker: the bridge gate and the restart call.</summary>
public class ConflictExitRecoveryTickTests
{
    private sealed class FakeDetector : IConflictDetector
    {
        public IReadOnlyList<DetectedConflict> Conflicts = Array.Empty<DetectedConflict>();
        public bool IsAppRunning(string appId) => false;
        public IReadOnlyList<DetectedConflict> GetConflicts() => Conflicts;
        public bool DetectionReady => true;
    }

    private static readonly long Debounce = (long)ConflictExitTracker.Debounce.TotalMilliseconds;

    private static DetectedConflict App(string id) => new() { Id = id, DisplayName = id, Pid = 100 };

    [Fact]
    public void AnExitWhileTheBridgeIsActiveRestartsTheDaemon()
    {
        var detector = new FakeDetector { Conflicts = new[] { App("icue") } };
        var restarts = 0;
        var recovery = new ConflictExitRecovery(detector, () => true, () => restarts++);

        recovery.Tick(0);
        detector.Conflicts = Array.Empty<DetectedConflict>();
        recovery.Tick(1_000);
        Assert.Equal(0, restarts);
        recovery.Tick(1_000 + Debounce);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void AnInactiveBridgeNeverRestartsAndDropsTheBaseline()
    {
        var detector = new FakeDetector { Conflicts = new[] { App("icue") } };
        var restarts = 0;
        var active = true;
        var recovery = new ConflictExitRecovery(detector, () => active, () => restarts++);

        recovery.Tick(0);
        active = false;
        detector.Conflicts = Array.Empty<DetectedConflict>();
        recovery.Tick(1_000);
        recovery.Tick(1_000 + Debounce * 2);
        Assert.Equal(0, restarts);

        // Back on: the app is already gone, so its absence is the baseline, not an exit.
        active = true;
        recovery.Tick(1_000 + Debounce * 3);
        recovery.Tick(1_000 + Debounce * 5);
        Assert.Equal(0, restarts);
    }
}
