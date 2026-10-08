using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Conflicts;
using Xunit;

namespace Nexus.Service.Tests;

public class VendorAppControlPauseTests
{
    private sealed class FakeDetector : IConflictDetector
    {
        public HashSet<string> Running = new();
        public int Checks;
        public bool IsAppRunning(string appId)
        {
            Checks++;
            return Running.Contains(appId);
        }
        public IReadOnlyList<DetectedConflict> GetConflicts() =>
            Running.Select(id => new DetectedConflict { Id = id, DisplayName = id }).ToList();
        public bool DetectionReady => true;
    }

    private static (DeviceControlGate Gate, FakeDetector Detector, VendorAppControlPause Pause) Setup(params string[] on) =>
        Setup(on, whitelisted: Array.Empty<string>());

    // Turning a device on takes its app off the whitelist, so the whitelist is written after.
    private static (DeviceControlGate Gate, FakeDetector Detector, VendorAppControlPause Pause) Setup(string[] on, string[] whitelisted)
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        foreach (var handler in on) gate.SetEnabled(handler, true);
        store.Update(s => s.Ui.ConflictAutoKillExclusions = whitelisted.ToList());
        var detector = new FakeDetector();
        return (gate, detector, new VendorAppControlPause(gate, detector, store));
    }

    [Fact]
    public void ADeviceTheUserTurnedOnKeepsDrivingWhileANonWhitelistedAppRuns()
    {
        var (gate, detector, pause) = Setup("lianli");

        detector.Running.Add("lian-li-l-connect");
        pause.Refresh();

        Assert.True(gate.IsEnabled("lianli"));
        Assert.Null(gate.PausedByApp("lianli"));
    }

    [Fact]
    public void TheCorsairHubWaitsForICueEvenWhenICueIsNotWhitelisted()
    {
        var (gate, detector, pause) = Setup("corsair");

        detector.Running.Add("icue");
        pause.Refresh();
        Assert.False(gate.IsEnabled("corsair"));
        Assert.Equal("icue", gate.PausedByApp("corsair"));

        detector.Running.Clear();
        pause.Refresh();
        Assert.True(gate.IsEnabled("corsair"));
    }

    [Fact]
    public void ADeviceTheUserTurnedOnWaitsForItsWhitelistedAppToExit()
    {
        var (gate, detector, pause) = Setup(new[] { "corsair" }, whitelisted: new[] { "icue" });

        detector.Running.Add("icue");
        pause.Refresh();
        Assert.False(gate.IsEnabled("corsair"));
        Assert.True(gate.IsChosenOn("corsair"));
        Assert.Equal("icue", gate.PausedByApp("corsair"));

        detector.Running.Clear();
        pause.Refresh();
        Assert.True(gate.IsEnabled("corsair"));
        Assert.Null(gate.PausedByApp("corsair"));
    }

    [Fact]
    public void TurningADeviceOnWhileItsAppRunsTakesIt()
    {
        var (gate, detector, _) = Setup(Array.Empty<string>(), whitelisted: new[] { "lian-li-l-connect" });
        detector.Running.Add("lian-li-l-connect");

        gate.SetEnabled("lianli", true);

        Assert.True(gate.IsEnabled("lianli"));
    }

    [Fact]
    public void TurningTheCorsairHubOnWhileICueRunsWaitsForICue()
    {
        var (gate, detector, _) = Setup(Array.Empty<string>(), whitelisted: new[] { "icue" });
        detector.Running.Add("icue");

        gate.SetEnabled("corsair", true);

        Assert.True(gate.IsChosenOn("corsair"));
        Assert.False(gate.IsEnabled("corsair"));
    }

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli-tl")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli-aio")]
    [InlineData("strimer")]
    [InlineData("lianli-screen88")]
    [InlineData("lianli-hydroshift2")]
    public void EveryLianLiDeviceWaitsForAWhitelistedLConnect(string handler)
    {
        var (gate, detector, pause) = Setup(new[] { handler }, whitelisted: new[] { "lian-li-l-connect" });

        detector.Running.Add("lian-li-l-connect");
        pause.Refresh();

        Assert.False(gate.IsEnabled(handler));
    }

    [Fact]
    public void AnUnrelatedAppLeavesTheDeviceOn()
    {
        var (gate, detector, pause) = Setup(new[] { "corsair" }, whitelisted: new[] { "icue", "lian-li-l-connect" });

        detector.Running.Add("lian-li-l-connect");
        pause.Refresh();

        Assert.True(gate.IsEnabled("corsair"));
    }

    [Fact]
    public void TheMemoryBusStaysOnWhileICueRuns()
    {
        var (gate, detector, pause) = Setup();

        detector.Running.Add("icue");
        pause.Refresh();

        Assert.True(gate.IsEnabled(SmbusDramHandler.HandlerId));
    }

    [Fact]
    public void NothingIsScannedOnAFreshInstall()
    {
        var (_, detector, pause) = Setup();
        detector.Checks = 0;

        pause.Refresh();

        Assert.Equal(0, detector.Checks);
    }

    [Fact]
    public void WaitingAndTakingRaiseChanged()
    {
        var (gate, detector, pause) = Setup(new[] { "corsair" }, whitelisted: new[] { "icue" });
        var changes = new List<(string, bool)>();
        gate.Changed += (h, on) => changes.Add((h, on));

        detector.Running.Add("icue");
        pause.Refresh();
        detector.Running.Clear();
        pause.Refresh();

        Assert.Equal(new[] { ("corsair", false), ("corsair", true) }, changes.Where(c => c.Item1 == "corsair"));
    }

    [Fact]
    public void ADeviceTheUserTurnedOffRaisesNoChangeWhenItsAppComesAndGoes()
    {
        var (gate, detector, pause) = Setup();
        gate.SetEnabled("corsair", false);
        var changes = new List<string>();
        gate.Changed += (h, _) => changes.Add(h);

        detector.Running.Add("icue");
        pause.Refresh();
        detector.Running.Clear();
        pause.Refresh();

        Assert.DoesNotContain("corsair", changes);
    }
}
