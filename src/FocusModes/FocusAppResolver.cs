using System.Diagnostics;
using System.Text.Json;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;

namespace Nexus.Service.FocusModes;

/// <summary>
/// Resolves a picked app to the process name it actually runs under, for the
/// app trigger. Kept apart from the shortcuts providers because those names are
/// tuned for matching one focused window (display names, launcher stubs), while
/// this trigger compares against every running process and needs the real exe.
/// </summary>
internal static class FocusAppResolver
{
    private const string RunningPrefix = "proc:";

    // Launcher and installer stubs that front a real app (a Squirrel shortcut
    // targets Update.exe). Storing one would never match the app and would fire
    // on any unrelated updater, so such a pick counts as unresolved.
    private static readonly HashSet<string> LauncherNames = new(StringComparer.Ordinal)
    {
        "update", "updater", "squirrel", "setup", "install", "installer", "uninstall", "uninstaller",
    };

    /// <summary>Match key for the app's real process, or "" when it cannot be determined.</summary>
    internal static string Resolve(
        string appId, IShortcutsProvider shortcuts, bool isMac, Func<string, string?> bundleExecutable)
    {
        if (appId.StartsWith(RunningPrefix, StringComparison.Ordinal))
        {
            return Accept(AppPresetMatching.ProcessKey(appId[RunningPrefix.Length..]));
        }

        try
        {
            if (isMac)
            {
                // The provider's name is LSDisplayName, but a process is named by
                // its executable basename (VS Code is "Code" there and runs as
                // "Electron"), so read the bundle's CFBundleExecutable instead.
                var path = shortcuts.GetById(appId)?.Path ?? "";
                if (!path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return "";
                return Accept(AppPresetMatching.ProcessKey(bundleExecutable(path) ?? ""));
            }
            return Accept(AppPresetMatching.ProcessKey(shortcuts.ResolveProcessName(appId)));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Applies the launcher rule to any candidate key, wherever it came from.</summary>
    internal static string Accept(string key) => LauncherNames.Contains(key) ? "" : key;

    internal static string Resolve(string appId, IShortcutsProvider shortcuts)
        => Resolve(appId, shortcuts, OperatingSystem.IsMacOS(), ReadBundleExecutable);

    /// <summary>CFBundleExecutable from an Info.plist rendered as JSON, or null.</summary>
    internal static string? ExecutableFromInfoPlistJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("CFBundleExecutable", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadBundleExecutable(string appPath)
    {
        try
        {
            var plist = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plist)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/plutil",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-convert", "json", "-o", "-", plist }) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return ExecutableFromInfoPlistJson(output);
        }
        catch
        {
            return null;
        }
    }
}
