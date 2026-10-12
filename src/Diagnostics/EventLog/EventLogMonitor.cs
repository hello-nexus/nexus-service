using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;
#endif

namespace Nexus.Service.Diagnostics.EventLog;

/// <summary>
/// Backfills, then live-tails, every DiagnosticEventCatalog source into a
/// capped in-memory incident store. Windows watches the Event Log via
/// wevtapi; Linux polls journalctl (backfill, then a repeating poll since the
/// last-seen entry - journalctl has no push subscription API comparable to
/// EvtSubscribe). Every other platform's ExecuteAsync no-ops and
/// Snapshot/CountsSince stay empty, the same gating pattern
/// LibreHardwareSensorProvider's callers use.
/// </summary>
public sealed class EventLogMonitor : BackgroundService
{
    private const int MaxIncidents = 3000;

    private readonly object _lock = new();
    private readonly List<DiagnosticIncident> _incidents = new();
    private int _generation;
    // DiagnosticsShell (not ShellExecutor) specifically for its bounded
    // output - a runaway err-level burst on a noisy box must not buffer
    // unbounded journalctl output in memory. The ShellResult return (not
    // just Stdout) is load-bearing: it is the only way to tell "journalctl
    // is missing" from "journalctl ran and found nothing" once both produce
    // empty output.
    private readonly Func<string[], Nexus.Service.Diagnostics.ShellResult> _runJournalctl;
    private readonly FeatureGates _gates;
    private volatile bool _linuxSupported;

    public EventLogMonitor(FeatureGates? gates = null)
        : this(args => Nexus.Service.Diagnostics.DiagnosticsShell.Run("journalctl", JournalctlTimeoutMs, args), gates)
    {
    }

    // Injected journalctl seam for tests (no real journalctl needed).
    internal EventLogMonitor(Func<string[], Nexus.Service.Diagnostics.ShellResult> runJournalctl, FeatureGates? gates = null)
    {
        _runJournalctl = runJournalctl;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    /// <summary>True once a journalctl query has run and returned parseable
    /// output on Linux. Stays false on every other platform and before the
    /// first successful Linux poll.</summary>
    public bool IsLinuxSupported => _linuxSupported;

    public IReadOnlyList<DiagnosticIncident> Snapshot(int days)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);
        lock (_lock)
        {
            return _incidents
                .Where(i => i.TimeUtc >= cutoff)
                .OrderByDescending(i => i.TimeUtc)
                .ToList();
        }
    }

    public Dictionary<string, int> CountsSince(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var incident in _incidents)
            {
                if (incident.TimeUtc < cutoff)
                {
                    continue;
                }
                result[incident.Source] = result.GetValueOrDefault(incident.Source) + 1;
            }
        }
        return result;
    }

    /// <summary>Clears the in-memory store and (Windows only) re-runs the
    /// catalog backfill from scratch, offloaded to a thread-pool thread so the
    /// caller (POST /diagnostics/events/clear, awaited while wevtutil wipes
    /// the underlying System/Application logs) is never blocked on native
    /// EvtQuery/EvtNext calls. _generation is bumped under _lock on every
    /// reset; the boot-time backfill and every reset's own backfill each
    /// capture the generation current when they start and pass it through to
    /// every add. An add is dropped, not written, if the generation has since
    /// moved on - so a still-running stale pass (the boot backfill racing a
    /// clear during startup, or an older clear racing a newer one) can never
    /// resurrect incidents a later reset already superseded, and two
    /// concurrent clears can never interleave into duplicates. The live
    /// EvtSubscribe callback (<see cref="OnEvent"/>) is unaffected - it always
    /// adds under whatever generation is current at delivery time, since it
    /// reports a genuinely new event, not a replay.</summary>
    public Task ResetAndBackfillAsync()
    {
        int generation;
        lock (_lock)
        {
            _generation++;
            generation = _generation;
            _incidents.Clear();
        }
#if WINDOWS
        return Task.Run(() =>
        {
            foreach (var entry in DiagnosticEventCatalog.Entries)
            {
                Backfill(entry, generation);
            }
        });
#else
        return Task.CompletedTask;
#endif
    }

    private void AddIncident(DiagnosticIncident incident)
    {
        lock (_lock)
        {
            AddIncidentLocked(incident);
        }
    }

    // Returns false without adding if a later reset has bumped the generation
    // past what the caller's backfill pass started with. Internal (not
    // private) so EventLogMonitorTests can exercise the generation gate
    // directly - Backfill itself is Windows-only and needs live WevtApi state.
    internal bool TryAddIncident(DiagnosticIncident incident, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation)
            {
                return false;
            }
            AddIncidentLocked(incident);
            return true;
        }
    }

    // Caller holds _lock.
    private void AddIncidentLocked(DiagnosticIncident incident)
    {
        _incidents.Add(incident);
        if (_incidents.Count > MaxIncidents)
        {
            Trim();
        }
    }

    // Caller holds _lock. Drops the oldest info-level incidents first, then
    // the oldest of any severity, so a burst of noisy transient events can't
    // push a critical incident out of the capped window.
    private void Trim()
    {
        var overflow = _incidents.Count - MaxIncidents;
        if (overflow <= 0)
        {
            return;
        }
        _incidents.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        var removed = 0;
        for (var i = 0; i < _incidents.Count && removed < overflow;)
        {
            if (_incidents[i].Severity == DiagnosticSeverity.Info)
            {
                _incidents.RemoveAt(i);
                removed++;
            }
            else
            {
                i++;
            }
        }
        while (removed < overflow && _incidents.Count > 0)
        {
            _incidents.RemoveAt(0);
            removed++;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
#if WINDOWS
        return RunWindowsAsync(stoppingToken);
#else
        return OperatingSystem.IsLinux() ? RunLinuxAsync(stoppingToken) : Task.CompletedTask;
#endif
    }

    // ── Linux (journalctl) ──
    // Plain C# (no #if LINUX): journalctl is invoked via Process.Start, which
    // has no Windows/Linux-exclusive API surface, so this compiles and is
    // unit-testable (via the injected _runJournalctl seam) on every platform.
    // At runtime it only ever executes when OperatingSystem.IsLinux() is true.

    private const int JournalctlTimeoutMs = 15_000;
    private static readonly TimeSpan LinuxBackfillWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan LinuxPollInterval = TimeSpan.FromMinutes(2);

    private async Task RunLinuxAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var watermark = DateTimeOffset.UtcNow - LinuxBackfillWindow;
        watermark = PollJournalOnceSafe(watermark);
        Nexus.Service.Platform.ServiceLog.Info($"[event-log-monitor] linux journalctl backfill complete, watermark={watermark:o}");

        using var timer = new PeriodicTimer(LinuxPollInterval);
        while (await WaitForNextTickSafe(timer, stoppingToken).ConfigureAwait(false))
        {
            if (!_gates.Diagnostics)
            {
                continue;
            }
            watermark = PollJournalOnceSafe(watermark);
        }
    }

    private static async Task<bool> WaitForNextTickSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private DateTimeOffset PollJournalOnceSafe(DateTimeOffset sinceUtc)
    {
        try
        {
            return PollJournalOnce(sinceUtc);
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[event-log-monitor] journalctl poll failed: {ex.Message}");
            return sinceUtc;
        }
    }

    /// <summary>Runs both journalctl queries since `sinceUtc`, adds every
    /// newly classified incident under the generation current at call time,
    /// and returns the watermark to poll from next: the newest entry
    /// actually observed in either raw response, whether or not it
    /// classified into an incident (or sinceUtc unchanged if journalctl
    /// produced no output at all). Internal so EventLogMonitorTests can
    /// exercise it with an injected journalctl seam.</summary>
    internal DateTimeOffset PollJournalOnce(DateTimeOffset sinceUtc)
    {
        int generation;
        lock (_lock)
        {
            generation = _generation;
        }

        // The epoch form is timezone-unambiguous. A plain "yyyy-MM-dd
        // HH:mm:ss" --since value is parsed in the system's LOCAL timezone
        // regardless of any output-formatting flag, so rendering a UTC clock
        // string there queries the wrong window on any box not set to UTC.
        var sinceArg = $"@{sinceUtc.ToUnixTimeSeconds()}";

        // General query: everything at "err" or worse, across every
        // transport/syslog identifier - covers unitFailed/appCrash/diskError/
        // generic kernel errors.
        var generalResult = _runJournalctl(new[] { "--output=json", "--priority=err", "--since", sinceArg, "--no-pager" });

        // The kernel logs segfaults below "err", and the OOM killer isn't
        // guaranteed to be at "err" on every kernel either - both would be
        // missed by the --priority=err query above, so match them by message
        // pattern instead, with no priority filter.
        var kernelResult = _runJournalctl(new[]
        {
            "--output=json", "--grep", "Killed process|segfault at", "--since", sinceArg, "--no-pager",
        });

        // Either result's exit code being the DiagnosticsShell spawn/timeout
        // sentinel means journalctl never ran; any other exit code (including
        // a journalctl-reported failure) means the binary is present and
        // usable, which alone is enough to call it supported - zero matching
        // entries is not distinguishable from "missing" by output alone.
        if (generalResult.ExitCode != -1 || kernelResult.ExitCode != -1)
        {
            _linuxSupported = true;
        }

        var general = generalResult.Stdout;
        var kernelPatterns = kernelResult.Stdout;

        // The watermark tracks the newest entry actually seen in either raw
        // response, not the newest CLASSIFIED incident - a window with only
        // unrecognized err lines must still advance, or the same window gets
        // re-fetched forever and, with journalctl's oldest-first ordering and
        // a bounded response, the newest entries can become permanently
        // unreachable.
        var newest = sinceUtc;
        if (LinuxJournalIncidentParser.GetNewestTimestamp(general) is { } generalNewest && generalNewest > newest.UtcDateTime)
        {
            newest = new DateTimeOffset(generalNewest, TimeSpan.Zero);
        }
        if (LinuxJournalIncidentParser.GetNewestTimestamp(kernelPatterns) is { } kernelNewest && kernelNewest > newest.UtcDateTime)
        {
            newest = new DateTimeOffset(kernelNewest, TimeSpan.Zero);
        }

        // The two queries can both match the same journal entry (an
        // err-priority OOM kill, for instance) - merge by Id (the journal
        // cursor) so it is added once, not twice. No per-query take-N cap
        // here: journalctl returns oldest-first, so capping the parsed list
        // would drop the newest incidents while the watermark above (scanned
        // from the full raw response) still advances past them - permanent
        // loss. DiagnosticsShell already bounds the raw response size, and
        // the store's own MaxIncidents/Trim bounds what accumulates across
        // polls, so no further cap is needed here.
        var merged = new Dictionary<string, DiagnosticIncident>(StringComparer.Ordinal);
        foreach (var incident in LinuxJournalIncidentParser.ParseLines(general))
        {
            merged[incident.Id] = incident;
        }
        foreach (var incident in LinuxJournalIncidentParser.ParseLines(kernelPatterns))
        {
            merged[incident.Id] = incident;
        }

        foreach (var incident in merged.Values)
        {
            // journalctl's --since is inclusive, so entries at exactly the
            // watermark were already added on the previous poll.
            if (incident.TimeUtc <= sinceUtc.UtcDateTime)
            {
                continue;
            }
            TryAddIncident(incident, generation);
        }
        return newest;
    }

#if WINDOWS
    private const int BackfillCapPerSource = 300;
    private const long BackfillWindowMs = 2_592_000_000;

    private async Task RunWindowsAsync(CancellationToken stoppingToken)
    {
        // Hand control back to StartAsync immediately; backfill runs on a
        // thread-pool thread so /ping stays reachable during boot.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        int generation;
        lock (_lock)
        {
            generation = _generation;
        }

        var backfillCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in DiagnosticEventCatalog.Entries)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            backfillCounts[entry.Source] = Backfill(entry, generation);
        }
        var summary = string.Join(", ", backfillCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        ServiceLog.Info($"[event-log-monitor] backfill complete: {summary}");

        var subscriptions = new List<WevtApi.SafeEvtHandle>();
        var gcHandles = new List<GCHandle>();
        try
        {
            foreach (var group in DiagnosticEventCatalog.Entries.GroupBy(e => e.Channel, StringComparer.Ordinal))
            {
                var query = CombinedQuery(group);
                var state = new SubscriptionState(this, group.Key);
                var gcHandle = GCHandle.Alloc(state);
                gcHandles.Add(gcHandle);

                WevtApi.SafeEvtHandle sub;
                unsafe
                {
                    var callback = (IntPtr)(delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, uint>)&OnEvent;
                    sub = WevtApi.EvtSubscribe(IntPtr.Zero, IntPtr.Zero, group.Key, query,
                        IntPtr.Zero, GCHandle.ToIntPtr(gcHandle), callback, WevtApi.EvtSubscribeToFutureEvents);
                }
                if (sub.IsInvalid)
                {
                    ServiceLog.Warn($"[event-log-monitor] EvtSubscribe failed for channel {group.Key}: error {Marshal.GetLastWin32Error()}");
                    gcHandle.Free();
                    gcHandles.Remove(gcHandle);
                    continue;
                }
                subscriptions.Add(sub);
            }

            // Subscriptions deliver on native worker threads until closed;
            // block here so BackgroundService keeps the service alive and the
            // finally below runs on shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            // EvtClose on a subscription blocks until any in-flight callback
            // for it finishes, so every GCHandle it could reference is safe
            // to free right after.
            foreach (var sub in subscriptions)
            {
                sub.Dispose();
            }
            foreach (var gcHandle in gcHandles)
            {
                if (gcHandle.IsAllocated)
                {
                    gcHandle.Free();
                }
            }
        }
    }

    private static string CombinedQuery(IEnumerable<DiagnosticEventCatalogEntry> entries)
    {
        var predicates = entries.Select(e => $"({e.XPath})");
        return $"*[System[{string.Join(" or ", predicates)}]]";
    }

    private int Backfill(DiagnosticEventCatalogEntry entry, int generation)
    {
        var query = $"*[System[({entry.XPath}) and TimeCreated[timediff(@SystemTime) <= {BackfillWindowMs}]]]";
        using var resultSet = WevtApi.EvtQuery(IntPtr.Zero, entry.Channel, query,
            WevtApi.EvtQueryChannelPath | WevtApi.EvtQueryReverseDirection);
        if (resultSet.IsInvalid)
        {
            ServiceLog.Warn($"[event-log-monitor] backfill query failed for {entry.Source}: error {Marshal.GetLastWin32Error()}");
            return 0;
        }

        var count = 0;
        var buffer = new IntPtr[1];
        while (count < BackfillCapPerSource)
        {
            if (!WevtApi.EvtNext(resultSet, 1u, buffer, 0u, 0u, out var returned) || returned == 0)
            {
                break;
            }
            using var eventHandle = new WevtApi.SafeEvtHandle(buffer[0], ownsHandle: true);
            var xml = Render(eventHandle);
            var incident = xml is not null ? EventXmlParser.Parse(xml) : null;
            if (incident is not null && TryAddIncident(incident, generation))
            {
                count++;
            }
        }
        return count;
    }

    internal static string? Render(WevtApi.SafeEvtHandle eventHandle)
    {
        // First call is expected to fail with ERROR_INSUFFICIENT_BUFFER; it
        // still reports the required buffer size (bytes, including the
        // trailing null) via `required`.
        _ = WevtApi.EvtRender(IntPtr.Zero, eventHandle, WevtApi.EvtRenderEventXml, 0, IntPtr.Zero, out var required, out _);
        if (required == 0)
        {
            return null;
        }
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!WevtApi.EvtRender(IntPtr.Zero, eventHandle, WevtApi.EvtRenderEventXml, required, buffer, out var used, out _))
            {
                return null;
            }
            var charCount = (int)(used / 2) - 1;
            return charCount > 0 ? Marshal.PtrToStringUni(buffer, charCount) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint OnEvent(int action, IntPtr userContext, IntPtr eventHandle)
    {
        SubscriptionState? state = null;
        try
        {
            if (userContext != IntPtr.Zero)
            {
                state = GCHandle.FromIntPtr(userContext).Target as SubscriptionState;
            }
            if (action != WevtApi.EvtSubscribeActionDeliver || state is null)
            {
                return 0;
            }
            if (!state.Monitor._gates.Diagnostics)
            {
                return 0;
            }
            // EVT_SUBSCRIBE_CALLBACK borrows the handle: wevtapi closes it when
            // the callback returns, so closing it here double-frees inside
            // wevtapi (surfaces later as a 0xc0000409 fail-fast). Only EvtNext
            // handles (Backfill) are caller-owned.
            using var handle = new WevtApi.SafeEvtHandle(eventHandle, ownsHandle: false);
            var xml = Render(handle);
            var incident = xml is not null ? EventXmlParser.Parse(xml) : null;
            if (incident is not null)
            {
                state.Monitor.AddIncident(incident);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an [UnmanagedCallersOnly] frame fail-fasts
            // the AOT process; nothing may throw past this point.
            try { state?.WarnOnce(ex); } catch { }
        }
        return 0;
    }

    private sealed class SubscriptionState
    {
        public EventLogMonitor Monitor { get; }
        private readonly string _channel;
        private int _warned;

        public SubscriptionState(EventLogMonitor monitor, string channel)
        {
            Monitor = monitor;
            _channel = channel;
        }

        public void WarnOnce(Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                ServiceLog.Warn($"[event-log-monitor] subscription callback error on channel {_channel}: {ex.Message}");
            }
        }
    }
#endif
}
