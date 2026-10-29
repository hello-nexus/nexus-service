#if WINDOWS
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Resolves the active console user's SID, so callers can read that user's
/// hive under HKEY_USERS from the LocalSystem service, whose own HKCU is the
/// SYSTEM hive rather than the interactive user's.
/// </summary>
internal static class ConsoleUserSid
{
    private const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    // Warn once per failure streak, not once per call - Resolve runs on a
    // poll cadence, and a resolve failure is expected transiently around
    // profile-load races. A success clears this so the next real streak
    // still gets its own warning.
    private static bool _warnedThisStreak;

    /// <summary>Resolves the console user's SID, requiring that
    /// requiredSubPath (relative to the SID's hive root) is present and
    /// loaded - a caller-specific existence check, since a SID that maps to
    /// an unmounted or wrong profile is indistinguishable from a correct one
    /// without checking the path the caller actually needs. Returns null
    /// (no console user, no unique profile match, or the hive/path isn't
    /// there) and, other than the no-console-user case, warns once per
    /// failure streak.</summary>
    public static string? Resolve(string requiredSubPath, bool quietIfHiveMissing = false)
    {
        var username = UserHelperBootstrapper.ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }

        var sid = ResolveForUsername(username);
        if (sid is null)
        {
            WarnResolveFailure($"no unique ProfileList match for console user '{username}'");
            return null;
        }

        using var hive = Registry.Users.OpenSubKey($@"{sid}\{requiredSubPath}");
        if (hive is null)
        {
            // A caller whose key legitimately may not exist (Windows writes
            // some keys only once a page is touched) polls this, so a missing
            // key is its normal answer, not a streak worth a warning.
            if (!quietIfHiveMissing) WarnResolveFailure($"SID {sid}'s {requiredSubPath} hive is unloaded or empty");
            return null;
        }

        _warnedThisStreak = false;
        return sid;
    }

    /// <summary>Resolves the console user's profile directory (ProfileList's
    /// ProfileImagePath) without the hardcoded-C:\Users\{user} shortcut. Null
    /// under the same conditions as <see cref="Resolve"/>'s SID lookup.</summary>
    public static string? ResolveProfilePath()
    {
        var username = UserHelperBootstrapper.ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }
        var sid = ResolveForUsername(username);
        if (sid is null)
        {
            return null;
        }
        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListPath);
        using var profile = profileList?.OpenSubKey(sid);
        return profile?.GetValue("ProfileImagePath") as string;
    }

    // ProfileList maps a profile folder name (e.g. "C:\Users\Nicola") to the
    // SID that owns it; there is no direct username-to-SID registry value.
    internal static string? ResolveForUsername(string username)
    {
        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListPath);
        if (profileList is null)
        {
            return null;
        }

        var profiles = new List<(string Sid, string ProfileUsername)>();
        foreach (var sid in profileList.GetSubKeyNames())
        {
            using var profile = profileList.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is not string imagePath)
            {
                continue;
            }
            profiles.Add((sid, Path.GetFileName(imagePath.TrimEnd('\\'))));
        }
        return ConsoleUserSidMatcher.Match(profiles, username);
    }

    private static void WarnResolveFailure(string reason)
    {
        if (_warnedThisStreak)
        {
            return;
        }
        _warnedThisStreak = true;
        ServiceLog.Warn($"[console-user-sid] {reason}");
    }
}
#endif
