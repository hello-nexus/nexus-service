using System;
using System.IO;
using System.Linq;
using System.Threading;
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

    private const string KWinName = "org.kde.KWin";
    private const string BusName = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const int WatchRearmMs = 600_000;

    // A bound for KWin exporting /Scripting after it claims its name, not a measured latency.
    private const int ReloadAttempts = 5;
    private const int ReloadBackoffMs = 250;

    private static readonly SemaphoreSlim LoadLock = new(1, 1);
    private static readonly BusChangeGate BusChanges = new();
    private static readonly object SetupGate = new();
    private static string? _wantedConnector;
    private static string? _loadedConnector;
    private static bool _loaded;
    private static bool _started;

    /// <summary>Reloads the script whenever the Y70's connector changes, and after a failed load; "" leaves only the portrait fallback.</summary>
    public static Task EnsureLoadedAsync(DBusConnection dbus, string connector)
    {
        _wantedConnector = connector;
        Start(dbus);
        // A watcher parked on a bus that never came up gets another go at each kiosk spawn.
        BusChanges.Signal();
        return LoadAsync(dbus, force: false);
    }

    internal static bool NeedsLoad(bool loaded, string? loadedConnector, string wanted, bool force)
        => force || !loaded || loadedConnector != wanted;

    /// <summary>True when a NameOwnerChanged body says org.kde.KWin gained an owner (first start or restart).</summary>
    internal static bool KWinAppeared(byte[] body)
    {
        try
        {
            var r = new DBusReader(body);
            var name = r.ReadString();
            r.ReadString();
            var newOwner = r.ReadString();
            return name == KWinName && newOwner.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Start(DBusConnection dbus)
    {
        lock (SetupGate)
        {
            if (_started) return;
            _started = true;
        }
        dbus.Reconnected += () => OnBusChanged(dbus);
        LinuxSession.SessionAdopted += () => OnBusChanged(dbus);
        _ = Task.Run(() => WatchAsync(dbus));
    }

    // A new connection or session means a new KWin that has none of our scripts.
    private static void OnBusChanged(DBusConnection dbus)
    {
        BusChanges.Signal();
        _ = Task.Run(async () =>
        {
            await LoadLock.WaitAsync();
            try { _loaded = false; }
            finally { LoadLock.Release(); }
            await LoadAsync(dbus, force: false);
        });
    }

    // The one task that touches the NameOwnerChanged waiter. It survives bus loss: match rules die with the connection, so it re-arms after each reconnect.
    private static async Task WatchAsync(DBusConnection dbus)
    {
        // Kept across iterations: re-arming would orphan a live waiter, whose timeout then removes the new one's key.
        Task<DBusMessage>? next = null;
        while (true)
        {
            var seen = BusChanges.Version;
            try
            {
                await dbus.StartAsync();
                await dbus.AddMatchAsync(
                    $"type='signal',sender='{BusName}',interface='{BusName}',member='NameOwnerChanged',arg0='{KWinName}'");
                // Armed before the owner check so an appearance in between is not missed.
                next ??= dbus.WaitForSignalAsync(BusPath, "NameOwnerChanged", WatchRearmMs);
                // A bus change since the match was added may have left it on a dead connection.
                if (BusChanges.Version != seen) continue;
                if (await HasOwnerAsync(dbus))
                    await LoadWithRetryAsync(dbus, force: false);
                while (true)
                {
                    DBusMessage? signal = null;
                    try { signal = await next; }
                    catch (TimeoutException) { }
                    next = dbus.WaitForSignalAsync(BusPath, "NameOwnerChanged", WatchRearmMs);
                    if (signal is not null && KWinAppeared(signal.Body))
                        await LoadWithRetryAsync(dbus, force: true);
                    if (BusChanges.Version != seen) break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[panel-kiosk] KWin watch interrupted: {ex.Message}");
                next = null;
                await BusChanges.WaitAfterAsync(seen);
            }
        }
    }

    private static async Task<bool> HasOwnerAsync(DBusConnection dbus)
    {
        try
        {
            await dbus.CallAsync(BusName, BusPath, BusName, "GetNameOwner", "s", w => w.WriteString(KWinName));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task LoadWithRetryAsync(DBusConnection dbus, bool force)
    {
        for (var attempt = 1; attempt <= ReloadAttempts; attempt++)
        {
            if (await LoadAsync(dbus, force)) return;
            if (attempt < ReloadAttempts) await Task.Delay(ReloadBackoffMs * attempt);
        }
    }

    private static async Task<bool> LoadAsync(DBusConnection dbus, bool force)
    {
        await LoadLock.WaitAsync();
        try
        {
            var connector = _wantedConnector;
            if (connector is null) return true;
            if (!NeedsLoad(_loaded, _loadedConnector, connector, force)) return true;
            _loaded = await LoadCoreAsync(dbus, connector);
            _loadedConnector = _loaded ? connector : null;
            return _loaded;
        }
        finally
        {
            LoadLock.Release();
        }
    }

    private static async Task<bool> LoadCoreAsync(DBusConnection dbus, string connector)
    {
        var scriptPath = WriteScript(connector);
        if (scriptPath is null) return false;
        try
        {
            try { await dbus.StartAsync(); } catch { }
            try
            {
                await dbus.CallAsync(KWinName, "/Scripting", "org.kde.kwin.Scripting", "unloadScript", "s",
                    w => w.WriteString(PluginName));
            }
            catch { }
            var reply = await dbus.CallAsync(KWinName, "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
                w =>
                {
                    w.WriteString(scriptPath);
                    w.WriteString(PluginName);
                });
            var id = new DBusReader(reply.Body).ReadInt32();
            await dbus.CallAsync(KWinName, $"/Scripting/Script{id}", "org.kde.kwin.Script", "run", "", null);
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script loaded (id {id})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] KWin placement script not loaded: {ex.Message}");
            return false;
        }
    }

    /// <summary>Counts bus changes so a waiter that missed one between reading the count and waiting still wakes.</summary>
    internal sealed class BusChangeGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _version;

        public int Version
        {
            get { lock (_lock) return _version; }
        }

        public void Signal()
        {
            TaskCompletionSource old;
            lock (_lock)
            {
                _version++;
                old = _next;
                _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            old.TrySetResult();
        }

        public Task WaitAfterAsync(int seen)
        {
            lock (_lock) return _version != seen ? Task.CompletedTask : _next.Task;
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
