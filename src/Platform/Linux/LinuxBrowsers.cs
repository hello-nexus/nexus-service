using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Chromium-family binary probe order and the dashboard-URL opener, shared by
/// the tray (<see cref="LinuxTrayHost"/>), the panel kiosk host, and any
/// caller that needs to open the dashboard outside those two (open-app route,
/// deck OpenDashboard action). --app / --kiosk windows require a Chromium
/// engine; Firefox and the portal openers are dashboard-only fallbacks and
/// stay in <see cref="OpenUrl"/>'s list.
/// </summary>
internal static class LinuxBrowsers
{
    // Computed per call: HOME is adopted from the active session after the
    // root daemon starts, so a type-init snapshot could point at /root.
    public static string[] ChromiumFamily()
    {
        var userFlatpakBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "flatpak", "exports", "bin");
        const string sysFlatpakBin = "/var/lib/flatpak/exports/bin";
        return new[]
        {
            Path.Combine(userFlatpakBin, "org.chromium.Chromium"),
            sysFlatpakBin + "/org.chromium.Chromium",
            Path.Combine(userFlatpakBin, "com.google.Chrome"),
            sysFlatpakBin + "/com.google.Chrome",
            Path.Combine(userFlatpakBin, "com.brave.Browser"),
            sysFlatpakBin + "/com.brave.Browser",
            sysFlatpakBin + "/com.microsoft.Edge",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome",
            "/usr/bin/brave-browser",
            // Ubuntu ships Chromium only as a snap and exports no /usr/bin
            // symlink, so nothing above matches on a stock install.
            "/snap/bin/chromium",
            "/snap/bin/brave",
        };
    }

    /// <summary>Application id for a flatpak export path (<c>…/flatpak/exports/bin/org.chromium.Chromium</c>), else null.</summary>
    public static string? FlatpakAppId(string browserPath)
    {
        var dir = Path.GetDirectoryName(browserPath) ?? "";
        return dir.Replace('\\', '/').EndsWith("/flatpak/exports/bin", StringComparison.Ordinal)
            ? Path.GetFileName(browserPath)
            : null;
    }

    public static string? FindChromium()
    {
        foreach (var path in ChromiumFamily())
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>
    /// A browser launcher: the binary, any args before the URL, and whether to
    /// pass the URL as a Chromium <c>--app=&lt;url&gt;</c> flag (a clean chromeless
    /// app window - no tabs/URL bar, just native window controls) versus a normal
    /// positional URL argument.
    /// </summary>
    private readonly record struct Launcher(string Path, string[] PreArgs, bool AppMode);

    // Both flatpak export roots - user (~/.local/share/flatpak) is checked before
    // system (/var/lib/flatpak) since a CLI install without root lands in user.
    private static readonly string UserFlatpakBin = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "flatpak", "exports", "bin");
    private const string SysFlatpakBin = "/var/lib/flatpak/exports/bin";

    private static Launcher[] BuildLaunchers()
    {
        var list = new List<Launcher>();
        // Chromium-family in --app mode FIRST: opens the dashboard as a clean,
        // chromeless window - the closest Linux equivalent to the Windows/macOS
        // embedded panel (no native WebView host exists on Linux yet). Probe
        // order shared with the panel kiosk host.
        foreach (var path in ChromiumFamily())
            // Same keyring opt-out as the panel kiosk: a locked KWallet stalls
            // Chromium's first navigation, and the dashboard window keeps no
            // credentials either.
            list.Add(new Launcher(path, new[] { "--password-store=basic" }, true));
        // Fallbacks: desktop-portal openers launch the default browser in the
        // user's session context (avoids the flatpak sandbox EPERM), normal window.
        list.Add(new Launcher("/usr/bin/xdg-open", Array.Empty<string>(), false));
        list.Add(new Launcher("/usr/bin/kde-open", Array.Empty<string>(), false));
        list.Add(new Launcher("/usr/bin/gio", new[] { "open" }, false));
        // Last resort: Firefox (no --app mode) in a normal window.
        list.Add(new Launcher(UserFlatpakBin + "/org.mozilla.firefox", new[] { "--new-window" }, false));
        list.Add(new Launcher(SysFlatpakBin + "/org.mozilla.firefox", new[] { "--new-window" }, false));
        list.Add(new Launcher("/usr/bin/firefox", new[] { "--new-window" }, false));
        return list.ToArray();
    }

    /// <summary>
    /// Opens a URL: Chromium-family --app window first, then desktop-portal
    /// openers, then Firefox as a last resort. Returns whether a launcher was
    /// found and started.
    /// </summary>
    public static bool OpenUrl(string url)
    {
        foreach (var l in BuildLaunchers())
        {
            if (!File.Exists(l.Path))
                continue;
            try
            {
                var args = new List<string>(l.PreArgs);
                if (l.AppMode)
                {
                    // Force Wayland in a Wayland session: Chromium otherwise
                    // defaults to X11/XWayland and fails on a pure-Wayland login
                    // (missing/invalid XAUTHORITY -> "Missing X server or $DISPLAY").
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                        args.Add("--ozone-platform=wayland");
                    args.Add($"--app={url}");
                }
                else
                {
                    args.Add(url);
                }
                // A root daemon must launch the browser as the session user -
                // Chromium refuses to run as root. Pass-through as a --user run.
                var (spawnFile, spawnArgs) = LinuxSession.WrapSpawnAsSessionUser(l.Path, args);
                var psi = new ProcessStartInfo
                {
                    FileName = spawnFile,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in spawnArgs)
                    psi.ArgumentList.Add(a);
                if (Process.Start(psi) is not null)
                {
                    Console.Error.WriteLine($"[browser] opened {url} via {l.Path}{(l.AppMode ? " (app mode)" : "")}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[browser] {l.Path} failed: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[browser] no browser launcher found for {url}");
        return false;
    }
}
