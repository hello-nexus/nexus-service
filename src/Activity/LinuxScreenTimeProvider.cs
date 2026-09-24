using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Linux.DBus;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux screen-time tracker for KDE Plasma 6 (Wayland + X11). Injects a tiny
/// KWin JS script that listens to <c>workspace.windowActivated</c> and calls
/// back via D-Bus with <c>(uint pid, string name)</c>. Event-driven - zero
/// polling CPU cost.
///
/// Fails soft on non-KDE desktops (script never loads, provider returns empty).
/// IFocusDetailsProvider carries the focused pid's /proc exe target as ExePath
/// (Recent Apps launches it and keys its icon on the basename).
/// </summary>
public sealed class LinuxScreenTimeProvider : IScreenTimeProvider, IFocusDetailsProvider, IHostedService, IDisposable
{
    private const string ServiceName = "org.nexus.ScreenTime";
    private const string ObjectPath = "/ScreenTime";
    private const string InterfaceName = "org.nexus.ScreenTime";
    private const string KWinPluginName = "nexus-focus";
    private const long IdleCapMs = 3 * 60 * 1000;

    private readonly DBusConnection _dbus;
    private readonly IScreenTimeStore _store;
    private readonly IConfigStore _config;
    private readonly object _lock = new();

    public event Action? FocusChanged;

    public event Action<FocusSessionEnded>? SessionEnded;

    private string _currentApp = "";
    private int _currentPid;
    private string? _currentExePath;
    private long _sessionStartUtcMs;
    private long _lastEventUtcMs;
    private int _scriptId = -1;
    // 1 while a start attempt is in flight or has registered on the bus; see
    // LinuxTrayService for why a plain bool cannot gate this.
    private int _starting;

    public LinuxScreenTimeProvider(DBusConnection dbus, IScreenTimeStore store, IConfigStore config)
    {
        _dbus = dbus;
        _store = store;
        _config = config;
        _lastEventUtcMs = NowUtcMs();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A root daemon that booted before login has no session bus yet; the
        // session watcher re-runs this once the user logs in.
        Platform.Linux.LinuxSession.SessionAdopted += OnSessionAdopted;
        await StartOnSessionBusAsync(retry: false);
    }

    private void OnSessionAdopted() => _ = StartOnSessionBusAsync(retry: true);

    private async Task StartOnSessionBusAsync(bool retry = false)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            return;
        try
        {
            await _dbus.StartAsync();

            var scriptPath = EnsureKWinScript();
            if (scriptPath is null)
            {
                // The script dir hangs off the session user's home, so pre-login
                // this is "not yet", not "never" - let a later adopt retry.
                Console.Error.WriteLine("[screentime] KWin script dir unavailable; disabled");
                Volatile.Write(ref _starting, 0);
                return;
            }

            _dbus.RegisterHandler(ObjectPath, HandleCall);
            var rn = await _dbus.RequestNameAsync(ServiceName, flags: 3);
            Console.Error.WriteLine($"[screentime] RequestName {ServiceName} -> {rn} uniq={_dbus.UniqueName}");

            try
            {
                try
                {
                    await _dbus.CallAsync("org.kde.KWin", "/Scripting",
                        "org.kde.kwin.Scripting", "unloadScript", "s",
                        w => w.WriteString(KWinPluginName));
                }
                catch { }

                var reply = await _dbus.CallAsync(
                    "org.kde.KWin", "/Scripting",
                    "org.kde.kwin.Scripting", "loadScript", "ss",
                    w =>
                    {
                        w.WriteString(scriptPath);
                        w.WriteString(KWinPluginName);
                    });
                var rd = new DBusReader(reply.Body);
                _scriptId = rd.ReadInt32();

                try
                {
                    await _dbus.CallAsync("org.kde.KWin", $"/Scripting/Script{_scriptId}",
                        "org.kde.kwin.Script", "run", "", null);
                }
                catch (Exception exRun)
                {
                    Console.Error.WriteLine($"[screentime] Script.run fallback: {exRun.Message}");
                }

                try
                {
                    await _dbus.CallAsync("org.kde.KWin", "/Scripting",
                        "org.kde.kwin.Scripting", "start", "", null);
                }
                catch { }

                Console.Error.WriteLine($"[screentime] KWin focus script loaded (id {_scriptId})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screentime] KWin scripting unavailable: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screentime] startup failed: {ex.Message}");
            Volatile.Write(ref _starting, 0);
            // See LinuxTrayService: the one-shot event can fire while this
            // attempt unwinds, its handler bouncing off our CAS.
            if (!retry && Platform.Linux.LinuxSession.SessionUid is not null)
                await StartOnSessionBusAsync(retry: true);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Platform.Linux.LinuxSession.SessionAdopted -= OnSessionAdopted;
        _dbus.UnregisterHandler(ObjectPath);
        FlushCurrentSession();
        return Task.CompletedTask;
    }

    public void Dispose() => FlushCurrentSession();

    public FocusSession? GetCurrentSession()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp))
            {
                return null;
            }
            var elapsed = TimeSpan.FromMilliseconds(BoundedElapsed());
            return new FocusSession
            {
                Id = _currentPid.ToString(),
                Name = _currentApp,
                Today = ToDuration(elapsed),
            };
        }
    }

    public FocusDetails? GetCurrentFocusDetails()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp))
            {
                return null;
            }
            return new FocusDetails(_currentPid, _currentApp, _sessionStartUtcMs, _currentExePath, 0, 0, null);
        }
    }

    public IReadOnlyList<AppUsage> GetTodayUsage()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var committed = _store.GetTodayUsage(today);
        lock (_lock)
        {
            var result = new List<AppUsage>(committed.Select(a => new AppUsage { Name = a.Name, TotalMs = a.TotalMs }));
            if (string.IsNullOrEmpty(_currentApp))
            {
                return result;
            }
            var elapsed = BoundedElapsed();
            if (elapsed <= 0)
            {
                return result;
            }
            var existing = result.FirstOrDefault(a => a.Name == _currentApp);
            if (existing is not null)
            {
                existing.TotalMs += elapsed;
            }
            else
            {
                result.Add(new AppUsage { Name = _currentApp, TotalMs = elapsed });
            }
            return result.OrderByDescending(a => a.TotalMs).ToList();
        }
    }

    private DBusMessage? HandleCall(DBusMessage msg)
    {
        var iface = msg.Interface ?? "";
        var member = msg.Member ?? "";

        if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
        {
            return _dbus.MakeReply(msg, "s", w => w.WriteString(IntrospectionXml));
        }

        if (iface == InterfaceName && member == "ReportFocus")
        {
            try
            {
                var r = new DBusReader(msg.Body);
                var pid = (int)r.ReadUInt32();
                var name = r.ReadString();
                // The script's load marker is not a focused window.
                if (pid == 0 && name == "__kwin_script_loaded__")
                {
                    Console.Error.WriteLine("[screentime] KWin focus script reported in");
                }
                else
                {
                    OnFocusChanged(pid, name);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screentime] parse ReportFocus: {ex.Message}");
            }
            return _dbus.MakeReply(msg, "", null);
        }

        return _dbus.MakeErrorReply(msg, "org.freedesktop.DBus.Error.UnknownMethod",
            $"{iface}.{member}");
    }

    private void OnFocusChanged(int pid, string name)
    {
        var resolvedName = string.IsNullOrEmpty(name) ? ResolveNameFromPid(pid) : name;
        if (string.IsNullOrEmpty(resolvedName))
        {
            return;
        }

        var changed = false;
        FocusSessionEnded? ended = null;
        lock (_lock)
        {
            var now = NowUtcMs();

            if (!string.IsNullOrEmpty(_currentApp) && (resolvedName != _currentApp || pid != _currentPid))
            {
                var elapsedSinceLastEvent = now - _lastEventUtcMs;
                var endUtc = elapsedSinceLastEvent > IdleCapMs ? _lastEventUtcMs + IdleCapMs : now;
                TryRecord(_currentApp, _sessionStartUtcMs, endUtc);
                ended = new FocusSessionEnded(_currentPid, _currentApp, _sessionStartUtcMs, endUtc);
            }

            if (resolvedName != _currentApp || pid != _currentPid)
            {
                _currentApp = resolvedName;
                _currentPid = pid;
                _currentExePath = ResolveExePathFromPid(pid);
                _sessionStartUtcMs = now;
                changed = true;
            }
            _lastEventUtcMs = now;
        }

        // Outside the lock: a subscriber activating a preset must not run
        // under the focus lock.
        if (ended is not null) SessionEnded?.Invoke(ended);
        if (changed) FocusChanged?.Invoke();
    }

    private long BoundedElapsed()
    {
        var now = NowUtcMs();
        var raw = now - _sessionStartUtcMs;
        var sinceEvent = now - _lastEventUtcMs;
        if (sinceEvent > IdleCapMs)
        {
            raw -= sinceEvent - IdleCapMs;
        }
        return raw < 0 ? 0 : raw;
    }

    private void FlushCurrentSession()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentApp))
            {
                return;
            }
            var now = NowUtcMs();
            var sinceEvent = now - _lastEventUtcMs;
            var endUtc = sinceEvent > IdleCapMs ? _lastEventUtcMs + IdleCapMs : now;
            TryRecord(_currentApp, _sessionStartUtcMs, endUtc);
            _currentApp = "";
            _currentPid = 0;
            _currentExePath = null;
        }
    }

    private void TryRecord(string app, long startUtc, long endUtc)
    {
        if (!IsTrackingEnabled())
        {
            return;
        }
        _store.RecordSession(app, null, startUtc, endUtc);
    }

    private bool IsTrackingEnabled()
    {
        try
        { return _config.Load().ScreenTime?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    private static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? ResolveExePathFromPid(int pid)
    {
        if (pid <= 0)
            return null;
        try
        {
            var target = File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: true)?.FullName;
            return string.IsNullOrEmpty(target) || target.EndsWith(" (deleted)", StringComparison.Ordinal) ? null : target;
        }
        catch { return null; }
    }

    private static string ResolveNameFromPid(int pid)
    {
        if (pid <= 0)
            return "";
        try
        {
            var path = $"/proc/{pid}/comm";
            if (!File.Exists(path))
                return "";
            return File.ReadAllText(path).Trim();
        }
        catch { return ""; }
    }

    private static string? EnsureKWinScript()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
            {
                return null;
            }
            var baseDir = Path.Combine(home, ".local", "share", "kwin", "scripts", KWinPluginName);
            var codeDir = Path.Combine(baseDir, "contents", "code");
            Directory.CreateDirectory(codeDir);

            AtomicJsonFile.Write(Path.Combine(baseDir, "metadata.json"), MetadataJson);
            AtomicJsonFile.Write(Path.Combine(codeDir, "main.js"), KWinScript);

            return Path.Combine(codeDir, "main.js");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screentime] script write failed: {ex.Message}");
            return null;
        }
    }

    private static Duration ToDuration(TimeSpan ts) => new()
    {
        Total = (long)ts.TotalMilliseconds,
        Milliseconds = ts.Milliseconds,
        Seconds = ts.Seconds,
        Minutes = ts.Minutes,
        Hours = ts.Hours,
        Days = ts.Days,
    };

    private const string MetadataJson = """
{
  "KPlugin": {
    "Authors": [{"Name": "Nexus"}],
    "Category": "Window Management",
    "Description": "Reports focused window changes to the Nexus service via D-Bus",
    "Id": "nexus-focus",
    "Name": "Nexus Focus Tracker",
    "ServiceTypes": ["KWin/Script"],
    "Version": "1.0",
    "EnabledByDefault": true
  },
  "X-Plasma-API": "declarativescript"
}
""";

    private const string KWinScript = """
function sendFocus(win) {
    if (!win) return;
    var pid = (typeof win.pid === 'number') ? win.pid : 0;
    var name = win.resourceName || win.caption || win.windowClass || "";
    try {
        callDBus("org.nexus.ScreenTime", "/ScreenTime",
                 "org.nexus.ScreenTime", "ReportFocus",
                 pid, name);
    } catch (e) {
    }
}

try {
    callDBus("org.nexus.ScreenTime", "/ScreenTime",
             "org.nexus.ScreenTime", "ReportFocus",
             0, "__kwin_script_loaded__");
} catch (e) { }

if (typeof workspace !== 'undefined') {
    var active = workspace.activeWindow || workspace.activeClient;
    if (active) sendFocus(active);

    if (workspace.windowActivated && workspace.windowActivated.connect) {
        workspace.windowActivated.connect(sendFocus);
    } else if (workspace.clientActivated && workspace.clientActivated.connect) {
        workspace.clientActivated.connect(sendFocus);
    }
}
""";

    private const string IntrospectionXml = """
<!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
 "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
<node>
  <interface name="org.nexus.ScreenTime">
    <method name="ReportFocus">
      <arg type="u" direction="in" name="pid"/>
      <arg type="s" direction="in" name="name"/>
    </method>
  </interface>
</node>
""";
}
