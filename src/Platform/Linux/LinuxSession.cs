using System;
using System.Collections.Generic;
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
/// config home, display) so those features keep working, and so the daemon
/// reads the user's existing settings/profiles instead of root's empty home.
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

    // Startup wait for the user session (see AdoptActiveSessionEnv).
    private const int SessionWaitMs = 30_000;
    private const int SessionPollMs = 2_000;

    public static void AdoptActiveSessionEnv()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // A --user service / dev run already has the session env; only a root
        // daemon arrives here bare.
        if (!IsRoot() || HasSessionEnv())
            return;

        // Ordered after graphical.target, the daemon still routinely starts
        // before the user's session exists (autologin lands seconds later). A
        // bounded wait covers that; a machine parked at the login screen past
        // it keeps the documented restart-after-login behaviour.
        var s = Detect();
        for (var waited = 0; s is null && waited < SessionWaitMs; waited += SessionPollMs)
        {
            if (waited == 0)
                Console.Error.WriteLine("[session] root daemon: no graphical session yet; waiting for one");
            Thread.Sleep(SessionPollMs);
            s = Detect();
        }
        if (s is null)
        {
            Console.Error.WriteLine("[session] root daemon: no graphical session found; session features (tray/media/volume) disabled this run");
            return;
        }

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
            // Adopt the user's home so config (settings.json, openrgb-config,
            // curves, profiles) and ~/.local browsers resolve to the user, not /root.
            Environment.SetEnvironmentVariable("HOME", s.Home);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(s.Home, ".config"));
        }
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", s.Wayland ?? "wayland-0");
        // Deliberately do NOT export DISPLAY/XAUTHORITY. The GPU lighting shader
        // context renders fully headless via EGL on the GPU device platform (see
        // LinuxEglContext) - no display required. Exporting DISPLAY would only
        // tempt a GLFW/GLX path that segfaults creating an nvidia GL context as
        // root on the user's XWayland (uncatchable native fault); EGL needs none
        // of it. Direct device control (identify, static) is unaffected either way.

        Console.Error.WriteLine($"[session] root daemon adopted session of uid {s.Uid} (home {s.Home}, wayland {s.Wayland})");
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

    private sealed record SessionInfo(uint Uid, uint Gid, string? Home, string? Display, string? Wayland);

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
                "-p", "State", "-p", "Type", "-p", "Class", "-p", "User", "-p", "Display"))));
        }
        var chosen = SelectSession(sessions);
        if (chosen is null)
            return null;
        var props = chosen.Value.Props;
        var uid = uint.Parse(props["User"]);
        var (gid, home) = PasswdForUid(uid);
        if (string.IsNullOrEmpty(home))
            return null; // no passwd entry - can't safely adopt this session
        var display = props.GetValueOrDefault("Display");
        return new SessionInfo(uid, gid, home, string.IsNullOrEmpty(display) ? null : display, WaylandSocket(uid));
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
