using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Nexus.Service.Conflicts;

namespace Nexus.Service.Diagnostics;

/// <summary>Install state of a fixed allowlist of vendor RGB apps; never enumerates installed software.</summary>
public static partial class VendorSoftwareProbe
{
    /// <param name="Id">ConflictAppCatalog id, which also supplies the process names the crash filter allows.</param>
    /// <param name="Uninstall">Case-insensitive substring of the Windows Uninstall DisplayName.</param>
    /// <param name="MacApp">Bundle name under /Applications, null when the vendor has no macOS app.</param>
    private sealed record Entry(string Id, string Uninstall, string? MacApp);

    private static readonly Entry[] Entries =
    {
        new("hyte-nexus-2", "HYTE Nexus", "HYTE Nexus"),
        new("icue", "iCUE", "iCUE"),
        new("armoury-crate", "Armoury Crate", null),
        new("aura-sync", "Aura Sync", null),
        new("lian-li-l-connect", "L-Connect", null),
        new("signalrgb", "SignalRgb", "SignalRgb"),
        new("msi-mystic-light", "Mystic Light", null),
        new("msi-center", "MSI Center", null),
        new("nzxt-cam", "NZXT CAM", null),
        new("tryx-kanali", "Kanali", null),
        new("gigabyte-rgb-fusion", "RGB Fusion", null),
        new("gigabyte-aorus", "GIGABYTE Control Center", null),
        new("openrgb", "OpenRGB", "OpenRGB"),
        new("razer-synapse", "Razer Synapse", "Razer Synapse"),
        new("logitech-ghub", "G HUB", "lghub"),
    };

    /// <summary>Process names of the allowlisted apps, for the app-crash filter.</summary>
    public static IReadOnlyList<string> ProcessNames() =>
        Entries.SelectMany(e => ConflictWatcher.FindById(e.Id)?.ProcessNames ?? Array.Empty<string>()).ToList();

    /// <summary>Id to a one-line state: "installed 5.2.1, running", "installed", "not installed" or "not applicable".</summary>
    public static Dictionary<string, string> Collect(IReadOnlySet<string> runningIds, Func<string, bool> serviceInstalled)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<(string Name, string Version)> uninstall = OperatingSystem.IsWindows() ? ReadUninstall() : Array.Empty<(string, string)>();
        foreach (var e in Entries)
        {
            string? version = null;
            bool installed;
            if (OperatingSystem.IsWindows())
            {
                var hit = uninstall.FirstOrDefault(u => u.Name.Contains(e.Uninstall, StringComparison.OrdinalIgnoreCase));
                installed = hit.Name is not null || serviceInstalled(e.Id);
                version = hit.Version;
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (e.MacApp is null)
                {
                    result[e.Id] = "not applicable";
                    continue;
                }
                var plist = Path.Combine("/Applications", e.MacApp + ".app", "Contents", "Info.plist");
                installed = File.Exists(plist);
                version = installed ? ReadPlistVersion(plist) : null;
            }
            else
            {
                if (e.Id != "openrgb")
                {
                    result[e.Id] = "not applicable";
                    continue;
                }
                installed = OnPath("openrgb") || FlatpakExportExists("org.openrgb.OpenRGB");
            }
            result[e.Id] = !installed ? "not installed"
                : "installed" + (string.IsNullOrEmpty(version) ? "" : " " + version) + (runningIds.Contains(e.Id) ? ", running" : "");
        }
        return result;
    }

    /// <summary>CFBundleShortVersionString of an XML Info.plist; null for a binary plist or a missing key.</summary>
    internal static string? ReadPlistVersion(string plistPath)
    {
        try
        {
            var m = PlistVersion().Match(File.ReadAllText(plistPath));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>")]
    private static partial Regex PlistVersion();

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, exe)));

    private static bool FlatpakExportExists(string appId) =>
        File.Exists(Path.Combine("/var/lib/flatpak/exports/bin", appId))
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/flatpak/exports/bin", appId));

    [SupportedOSPlatform("windows")]
    private static List<(string Name, string Version)> ReadUninstall()
    {
        var found = new List<(string, string)>();
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            try
            {
                using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (root is null)
                {
                    continue;
                }
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(sub);
                    if (key?.GetValue("DisplayName") is string name)
                    {
                        found.Add((name, key.GetValue("DisplayVersion") as string ?? ""));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return found;
    }
}
