using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Nexus.Service.Platform;

namespace Nexus.Service.Widgets;

/// <summary>
/// Per-OS install roots scanned by <see cref="AppRegistry"/>. Precedence
/// matches <c>plans/third-party-app-sdk.md</c>: user installs shadow bundled.
/// </summary>
/// <remarks>
/// Discovery + serving live here; install / uninstall writes are handled by
/// <see cref="AppInstaller"/>. No root is signature-checked.
/// </remarks>
public static class AppInstallPaths
{
    public enum Source
    {
        /// <summary><c>apps/</c> in the user profile.</summary>
        User,
        /// <summary><c>apps/</c> next to the service binary.</summary>
        Bundled,
    }

    public readonly record struct Root(string Path, Source Source);

    /// <summary>
    /// All install roots that may contain widget directories, ordered such
    /// that earlier roots shadow later roots for the same widget id.
    /// </summary>
    public static IReadOnlyList<Root> Enumerate(string? baseDir = null)
    {
        var roots = new List<Root>();
        var appData = ResolveAppData();
        if (!string.IsNullOrEmpty(appData))
        {
            roots.Add(new Root(Path.Combine(appData, "apps"), Source.User));
        }
        var bundled = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
        roots.Add(new Root(Path.Combine(bundled, "apps"), Source.Bundled));
        return roots;
    }

    private static string ResolveAppData()
    {
        if (OperatingSystem.IsWindows())
        {
            // Machine-wide under %ProgramData%, next to logs/settings, so the
            // LocalSystem daemon installs once for every user instead of burying
            // apps in the SYSTEM profile's Roaming. ProgramData is user-writable
            // by default, so SecureUserRoots() locks apps/ in prod.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrEmpty(programData) ? "" : Path.Combine(programData, "Nexus");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "";
            return string.IsNullOrEmpty(home) ? "" : Path.Combine(home, "Library", "Application Support", "Nexus");
        }
        // Linux: XDG_DATA_HOME or ~/.local/share/Nexus
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "Nexus");
        var linHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        return string.IsNullOrEmpty(linHome) ? "" : Path.Combine(linHome, ".local", "share", "Nexus");
    }

    /// <summary>
    /// Lock the user app root (<c>apps/</c>) so a non-admin local user can't
    /// plant a widget the service then discovers and serves. %ProgramData% is
    /// user-writable by default and app bundles execute (in the SDK sandbox), so
    /// the dir must be writable only by SYSTEM + Administrators.
    /// No-op unless running as the LocalSystem daemon: a dev running the service
    /// interactively keeps the root writable.
    /// Best-effort; never blocks startup.
    /// </summary>
    public static void SecureUserRoots()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            if (!WindowsIdentity.GetCurrent().IsSystem)
                return;
            var appData = ResolveAppData();
            if (string.IsNullOrEmpty(appData))
                return;
            SecureDir(Path.Combine(appData, "apps"));
        }
        catch { /* never block startup on an ACL failure */ }
    }

    [SupportedOSPlatform("windows")]
    private static void SecureDir(string dir) => WindowsDirectorySecurity.Protect(dir);
}
