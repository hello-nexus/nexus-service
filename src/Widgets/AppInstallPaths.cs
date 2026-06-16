using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Service.Widgets;

/// <summary>
/// Per-OS install roots scanned by <see cref="AppRegistry"/>. Precedence
/// matches <c>plans/widget-sdk.md</c>: user installs (signed) shadow bundled
/// (signed); dev installs (unsigned) shadow both but render a dev banner.
/// </summary>
/// <remarks>
/// Discovery + serving live here; install / uninstall writes are handled by
/// <see cref="AppInstaller"/>. Signing verification and dev-banner
/// rendering are still pending.
/// </remarks>
public static class AppInstallPaths
{
    public enum Source
    {
        /// <summary><c>apps-dev/</c> - unpacked local copies authors symlink for iteration.</summary>
        Dev,
        /// <summary><c>apps/</c> in the user profile.</summary>
        User,
        /// <summary><c>apps/</c> next to the service binary.</summary>
        Bundled,
    }

    public readonly record struct Root(string Path, Source Source);

    /// <summary>
    /// All install roots that may contain app directories, ordered such
    /// that earlier roots shadow later roots for the same app id.
    /// </summary>
    public static IReadOnlyList<Root> Enumerate(string? baseDir = null)
    {
        var roots = new List<Root>();
        var appData = ResolveAppData();
        if (!string.IsNullOrEmpty(appData))
        {
            MigrateWidgetsDirs(appData);
            roots.Add(new Root(Path.Combine(appData, "apps-dev"), Source.Dev));
            roots.Add(new Root(Path.Combine(appData, "apps"), Source.User));
        }
        var bundled = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
        roots.Add(new Root(Path.Combine(bundled, "apps"), Source.Bundled));
        return roots;
    }

    /// <summary>
    /// One-time migration: rename legacy on-disk dirs so existing user apps
    /// are preserved. Runs once per process start; skipped if the new dirs
    /// already exist or the old ones are absent.
    /// </summary>
    private static void MigrateWidgetsDirs(string appData)
    {
        TryRenameDir(Path.Combine(appData, "widgets-dev"), Path.Combine(appData, "apps-dev"));
        TryRenameDir(Path.Combine(appData, "widgets"), Path.Combine(appData, "apps"));
    }

    private static void TryRenameDir(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath) || Directory.Exists(newPath)) return;
        try
        {
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[apps] migration rename {oldPath} -> {newPath} failed: {ex.Message}");
        }
    }

    private static string ResolveAppData()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? "" : Path.Combine(appData, "Nexus");
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
}
