using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Service.Sensors;

/// <summary>
/// Decisions for re-reading the Super I/O after a sleep resume. The chip's
/// config port may not answer right at resume, so checks are spaced out and a
/// reopen is tried only when the window reads dead at that moment.
/// </summary>
public static class SuperIoResumePolicy
{
    /// <summary>Time after resume of each check. The first doubles as the settle delay.</summary>
    internal static readonly TimeSpan[] CheckOffsets =
    {
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };

    internal enum Outcome { Healthy, Recovered, GaveUp, Superseded }

    /// <summary>Process names that mark ASUS software worth correlating with a dead window.</summary>
    internal static bool IsAsusRelatedProcess(string name) =>
        name.Contains("asus", StringComparison.OrdinalIgnoreCase)
        || name.Contains("armoury", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("aac", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the check schedule. A healthy window is left alone and rechecked at
    /// the next offset, since it can die minutes after resume. The first
    /// successful reopen ends the run; the log names every attempt.
    /// </summary>
    internal static async Task<Outcome> RunAsync(
        IReadOnlyList<TimeSpan> offsets,
        Func<TimeSpan, Task> delay,
        Func<bool> superseded,
        Func<bool> windowDead,
        Func<bool> reopenRecovered,
        Action<string> log)
    {
        var previous = TimeSpan.Zero;
        var sawHealthy = false;
        for (var i = 0; i < offsets.Count; i++)
        {
            await delay(offsets[i] - previous).ConfigureAwait(false);
            previous = offsets[i];
            if (superseded()) return Outcome.Superseded;
            if (!windowDead())
            {
                sawHealthy = true;
                log($"resume check {i + 1}/{offsets.Count} at +{offsets[i].TotalSeconds:0}s: window healthy, no reopen");
                continue;
            }
            sawHealthy = false;
            log($"resume check {i + 1}/{offsets.Count} at +{offsets[i].TotalSeconds:0}s: window dead, reopening");
            var recovered = reopenRecovered();
            log($"resume reopen {i + 1}/{offsets.Count}: {(recovered ? "recovered" : "still dead")}");
            if (recovered) return Outcome.Recovered;
        }
        return sawHealthy ? Outcome.Healthy : Outcome.GaveUp;
    }
}
