using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Platform.Displays;

public enum DigitizerHealth
{
    /// <summary>Cannot judge (query error, no panel, no HID collection, ambiguous panel). Never acted on.</summary>
    Unknown,
    /// <summary>Windows exposes a touch pointer device for the panel, or the guard has its own repair to run.</summary>
    Healthy,
    /// <summary>HID touch collection present, pointer query succeeded and lists no touch device for it.</summary>
    Absent,
}

public enum TouchRecoveryStep
{
    Idle,
    FirstObserved,
    Waiting,
    Restart,
    Recovered,
    GaveUp,
}

/// <summary>
/// Pure gate: decides whether a snapshot shows a catalog panel whose HID
/// touch collection exists while Windows has no pointer device for it.
/// Anything short of that is Unknown or Healthy, so a working system can
/// never reach the restart.
/// </summary>
public static class TouchDigitizerRecoveryGate
{
    internal const int TouchPageUsagePage = 0x0D;
    internal const int TouchScreenUsage = 0x04;

    public static (DigitizerHealth Health, string? HidPath) Assess(
        TouchMapSnapshot snapshot, TouchMappingOutcome outcome, Func<int, int, IReadOnlyList<HidDeviceInfo>> findHid)
    {
        if (outcome is TouchMappingOutcome.AlreadyCorrect or TouchMappingOutcome.NeedsRepair)
            return (DigitizerHealth.Healthy, null);
        if (outcome != TouchMappingOutcome.NoDigitizer) return (DigitizerHealth.Unknown, null);
        if (!snapshot.PointerQueryOk || snapshot.UnnamedTouchPointers > 0) return (DigitizerHealth.Unknown, null);

        TouchPanelCatalogEntry? y70 = null;
        foreach (var display in snapshot.Displays)
        {
            var entry = TouchPanelCatalog.MatchDisplay(display.MonitorInterfacePath);
            if (entry is null) continue;
            // A companion-scoped panel (VID/PID shared with the Y70) makes the
            // HID collection ambiguous: leave every such box alone.
            if (entry.IsCompanionScoped) return (DigitizerHealth.Unknown, null);
            y70 = entry;
        }
        if (y70 is null) return (DigitizerHealth.Unknown, null);

        foreach (var id in y70.DigitizerIds)
        {
            foreach (var hid in findHid(id.VendorId, id.ProductId))
            {
                if (hid.UsagePage == TouchPageUsagePage && hid.Usage == TouchScreenUsage && !string.IsNullOrEmpty(hid.Path))
                    return (DigitizerHealth.Absent, hid.Path);
            }
        }
        return (DigitizerHealth.Unknown, null);
    }
}

/// <summary>
/// Pure state machine over successive health observations. At most one
/// restart per HID path per service run; any non-Absent observation resets
/// the pending window.
/// </summary>
public sealed class TouchRecoveryTracker
{
    private readonly TimeSpan _settle;
    private readonly TimeSpan _postRestartDelay;
    private readonly HashSet<string> _restarted = new(StringComparer.OrdinalIgnoreCase);
    private string? _hidPath;
    private DateTime? _hidFirstSeen;
    private DateTime? _firstAbsent;
    private int _absentCount;
    private DateTime? _restartAt;

    public TouchRecoveryTracker(TimeSpan settle, TimeSpan postRestartDelay)
    {
        _settle = settle;
        _postRestartDelay = postRestartDelay;
    }

    /// <summary>True while a follow-up check is worth scheduling (pending window or post-restart verify).</summary>
    public bool NeedsFollowUp => _restartAt is not null || (_firstAbsent is not null && _hidPath is not null && !_restarted.Contains(_hidPath));

    public void RestartFailed() => _restartAt = null;

    public TouchRecoveryStep Observe(DateTime nowUtc, DigitizerHealth health, string? hidPath, bool capBlocked)
    {
        if (health != DigitizerHealth.Absent || string.IsNullOrEmpty(hidPath))
        {
            var recovered = health == DigitizerHealth.Healthy && _restartAt is not null;
            _restartAt = null;
            _firstAbsent = null;
            _absentCount = 0;
            _hidFirstSeen = null;
            _hidPath = null;
            return recovered ? TouchRecoveryStep.Recovered : TouchRecoveryStep.Idle;
        }

        if (!string.Equals(_hidPath, hidPath, StringComparison.OrdinalIgnoreCase))
        {
            _hidPath = hidPath;
            _hidFirstSeen = nowUtc;
            _firstAbsent = null;
            _absentCount = 0;
        }
        _hidFirstSeen ??= nowUtc;

        if (_restarted.Contains(hidPath))
        {
            if (_restartAt is { } at && nowUtc - at >= _postRestartDelay)
            {
                _restartAt = null;
                return TouchRecoveryStep.GaveUp;
            }
            return TouchRecoveryStep.Idle;
        }

        var first = _firstAbsent is null;
        _firstAbsent ??= nowUtc;
        _absentCount++;
        if (first) return TouchRecoveryStep.FirstObserved;

        if (_absentCount >= 2
            && nowUtc - _firstAbsent.Value >= _settle
            && nowUtc - _hidFirstSeen.Value >= _settle
            && !capBlocked)
        {
            _restarted.Add(hidPath);
            _restartAt = nowUtc;
            return TouchRecoveryStep.Restart;
        }
        return TouchRecoveryStep.Waiting;
    }
}

/// <summary>Persists the last automatic restart so a service restart loop cannot repeat it.</summary>
public interface ITouchRecoveryStampStore
{
    DateTime? LastRestartUtc();
    void Record(DateTime utc);
}

public sealed class FileTouchRecoveryStampStore : ITouchRecoveryStampStore
{
    private static string StampPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "touch-digitizer-recovery");

    public DateTime? LastRestartUtc()
    {
        try
        {
            if (!File.Exists(StampPath)) return null;
            return long.TryParse(File.ReadAllText(StampPath).Trim(), out var ticks) && ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc)
                : null;
        }
        catch { return DateTime.UtcNow; }
    }

    public void Record(DateTime utc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StampPath)!);
            File.WriteAllText(StampPath, utc.Ticks.ToString());
        }
        catch { }
    }
}

/// <summary>
/// Restarts the Y70 touch HID devnode when its HID collection exists but
/// Windows never exposed a pointer device (a boot-time race that only a
/// manual disable/enable cleared). Reuses the existing devnode restarter.
/// </summary>
public sealed class TouchDigitizerRecovery
{
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PostRestartDelay = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan FollowUpInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PersistedCap = TimeSpan.FromHours(2);

    private readonly Func<int, int, IReadOnlyList<HidDeviceInfo>> _findHid;
    private readonly ITouchDigitizerDevnodeRestarter _restarter;
    private readonly ITouchRecoveryStampStore _stamps;
    private readonly Func<DateTime> _utcNow;
    private readonly TouchRecoveryTracker _tracker = new(Settle, PostRestartDelay);

    public TouchDigitizerRecovery(IHidEnumerator hid, ITouchDigitizerDevnodeRestarter restarter)
        : this(hid.Find, restarter, new FileTouchRecoveryStampStore(), () => DateTime.UtcNow)
    {
    }

    internal TouchDigitizerRecovery(
        Func<int, int, IReadOnlyList<HidDeviceInfo>> findHid,
        ITouchDigitizerDevnodeRestarter restarter,
        ITouchRecoveryStampStore stamps,
        Func<DateTime> utcNow)
    {
        _findHid = findHid;
        _restarter = restarter;
        _stamps = stamps;
        _utcNow = utcNow;
    }

    public void Reset() => _tracker.Observe(_utcNow(), DigitizerHealth.Unknown, null, capBlocked: true);

    /// <summary>Feeds one guard pass. Returns the delay for a bounded follow-up pass, or null when none is needed.</summary>
    public TimeSpan? Observe(TouchMapSnapshot snapshot, TouchMappingOutcome outcome)
    {
        var now = _utcNow();
        var (health, hidPath) = TouchDigitizerRecoveryGate.Assess(snapshot, outcome, _findHid);
        var last = _stamps.LastRestartUtc();
        var capBlocked = last is { } l && (l > now || now - l < PersistedCap);
        var step = _tracker.Observe(now, health, hidPath, capBlocked);
        switch (step)
        {
            case TouchRecoveryStep.FirstObserved:
                Log($"touch HID collection present but Windows has no pointer device for it; waiting {Settle.TotalSeconds:0}s to confirm");
                break;
            case TouchRecoveryStep.Restart:
                Log("digitizer absence confirmed; restarting the touch devnode once");
                _stamps.Record(now);
                if (!_restarter.Restart(hidPath!))
                {
                    _tracker.RestartFailed();
                    Log("devnode restart could not be issued; not retrying");
                }
                break;
            case TouchRecoveryStep.Recovered:
                Log("digitizer recovered after devnode restart");
                break;
            case TouchRecoveryStep.GaveUp:
                Log("digitizer still absent after devnode restart; giving up until the next service run");
                break;
        }
        return _tracker.NeedsFollowUp ? FollowUpInterval : null;
    }

    private static void Log(string message) => ServiceLog.Info($"[touch-map] {message}");
}
