using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Bridges a root system daemon to the active graphical login session.
///
/// Run as root (like coolercontrol's <c>coolercontrold</c>) Nexus gets full
/// hardware access - direct pwm writes, NVML fan control, kernel-module loading,
/// raw i2c/hidraw - but loses the per-user session context the tray (D-Bus
/// StatusNotifierItem), MPRIS media, <c>wpctl</c>/<c>pactl</c> volume, and the
/// dashboard launcher need. This detects the active seat's user via
/// <c>loginctl</c> and adopts their session environment (bus, runtime dir,
/// config home, display) so those features keep working. The daemon's OWN
/// data does not ride on that: it lives at a machine-scope root that needs no
/// session (see <c>NexusDataPaths</c>), so booting before login can never
/// strand it in a second config.
///
/// No-ops unless running as root with no session env already set - so a normal
/// <c>systemd --user</c> install or a dev run is untouched. Single active
/// graphical session is assumed (the desktop case); multi-seat picks the first.
/// </summary>
public static partial class LinuxSession
{
    /// <summary>
    /// The active session user's uid when running as a root daemon that adopted
    /// a session; null otherwise (normal --user run). Used to authenticate
    /// session-bus connects as that user (see <see cref="ConnectAsSessionUser"/>).
    /// </summary>
    public static uint? SessionUid { get; private set; }

    /// <summary>The session user's primary gid; pairs with <see cref="SessionUid"/>.</summary>
    public static uint? SessionGid { get; private set; }

    /// <summary>The adopted session's X11 <c>DISPLAY</c>, null on Wayland. Kept out
    /// of this process's environment (see <see cref="Apply"/>); browsers get it from
    /// <see cref="ApplySessionDisplayEnv"/>.</summary>
    public static string? SessionDisplay { get; private set; }

    /// <summary>X11 authority file pairing with <see cref="SessionDisplay"/>.</summary>
    public static string? SessionXauthority { get; private set; }

    /// <summary>
    /// True when this process is the root system daemon (running as root with no
    /// session env of its own), whether or not a graphical session was found.
    /// Decided before any wait, so path resolution never depends on a login:
    /// the daemon's data lives at a machine-scope root (see
    /// <c>NexusDataPaths</c>), exactly like %ProgramData% on Windows. False for
    /// a <c>systemd --user</c> install or a dev run, which keep the XDG layout.
    /// </summary>
    public static bool IsRootDaemon { get; private set; }

    /// <summary>
    /// Raised once the daemon adopts a graphical session that did not exist at
    /// startup. Subsystems that need the session bus (tray, screen time) fail
    /// at boot on a pre-login start and retry from here instead of forcing the
    /// user to restart the service after logging in.
    /// </summary>
    public static event Action? SessionAdopted;

    // Background poll for a user session that is not up yet (see AdoptActiveSessionEnv).
    // Tight at first for an autologin landing seconds after boot, then slow, because
    // a box can sit at the login screen for days and every tick is two loginctl
    // processes. Never gives up: giving up is the restart-after-login step this
    // whole path exists to remove.
    private const int SessionPollFastMs = 2_000;
    private const int SessionPollSlowMs = 30_000;
    private const int SessionFastWindowMs = 2 * 60 * 1000;

    public static void AdoptActiveSessionEnv()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A --user service / dev run already has the session env; only a root
        // daemon arrives here bare.
        if (!IsRoot() || HasSessionEnv())
            return;
        IsRootDaemon = true;

        // Ordered after graphical.target, the daemon still routinely starts
        // before the user's session exists (autologin lands seconds later, a
        // box parked at the login screen much later than that). Never block on
        // it: hardware control - fans, pump, RGB - must come up at boot like
        // the Windows service does, so a missing session only costs the
        // session-bound extras, and a watcher attaches them when the user logs in.
        var s = Detect();
        if (s is null)
        {
            Console.Error.WriteLine("[session] root daemon: no graphical session yet; hardware control starts now, session features (tray/media/volume) attach at login");
            StartSessionWatch();
            return;
        }

        Apply(s);
    }

    // Poll for the session the daemon booted without, then adopt it and let the
    // session-bound subsystems re-arm.
    private static void StartSessionWatch()
    {
        var t = new Thread(() =>
        {
            var waited = 0;
            while (true)
            {
                var nap = waited < SessionFastWindowMs ? SessionPollFastMs : SessionPollSlowMs;
                Thread.Sleep(nap);
                waited += nap;
                SessionInfo? found;
                try { found = Detect(); }
                catch { continue; }
                if (found is null)
                    continue;
                Apply(found);
                try { SessionAdopted?.Invoke(); }
                catch (Exception ex)
                { Console.Error.WriteLine($"[session] adopt handler failed: {ex.Message}"); }
                return;
            }
        })
        { IsBackground = true, Name = "session-watch" };
        t.Start();
    }

    private static void Apply(SessionInfo s)
    {
        SessionUid = s.Uid;
        SessionGid = s.Gid;
        // Override unconditionally: this only runs as a root daemon with no
        // session env, where HOME/XDG_CONFIG_HOME are pre-set to root's by
        // sudo/systemd - set-if-unset would leave them pointing at /root.
        var run = $"/run/user/{s.Uid}";
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", run);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", $"unix:path={run}/bus");
        if (!string.IsNullOrEmpty(s.Home))
        {
            // Adopt the user's home so the DESKTOP files we read (dconf
            // wallpaper, Plasma config, Steam library, .desktop shortcuts,
            // ~/.local browsers) resolve to the user, not /root. Our own
            // stores ignore HOME on this path - see NexusDataPaths.
            Environment.SetEnvironmentVariable("HOME", s.Home);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(s.Home, ".config"));
        }
        // Presence of WAYLAND_DISPLAY selects Chromium's ozone platform and the
        // clipboard backend, so an X11 session must leave it unset.
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", ResolveWaylandDisplay(s.Type, s.Wayland));
        // DISPLAY/XAUTHORITY stay out of this process's environment: the lighting
        // shader context renders headless via EGL (LinuxEglContext), and a visible
        // DISPLAY tempts a GLFW/GLX path that segfaults creating an nvidia GL
        // context as root on the user's XWayland. Browsers take them per-child.
        SessionDisplay = s.Display;
        SessionXauthority = s.Xauthority;

        Console.Error.WriteLine($"[session] root daemon adopted {s.Type ?? "unknown"} session of uid {s.Uid} (home {s.Home}, wayland {s.Wayland ?? "none"}, display {s.Display ?? "none"})");
    }

    private static bool IsRoot()
    {
        // Environment.UserName resolves via getpwuid(geteuid()) on Unix, so it
        // reflects the effective user - "root" for a root daemon.
        try { return string.Equals(Environment.UserName, "root", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static bool HasSessionEnv()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    private sealed record SessionInfo(uint Uid, uint Gid, string? Home, string? Type, string? Display, string? Xauthority, string? Wayland);

    private static SessionInfo? Detect()
    {
        // First session id on each line of `loginctl list-sessions --no-legend`.
        var list = ShellExecutor.Run("loginctl", "list-sessions", "--no-legend");
        var sessions = new List<(string Id, Dictionary<string, string> Props)>();
        foreach (var raw in list.Split('\n'))
        {
            var id = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(id))
                continue;
            sessions.Add((id, ParseProps(ShellExecutor.Run(
                "loginctl", "show-session", id,
                "-p", "State", "-p", "Type", "-p", "Class", "-p", "User", "-p", "Display", "-p", "Leader"))));
        }
        var chosen = SelectSession(sessions);
        if (chosen is null)
            return null;
        var props = chosen.Value.Props;
        var uid = uint.Parse(props["User"]);
        var (gid, home) = PasswdForUid(uid);
        if (string.IsNullOrEmpty(home))
            return null; // no passwd entry - can't safely adopt this session
        var type = Blank(props.GetValueOrDefault("Type"));
        var (display, xauthority) = ResolveX11Env(
            type,
            LeaderEnviron(props.GetValueOrDefault("Leader")),
            props.GetValueOrDefault("Display"),
            home,
            File.Exists);
        return new SessionInfo(uid, gid, home, type, display, xauthority, WaylandSocket(uid));
    }

    /// <summary>
    /// The user's graphical session in logind's own vocabulary: a <c>Class=user</c>
    /// session of <c>Type</c> wayland/x11 whose <c>State</c> is <c>active</c>
    /// (foreground on its seat) or <c>online</c> (logged in with live sockets but
    /// not foreground - what logind reports after a resume or VT switch). Active
    /// wins over online; <c>closing</c> is never adopted. Greeter/manager/background
    /// sessions are skipped by class and system users (uid &lt; 1000, e.g. sddm)
    /// by uid, since their bus is unusable for the user.
    /// </summary>
    internal static (string Id, Dictionary<string, string> Props)? SelectSession(
        IReadOnlyList<(string Id, Dictionary<string, string> Props)> sessions)
    {
        (string, Dictionary<string, string>)? online = null;
        foreach (var session in sessions)
        {
            var props = session.Props;
            var type = props.GetValueOrDefault("Type");
            if (type != "wayland" && type != "x11")
                continue;
            if (!string.Equals(props.GetValueOrDefault("Class"), "user", StringComparison.Ordinal))
                continue;
            if (!uint.TryParse(props.GetValueOrDefault("User"), out var uid) || uid < 1000)
                continue;
            switch (props.GetValueOrDefault("State"))
            {
                case "active":
                    return session;
                case "online":
                    online ??= session;
                    break;
            }
        }
        return online;
    }

    // `key=value` lines from loginctl show-session.
    private static Dictionary<string, string> ParseProps(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    private static (uint Gid, string? Home) PasswdForUid(uint uid)
    {
        // getent passwd: name:x:uid:gid:gecos:home:shell
        var fields = ShellExecutor.Run("getent", "passwd", uid.ToString()).Trim().Split(':');
        var gid = fields.Length >= 4 && uint.TryParse(fields[3], out var g) ? g : uid;
        var home = fields.Length >= 6 && fields[5].Length > 0 ? fields[5] : null;
        return (gid, home);
    }

    /// <summary>
    /// Wrap a command so a root daemon spawns it as the session user (e.g. the
    /// dashboard browser): <c>setpriv</c> drops uid/gid + supplementary groups
    /// WITHOUT resetting env, so the child inherits our adopted
    /// XDG_RUNTIME_DIR/WAYLAND_DISPLAY and runs inside the user's compositor.
    /// Chromium refuses to run as root, and a root GUI client in a user's
    /// session is wrong anyway. Pass-through when not a root daemon.
    /// </summary>
    public static (string File, List<string> Args) WrapSpawnAsSessionUser(string file, List<string> args)
    {
        if (SessionUid is null || SessionGid is null)
            return (file, args);
        var wrapped = new List<string>
        {
            "--reuid", SessionUid.Value.ToString(),
            "--regid", SessionGid.Value.ToString(),
            "--init-groups", "--", file,
        };
        wrapped.AddRange(args);
        return ("setpriv", wrapped);
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>WAYLAND_DISPLAY to export, or null to leave it unset. An x11 session
    /// yields null even when another seat left a socket in the runtime dir.</summary>
    internal static string? ResolveWaylandDisplay(string? sessionType, string? waylandSocket)
    {
        if (string.Equals(sessionType, "x11", StringComparison.Ordinal))
            return null;
        return waylandSocket ?? (string.Equals(sessionType, "wayland", StringComparison.Ordinal) ? "wayland-0" : null);
    }

    /// <summary>DISPLAY/XAUTHORITY for an x11 session, else (null, null): on Wayland
    /// these name only the XWayland server and would steer a kiosk onto it.
    /// <paramref name="fileExists"/> is a test seam.</summary>
    internal static (string? Display, string? Xauthority) ResolveX11Env(
        string? sessionType,
        IReadOnlyDictionary<string, string> leaderEnv,
        string? logindDisplay,
        string? home,
        Func<string, bool> fileExists)
    {
        if (!string.Equals(sessionType, "x11", StringComparison.Ordinal))
            return (null, null);
        var display = Blank(leaderEnv.GetValueOrDefault("DISPLAY")) ?? Blank(logindDisplay);
        var xauthority = Blank(leaderEnv.GetValueOrDefault("XAUTHORITY"));
        if (xauthority is null && !string.IsNullOrEmpty(home))
        {
            // Classic LightDM/startx layout, where the cookie sits in the home
            // dir and nothing exports XAUTHORITY at all.
            var fallback = Path.Combine(home, ".Xauthority");
            if (fileExists(fallback))
                xauthority = fallback;
        }
        return (display, xauthority);
    }

    /// <summary>The session leader's environment from <c>/proc/&lt;pid&gt;/environ</c>
    /// (NUL-separated). logind never reports XAUTHORITY, and a display manager points
    /// it at a runtime file rather than <c>~/.Xauthority</c>.</summary>
    private static Dictionary<string, string> LeaderEnviron(string? leaderPid)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        // Parsed, not interpolated: loginctl output is about to become a path.
        if (!uint.TryParse(leaderPid, out var pid) || pid == 0)
            return env;
        try
        {
            foreach (var entry in File.ReadAllText($"/proc/{pid}/environ").Split('\0'))
            {
                var eq = entry.IndexOf('=');
                if (eq > 0)
                    env[entry[..eq]] = entry[(eq + 1)..];
            }
        }
        catch { /* leader already gone, or /proc not readable */ }
        return env;
    }

    /// <summary><c>--ozone-platform</c> for a spawned Chromium, or null to let it
    /// auto-detect. Only wayland is ever forced: Chromium defaults to X11 and dies on
    /// a pure-Wayland login, while an X11 session needs no flag and a fork built
    /// without the x11 ozone backend would fail on one.</summary>
    public static string? ChromiumOzonePlatform()
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? null : "wayland";

    /// <summary>Hand a spawned browser the session's X11 environment. Browser
    /// launches only - a Nexus child would inherit the GLX hazard <see cref="Apply"/>
    /// avoids. No-op on Wayland and on a non-root-daemon run.</summary>
    public static void ApplySessionDisplayEnv(ProcessStartInfo psi)
    {
        if (SessionDisplay is { Length: > 0 } display)
            psi.Environment["DISPLAY"] = display;
        if (SessionXauthority is { Length: > 0 } xauthority)
            psi.Environment["XAUTHORITY"] = xauthority;
    }

    private static string? WaylandSocket(uint uid)
    {
        try
        {
            var sock = Directory.GetFiles($"/run/user/{uid}", "wayland-*")
                .FirstOrDefault(f => !f.EndsWith(".lock", StringComparison.Ordinal));
            return sock is null ? null : Path.GetFileName(sock);
        }
        catch { return null; }
    }

    private static readonly object EuidGate = new();

    /// <summary>
    /// Run <paramref name="connect"/> with the effective uid temporarily dropped
    /// to the session user, then restore root. A D-Bus session bus authenticates
    /// by the peer's <c>SO_PEERCRED</c> (effective uid at connect time) and
    /// rejects root, so the socket connect must happen as the user; the resulting
    /// connection keeps that identity after euid is restored. No-op (just calls
    /// <paramref name="connect"/>) when not a root daemon. Serialized so two
    /// connects can't race the process-wide euid.
    /// </summary>
    public static void ConnectAsSessionUser(Action connect)
    {
        var uid = SessionUid;
        if (uid is null || !OperatingSystem.IsLinux())
        {
            connect();
            return;
        }
        lock (EuidGate)
        {
            if (seteuid(uid.Value) != 0)
            {
                connect(); // couldn't drop - try anyway (will likely fail upstream)
                return;
            }
            try { connect(); }
            finally
            {
                // A root daemon (ruid=suid=0) can always restore euid 0; if it
                // somehow can't, every later hardware write would fail forever -
                // crash instead so systemd restarts us clean.
                if (seteuid(0) != 0)
                    Environment.FailFast("[session] could not restore root euid after a session-bus connect");
            }
        }
    }

    /// <summary>
    /// Run <paramref name="connect"/> serialized against
    /// <see cref="ConnectAsSessionUser"/>'s critical section, without dropping
    /// euid itself. A D-Bus system bus authenticates the caller's own (root)
    /// euid directly and must not race a concurrent session-bus connect's
    /// temporary euid drop - SO_PEERCRED would otherwise capture the dropped
    /// uid instead of root's. No-op (calls <paramref name="connect"/>
    /// directly) when not a root daemon, matching ConnectAsSessionUser.
    /// </summary>
    public static void ConnectSerialized(Action connect)
    {
        if (SessionUid is null || !OperatingSystem.IsLinux())
        {
            connect();
            return;
        }
        lock (EuidGate)
        {
            connect();
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int seteuid(uint uid);
}
