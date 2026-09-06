using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Hosts promoted-monitor panel kiosks on Linux: one Chromium-family
/// <c>--app --kiosk</c> window per active display assignment, spawned into
/// the user's graphical session (the setpriv root-daemon pattern shared
/// with the dashboard launcher and screencast helper). Counterpart of
/// nexus-overlay's MonitorKioskManager (Windows) and the overlay-helper
/// KioskController (macOS).
///
/// Also hosts the Y70's own kiosk (via <see cref="LinuxOverlayHost"/>) the way
/// the Windows overlay does: opened from hardware detection, not a display
/// assignment, and loading <c>/panel</c> with no device id so the kiosk
/// self-registers a <c>y70</c> record from its own viewport and re-reports it on
/// resize. That keeps compositor rotation and scale authoritative - DRM sysfs
/// only knows the panel's native scanout mode.
///
/// Placement caveat: Wayland gives clients no protocol to target a specific
/// output, so the kiosk fullscreens on the compositor-chosen monitor -
/// exact on single-monitor rigs, best-effort on multi-monitor. The DRM
/// topology also carries no positions, so there is nothing to translate a
/// displayId into screen coordinates with from the daemon side.
///
/// Each kiosk gets its own --user-data-dir (see ProfileDir) so the spawned
/// pid IS the browser process (no delegation to a running instance) and
/// Kill(tree) closes exactly that window.
/// </summary>
public sealed class LinuxPanelKioskHost : IDisposable
{
    private static int _servicePort = 9400;
    public static void Configure(int servicePort) => _servicePort = servicePort;

    private const int RapidFailureWindowSeconds = 30;
    private const int MaxRapidFailures = 3;

    /// <summary>Reserved running-map key for the Y70 kiosk; never a real displayId.</summary>
    internal const string Y70Slot = "y70";

    private readonly TokenService _tokens;
    private readonly PanelDeviceRegistry _registry;
    private readonly IConfigStore _store;
    private readonly DisplayTopologyService _topology;
    private readonly DBus.DBusConnection _dbus;
    private readonly object _lock = new();
    private readonly Dictionary<string, Kiosk> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rapidFailures = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _noBrowserLogged;

    private sealed class Kiosk
    {
        public Kiosk(Process process, string deviceId, DateTime spawnedUtc, string? flatpakApp)
        {
            Process = process;
            FlatpakApp = flatpakApp;
            DeviceId = deviceId;
            SpawnedUtc = spawnedUtc;
        }

        public Process Process { get; }
        /// <summary>Flatpak application id when the browser is a flatpak export, else null.</summary>
        public string? FlatpakApp { get; }
        public string DeviceId { get; }
        public DateTime SpawnedUtc { get; }
    }

    public LinuxPanelKioskHost(TokenService tokens, PanelDeviceRegistry registry, IConfigStore store, DisplayTopologyService topology, DBus.DBusConnection dbus)
    {
        _tokens = tokens;
        _registry = registry;
        _store = store;
        _topology = topology;
        _dbus = dbus;
    }

    /// <summary>Same predicate as the Windows overlay's PanelDisplay auto-launch.</summary>
    private bool Y70Desired() => _store.Load().Panel.AutoLaunch && _topology.HasY70Display();

    // Read from the settings-store change handler on route threads: must not
    // wait behind a CloseKiosk that is killing a flatpak process tree under _lock.
    private volatile bool _y70Running;
    private bool _y70WasDesired;

    public bool Y70Running => _y70Running;

    /// <summary>Close the Y70 kiosk regardless of desire (AutoLaunch turned off).</summary>
    public void CloseY70()
    {
        lock (_lock)
            CloseKiosk(Y70Slot);
    }

    /// <summary>
    /// Re-reads the active assignment set from the registry under the host
    /// lock - racing callers each see fresh state, so the last reconcile to
    /// run reflects the newest store snapshot (no stale-args ordering race).
    /// </summary>
    public void Reconcile()
    {
        if (!OperatingSystem.IsLinux())
            return;
        lock (_lock)
        {
            if (_disposed)
                return;
            var assignments = _registry.ListAssignments()
                .Select(a => (a.DisplayId, a.PanelDeviceId))
                .ToList();
            var y70 = Y70Desired();
            if (y70 && !_y70WasDesired)
                _rapidFailures.Remove(Y70Slot);
            _y70WasDesired = y70;
            if (y70)
                assignments.Add((Y70Slot, Y70Slot));

            // Drop exited entries so the open pass below respawns them.
            foreach (var key in _running.Keys.ToList())
            {
                if (_running[key].Process.HasExited)
                {
                    try { _running[key].Process.Dispose(); } catch { }
                    _running.Remove(key);
                }
            }

            var runningMap = _running.ToDictionary(kv => kv.Key, kv => kv.Value.DeviceId, StringComparer.Ordinal);
            var (toClose, toOpen) = Diff(runningMap, assignments);
            // A demoted or re-bound display starts from a clean slate: the
            // rapid-failure cap only spans one continuous assignment.
            foreach (var displayId in toClose)
                _rapidFailures.Remove(displayId);
            foreach (var capped in _rapidFailures.Keys.ToList())
            {
                if (!assignments.Any(a => string.Equals(a.DisplayId, capped, StringComparison.Ordinal)))
                    _rapidFailures.Remove(capped);
            }
            foreach (var displayId in toClose)
                CloseKiosk(displayId);
            foreach (var (displayId, deviceId) in toOpen)
                SpawnKiosk(displayId, deviceId);
        }
    }

    /// <summary>
    /// The Y70 slot loads <c>/panel</c> with no device id: the SPA allocates (or
    /// re-touches, via the profile's localStorage) a self-registered y70 record.
    /// Promoted monitors load their bound record directly.
    /// </summary>
    internal static string KioskUrl(int port, string deviceId, string token)
    {
        var t = Uri.EscapeDataString(token);
        return string.Equals(deviceId, Y70Slot, StringComparison.Ordinal)
            ? $"http://localhost:{port}/panel?token={t}"
            : $"http://localhost:{port}/panel/{Uri.EscapeDataString(deviceId)}?token={t}";
    }

    /// <summary>Pure reconcile diff: close what is no longer desired (or
    /// changed device), open what is desired but not running.</summary>
    internal static (List<string> ToClose, List<(string DisplayId, string DeviceId)> ToOpen) Diff(
        IReadOnlyDictionary<string, string> running,
        IReadOnlyList<(string DisplayId, string DeviceId)> desired)
    {
        var desiredById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (displayId, deviceId) in desired)
            desiredById[displayId] = deviceId;

        var toClose = new List<string>();
        foreach (var (displayId, deviceId) in running)
        {
            if (!desiredById.TryGetValue(displayId, out var want) || !string.Equals(want, deviceId, StringComparison.Ordinal))
                toClose.Add(displayId);
        }

        var toOpen = new List<(string, string)>();
        foreach (var (displayId, deviceId) in desiredById)
        {
            if (!running.TryGetValue(displayId, out var have) || !string.Equals(have, deviceId, StringComparison.Ordinal))
                toOpen.Add((displayId, deviceId));
        }
        return (toClose, toOpen);
    }

    private void CloseKiosk(string displayId)
    {
        if (!_running.Remove(displayId, out var kiosk))
            return;
        if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
        {
            _y70Running = false;
            _ = LinuxScreenInhibit.ReleaseAsync(_dbus);
        }
        try
        {
            if (!kiosk.Process.HasExited)
                TerminateBrowser(kiosk);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] close failed display={displayId}: {ex.Message}");
        }
        finally
        {
            try { kiosk.Process.Dispose(); } catch { }
        }
        Console.Error.WriteLine($"[panel-kiosk] closed display={displayId}");
    }

    /// <summary>
    /// A flatpak browser runs in its own pid namespace: the tracked pid is the
    /// bwrap launcher, and Process.Kill(entireProcessTree) can neither see nor
    /// reap the sandboxed browser behind it. That leaves a live profile lock
    /// (every respawn then exits at once) and blocks service stop until systemd
    /// aborts it. <c>flatpak kill</c> on the instance owning our pid is the
    /// sanctioned teardown; the pid-tree kill stays for a package browser.
    /// </summary>
    private static void TerminateBrowser(Kiosk kiosk)
    {
        if (kiosk.FlatpakApp is not null)
        {
            var instance = FlatpakInstanceForPid(kiosk.Process.Id);
            if (instance is not null)
            {
                RunAsSessionUser("flatpak", "kill", instance);
                if (kiosk.Process.WaitForExit(TerminateGraceMs))
                    return;
            }
        }
        kiosk.Process.Kill(entireProcessTree: true);
        kiosk.Process.WaitForExit(TerminateGraceMs);
    }

    private const int TerminateGraceMs = 3000;

    private static string? FlatpakInstanceForPid(int pid)
    {
        var table = RunAsSessionUser("flatpak", "ps", "--columns=instance,pid");
        foreach (var line in table.Split('\n'))
        {
            var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 2 && int.TryParse(cols[1].Trim(), out var p) && p == pid)
                return cols[0].Trim();
        }
        return null;
    }

    private static string RunAsSessionUser(string file, params string[] args)
    {
        try
        {
            var (f, wrapped) = LinuxSession.WrapSpawnAsSessionUser(file, new List<string>(args));
            var psi = new ProcessStartInfo
            {
                FileName = f,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in wrapped) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(TerminateGraceMs))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            return stdout;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] {file} {string.Join(' ', args)} failed: {ex.Message}");
            return "";
        }
    }

    private void SpawnKiosk(string displayId, string deviceId)
    {
        if (_rapidFailures.GetValueOrDefault(displayId) > MaxRapidFailures)
        {
            Console.Error.WriteLine($"[panel-kiosk] display={displayId} is capped after rapid failures; not spawning");
            return;
        }

        var browser = LinuxBrowsers.FindChromium();
        if (browser is null)
        {
            if (!_noBrowserLogged)
            {
                _noBrowserLogged = true;
                Console.Error.WriteLine("[panel-kiosk] no Chromium-family browser found; monitor panels need chromium/chrome/brave/edge installed");
            }
            return;
        }

        // Token rides in argv like the macOS helper's --token; /proc/*/cmdline
        // exposure on multi-user boxes is a known follow-up (short-TTL
        // bootstrap token), not solved here.
        // KDE places the window on the primary screen; the script moves it to
        // the Y70. Loaded before the spawn so windowAdded catches this window.
        if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
            _ = LinuxKWinPanelPlacement.EnsureLoadedAsync(_dbus);

        var url = KioskUrl(_servicePort, deviceId, _tokens.Token);
        var args = new List<string>();
        // Same Wayland forcing as the dashboard launcher: Chromium otherwise
        // tries X11 and dies on a pure-Wayland login.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            args.Add("--ozone-platform=wayland");
        args.Add($"--app={url}");
        // Client-requested fullscreen: KWin keeps it across the placement
        // script's move, while a compositor-set fullscreen on a normal Chromium
        // window is not honoured.
        args.Add("--kiosk");
        // Chromium initialises OS crypt from the desktop keyring before the
        // first navigation; on KDE a locked KWallet (autologin, or a session
        // unlocked without PAM) leaves it waiting on a wallet prompt and every
        // document load aborts, so the kiosk sits on Chromium's blank grey. A
        // kiosk stores no credentials: opt out of the keyring entirely.
        args.Add("--password-store=basic");
        args.Add($"--user-data-dir={ProfileDir(deviceId)}");
        args.Add("--no-first-run");
        args.Add("--noerrdialogs");
        args.Add("--disable-session-crashed-bubble");

        var (file, wrapped) = LinuxSession.WrapSpawnAsSessionUser(browser, args);
        // No stream redirection: Chromium logs to stderr for its whole
        // lifetime, and an undrained 64KB pipe would eventually block its
        // write() and freeze the kiosk. Inherit the service's stdio instead.
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in wrapped)
            psi.ArgumentList.Add(a);

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine($"[panel-kiosk] spawn returned null display={displayId}");
                return;
            }
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnKioskExited(displayId);
            _running[displayId] = new Kiosk(proc, deviceId, DateTime.UtcNow, LinuxBrowsers.FlatpakAppId(browser));
            if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
            {
                _y70Running = true;
                _ = LinuxScreenInhibit.AcquireAsync(_dbus);
            }
            Console.Error.WriteLine($"[panel-kiosk] opened display={displayId} device={deviceId} via {browser} pid {proc.Id}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] spawn failed display={displayId}: {ex.Message}");
        }
    }

    private void OnKioskExited(string displayId)
    {
        bool respawn;
        lock (_lock)
        {
            if (_disposed)
                return;
            if (!_running.TryGetValue(displayId, out var kiosk) || !kiosk.Process.HasExited)
                return;
            _running.Remove(displayId);
            if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
            {
                _y70Running = false;
                _ = LinuxScreenInhibit.ReleaseAsync(_dbus);
            }
            try { kiosk.Process.Dispose(); } catch { }

            if (DateTime.UtcNow - kiosk.SpawnedUtc < TimeSpan.FromSeconds(RapidFailureWindowSeconds))
            {
                var failures = _rapidFailures.GetValueOrDefault(displayId) + 1;
                _rapidFailures[displayId] = failures;
                if (failures > MaxRapidFailures)
                {
                    Console.Error.WriteLine($"[panel-kiosk] giving up on display={displayId} after {failures} rapid failures (turn the panel off and on to retry)");
                    return;
                }
            }
            else
            {
                _rapidFailures.Remove(displayId);
            }
            respawn = string.Equals(displayId, Y70Slot, StringComparison.Ordinal)
                ? Y70Desired()
                : _registry.ListAssignments()
                    .Any(a => string.Equals(a.DisplayId, displayId, StringComparison.Ordinal));
        }
        if (!respawn)
        {
            Console.Error.WriteLine($"[panel-kiosk] display={displayId} exited; no longer desired, not respawning");
            return;
        }
        Console.Error.WriteLine($"[panel-kiosk] display={displayId} exited; respawning");
        // 2s crash backoff, same as the overlay host launchers.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000);
            Reconcile();
        });
    }

    // Snap confinement grants only non-hidden $HOME paths, and exits on "Failed
    // To Create Data Directory" elsewhere; a confined browser is unidentifiable
    // by path, since Ubuntu's /usr/bin/chromium-browser execs /snap/bin/chromium.
    // HOME is read per call: the root daemon adopts the session user's after
    // start, so a cached value would point at /root. Not pre-created - the
    // browser makes it as the session user, and a root-owned dir is unwritable.
    internal static string ProfileDir(string deviceId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            // GetFolderPath yields "" for an unresolvable home, which would make
            // --user-data-dir relative to the browser's CWD.
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            home = string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir;
        }
        return Path.Combine(home, "nexus-kiosk", deviceId);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var displayId in _running.Keys.ToList())
                CloseKiosk(displayId);
        }
    }
}
