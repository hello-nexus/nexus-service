using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Auth;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Hosts promoted-monitor panel kiosks on Linux: one Chromium-family
/// <c>--app --kiosk</c> window (Firefox <c>--kiosk</c> when no Chromium is
/// installed) per active display assignment, spawned into
/// the user's graphical session (the setpriv root-daemon pattern shared
/// with the dashboard launcher and screencast helper). Counterpart of
/// nexus-overlay's MonitorKioskManager (Windows) and the overlay-helper
/// KioskController (macOS).
///
/// Also hosts the Y70's own kiosk (via <see cref="LinuxOverlayHost"/>) the way
/// the Windows overlay does: opened from hardware detection, not a display
/// assignment, and loading <c>/panel</c> with no device id so the kiosk
/// self-registers a <c>y70</c> record and reports its own viewport on resize.
/// That keeps compositor rotation and scale authoritative - DRM sysfs only
/// knows the panel's native scanout mode.
///
/// Placement caveat: Wayland gives clients no protocol to target a specific
/// output, so the kiosk fullscreens on the compositor-chosen monitor -
/// exact on single-monitor rigs, best-effort on multi-monitor. The DRM
/// topology also carries no positions, so there is nothing to translate a
/// displayId into screen coordinates with from the daemon side.
///
/// Each kiosk gets its own profile (see ProfileDir, FirefoxProfileDir) so the
/// spawned pid IS the browser process (no delegation to a running instance)
/// and Kill(tree) closes exactly that window.
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
    // Reconcile runs on each assignment, launch and session change; without this the cap logs on each one.
    private readonly HashSet<string> _cappedLogged = new(StringComparer.Ordinal);
    // Slots whose browser is being killed outside _lock. A spawn on the same profile while the
    // old browser still holds its singleton lock exits at once and reads as a rapid failure.
    private readonly HashSet<string> _closing = new(StringComparer.Ordinal);
    private bool _reconcileDeferred;
    private bool _disposed;
    private bool _noBrowserLogged;
    private bool _noSessionLogged;

    internal sealed class Kiosk
    {
        public Kiosk(Process process, string deviceId, DateTime spawnedUtc, string? flatpakApp, IDisposable? placement = null)
        {
            Placement = placement;
            Process = process;
            FlatpakApp = flatpakApp;
            DeviceId = deviceId;
            SpawnedUtc = spawnedUtc;
        }

        public Process Process { get; }
        /// <summary>Hyprland placement and touch re-binding for the Y70 kiosk, stopped with the kiosk.</summary>
        public IDisposable? Placement { get; }
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
        LinuxSession.SessionAdopted += Reconcile;
    }

    /// <summary>Same predicate as the Windows overlay's PanelDisplay auto-launch.</summary>
    private bool Y70Desired() => _store.Load().Panel.AutoLaunch && _topology.HasY70Display();

    // Read from the settings-store change handler on route threads, so it never takes _lock.
    private volatile bool _y70Running;

    internal bool WantsY70DisplayWatch()
        => !_y70Running && _store.Load().Panel.AutoLaunch && (!LinuxSession.IsRootDaemon || LinuxSession.SessionUid is not null);

    internal bool Y70DisplayPresent() => _topology.HasY70Display();

    private bool _y70WasDesired;

    public bool Y70Running => _y70Running;

    /// <summary>Close the Y70 kiosk regardless of desire (AutoLaunch turned off).</summary>
    public void CloseY70()
    {
        Kiosk? kiosk;
        lock (_lock)
            kiosk = DetachKiosk(Y70Slot);
        if (kiosk is not null)
            CloseDetached(kiosk, Y70Slot, TerminateGraceMs);
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
        var detached = new List<(string DisplayId, Kiosk Kiosk)>();
        lock (_lock)
        {
            if (_disposed)
                return;
            // A root daemon started before login has no display: every spawn exits
            // at once and trips the rapid-failure cap. SessionAdopted reconciles.
            if (LinuxSession.IsRootDaemon && LinuxSession.SessionUid is null)
            {
                if (!_noSessionLogged)
                {
                    _noSessionLogged = true;
                    Console.Error.WriteLine("[panel-kiosk] no graphical session yet; kiosks open at login");
                }
                return;
            }
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
                if (_running[key].Process.HasExited && DetachKiosk(key, reserve: false) is { } gone)
                    detached.Add((key, gone));
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
            {
                if (DetachKiosk(displayId) is { } closing)
                    detached.Add((displayId, closing));
            }
            foreach (var (displayId, deviceId) in toOpen)
            {
                // A browser being closed still owns the slot; the close reconciles again when it is gone.
                if (!DeferIfClosing(displayId))
                    SpawnKiosk(displayId, deviceId);
            }
        }
        // Kill, wait and dispose off _lock: Process.Dispose and WaitForExit take
        // the Process monitor, which the Exited callback holds while it runs.
        foreach (var (displayId, kiosk) in detached)
            CloseDetached(kiosk, displayId, TerminateGraceMs);
    }

    /// <summary>
    /// The Y70 slot loads <c>/panel</c> with no device id: the SPA allocates (or
    /// re-touches, via the profile's localStorage) a self-registered y70 record.
    /// <c>surface=y70</c> names it, because the window maps on the primary
    /// monitor before KWin moves it and that viewport classifies as a phone.
    /// Promoted monitors load their bound record directly.
    /// </summary>
    internal static string KioskUrl(int port, string deviceId, string token)
    {
        var t = Uri.EscapeDataString(token);
        return string.Equals(deviceId, Y70Slot, StringComparison.Ordinal)
            ? $"http://localhost:{port}/panel?surface=y70&token={t}"
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

    /// <summary>Removes the kiosk from state under <c>_lock</c>; the caller terminates it after releasing the lock.</summary>
    private Kiosk? DetachKiosk(string displayId, bool reserve = true)
    {
        if (!_running.Remove(displayId, out var kiosk))
            return null;
        if (reserve)
            _closing.Add(displayId);
        if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
        {
            _y70Running = false;
            _ = LinuxScreenInhibit.ReleaseAsync(_dbus);
        }
        return kiosk;
    }

    /// <summary>Caller holds <c>_lock</c>. True when a browser is still being closed on this slot, so the spawn must wait.</summary>
    internal bool DeferIfClosing(string displayId)
    {
        if (!_closing.Contains(displayId))
            return false;
        _reconcileDeferred = true;
        return true;
    }

    internal void AddRunningForTest(string displayId, string deviceId, Process process)
    {
        lock (_lock)
            _running[displayId] = new Kiosk(process, deviceId, DateTime.UtcNow, null);
    }

    internal bool DetachForTest(string displayId, out object? kiosk)
    {
        lock (_lock)
        {
            var k = DetachKiosk(displayId);
            kiosk = k;
            return k is not null;
        }
    }

    internal void CloseDetachedForTest(object kiosk, string displayId)
        => CloseDetached((Kiosk)kiosk, displayId, ShutdownGraceMs);

    internal bool DeferIfClosingForTest(string displayId)
    {
        lock (_lock)
            return DeferIfClosing(displayId);
    }

    internal int RunningCount { get { lock (_lock) return _running.Count; } }
    internal bool IsRunning(string displayId) { lock (_lock) return _running.ContainsKey(displayId); }
    internal bool IsClosing(string displayId) { lock (_lock) return _closing.Contains(displayId); }
    internal bool ReconcileDeferred { get { lock (_lock) return _reconcileDeferred; } }

    /// <summary>Terminates a detached kiosk, frees its slot, and reconciles if a spawn was waiting on it. Must not run under <c>_lock</c>.</summary>
    private void CloseDetached(Kiosk kiosk, string displayId, int graceMs)
    {
        try
        {
            TerminateKiosk(kiosk, displayId, graceMs);
        }
        finally
        {
            bool again;
            lock (_lock)
            {
                _closing.Remove(displayId);
                again = _reconcileDeferred;
                _reconcileDeferred = false;
            }
            if (again)
                Reconcile();
        }
    }

    /// <summary>Must not run under <c>_lock</c>.</summary>
    internal static void TerminateKiosk(Kiosk kiosk, string displayId, int graceMs = TerminateGraceMs)
    {
        try { kiosk.Placement?.Dispose(); } catch { }
        try
        {
            if (!kiosk.Process.HasExited)
                TerminateBrowser(kiosk, graceMs);
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
    private static void TerminateBrowser(Kiosk kiosk, int graceMs)
    {
        if (kiosk.FlatpakApp is not null)
        {
            var instance = FlatpakInstanceForPid(kiosk.Process.Id, graceMs);
            if (instance is not null)
            {
                RunAsSessionUser(graceMs, "flatpak", new[] { "kill", instance });
                if (kiosk.Process.WaitForExit(graceMs))
                    return;
            }
        }
        kiosk.Process.Kill(entireProcessTree: true);
        kiosk.Process.WaitForExit(graceMs);
    }

    internal const int TerminateGraceMs = 3000;
    // Shutdown shares one overall budget across the flatpak ps, kill and tree-kill waits.
    internal const int ShutdownGraceMs = 1500;
    private const int ShutdownStdoutWaitMs = 500;

    private static string? FlatpakInstanceForPid(int pid, int graceMs)
    {
        var table = RunAsSessionUser(graceMs, "flatpak", new[] { "ps", "--columns=instance,pid" });
        foreach (var line in table.Split('\n'))
        {
            var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 2 && int.TryParse(cols[1].Trim(), out var p) && p == pid)
                return cols[0].Trim();
        }
        return null;
    }

    private static string RunAsSessionUser(string file, params string[] args)
        => RunAsSessionUser(TerminateGraceMs, file, args);

    private static string RunAsSessionUser(int graceMs, string file, string[] args)
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
            // Read concurrently: a ReadToEnd ahead of the bounded wait never returns for a hung child.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            // Once the child exited its pipe closes at once; the short second wait only covers a grandchild holding it.
            if (!proc.WaitForExit(graceMs) || !stdoutTask.Wait(ShutdownStdoutWaitMs))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            return stdoutTask.Result;
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
            if (_cappedLogged.Add(displayId))
                Console.Error.WriteLine($"[panel-kiosk] display={displayId} is capped after rapid failures; not spawning");
            return;
        }
        _cappedLogged.Remove(displayId);

        var browser = LinuxBrowsers.FindChromium();
        var isFirefox = false;
        if (browser is null && LinuxBrowsers.FindFirefox() is { } firefox)
        {
            browser = firefox;
            isFirefox = true;
        }
        if (browser is null)
        {
            if (!_noBrowserLogged)
            {
                _noBrowserLogged = true;
                Console.Error.WriteLine("[panel-kiosk] no supported browser found; monitor panels need a Chromium-family browser or Firefox installed");
            }
            return;
        }

        // Token rides in argv like the macOS helper's --token; /proc/*/cmdline
        // exposure on multi-user boxes is a known follow-up (short-TTL
        // bootstrap token), not solved here.
        // KDE places the window on the primary screen; the script moves it to
        // the Y70. Loaded before the spawn so windowAdded catches this window.
        var isY70 = string.Equals(displayId, Y70Slot, StringComparison.Ordinal);
        var y70Connector = isY70 ? LinuxX11Outputs.ConnectorFromDisplayId(_topology.Y70DisplayId()) : "";
        if (isY70)
            _ = LinuxKWinPanelPlacement.EnsureLoadedAsync(_dbus, y70Connector);

        var url = KioskUrl(_servicePort, deviceId, _tokens.Token);
        var flatpakApp = LinuxBrowsers.FlatpakAppId(browser);
        var args = isFirefox
            ? FirefoxKioskArgs(url, PrepareFirefoxProfile(FirefoxProfileDir(deviceId, flatpakApp)), isY70)
            : ChromiumKioskArgs(url, ProfileDir(deviceId, flatpakApp), displayId);

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
        // Without these an X11 kiosk dies with "Missing X server or $DISPLAY".
        LinuxSession.ApplySessionDisplayEnv(psi);
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
            AttachExited(proc, () => OnKioskExited(displayId, proc));
            var placement = isY70 ? LinuxHyprlandPanelPlacement.Start(y70Connector) : null;
            _running[displayId] = new Kiosk(proc, deviceId, DateTime.UtcNow, flatpakApp, placement);
            if (isY70)
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

    /// <summary>
    /// Process raises Exited while holding its own monitor, the same one Dispose, Close and
    /// WaitForExit take. A handler that waits on a lock a closer holds while disposing
    /// deadlocks both, so the handler is handed to the pool and the callback returns at once.
    /// </summary>
    internal static void AttachExited(Process proc, Action onExited)
    {
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => _ = Task.Run(onExited);
    }

    private List<string> ChromiumKioskArgs(string url, string profileDir, string displayId)
    {
        var args = new List<string>();
        // Follows the session: wayland forced on an X11 login kills the kiosk on
        // its first frame ("Failed to connect to Wayland display").
        if (LinuxSession.ChromiumOzonePlatform() is { } ozone)
            args.Add($"--ozone-platform={ozone}");
        args.Add($"--app={url}");
        // Place before --kiosk: the window manager fullscreens onto the monitor the
        // window already occupies. Matters most for the Y70 slot, which self-registers
        // its record from whatever viewport it lands on. On KDE-under-X11 the KWin
        // script also moves it, to the same output.
        PlaceOnOutput(args, displayId);
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
        args.Add($"--user-data-dir={profileDir}");
        args.Add("--no-first-run");
        args.Add("--noerrdialogs");
        args.Add("--disable-session-crashed-bubble");
        return args;
    }

    /// <summary>Firefox has no --window-position, so placement is the KWin script's alone (KDE only).</summary>
    internal static List<string> FirefoxKioskArgs(string url, string profileDir, bool isY70)
    {
        // --new-instance: a running Firefox would otherwise take the URL and the spawned pid would
        // exit at once. Remoting is keyed by profile path, so no other invocation reaches the kiosk.
        var args = new List<string> { "--new-instance", "--profile", profileDir };
        if (isY70)
        {
            // --name sets the Wayland app_id, --class the X11 WM_CLASS the script matches.
            args.Add($"--name={LinuxKWinPanelPlacement.FirefoxClass}");
            args.Add($"--class={LinuxKWinPanelPlacement.FirefoxClass}");
        }
        args.Add("--kiosk");
        args.Add(url);
        return args;
    }

    // Prefs that would otherwise open a welcome, privacy-notice, default-browser,
    // restore-session or safe-mode page over the panel. user.js re-applies them every start.
    internal const string FirefoxUserJs = """
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("browser.aboutwelcome.enabled", false);
user_pref("browser.startup.homepage_override.mstone", "ignore");
user_pref("datareporting.policy.firstRunURL", "");
user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
user_pref("browser.sessionstore.resume_from_crash", false);
user_pref("toolkit.startup.max_resumed_crashes", -1);
user_pref("browser.tabs.warnOnClose", false);

""";

    /// <summary>
    /// Runs as the session user: the profile is user-owned, and a root write or
    /// chown there would follow a symlink planted in it. A killed (or sandboxed,
    /// other-pid-namespace) Firefox leaves its lock behind and the next start waits
    /// on an "already running" dialog, so the lock goes unless a live process still
    /// has this profile on its command line (two instances on one profile).
    /// </summary>
    internal static string PrepareFirefoxProfile(string dir)
    {
        const string script =
            "mkdir -p \"$1\" && { pgrep -f -- \"--profile $1\" >/dev/null || rm -f \"$1/lock\" \"$1/.parentlock\"; } " +
            "&& printf '%s' \"$2\" > \"$1/user.js\" || echo profile-setup-failed";
        if (RunAsSessionUser("sh", "-c", script, "sh", dir, FirefoxUserJs).Contains("profile-setup-failed", StringComparison.Ordinal))
            Console.Error.WriteLine($"[panel-kiosk] firefox profile setup failed at {dir}");
        return dir;
    }

    /// <summary>Add window-geometry flags for this kiosk's monitor when the session
    /// is X11 and xrandr can name it. No-op otherwise, including on Wayland.</summary>
    private void PlaceOnOutput(List<string> args, string displayId)
    {
        var isY70 = string.Equals(displayId, Y70Slot, StringComparison.Ordinal);
        // Y70Slot is a reserved key, not a /displays id.
        var connector = LinuxX11Outputs.ConnectorFromDisplayId(
            isY70 ? _topology.Y70DisplayId() : displayId);
        if (LinuxX11Outputs.Find(connector, allowPortraitFallback: isY70) is not { } output)
            return;
        args.Add($"--window-position={output.X},{output.Y}");
        args.Add($"--window-size={output.Width},{output.Height}");
        Console.Error.WriteLine(
            $"[panel-kiosk] display={displayId} placed on X11 output {output.Name} " +
            $"{output.Width}x{output.Height}+{output.X}+{output.Y}");
    }

    internal void OnKioskExited(string displayId, Process exited)
    {
        bool respawn = false, gaveUp = false;
        int failures = 0;
        // Chromium's stderr is inherited, not piped, so the exit code is the only
        // diagnosis a crash leaves: 0/1 a clean refusal, 139 a segfault.
        int? exitCode = null;
        Kiosk kiosk;
        lock (_lock)
        {
            if (_disposed)
                return;
            // A superseded process (closed, then respawned) must not tear down its successor.
            if (!_running.TryGetValue(displayId, out var current) || !ReferenceEquals(current.Process, exited) || !exited.HasExited)
                return;
            kiosk = current;
            try { exitCode = kiosk.Process.ExitCode; } catch { }
            _running.Remove(displayId);
            if (string.Equals(displayId, Y70Slot, StringComparison.Ordinal))
            {
                _y70Running = false;
                _ = LinuxScreenInhibit.ReleaseAsync(_dbus);
            }

            if (DateTime.UtcNow - kiosk.SpawnedUtc < TimeSpan.FromSeconds(RapidFailureWindowSeconds))
            {
                failures = _rapidFailures.GetValueOrDefault(displayId) + 1;
                _rapidFailures[displayId] = failures;
                gaveUp = failures > MaxRapidFailures;
            }
            else
            {
                _rapidFailures.Remove(displayId);
            }
            if (!gaveUp)
            {
                respawn = string.Equals(displayId, Y70Slot, StringComparison.Ordinal)
                    ? Y70Desired()
                    : _registry.ListAssignments()
                        .Any(a => string.Equals(a.DisplayId, displayId, StringComparison.Ordinal));
            }
        }
        try { kiosk.Placement?.Dispose(); } catch { }
        try { kiosk.Process.Dispose(); } catch { }
        if (gaveUp)
        {
            Console.Error.WriteLine(
                $"[panel-kiosk] giving up on display={displayId} after {failures} rapid failures " +
                $"(last exit code={exitCode}); turn the panel off and on to retry");
            return;
        }
        if (!respawn)
        {
            Console.Error.WriteLine($"[panel-kiosk] display={displayId} exited; no longer desired, not respawning");
            return;
        }
        Console.Error.WriteLine(
            $"[panel-kiosk] display={displayId} exited code={exitCode}; respawning");
        // 2s crash backoff, same as the overlay host launchers.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            Reconcile();
        });
    }

    // Snap confinement grants only non-hidden $HOME paths, and exits on "Failed
    // To Create Data Directory" elsewhere; a confined browser is unidentifiable
    // by path, since Ubuntu's /usr/bin/chromium-browser execs /snap/bin/chromium.
    // HOME is read per call: the root daemon adopts the session user's after
    // start, so a cached value would point at /root. Not pre-created - the
    // browser makes it as the session user, and a root-owned dir is unwritable.
    internal static string ProfileDir(string deviceId, string? flatpakApp) => flatpakApp is null
        ? Path.Combine(KioskHome(), "nexus-kiosk", deviceId)
        : FlatpakProfileDir(deviceId, flatpakApp);

    internal static string FirefoxProfileDir(string deviceId, string? flatpakApp) => flatpakApp is null
        ? Path.Combine(KioskHome(), "nexus-kiosk", "firefox", deviceId)
        : FlatpakProfileDir(deviceId, flatpakApp);

    // Flathub's Brave, Chrome and Firefox get no --filesystem=home: a profile
    // elsewhere in $HOME does not persist across launches, and with it goes the
    // localStorage that holds the kiosk's record id. ~/.var/app/<id> always does.
    private static string FlatpakProfileDir(string deviceId, string flatpakApp)
        => Path.Combine(KioskHome(), ".var", "app", flatpakApp, "nexus-kiosk", deviceId);

    private static string KioskHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            return home;
        // GetFolderPath yields "" for an unresolvable home, which would make
        // the profile path relative to the browser's CWD.
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir;
    }

    /// <summary>Upper bound for closing every kiosk at once; each close is itself bounded.</summary>
    internal const int ShutdownBudgetMs = 8000;

    /// <summary>
    /// Stops reconciling and closes every kiosk in parallel, bounded overall.
    /// Idempotent. Called at ApplicationStopping and again from Dispose.
    /// </summary>
    public void Shutdown()
    {
        List<(string DisplayId, Kiosk Kiosk)> all;
        lock (_lock)
        {
            _disposed = true;
            all = new List<(string, Kiosk)>();
            foreach (var displayId in _running.Keys.ToList())
            {
                if (DetachKiosk(displayId) is { } kiosk)
                    all.Add((displayId, kiosk));
            }
        }
        if (all.Count == 0)
            return;
        var tasks = all.Select(k => Task.Run(() => CloseDetached(k.Kiosk, k.DisplayId, ShutdownGraceMs))).ToArray();
        try { Task.WaitAll(tasks, ShutdownBudgetMs); } catch { }
    }

    public void Dispose()
    {
        LinuxSession.SessionAdopted -= Reconcile;
        Shutdown();
    }
}
