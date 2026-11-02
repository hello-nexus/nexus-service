using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nexus.Service.Sensors;

/// <summary>
/// Decisions around LHM's one-shot Super I/O enumeration on boards where an
/// ASUS service is already running at service start. Opened in that state, the
/// Nuvoton hardware-monitor window answers 0xFF for the whole process lifetime
/// even after the startup conflict shutdown ends the ASUS processes, while a
/// fresh open after they are gone reads normally.
/// </summary>
public static class SuperIoOpenPolicy
{
    /// <summary>Conflict catalog ids whose presence at open time leaves the window dead.</summary>
    internal static readonly string[] HolderIds = { "armoury-crate", "asus-lighting-service" };

    /// <summary>Measured: the first boot logs showed kills at +0, +15 and +25 s and the apps closed by +36 s.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(45);

    /// <summary>How long every holder must stay absent before the open proceeds.</summary>
    internal static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    internal readonly record struct WaitResult(bool SawHolders, bool TimedOut);

    /// <summary>
    /// The holders the startup shutdown will actually end. With the sweep off or
    /// a holder whitelisted nothing ends it, so waiting for it would only delay sensors.
    /// </summary>
    internal static IReadOnlyList<string> HoldersToWaitFor(bool sweepAllowed, IReadOnlyCollection<string> excludedIds)
    {
        if (!sweepAllowed) return Array.Empty<string>();
        return HolderIds
            .Where(id => !excludedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Returns immediately when no watched holder is running. Otherwise polls
    /// until all are gone for <see cref="QuietFor"/>, or <see cref="MaxWait"/>
    /// elapses. Clock and delay are injected so the loop is testable without real time.
    /// </summary>
    internal static async Task<WaitResult> WaitAsync(
        IReadOnlyList<string> watched, Func<string, bool> isRunning, Func<TimeSpan, Task> delay, Func<long> clockMs)
    {
        var start = clockMs();
        var saw = false;
        var quietSince = -1L;
        while (true)
        {
            var now = clockMs();
            if (watched.Any(isRunning))
            {
                saw = true;
                quietSince = -1;
            }
            else
            {
                if (!saw) return new WaitResult(false, false);
                if (quietSince < 0) quietSince = now;
                if (now - quietSince >= (long)QuietFor.TotalMilliseconds) return new WaitResult(true, false);
            }
            if (now - start >= (long)MaxWait.TotalMilliseconds) return new WaitResult(saw, true);
            await delay(PollEvery).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A retry is warranted only when holders were seen and are now gone, so a
    /// dead window is attributable to them and a re-open can read differently.
    /// </summary>
    internal static bool ShouldReopen(WaitResult wait, bool windowDead, bool holdersRunningNow) =>
        windowDead && wait is { SawHolders: true } && !holdersRunningNow;

    /// <summary>
    /// True when the chip shows the all-0xFF register signature: at least one
    /// temperature, every temperature at -1, every fan at 0 RPM. Null values
    /// are unread, not matching, so they never count as dead.
    /// </summary>
    internal static bool IsDeadWindow(IReadOnlyList<float?> temperatures, IReadOnlyList<float?> fans)
    {
        if (temperatures.Count == 0) return false;
        foreach (var t in temperatures)
        {
            if (t is not { } v || Math.Abs(v + 1f) > 0.01f) return false;
        }
        foreach (var f in fans)
        {
            if (f is not { } v || v > 0.5f) return false;
        }
        return true;
    }
}
