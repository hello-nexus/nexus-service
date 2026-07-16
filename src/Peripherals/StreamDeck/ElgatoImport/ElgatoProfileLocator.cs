using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

public enum ElgatoStoreStatus
{
    Ok,
    NotFound,
    UnsupportedVersion,
}

/// <summary>
/// Resolves the local Elgato Stream Deck software's profile store root
/// (macOS <c>~/Library/Application Support/com.elgato.StreamDeck</c>, Windows
/// <c>%APPDATA%\Elgato\StreamDeck</c>). The service runs as LocalSystem, whose
/// %APPDATA% is the SYSTEM profile's, so on Windows every real user profile
/// under <c>&lt;systemdrive&gt;\Users</c> is scanned to reach the logged-in
/// user's store. Only ProfilesV3 (schema "3.0") is supported; a ProfilesV2-only
/// install reports UnsupportedVersion so the caller can tell "no Elgato install"
/// from "too old" apart.
/// </summary>
public sealed class ElgatoProfileLocator
{
    private const string ProfilesV3DirName = "ProfilesV3";
    private const string ProfilesV2DirName = "ProfilesV2";
    private const string RootOverrideEnvVar = "NEXUS_ELGATO_STORE_ROOT";

    private readonly string? _rootOverride;

    public ElgatoProfileLocator() : this(null)
    {
    }

    /// <summary>Test/route seam: points at a fixture dir instead of the real platform default.</summary>
    public ElgatoProfileLocator(string? rootOverride)
    {
        _rootOverride = rootOverride;
    }

    public (ElgatoStoreStatus Status, string? ProfilesV3Root) Resolve()
    {
        var explicitRoot = _rootOverride ?? Environment.GetEnvironmentVariable(RootOverrideEnvVar);
        var candidates = !string.IsNullOrEmpty(explicitRoot)
            ? new[] { explicitRoot }
            : PlatformCandidateDirs();
        return Evaluate(candidates);
    }

    /// <summary>
    /// Picks the Elgato store among candidate dirs: the newest one carrying a
    /// ProfilesV3 dir (the active user's, when the LocalSystem scan yields more
    /// than one), else UnsupportedVersion if only a ProfilesV2 store exists,
    /// else NotFound. Separated from directory discovery so the selection is
    /// testable with fixture dirs.
    /// </summary>
    internal static (ElgatoStoreStatus Status, string? ProfilesV3Root) Evaluate(IEnumerable<string> elgatoDirs)
    {
        string? bestV3 = null;
        var bestStamp = DateTime.MinValue;
        var sawV2 = false;
        foreach (var dir in elgatoDirs)
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            var v3 = Path.Combine(dir, ProfilesV3DirName);
            if (SafeDirExists(v3))
            {
                var stamp = SafeLastWrite(v3);
                if (bestV3 is null || stamp >= bestStamp)
                {
                    bestV3 = v3;
                    bestStamp = stamp;
                }
                continue;
            }
            if (SafeDirExists(Path.Combine(dir, ProfilesV2DirName)))
            {
                sawV2 = true;
            }
        }
        if (bestV3 is not null)
        {
            return (ElgatoStoreStatus.Ok, bestV3);
        }
        return sawV2 ? (ElgatoStoreStatus.UnsupportedVersion, null) : (ElgatoStoreStatus.NotFound, null);
    }

    /// <summary>
    /// Elgato store dirs to probe. macOS resolves the one real home. Windows
    /// yields the process's own Roaming first (an interactive run), then every
    /// real user profile under &lt;systemdrive&gt;\Users: the service runs as
    /// LocalSystem, whose Roaming is the SYSTEM profile's, so the logged-in
    /// user's store is only reachable by scanning the profiles it can read.
    /// </summary>
    private static IEnumerable<string> PlatformCandidateDirs()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Library", "Application Support", "com.elgato.StreamDeck");
            }
            yield break;
        }
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? appData = null;
        try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch { /* ignore */ }
        if (!string.IsNullOrEmpty(appData))
        {
            var d = Path.Combine(appData, "Elgato", "StreamDeck");
            if (seen.Add(d)) yield return d;
        }

        string? usersRoot = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(root)) usersRoot = Path.Combine(root, "Users");
        }
        catch { /* ignore */ }
        if (usersRoot is null || !SafeDirExists(usersRoot))
        {
            yield break;
        }
        string[] profiles;
        try { profiles = Directory.GetDirectories(usersRoot); }
        catch { yield break; }
        foreach (var profile in profiles)
        {
            var leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users")
            {
                continue;
            }
            var d = Path.Combine(profile, "AppData", "Roaming", "Elgato", "StreamDeck");
            if (seen.Add(d)) yield return d;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
