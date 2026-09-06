using System;
using System.IO;
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
/// every start. Non-KDE compositors: the calls fail and are logged once.
/// </summary>
internal static class LinuxKWinPanelPlacement
{
    private const string PluginName = "nexus-panel-y70";
    private static bool _attempted;

    public static Task EnsureLoadedAsync(DBusConnection dbus)
    {
        if (_attempted) return Task.CompletedTask;
        _attempted = true;
        return LoadAsync(dbus);
    }

    private static async Task LoadAsync(DBusConnection dbus)
    {
        var scriptPath = WriteScript();
        if (scriptPath is null) return;
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
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script not loaded: {ex.Message}");
        }
    }

    private static string? WriteScript()
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
            AtomicJsonFile.Write(main, Script);
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
    "Description": "Pins the Nexus panel kiosk window to the portrait Y70 strip and keeps it fullscreen.",
    "Version": "1.0",
    "Authors": [{ "Name": "Nexus" }]
  },
  "X-Plasma-API": "javascript",
  "X-Plasma-MainScript": "code/main.js"
}
""";

    // Chromium under Ozone/Wayland ignores --class; its app_id is derived from
    // the --app URL and profile, so the kiosk is matched by that class. The Y70
    // is the portrait strip: the only output that is taller than wide by 3x
    // (a 32:9 desktop monitor is that wide, never that tall).
    private const string Script = """
const PANEL_CLASS = "chrome-localhost__panel-Default";
const MIN_ASPECT = 3;

function targetOutput() {
  for (let i = 0; i < workspace.screens.length; i++) {
    const s = workspace.screens[i];
    if (s.geometry.height > s.geometry.width && s.geometry.height / s.geometry.width >= MIN_ASPECT) return s;
  }
  return null;
}

function place(w) {
  if (!w || w.resourceClass !== PANEL_CLASS) return;
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
