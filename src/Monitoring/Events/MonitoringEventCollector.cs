using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;

namespace Nexus.Service.Monitoring.Events;

/// <summary>
/// Cross-platform producer for usb-attach/usb-detach and app-open/
/// uac-escalation timeline events. Polls IUsbEnumerator and ProcessMonitor
/// every PollInterval and diffs each snapshot against the previous one - the
/// first tick of each only primes the baseline and emits nothing, so
/// service start never dumps the whole USB bus or every already-running
/// process as a wall of events.
///
/// Calls ProcessMonitor.SetDemand so process sampling runs even with no
/// WebSocket subscriber, the same demand mechanism MetricsSampler's
/// per-app recording uses.
///
/// The "is this a user-facing app" source is platform-split because
/// IWindowSetProvider is registered on Windows only: elsewhere every
/// ProcessInfo.HasWindow is false, so the windowed-name classification the
/// processes frame uses would emit nothing at all. Non-Windows reads
/// IAppDetectionProvider instead (real on macOS via lsappinfo, a stub on
/// Linux, which therefore produces no app events).
/// </summary>
public sealed class MonitoringEventCollector : BackgroundService
{
    private const string DemandSource = "monitoring-events";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // A windowed app that closes and reopens in a loop would otherwise emit
    // an app-open every tick it is seen freshly; this cooldown suppresses a
    // repeat emission for the same name within the window.
    private static readonly TimeSpan AppEmitCooldown = TimeSpan.FromMinutes(1);

    // Caps app-open/uac-escalation events emitted in a single tick, so a
    // burst of many windowed apps opening at once cannot flood the log.
    private const int MaxAppEventsPerTick = 10;

    // Same protection for the bus: a device that re-enumerates in a loop (a
    // failing cable or hub, or a panel stuck in a reset cycle - see the
    // workspace failure log for real instances) would otherwise append an
    // attach and a detach every tick for as long as it flaps, filling the
    // retention window with tens of thousands of rows that all replay into
    // RAM at boot.
    private const int MaxUsbEventsPerTick = 10;
    private static readonly TimeSpan UsbEmitCooldown = TimeSpan.FromMinutes(1);

    // Self-contained hourly prune, the same shape and interval as
    // PrivacyAccessWatcher's own PruneInterval - each producer owns its
    // store's retention rather than threading a shared cadence through
    // MetricsSampler's unrelated 1Hz tick.
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    // lsappinfo is a subprocess spawn, so the non-Windows app source runs on
    // its own slower sub-cadence rather than every PollInterval.
    private static readonly TimeSpan DetectedAppPollInterval = TimeSpan.FromSeconds(15);

    // A newly seen app is stamped at the process's own start time when the
    // sample caught it that recently, so the marker sits at the real launch
    // rather than at poll time. Past this limit the start is not what this
    // tick observed (a long-running process that only now opened a window,
    // or that app detection only now listed), and back-dating the marker
    // there would place it outside the window the user is looking at.
    private const long AppStartBackdateLimitMs = 60_000;

    private readonly IUsbEnumerator _usb;
    private readonly ProcessMonitor _processes;
    private readonly IAppDetectionProvider _appDetection;
    private readonly IMonitoringEventStore _store;

    private HashSet<UsbKey>? _lastUsbKeys;
    private HashSet<string>? _lastAppNames;
    private readonly Dictionary<string, DateTime> _appEmitCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _usbEmitCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private DateTime _lastDetectedPollUtc = DateTime.MinValue;

    private readonly bool _useWindowedProcesses;
    private readonly FeatureGates _gates;

    /// <summary>useWindowedProcesses selects the app source and defaults to
    /// the platform that has an IWindowSetProvider; tests pin it so both
    /// sources are exercised regardless of the host OS.</summary>
    public MonitoringEventCollector(
        IUsbEnumerator usb, ProcessMonitor processes, IAppDetectionProvider appDetection, IMonitoringEventStore store,
        bool? useWindowedProcesses = null, FeatureGates? gates = null)
    {
        _usb = usb;
        _processes = processes;
        _appDetection = appDetection;
        _store = store;
        _useWindowedProcesses = useWindowedProcesses ?? OperatingSystem.IsWindows();
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processes.SetDemand(DemandSource, true);
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                try
                {
                    Tick(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[monitoring-events] tick failed: {ex.Message}");
                }
            } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
        }
        finally
        {
            _processes.SetDemand(DemandSource, false);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
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

    internal void Tick(DateTime nowUtc)
    {
        if (!_gates.Monitoring)
        {
            return;
        }
        var nowMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
        TickUsb(nowUtc, nowMs);
        TickApps(nowUtc, nowMs);
        MaybePrune(nowUtc);
    }

    private void MaybePrune(DateTime nowUtc)
    {
        if (nowUtc - _lastPruneUtc < PruneInterval)
        {
            return;
        }
        _lastPruneUtc = nowUtc;
        try
        {
            var cutoffMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds() - MetricsHistory.RetentionDays * 86_400_000L;
            _store.PruneOlderThan(cutoffMs);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[monitoring-events] prune failed: {ex.Message}");
        }
    }

    internal readonly record struct UsbKey(int VendorId, int ProductId, string Name);

    internal static HashSet<UsbKey> KeysFor(IReadOnlyList<UsbDeviceEntry> entries) =>
        entries.Select(e => new UsbKey(e.VendorId, e.ProductId, e.Name)).ToHashSet();

    private void TickUsb(DateTime nowUtc, long nowMs)
    {
        List<UsbDeviceEntry> entries;
        try
        {
            entries = _usb.Enumerate();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[monitoring-events] usb enumerate failed: {ex.Message}");
            return;
        }

        var currentKeys = KeysFor(entries);
        if (_lastUsbKeys is null)
        {
            _lastUsbKeys = currentKeys;
            return;
        }

        var (attached, detached) = DiffUsb(_lastUsbKeys, currentKeys);
        _lastUsbKeys = currentKeys;

        EmitUsbEvents(attached, MonitoringEventKinds.UsbAttach, nowUtc, nowMs);
        EmitUsbEvents(detached, MonitoringEventKinds.UsbDetach, nowUtc, nowMs);
    }

    // Cooldown is keyed per device AND direction, so a genuine unplug still
    // records right after that device's own attach; only the same device
    // repeating the same transition inside the window is suppressed.
    private void EmitUsbEvents(List<UsbKey> keys, string kind, DateTime nowUtc, long nowMs)
    {
        var candidates = keys.Select(k => $"{kind}|{k.VendorId:X4}:{k.ProductId:X4}|{k.Name}").ToList();
        var allowed = ApplyCooldownAndCap(candidates, _usbEmitCooldownUntil, nowUtc, UsbEmitCooldown, MaxUsbEventsPerTick);
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keys.Count; i++)
        {
            if (!allowedSet.Contains(candidates[i]))
            {
                continue;
            }
            var key = keys[i];
            _store.Append(nowMs, kind, key.Name, FormatVidPid(key.VendorId, key.ProductId), custom: false);
        }
    }

    /// <summary>Pure set diff, directly unit-tested: keys present in current
    /// but not previous attached, keys present in previous but not current
    /// detached. Two identical devices (same vid/pid/name) collapse to one
    /// key, matching the vid/pid/name identity the wire contract specifies.</summary>
    internal static (List<UsbKey> Attached, List<UsbKey> Detached) DiffUsb(HashSet<UsbKey> previous, HashSet<UsbKey> current)
    {
        var attached = new List<UsbKey>();
        var detached = new List<UsbKey>();
        foreach (var key in current)
        {
            if (!previous.Contains(key))
            {
                attached.Add(key);
            }
        }
        foreach (var key in previous)
        {
            if (!current.Contains(key))
            {
                detached.Add(key);
            }
        }
        return (attached, detached);
    }

    internal static string FormatVidPid(int vendorId, int productId) => $"{vendorId:X4}:{productId:X4}";

    /// <summary>One user-facing app this tick observed. Pid is set only where
    /// the source names it (app detection); the Windows windowed-name source
    /// resolves a representative process by name instead.</summary>
    internal readonly record struct AppCandidate(string Name, int? Pid);

    private void TickApps(DateTime nowUtc, long nowMs)
    {
        var procs = _processes.GetProcesses();
        var current = CurrentAppCandidates(nowUtc, procs);
        if (current is null)
        {
            return;
        }

        var currentNames = current.Select(c => c.Name).ToList();
        if (_lastAppNames is null)
        {
            _lastAppNames = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);
            return;
        }

        var newlySeen = DiffNewApps(_lastAppNames, currentNames);
        _lastAppNames = new HashSet<string>(currentNames, StringComparer.OrdinalIgnoreCase);

        var toEmit = ApplyCooldownAndCap(newlySeen, _appEmitCooldownUntil, nowUtc, AppEmitCooldown, MaxAppEventsPerTick);
        foreach (var name in toEmit)
        {
            var candidate = current.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            EmitAppEvent(candidate, procs, nowMs);
        }
    }

    /// <summary>This tick's app candidates, or null when the non-Windows
    /// source is inside its sub-cadence and produced no fresh sample - the
    /// caller must then leave the baseline untouched, since treating a
    /// skipped tick as an empty snapshot would re-emit every running app on
    /// the next real one.</summary>
    private IReadOnlyList<AppCandidate>? CurrentAppCandidates(DateTime nowUtc, IReadOnlyList<ProcessInfo> procs)
    {
        if (_useWindowedProcesses)
        {
            return ExtractWindowedAppNames(procs, Environment.ProcessId).Select(n => new AppCandidate(n, null)).ToList();
        }

        if (nowUtc - _lastDetectedPollUtc < DetectedAppPollInterval)
        {
            return null;
        }
        _lastDetectedPollUtc = nowUtc;

        try
        {
            return DetectedAppCandidates(_appDetection.GetDetected(), Environment.ProcessId);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[monitoring-events] app detection failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Detected entries to candidates, one per distinct name (a
    /// multi-process app lists several entries sharing a display name), Id
    /// parsed as the pid the platform provider records.</summary>
    internal static List<AppCandidate> DetectedAppCandidates(IReadOnlyList<Detected> detected, int selfPid)
    {
        var byName = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in detected)
        {
            if (string.IsNullOrWhiteSpace(app.Name) || IsNexusProcess(app.Name) || byName.ContainsKey(app.Name))
            {
                continue;
            }
            int? pid = int.TryParse(app.Id, out var parsed) ? parsed : null;
            if (pid == selfPid)
            {
                continue;
            }
            byName[app.Name] = new AppCandidate(app.Name, pid);
        }
        return byName.Values.ToList();
    }

    /// <summary>Names of every process currently owning a visible top-level
    /// window (Task-Manager-style App classification), reusing
    /// ProcessAggregation.GroupByName - the same isApp notion
    /// MonitoringBroadcaster.BuildProcessFrame uses - rather than
    /// re-deriving it.</summary>
    internal static IReadOnlyList<string> ExtractWindowedAppNames(IReadOnlyList<ProcessInfo> procs, int selfPid) =>
        ProcessAggregation.GroupByName(procs.Where(p => p.Pid != selfPid).ToList()).Where(kv => kv.Value.HasWindow && !IsNexusProcess(kv.Key)).Select(kv => kv.Key).ToList();

    // Our own service is matched by pid; only uniquely named exes are matched
    // by name, since other products ship a Nexus.exe. Not an exe path:
    // resolving one per candidate every tick is real process I/O.
    // msedgewebview2 is deliberately absent (shared with other apps).
    private static readonly HashSet<string> NexusProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nexus-overlay", "nexus-overlay-helper", "OpenRGB-headless", "NexusUpdater",
    };

    /// <summary>True for a process Nexus ships; its windows appearing (the
    /// dashboard opening) are not user apps opening.</summary>
    internal static bool IsNexusProcess(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return NexusProcessNames.Contains(trimmed);
    }

    /// <summary>Pure set diff: names in current not present in previous.</summary>
    internal static List<string> DiffNewApps(HashSet<string> previous, IReadOnlyList<string> current) =>
        current.Where(n => !previous.Contains(n)).ToList();

    /// <summary>Drops a candidate still inside its cooldown window, then caps
    /// the remainder to at most cap entries; every emitted candidate resets
    /// its own cooldown to nowUtc + cooldown. Pure aside from mutating the
    /// caller-owned cooldownUntil dictionary, which is the whole point (it
    /// is the collector's persistent per-name state).</summary>
    internal static List<string> ApplyCooldownAndCap(
        IReadOnlyList<string> candidates, Dictionary<string, DateTime> cooldownUntil, DateTime nowUtc, TimeSpan cooldown, int cap)
    {
        var result = new List<string>();
        foreach (var name in candidates)
        {
            if (result.Count >= cap)
            {
                break;
            }
            if (cooldownUntil.TryGetValue(name, out var until) && nowUtc < until)
            {
                continue;
            }
            cooldownUntil[name] = nowUtc + cooldown;
            result.Add(name);
        }
        return result;
    }

    // Elevation and exe-path resolution are real OS/process I/O, only
    // reachable for a name this tick just proved is newly seen - not pure,
    // not directly unit-tested (see the diff/cap methods above for the
    // parts that are).
    private void EmitAppEvent(AppCandidate candidate, IReadOnlyList<ProcessInfo> procs, long nowMs)
    {
        var representative = candidate.Pid is { } pid
            ? procs.FirstOrDefault(p => p.Pid == pid)
            : null;
        representative ??= procs
            .Where(p => string.Equals(p.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.StartedAtMs ?? 0)
            .FirstOrDefault();

        var elevated = representative is not null && ProcessElevation.IsProcessElevated(representative.Pid);
        var detail = _processes.ResolveExecutablePath(candidate.Name);
        var kind = elevated ? MonitoringEventKinds.UacEscalation : MonitoringEventKinds.AppOpen;
        _store.Append(ResolveEventTime(representative?.StartedAtMs, nowMs), kind, candidate.Name, detail, custom: false);
    }

    /// <summary>The process's own start when this tick caught it within
    /// AppStartBackdateLimitMs, else detection time.</summary>
    internal static long ResolveEventTime(long? startedAtMs, long nowMs) =>
        startedAtMs is { } started && started <= nowMs && nowMs - started <= AppStartBackdateLimitMs
            ? started
            : nowMs;
}
