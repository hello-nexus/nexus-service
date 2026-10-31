using System;
using System.Collections.Generic;

namespace Nexus.Service.Lifecycle;

/// <summary>What the service does about a console session when it wants to start something in it.</summary>
internal enum ConsoleLaunchPlan
{
    /// <summary>A user is signed in: the existing schtasks launch for that user.</summary>
    ConsoleUser,
    /// <summary>Nobody is signed in and OOBE is not running: wait for a logon.</summary>
    WaitForLogon,
    /// <summary>Nobody is signed in and Windows is in OOBE: start directly in the console session.</summary>
    OobeNoUser,
    /// <summary>The temporary OOBE account is signed in and Windows is in OOBE: start in the console session with that account's token.</summary>
    OobeTempUser,
}

/// <summary>
/// The decisions behind showing the panel kiosk during first-boot setup. Pure so
/// the normal path (a signed-in user, or OOBE finished) is pinned by tests.
/// </summary>
internal static class OobePanelPolicy
{
    /// <summary>
    /// <paramref name="inOobe"/> and <paramref name="userIsLocal"/> are evaluated only for a
    /// console user named like the OOBE temporary account, or for no user at all, so a
    /// signed-in user, locked or not, never reaches them.
    /// </summary>
    internal static ConsoleLaunchPlan Decide(string consoleUser, bool consoleSessionExists, Func<bool> inOobe, Func<bool>? userIsLocal = null)
    {
        if (string.IsNullOrEmpty(consoleUser))
        {
            return consoleSessionExists && inOobe() ? ConsoleLaunchPlan.OobeNoUser : ConsoleLaunchPlan.WaitForLogon;
        }
        if (!IsOobeTempAccount(consoleUser))
        {
            return ConsoleLaunchPlan.ConsoleUser;
        }
        return (userIsLocal?.Invoke() ?? true) && inOobe() ? ConsoleLaunchPlan.OobeTempUser : ConsoleLaunchPlan.ConsoleUser;
    }

    /// <summary>The account Windows setup runs under: defaultuser0, or defaultuser followed by digits.</summary>
    internal static bool IsOobeTempAccount(string user)
    {
        const string prefix = "defaultuser";
        if (user.Length <= prefix.Length || !user.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        for (var i = prefix.Length; i < user.Length; i++)
        {
            if (user[i] < '0' || user[i] > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// Setup processes started with the service token (nobody was signed in) are replaced once the
    /// temporary setup account is on the console, since WebView2 does not run as SYSTEM.
    /// </summary>
    internal static bool ShouldReplaceServiceTokenLaunches(ConsoleLaunchPlan plan, int serviceTokenTracked)
        => plan == ConsoleLaunchPlan.OobeTempUser && serviceTokenTracked > 0;

    /// <summary>
    /// Whether the panel overlay (a WebView2 host) is started for this plan. The service token is the
    /// SYSTEM user, which WebView2 cannot run as, so with nobody signed in only the helper starts.
    /// </summary>
    internal static bool OverlayCanStart(ConsoleLaunchPlan plan) => plan != ConsoleLaunchPlan.OobeNoUser;

    /// <summary>A console user other than the OOBE temporary account.</summary>
    internal static bool IsRealUser(string user) => !string.IsNullOrEmpty(user) && !IsOobeTempAccount(user);

    /// <summary>
    /// OOBE-launched processes are reaped once OOBE is over, or as soon as a real user is
    /// on the console (OOBEComplete can lag a real logon); the temporary OOBE account does not count. Nothing tracked means
    /// nothing is evaluated.
    /// </summary>
    internal static bool ShouldReap(int trackedCount, Func<bool> inOobe, Func<bool> consoleUserPresent)
        => trackedCount > 0 && (consoleUserPresent() || !inOobe());

    /// <summary>
    /// Whether <paramref name="imagePath"/> is one of the two binaries this service
    /// launches in an OOBE session: the overlay host or Nexus.exe, both under
    /// <paramref name="baseDirectory"/>. A pid file is writable by others, so no
    /// process is ended on its word alone.
    /// </summary>
    internal static bool IsOwnImage(string? imagePath, string baseDirectory)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(baseDirectory))
        {
            return false;
        }
        var full = System.IO.Path.GetFullPath(imagePath);
        return string.Equals(full, System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, "overlay", "nexus-overlay.exe")), StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, "Nexus.exe")), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Setup helpers this service ended on purpose, by pid and start time. The drop handler skips
/// them and the launch decision ignores a still-registered connection that belongs to one.
/// Entries expire, so a stale pid cannot swallow a later crash.
/// </summary>
internal sealed class ReapedHelperSet
{
    private readonly object _lock = new();
    private readonly List<(int Pid, long StartFileTime, int Session, DateTime At)> _entries = new();
    private readonly Func<DateTime> _now;
    private readonly Func<int, long, bool> _startMatchesOrGone;
    private readonly TimeSpan _ttl;

    /// <param name="startMatchesOrGone">True when the pid is gone or still has the recorded start time; false when it was reused.</param>
    internal ReapedHelperSet(Func<DateTime> now, Func<int, long, bool> startMatchesOrGone, TimeSpan ttl)
    {
        _now = now;
        _startMatchesOrGone = startMatchesOrGone;
        _ttl = ttl;
    }

    internal void Add(int pid, long startFileTime, int session)
    {
        lock (_lock) { _entries.Add((pid, startFileTime, session, _now())); }
    }

    internal bool IsReaped(int pid)
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return false;
            Expire();
            return _entries.Exists(e => e.Pid == pid && _startMatchesOrGone(e.Pid, e.StartFileTime));
        }
    }

    /// <summary>True once for an ended helper pid; also forgets it.</summary>
    internal bool Take(int pid)
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return false;
            Expire();
            return _entries.RemoveAll(e => e.Pid == pid && _startMatchesOrGone(e.Pid, e.StartFileTime)) > 0;
        }
    }

    /// <summary>A new helper connected in <paramref name="session"/>: earlier ones there no longer apply.</summary>
    internal void Clear(int session)
    {
        lock (_lock) { _entries.RemoveAll(e => e.Session == session); }
    }

    private void Expire()
    {
        var now = _now();
        _entries.RemoveAll(e => now - e.At > _ttl);
    }
}
