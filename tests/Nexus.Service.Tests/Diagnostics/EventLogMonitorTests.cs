using System;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics.EventLog;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class EventLogMonitorTests
{
    [Fact]
    public async Task ResetAndBackfillAsync_LeavesStoreQueryable()
    {
        var monitor = new EventLogMonitor();

        await monitor.ResetAndBackfillAsync();

        // Windows backfills from the host's real event log, so only the
        // other platforms start from an empty store.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(monitor.Snapshot(30));
            Assert.Empty(monitor.CountsSince(TimeSpan.FromDays(30)));
        }
        Assert.NotNull(monitor.Snapshot(30));
        Assert.NotNull(monitor.CountsSince(TimeSpan.FromDays(30)));
    }

    [Fact]
    public async Task TryAddIncident_DropsStaleGeneration_AfterReset()
    {
        var monitor = new EventLogMonitor();
        const int staleGeneration = 0;

        // A reset bumps the generation past staleGeneration, simulating a
        // clear landing after a backfill pass captured its generation.
        await monitor.ResetAndBackfillAsync();

        var stale = monitor.TryAddIncident(BuildIncident("stale"), staleGeneration);
        Assert.False(stale);
        Assert.DoesNotContain(monitor.Snapshot(30), i => i.Id == "stale");

        var currentGeneration = staleGeneration + 1;
        var fresh = monitor.TryAddIncident(BuildIncident("fresh"), currentGeneration);
        Assert.True(fresh);
        Assert.Single(monitor.Snapshot(30), i => i.Id == "fresh");
    }

    private static DiagnosticIncident BuildIncident(string id) => new()
    {
        Id = id,
        TimeUtc = DateTime.UtcNow,
        Source = "whea",
        Severity = "warning",
        Title = "test",
        Detail = "test",
    };
}
