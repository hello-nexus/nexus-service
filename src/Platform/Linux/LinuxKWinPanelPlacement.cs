using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Loads a KWin script that moves the Nexus panel kiosk onto the Y70 and keeps
/// it fullscreen. Wayland gives clients no way to choose an output and KWin
/// places a new fullscreen window on the primary screen, so without this the
/// kiosk lands on the desktop monitor and self-registers with that monitor's
/// viewport. Same install path as LinuxScreenTimeProvider's nexus-focus script:
/// written under the session user's kwin scripts dir and loaded over D-Bus on
/// every start. Non-KDE compositors: the calls fail and are logged.
/// </summary>
internal static class LinuxKWinPanelPlacement
{
    private const string PluginName = "nexus-panel-y70";

    /// <summary>X11 WM_CLASS and Wayland app_id the Firefox Y70 kiosk is launched with.</summary>
    internal const string FirefoxClass = "nexus-panel-y70";

    private static string? _loadedFor;

    /// <summary>Reloads the script whenever the Y70's connector changes, and after a failed load; "" leaves only the portrait fallback.</summary>
    public static Task EnsureLoadedAsync(DBusConnection dbus, string connector)
    {
        if (_loadedFor == connector) return Task.CompletedTask;
        _loadedFor = connector;
        return LoadAsync(dbus, connector);
    }

    private static async Task LoadAsync(DBusConnection dbus, string connector)
    {
        var scriptPath = WriteScript(connector);
        if (scriptPath is null)
        {
            _loadedFor = null;
            return;
        }
        try
        {
            try { await dbus.StartAsync(); } catch { }
            try
            {
                await dbus.CallAsync("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s",
                    w => w.WriteString(PluginName));
            }
            catch { }
            var reply = await dbus.CallAsync("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
                w =>
                {
                    w.WriteString(scriptPath);
                    w.WriteString(PluginName);
                });
            var id = new DBusReader(reply.Body).ReadInt32();
            await dbus.CallAsync("org.kde.KWin", $"/Scripting/Script{id}", "org.kde.kwin.Script", "run", "", null);
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script loaded (id {id})");
        }
        catch (Exception ex)
        {
            _loadedFor = null;
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script not loaded: {ex.Message}");
        }
    }

    private static string? WriteScript(string connector)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) return null;
            var baseDir = Path.Combine(home, ".local", "share", "kwin", "scripts", PluginName);
            var codeDir = Path.Combine(baseDir, "contents", "code");
            Directory.CreateDirectory(codeDir);
            AtomicJsonFile.Write(Path.Combine(baseDir, "metadata.json"), MetadataJson);
            var main = Path.Combine(codeDir, "main.js");
            AtomicJsonFile.Write(main, Script(connector));
            // Written by the root daemon into the user's home: hand it to the
            // user so their own KDE tooling can manage the package.
            if (LinuxSession.SessionUid is { } uid && LinuxSession.SessionGid is { } gid)
            {
                foreach (var path in new[] { baseDir, Path.Combine(baseDir, "contents"), codeDir, Path.Combine(baseDir, "metadata.json"), main })
                    LinuxSysfs.TryChown(path, uid, gid);
            }
            return main;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script write failed: {ex.Message}");
            return null;
        }
    }

    private const string MetadataJson = """
{
  "KPackageStructure": "KWin/Script",
  "KPlugin": {
    "Id": "nexus-panel-y70",
    "Name": "Nexus panel on the Y70",
    "Description": "Pins the Nexus panel kiosk window to the Y70 and keeps it fullscreen.",
    "Version": "1.0",
    "Authors": [{ "Name": "Nexus" }]
  },
  "X-Plasma-API": "javascript",
  "X-Plasma-MainScript": "code/main.js"
}
""";

    internal static string Script(string connector)
    {
        var safe = new string(connector.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return ScriptTemplate
            .Replace("__TARGET_OUTPUT__", safe, StringComparison.Ordinal)
            .Replace("__FIREFOX_CLASS__", FirefoxClass, StringComparison.Ordinal);
    }

    // Chromium ignores --class on Wayland and prefixes its app_id with the
    // executable name (chrome-, brave-, msedge-), so only the suffix is stable.
    private const string ScriptTemplate = """
const TARGET_OUTPUT = "__TARGET_OUTPUT__";
const CHROMIUM_SUFFIX = "-localhost__panel-Default";
const FIREFOX_CLASS = "__FIREFOX_CLASS__";
const MIN_ASPECT = 3;

function isPanel(w) {
  const c = String(w.resourceClass || "");
  return c === FIREFOX_CLASS || c.endsWith(CHROMIUM_SUFFIX);
}

function targetOutput() {
  const screens = workspace.screens;
  for (let i = 0; i < screens.length; i++) {
    if (TARGET_OUTPUT && screens[i].name === TARGET_OUTPUT) return screens[i];
  }
  for (let i = 0; i < screens.length; i++) {
    const s = screens[i];
    if (s.geometry.height > s.geometry.width && s.geometry.height / s.geometry.width >= MIN_ASPECT) return s;
  }
  return null;
}

function place(w) {
  if (!w || !isPanel(w)) return;
  const target = targetOutput();
  if (!target) return;
  if (!w.output || w.output.name !== target.name) workspace.sendClientToScreen(w, target);
  w.fullScreen = true;
}

// Chromium maps the surface first and sets the app_id afterwards, so the
// class seen at windowAdded can still be the launcher's. Re-check on every
// signal that follows the app_id: class, caption (set once the page loads)
// and geometry (the first configure after map).
function track(w) {
  if (!w) return;
  place(w);
  const again = function () { place(w); };
  for (const name of ["windowClassChanged", "captionChanged", "frameGeometryChanged"]) {
    const sig = w[name];
    if (sig && typeof sig.connect === "function") sig.connect(again);
  }
}

workspace.windowList().forEach(track);
workspace.windowAdded.connect(track);
""";
}
