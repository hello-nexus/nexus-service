using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictBackOffOnlyTests
{
    private const string Installer = "Install iCUE";

    private static ConflictWatcher Watcher(Dictionary<string, List<(int Pid, string? Path)>> processes)
    {
        var store = new InMemoryConfigStore();
        return new ConflictWatcher(new MultiplexHub(), null, new FeatureGates(store), null, () => processes, null, ConflictWatcherPillarTests.TrustAll) { HandlerCacheMs = 0 };
    }

    private static Dictionary<string, List<(int Pid, string? Path)>> Running(params (string Name, int Pid)[] procs) =>
        procs.ToDictionary(p => p.Name, p => new List<(int, string?)> { (p.Pid, null) }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_installer_alone_counts_as_the_app_running_but_is_marked_back_off_only()
    {
        var detected = Watcher(Running((Installer, 500))).GetConflicts().Single(c => c.Id == "icue");

        Assert.True(detected.BackOffOnly);
        Assert.Equal(500, detected.Pid);
    }

    [Fact]
    public void The_app_itself_wins_over_an_installer_even_with_a_higher_pid()
    {
        var detected = Watcher(Running((Installer, 100), ("iCUE", 900))).GetConflicts().Single(c => c.Id == "icue");

        Assert.False(detected.BackOffOnly);
        Assert.Equal(900, detected.Pid);
    }

    [Fact]
    public void The_installers_are_never_in_the_list_the_end_paths_walk()
    {
        var icue = ConflictAppCatalog.All.Single(a => a.Id == "icue");

        Assert.Contains("Install iCUE", icue.BackOffOnlyProcessNames);
        Assert.Contains("icue-uninstaller", icue.BackOffOnlyProcessNames);
        Assert.All(icue.BackOffOnlyProcessNames, n => Assert.DoesNotContain(n, icue.ProcessNames));
        Assert.All(icue.BackOffOnlyProcessNames, n => Assert.DoesNotContain(n, icue.GracefulProcessNames));
    }

    private sealed class FakeDetector : IConflictDetector
    {
        public IReadOnlyList<DetectedConflict> Conflicts = Array.Empty<DetectedConflict>();
        public bool IsAppRunning(string appId) => Conflicts.Any(c => c.Id == appId);
        public IReadOnlyList<DetectedConflict> GetConflicts() => Conflicts;
        public bool DetectionReady => true;
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void The_sweep_never_ends_a_back_off_only_detection(bool backOffOnly, int ended)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        var detector = new FakeDetector { Conflicts = new[] { new DetectedConflict { Id = "icue", DisplayName = "Corsair iCUE", Pid = 7, BackOffOnly = backOffOnly } } };
        var killed = new List<string>();
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false);

        shutdown.EndRunningApps(sweep: true);

        Assert.Equal(ended, killed.Count);
    }

    [Fact]
    public void An_app_skipped_for_a_running_installer_is_ended_once_the_installer_is_gone()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        var detector = new FakeDetector { Conflicts = new[] { new DetectedConflict { Id = "icue", DisplayName = "Corsair iCUE", Pid = 7 } } };
        var killed = new List<string>();
        var installer = true;
        var shutdown = new ConflictStartupShutdown(store, detector, NullLogger<ConflictStartupShutdown>.Instance,
            def => killed.Add(def.Id), _ => false, backOffRunning: _ => installer);

        shutdown.EndRunningApps(sweep: true);
        Assert.Empty(killed);

        installer = false;
        shutdown.EndRunningApps(sweep: true);
        Assert.Equal(new[] { "icue" }, killed);
    }
}
