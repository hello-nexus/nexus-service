using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// User-side screen-mirror capture helper. The root daemon spawns this as the
/// logged-in user (via setpriv) because the xdg-desktop-portal ScreenCast portal
/// rejects a root caller - it tries to read the caller's <c>/proc/&lt;pid&gt;/root</c>
/// to identify the app and can't read root's. Running as the user, the portal
/// handshake succeeds and shows the share dialog on the user's screen.
///
/// It does the handshake (<see cref="LinuxScreenCastPortal"/>), then runs a
/// <c>gst-launch pipewiresrc</c> consumer whose stdout is inherited from us -
/// raw RGB frames flow straight to the daemon's read pipe. We hold the portal
/// session (our D-Bus connection) open for the stream's lifetime and only ever
/// write logs to stderr, so the frame stream on fd&#160;1 stays uncorrupted. The
/// daemon's process-tree kill tears us (and gst, and the session) down.
///
/// Invoked as: <c>Nexus screencast-helper &lt;width&gt; &lt;height&gt;</c>
/// </summary>
public static partial class LinuxScreenCastHelper
{
    public const string Verb = "screencast-helper";

    private const int PR_SET_PDEATHSIG = 1;
    private const nuint SIGTERM = 15;

    [LibraryImport("libc")]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    public static int Run(string[] args)
    {
        // fd 1 carries the raw RGB frame stream to the daemon; reroute any managed
        // stdout write to stderr so a stray Console.Write can never corrupt frames.
        Console.SetOut(Console.Error);
        // Die if the daemon (our parent) dies, so a daemon crash can't orphan us +
        // gst, which would hold the portal screencast open forever.
        try { prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0); } catch { }
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screencast-helper] fatal: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var w) || !int.TryParse(args[2], out var h))
        {
            Console.Error.WriteLine("[screencast-helper] usage: screencast-helper <width> <height>");
            return 2;
        }

        var bus = new DBusConnection();
        await bus.StartAsync();
        var portal = new LinuxScreenCastPortal(bus);

        var token = LoadRestoreToken();
        var result = await portal.StartAsync(token);
        if (result is null)
        {
            Console.Error.WriteLine("[screencast-helper] portal yielded no stream (cancelled?)");
            return 1;
        }
        if (!string.IsNullOrEmpty(result.RestoreToken) && result.RestoreToken != token)
            SaveRestoreToken(result.RestoreToken!);

        var psi = new ProcessStartInfo
        {
            FileName = "gst-launch-1.0",
            RedirectStandardOutput = false, // inherit fd 1 -> daemon's read pipe
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-q",
            "pipewiresrc", $"path={result.NodeId}", "keepalive-time=1000", "resend-last=true",
            "!", "videoconvert",
            "!", "videoscale",
            "!", $"video/x-raw,format=RGB,width={w},height={h},pixel-aspect-ratio=1/1",
            "!", "fdsink", "fd=1", "sync=false",
        })
        {
            psi.ArgumentList.Add(a);
        }

        var gst = Process.Start(psi);
        if (gst is null)
        {
            Console.Error.WriteLine("[screencast-helper] failed to start gst-launch");
            return 1;
        }
        Console.Error.WriteLine($"[screencast-helper] streaming pipewire node {result.NodeId} at {w}x{h}");

        // On a termination signal, take gst (and thus the portal session) down
        // with us instead of leaving an orphan holding the screencast.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => KillTree(gst));
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => KillTree(gst));

        // Drain gst stderr to EOF (reached only after it exits and the pipe
        // empties) so a full pipe buffer can't block gst and wedge the stream.
        var drain = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await gst.StandardError.ReadLineAsync()) is not null)
                {
                    if (line.Length > 0)
                        Console.Error.WriteLine($"[screencast-helper/gst] {line}");
                }
            }
            catch { }
        });

        await gst.WaitForExitAsync();
        try { await drain; } catch { }
        Console.Error.WriteLine($"[screencast-helper] gst exited {gst.ExitCode}");
        return gst.ExitCode;
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    // Deliberately NOT on the system-daemon root: this is a per-user portal
    // grant, and the helper that writes it runs unprivileged (setpriv, as the
    // session user) so it could not write a root-owned dir anyway. Both sides
    // read XDG_CONFIG_HOME, which the helper inherits from the adopting daemon.
    private static string TokenPath()
    {
        var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(cfg))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/root";
            cfg = Path.Combine(home, ".config");
        }
        return Path.Combine(cfg, "Nexus", "screencast-restore.token");
    }

    internal static string? LoadRestoreToken()
    {
        try
        {
            var p = TokenPath();
            if (!File.Exists(p))
                return null;
            var t = File.ReadAllText(p).Trim();
            return t.Length > 0 ? t : null;
        }
        catch { return null; }
    }

    internal static void SaveRestoreToken(string token)
    {
        try
        {
            var p = TokenPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, token);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[screencast-helper] could not save restore token: {ex.Message}"); }
    }

    /// <summary>Forget the saved grant so the next handshake re-opens the picker.</summary>
    internal static void DeleteRestoreToken()
    {
        try
        {
            var p = TokenPath();
            if (File.Exists(p))
                File.Delete(p);
        }
        catch { }
    }
}
