using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Read-only view of the conflict watcher's detected-app state, exposed so
/// other services can gate behavior on whether a competing app is running
/// without depending on the watcher's broadcast/hosted-service surface.
/// </summary>
public interface IConflictDetector
{
    /// <summary>True if the competing app with this <see cref="ConflictAppCatalog"/> id is currently running.</summary>
    bool IsAppRunning(string appId);

    /// <summary>Every competing app currently running, for callers that report the set rather than test one id.</summary>
    IReadOnlyList<DetectedConflict> GetConflicts();

    /// <summary>False until the first process scan completes, so callers do not treat "not yet scanned" as "no apps running".</summary>
    bool DetectionReady { get; }
}

/// <summary>
/// Background service that polls the running process list every 5 s, matches
/// it against <see cref="ConflictAppCatalog.All"/>, and broadcasts the
/// current snapshot on the multiplex topic <c>conflicts</c> whenever the
/// detected set changes.
///
/// Scanning walks process names only and never queries CPU% / memory /
/// handles, so it runs regardless of subscriber count. Registers a snapshot
/// provider so newly-subscribing clients receive the current state without
/// waiting for the next change tick.
/// </summary>
public sealed class ConflictWatcher : BackgroundService, IConflictDetector
{
    public const string Topic = "conflicts";

    private readonly MultiplexHub _hub;
    private readonly OpenRgbProcessManager? _openRgb;

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A gap longer than this between scans means scanning had stopped, so the
    /// next pass restates what is running instead of dating every app's launch
    /// to the moment scanning resumed.
    /// </summary>
    private static readonly TimeSpan TransitionGapLimit = TimeSpan.FromSeconds(30);

    /// <summary>Transitions one app may log per <see cref="FlapWindow"/> before it is summarised instead.</summary>
    private const int MaxTransitionsPerWindow = 6;

    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Last-published snapshot. Reads on broadcaster thread, also read on
    /// receive threads via <see cref="GetCachedSnapshotEnvelope"/>; the byte
    /// array is replaced atomically (volatile reference-assignment is atomic
    /// on every supported runtime).
    /// </summary>
    private volatile byte[]? _cachedEnvelope;

    /// <summary>Plain list mirror of the same snapshot for the REST endpoint.</summary>
    private volatile IReadOnlyList<DetectedConflict> _latest = Array.Empty<DetectedConflict>();

    /// <summary>Set of conflict ids currently detected. Used to detect changes.</summary>
    private string[] _lastDetectedIds = Array.Empty<string>();

    private volatile bool _detectionReady;

    /// <summary>
    /// Ids logged as present by the last transition pass. Separate from
    /// <see cref="_lastDetectedIds"/>, which keys on id:pid so the UI sees a
    /// restart; the log wants open/close, not a pid churn.
    /// </summary>
    private string[] _lastLoggedIds = Array.Empty<string>();

    /// <summary>Tick of the scan that produced <see cref="_lastLoggedIds"/>.</summary>
    private long _lastLoggedTicks;

    /// <summary>Transitions logged per app in the open flap window, and when it opened.</summary>
    private readonly Dictionary<string, int> _transitionCounts = new(StringComparer.OrdinalIgnoreCase);
    private long _flapWindowTicks;
    private readonly object _scanLock = new();
    private long _lastScanTicks = long.MinValue / 2;

    public ConflictWatcher(MultiplexHub hub, OpenRgbProcessManager? openRgb = null)
    {
        _hub = hub;
        _openRgb = openRgb;
        _hub.RegisterSnapshotProvider(Topic, GetCachedSnapshotEnvelope);
    }

    public IReadOnlyList<DetectedConflict> GetConflicts()
    {
        EnsureFresh();
        return _latest;
    }

    public bool DetectionReady
    {
        get
        {
            EnsureFresh();
            return _detectionReady;
        }
    }

    /// <summary>
    /// Scans if the cache is older than the poll interval. The background loop
    /// only scans while something is subscribed, so on an idle box (no
    /// dashboard, no adoption candidates) the process enumeration stops
    /// happening at all - but every reader still sees data no staler than it
    /// did when the loop ran unconditionally.
    /// </summary>
    private void EnsureFresh()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastScanTicks) < (long)PollInterval.TotalMilliseconds)
        {
            return;
        }

        lock (_scanLock)
        {
            if (Environment.TickCount64 - _lastScanTicks < (long)PollInterval.TotalMilliseconds)
            {
                return;
            }
            ScanAndPublish();
        }
    }

    public bool IsAppRunning(string appId)
    {
        EnsureFresh();
        var latest = _latest;
        foreach (var conflict in latest)
        {
            if (string.Equals(conflict.Id, appId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve the catalog entry matching a given id (case-insensitive).
    /// Returns null when the id is unknown - used by ConflictRoutes to
    /// validate the kill payload before terminating anything.
    /// </summary>
    public static ConflictAppDefinition? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        foreach (var def in ConflictAppCatalog.All)
        {
            if (string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase))
                return def;
        }
        return null;
    }

    private ReadOnlyMemory<byte>? GetCachedSnapshotEnvelope()
    {
        // A client subscribing after an idle stretch is a reader like any
        // other: without this it would be handed whatever the cache last held.
        EnsureFresh();
        var bytes = _cachedEnvelope;
        // Not a ternary: null would convert through byte[] into an empty, non-null envelope.
        if (bytes is null) return null;
        return new ReadOnlyMemory<byte>(bytes);
    }

    public override void Dispose()
    {
        try
        { _hub.UnregisterSnapshotProvider(Topic); }
        catch { }
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the rest of the service finish boot - DI'd
        // dependencies (OpenRGB manager) may not have started their own work
        // yet, and a noisy first scan would race with the same processes we
        // are trying to ignore.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Push updates exist for subscribers only. Everyone else reads
                // through EnsureFresh, which scans on demand.
                if (_hub.TopicHasSubscribers(Topic))
                {
                    lock (_scanLock)
                    {
                        ScanAndPublish();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[conflicts] scan failed: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void ScanAndPublish()
    {
        var detected = DetectRunningConflicts();

        var ids = ChangeKey(detected);

        LogTransitions(detected, first: !_detectionReady);

        bool changed = !ArraysEqual(ids, _lastDetectedIds);
        _lastDetectedIds = ids;
        _latest = detected;
        _detectionReady = true;
        Volatile.Write(ref _lastScanTicks, Environment.TickCount64);

        if (!changed && _cachedEnvelope is not null)
            return;

        var frame = new ConflictsFrame { Conflicts = detected.ToList() };
        var env = WsEnvelope.Build(Topic, frame, AppJsonContext.Default.ConflictsFrame);
        _cachedEnvelope = env.ToArray();

        if (_hub.TopicHasSubscribers(Topic))
        {
            _ = _hub.BroadcastTopicAsync(Topic, env);
        }
    }

    private List<DetectedConflict> DetectRunningConflicts()
    {
        var byName = BuildProcessNameIndex();
        if (byName.Count == 0)
            return new List<DetectedConflict>(0);

        var bundledOpenRgbPath = _openRgb?.ExePath;
        var result = new List<DetectedConflict>();

        foreach (var def in ConflictAppCatalog.All)
        {
            DetectedConflict? best = null;
            foreach (var procName in def.ProcessNames)
            {
                if (!byName.TryGetValue(procName, out var hits))
                    continue;

                foreach (var hit in hits)
                {
                    if (IsOurOwnOpenRgb(def.Id, hit.Pid, bundledOpenRgbPath))
                        continue;

                    // Keep the lowest pid as canonical for the catalog id so
                    // the SPA shows a deterministic value across renders.
                    if (best is null || hit.Pid < best.Pid)
                    {
                        best = new DetectedConflict
                        {
                            Id = def.Id,
                            DisplayName = def.DisplayName,
                            Category = def.Category,
                            ProcessName = procName,
                            Pid = hit.Pid,
                        };
                    }
                }
            }
            if (best is not null)
                result.Add(best);
        }
        return result;
    }

    /// <summary>
    /// Walk every running process exactly once and group by ProcessName so
    /// matching against the catalog is O(catalog × hits) instead of
    /// O(catalog × procs). OrdinalIgnoreCase matches how .NET casefolds
    /// Windows executable names. macOS returns an empty index - the catalog is
    /// Windows-only (see the #if MACOS arm).
    /// </summary>
    private static Dictionary<string, List<(int Pid, string? Path)>> BuildProcessNameIndex()
    {
        var index = new Dictionary<string, List<(int Pid, string? Path)>>(StringComparer.OrdinalIgnoreCase);

#if MACOS
        // Every catalog entry is Windows hardware-control software, and a bare
        // process-name match collides with macOS's own always-running
        // "ControlCenter" system process - surfacing a phantom "MSI Control
        // Center" conflict. No catalog entry applies on macOS, so never scan:
        // the empty index makes DetectRunningConflicts report nothing.
        return index;
#else
        // Windows + everything else: Process.GetProcesses() is the most
        // portable cross-platform path. Dispose every Process handle the
        // moment we have what we need so we don't accumulate kernel objects.
        var processes = Process.GetProcesses();
        foreach (var proc in processes)
        {
            try
            {
                string name;
                int pid;
                try
                {
                    name = proc.ProcessName;
                    pid = proc.Id;
                }
                catch { continue; }

                if (string.IsNullOrEmpty(name))
                    continue;
                if (!index.TryGetValue(name, out var bucket))
                {
                    bucket = new List<(int, string?)>(1);
                    index[name] = bucket;
                }
                bucket.Add((pid, null));
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return index;
#endif
    }

    /// <summary>
    /// Suppress matches against the bundled headless OpenRGB subprocess that
    /// the lighting stack spawns inside our install directory. Without this
    /// the warning would fire on every install that uses RGB.
    /// </summary>
    private static bool IsOurOwnOpenRgb(string defId, int pid, string? bundledPath)
    {
        if (!string.Equals(defId, "openrgb", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(bundledPath))
            return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var modulePath = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(modulePath))
                return false;
            return string.Equals(
                Path.GetFullPath(modulePath),
                Path.GetFullPath(bundledPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // MainModule access is denied for cross-session / elevated
            // processes. We can't prove the binary is ours, so let the
            // warning fire - even if the suspect is in fact our headless
            // OpenRGB.
            return false;
        }
    }

    /// <summary>
    /// What counts as a change worth republishing: id AND pid. A pid only moves
    /// when the app restarted, which clients read as "the kill did not stick",
    /// and keying on ids alone also left the cached envelope handing new
    /// subscribers pids that no longer exist.
    /// </summary>
    internal static string[] ChangeKey(IReadOnlyList<DetectedConflict> detected)
    {
        var ids = new string[detected.Count];
        for (int i = 0; i < detected.Count; i++)
            ids[i] = $"{detected[i].Id}:{detected[i].Pid}";
        Array.Sort(ids, StringComparer.Ordinal);
        return ids;
    }

    /// <summary>
    /// Log each competing app as it opens and closes. The service starts in
    /// session 0, so the vendor app that actually contends for the hardware
    /// launches at logon, minutes after the startup snapshot is written - without
    /// these lines a submitted log cannot say whether one was ever running.
    /// </summary>
    private void LogTransitions(IReadOnlyList<DetectedConflict> detected, bool first)
    {
        var ids = SortedIds(detected);
        var previous = _lastLoggedIds;
        var previousTicks = _lastLoggedTicks;
        var now = Environment.TickCount64;
        _lastLoggedIds = ids;
        _lastLoggedTicks = now;

        // The loop scans only while the topic has subscribers, so a dashboard
        // opened hours after boot produces the first scan since startup. Diffing
        // across that gap would stamp every running app as opened just now,
        // which reads as a launch time and sends a triager after the wrong hour.
        if (first || now - previousTicks > (long)TransitionGapLimit.TotalMilliseconds)
        {
            // An on-demand reader can force a scan at any cadence - every new topic
            // subscription does - so restate only when the set actually moved, or a
            // reconnect loop republishes the same baseline without bound.
            if (first || !ArraysEqual(ids, previous))
            {
                var scope = first ? " at startup" : "";
                ServiceLog.Info(detected.Count == 0
                    ? $"[conflicts] none running{scope}"
                    : $"[conflicts] running{scope}: {string.Join(", ", ids)}");
            }
            return;
        }

        foreach (var (id, message) in TransitionLines(previous, detected))
        {
            LogTransition(id, message);
        }
    }

    /// <summary>Ids of the detected set, sorted, for diffing one scan against the next.</summary>
    internal static string[] SortedIds(IReadOnlyList<DetectedConflict> detected)
    {
        var ids = new string[detected.Count];
        for (var i = 0; i < detected.Count; i++)
        {
            ids[i] = detected[i].Id;
        }
        Array.Sort(ids, StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    /// <summary>
    /// The open/close lines one scan produces against the previous id set. Keys
    /// on id, not <see cref="ChangeKey"/>'s id:pid, so an app that restarted
    /// between scans does not read as having closed and reopened. Pure; the
    /// caller owns rate limiting and the log.
    /// </summary>
    internal static List<(string Id, string Message)> TransitionLines(
        IReadOnlyList<string> previous, IReadOnlyList<DetectedConflict> detected)
    {
        var lines = new List<(string, string)>();
        var current = SortedIds(detected);
        foreach (var conflict in detected)
        {
            if (!Contains(previous, conflict.Id))
            {
                lines.Add((conflict.Id, $"{conflict.DisplayName} opened (id={conflict.Id} pid={conflict.Pid})"));
            }
        }
        foreach (var id in previous)
        {
            if (!Contains(current, id))
            {
                lines.Add((id, $"{FindById(id)?.DisplayName ?? id} closed (id={id})"));
            }
        }
        return lines;
    }

    /// <summary>
    /// One line per transition until an app has cycled past
    /// <see cref="MaxTransitionsPerWindow"/>, then one line saying so and silence
    /// until the window rolls. ServiceLog rotates per run and never by size, so
    /// an app restarting on a loop would otherwise bury what this logging exists
    /// to surface.
    /// </summary>
    private void LogTransition(string id, string message)
    {
        var now = Environment.TickCount64;
        if (now - _flapWindowTicks > (long)FlapWindow.TotalMilliseconds)
        {
            _flapWindowTicks = now;
            _transitionCounts.Clear();
        }

        _transitionCounts.TryGetValue(id, out var seen);
        _transitionCounts[id] = seen + 1;

        if (seen < MaxTransitionsPerWindow)
        {
            ServiceLog.Info($"[conflicts] {message}");
        }
        else if (seen == MaxTransitionsPerWindow)
        {
            ServiceLog.Info($"[conflicts] {FindById(id)?.DisplayName ?? id} is cycling; "
                + $"further transitions suppressed for {FlapWindow.TotalMinutes:0} min");
        }
    }

    private static bool Contains(IReadOnlyList<string> ids, string id)
    {
        foreach (var candidate in ids)
        {
            if (string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ArraysEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
