#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// The one place that decides whether a DDC/CI transaction may go out right
/// now. Every transaction in <see cref="WindowsDisplayBrightnessProvider"/>
/// passes through <see cref="ShouldSkip"/>, so the policy holds whoever the
/// caller is - the Displays widget's 5 s poll, a Stream Deck key, an SDK app
/// action, or the dashboard.
///
/// Why it exists: a monitor entering, sitting in, or leaving DPMS-off can hang
/// its scaler on a DDC transaction, and its OSD and power button then stop
/// responding until the panel is physically unplugged. A session lock is the
/// case that bites. Unlike sleep, nothing suspends this process: Windows powers
/// the monitors down behind the lock screen while we keep talking to them on
/// the caller's cadence. Reported on a Samsung Odyssey G7 (SAM105C) over
/// DisplayPort and confirmed by A/B - our traffic removed, no hang; restored,
/// the hang returns.
///
/// Lock state is queried per call from WTS for this process's own session.
/// Unlocks arrive as session notifications through
/// <see cref="OnSessionLockChanged"/>, so one is seen even when no transaction
/// was asking at the time, and helper start counts as one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DdcGate
{
    /// <summary>
    /// How long after an unlock to stay quiet. The desktop is back before the
    /// panels have finished re-training their links, and a transaction landing
    /// in that window is the same hazard as one landing during the blank.
    /// </summary>
    private static readonly TimeSpan UnlockSettle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Without session notifications, a gap this long between calls may hide
    /// an unlock nobody was asking about, so it is treated as one.
    /// </summary>
    private static readonly TimeSpan ObservationStale = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static bool _lastLocked;
    // Monotonic (Environment.TickCount64), not wall clock: a backward clock
    // correction must not extend the closed window arbitrarily.
    private static long _unlockedAtMs = -1;
    private static long _lastSeenMs = -1;
    private static Func<bool> _notificationsActive = () => false;
    private static Timer? _settleTimer;

    /// <summary>Raised on a timer thread when a settle window ends with the session unlocked.</summary>
    public static event Action? Opened;

    /// <summary>
    /// Called once at helper start, which counts as an unlock: the logon that
    /// started the helper is one no notification reports.
    /// <paramref name="notificationsActive"/> says whether
    /// <see cref="OnSessionLockChanged"/> is currently being fed.
    /// </summary>
    public static void Start(Func<bool> notificationsActive)
    {
        lock (Gate)
        {
            _notificationsActive = notificationsActive;
            MarkUnlockedLocked(Environment.TickCount64);
        }
    }

    /// <summary>WTS_SESSION_LOCK (true) / WTS_SESSION_UNLOCK (false) for the helper's session.</summary>
    public static void OnSessionLockChanged(bool locked)
    {
        lock (Gate)
        {
            if (!locked) MarkUnlockedLocked(Environment.TickCount64);
            _lastLocked = locked;
        }
    }

    /// <summary>
    /// True when no DDC transaction should be issued right now.
    /// <paramref name="reason"/> is a short tag for the log, empty when the
    /// call is allowed.
    /// </summary>
    public static bool ShouldSkip(out string reason)
    {
        var locked = QuerySessionLocked();
        var nowMs = Environment.TickCount64;
        long unlockedAtMs;
        lock (Gate)
        {
            // An unlock this query sees before its notification is delivered,
            // or, with no notifications, one a long gap may have hidden.
            var unseen = !_notificationsActive() && nowMs - _lastSeenMs > (long)ObservationStale.TotalMilliseconds;
            if (!locked && (_lastLocked || unseen)) MarkUnlockedLocked(nowMs);
            _lastLocked = locked;
            _lastSeenMs = nowMs;
            unlockedAtMs = _unlockedAtMs;
        }

        if (locked)
        {
            reason = "session locked";
            return true;
        }

        if (unlockedAtMs >= 0 && nowMs - unlockedAtMs < (long)UnlockSettle.TotalMilliseconds)
        {
            reason = "unlock settling";
            return true;
        }

        reason = "";
        return false;
    }

    private static void MarkUnlockedLocked(long nowMs)
    {
        _unlockedAtMs = nowMs;
        _settleTimer ??= new Timer(_ => OnSettleElapsed());
        _settleTimer.Change(UnlockSettle, Timeout.InfiniteTimeSpan);
    }

    // An exception escaping a Timer callback is process-fatal.
    private static void OnSettleElapsed()
    {
        try
        {
            lock (Gate)
            {
                var remainingMs = _unlockedAtMs + (long)UnlockSettle.TotalMilliseconds - Environment.TickCount64;
                if (remainingMs > 0)
                {
                    _settleTimer?.Change(remainingMs, Timeout.Infinite);
                    return;
                }
            }
            // Locked again: the next unlock re-arms the timer.
            if (ShouldSkip(out _)) return;
            Opened?.Invoke();
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.HelperLog.Write($"[ddc] settle callback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// WTS lock state for this process's own session. UNKNOWN (0xFFFFFFFF) and
    /// every failure path read as unlocked: a query that cannot answer must not
    /// disable brightness control forever.
    /// </summary>
    private static bool QuerySessionLocked()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx, out var buf, out var len)
                || buf == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (len < (uint)Marshal.SizeOf<WTSINFOEX_PREFIX>()) return false;
                var info = Marshal.PtrToStructure<WTSINFOEX_PREFIX>(buf);
                return info.Level == 1 && info.SessionFlags == WTS_SESSIONSTATE_LOCK;
            }
            finally { WTSFreeMemory(buf); }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.HelperLog.Write($"[ddc] lock-state query failed: {ex.Message}");
            return false;
        }
    }

    private const uint WTS_CURRENT_SESSION = unchecked((uint)-1);
    // WTS_INFO_CLASS.WTSSessionInfoEx
    private const int WTSSessionInfoEx = 25;
    // WTSINFOEX_LEVEL1_W.SessionFlags, Win10+ semantics (the documented Win7
    // lock/unlock inversion predates the 19041 floor). UNKNOWN is 0xFFFFFFFF.
    private const int WTS_SESSIONSTATE_LOCK = 0;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    // Leading fields of WTSINFOEXW (x64): Level at 0; the Data union starts at
    // 8 because WTSINFOEX_LEVEL1_W carries LARGE_INTEGER members (8-byte
    // alignment). Only the fields ahead of the union's WCHAR arrays are mapped.
    [StructLayout(LayoutKind.Explicit)]
    private struct WTSINFOEX_PREFIX
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }
}
#endif
