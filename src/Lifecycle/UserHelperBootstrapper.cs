#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Spawns <c>Nexus.exe --helper</c> in the active console user's session when
/// the LocalSystem service comes up. Necessary because the service is
/// LocalSystem in Session 0 - it can't draw a tray icon, can't see the
/// foreground window, can't query SMTC media, etc. The helper lives in the
/// user session and does all of that on the service's behalf.
///
/// Unconditional: no <c>ShowWindowsTrayIcon</c> gate. That preference controls
/// tray-icon visibility only; the helper hosts more than the tray, so it runs
/// regardless.
///
/// Cross-session launch uses schtasks (Task Scheduler service handles the
/// session/profile setup that direct CreateProcessAsUser fails on).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserHelperBootstrapper
{
    private const int LaunchAttempts = 3;
    // Not shorter than the helper's pipe reconnect backoff cap
    // (HelperClientLoop): a helper that is alive but backing off would
    // otherwise read as a failed spawn.
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(30);

    private static HelperRegistry? s_registry;
    private static int s_launchInProgress;
    private static int s_rerunRequested;

    public static void EnsureLaunched(HelperRegistry registry)
    {
        s_registry = registry;

        // A logon after the poll below has ended (a user who sits at the
        // sign-in screen past its window, or logs off and back on) used to
        // leave the session helper-less until the next service start. The SCM
        // delivers the logon on its control-handler thread, so hop off it
        // before touching WTS or schtasks.
        WindowsServiceHost.SessionLogon += () => _ = Task.Run(() => LaunchAndVerifyAsync("logon"));

        // Cold boot: the service starts seconds after power-on, before the
        // auto-login console session exists, so no launch target is resolvable
        // yet. A one-shot here skipped and never retried, stranding the tray
        // icon (the helper owns it) until the next service start. Poll for the
        // console session, then spawn. Fire-and-forget so service startup is
        // not blocked. Kept alongside the logon hook: a logon that lands before
        // ApplicationStarted subscribes above is only covered here.
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            while (true)
            {
                if (!string.IsNullOrEmpty(ResolveActiveConsoleUsername()))
                {
                    await LaunchAndVerifyAsync("startup");
                    return;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    Console.WriteLine("[helper-bootstrap] no active console user after 5 min; waiting for a logon");
                    return;
                }
                await Task.Delay(2000);
            }
        });
    }

    /// <summary>
    /// Spawns the helper in the active console session and waits for its pipe
    /// to connect, retrying a bounded number of times. A <c>schtasks /Run</c>
    /// that returns 0 has only had the request accepted; one field boot showed
    /// it produce no process at all, so the pipe connection is the success
    /// signal (process presence is ambiguous: every one-shot and every
    /// mutex-losing duplicate is also Nexus.exe). The task is deleted only
    /// after that wait so a launch the scheduler has queued is never pulled
    /// out from under it. One launch runs at a time; a trigger that arrives
    /// while one is running asks it to go round once more, so a logon during
    /// the final wait is not lost.
    /// </summary>
    private static async Task LaunchAndVerifyAsync(string trigger)
    {
        if (Interlocked.CompareExchange(ref s_launchInProgress, 1, 0) != 0)
        {
            Volatile.Write(ref s_rerunRequested, 1);
            return;
        }
        try
        {
            do
            {
                Volatile.Write(ref s_rerunRequested, 0);
                await LaunchOnceAsync(trigger);
            }
            while (Interlocked.Exchange(ref s_rerunRequested, 0) == 1);
        }
        finally
        {
            Volatile.Write(ref s_launchInProgress, 0);
        }
        // A request that landed between the loop's last check and the release
        // above would otherwise be lost; re-entering picks it up.
        if (Volatile.Read(ref s_rerunRequested) == 1) await LaunchAndVerifyAsync(trigger);
    }

    private static async Task LaunchOnceAsync(string trigger)
    {
        for (var attempt = 1; attempt <= LaunchAttempts; attempt++)
        {
            if (HelperConnected()) return;
            var tag = $"({trigger}, attempt {attempt}/{LaunchAttempts})";

            var username = ResolveActiveConsoleUsername();
            if (string.IsNullOrEmpty(username))
            {
                Console.WriteLine($"[helper-bootstrap] no active console user; skipping {tag}");
                await Task.Delay(5000);
                continue;
            }

            var exePath = Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"[helper-bootstrap] Nexus.exe not found at {exePath} {tag}");
                return;
            }

            var taskName = $"NexusHelperBootstrap_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
            var launched = false;
            var connected = false;
            try
            {
                launched = CreateAndRun(taskName, username, $"\"{exePath}\" {HelperSpawnArg()}");
                if (launched)
                {
                    Console.WriteLine($"[helper-bootstrap] launched --helper as {username} {tag}");
                    connected = await WaitForHelperAsync(ConnectWait);
                }
                else
                {
                    Console.Error.WriteLine($"[helper-bootstrap] schtasks could not start --helper as {username} {tag}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[helper-bootstrap] launch threw: {ex.Message} {tag}");
            }
            finally
            {
                Schtasks("/Delete", "/TN", taskName, "/F");
            }
            if (connected) return;
            if (launched) Console.Error.WriteLine($"[helper-bootstrap] no helper connected within {ConnectWait.TotalSeconds:0}s {tag}");
            else await Task.Delay(5000);
        }

        // Distinguishes "never launched" from "alive but silent" (a stale
        // mutex holder, a pipe that cannot be reached) in the next bundle.
        Console.Error.WriteLine($"[helper-bootstrap] giving up after {LaunchAttempts} attempts ({trigger}); Nexus.exe processes in the console session: {CountNexusProcessesInConsoleSession()}");
    }

    private static async Task<bool> WaitForHelperAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (HelperConnected()) return true;
            await Task.Delay(500);
        }
        return HelperConnected();
    }

    // Per console session rather than IsAnyConnected: an RDP session's helper
    // says nothing about the console user's.
    private static bool HelperConnected()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return false;
        return s_registry?.GetForSession((int)sessionId) is not null;
    }

    private static int CountNexusProcessesInConsoleSession()
    {
        try
        {
            var sessionId = (int)WTSGetActiveConsoleSessionId();
            var count = 0;
            foreach (var p in Process.GetProcessesByName("Nexus"))
            {
                try { if (p.SessionId == sessionId) count++; }
                catch { }
                finally { p.Dispose(); }
            }
            return count;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Mirror of <see cref="EnsureLaunched"/> for the "open the dashboard window"
    /// hotline. The desktop widget context menu's "Open dashboard" entry hits
    /// <c>POST /service/open-app</c>; the service handler runs as LocalSystem
    /// in Session 0 and cannot spawn an interactive Edge --app on its own,
    /// so we delegate to a one-shot <c>Nexus.exe --open-app</c> in the active
    /// console session, which then runs the same Edge --app spawn path the
    /// helper uses.
    /// </summary>
    public static void LaunchOpenApp()
    {
        try
        {
            SpawnInUserSession("--open-app", "open-app", "NexusOpenApp");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[open-app] failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Run an arbitrary command in the active console user's session via a
    /// one-shot scheduled task. Used for actions that no-op from Session 0
    /// (e.g. LockWorkStation). Returns true if the task ran.
    /// </summary>
    public static bool RunInUserSession(string command, string logTag, string taskPrefix)
    {
        var username = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            Console.WriteLine($"[{logTag}] no active console user; skipping");
            return false;
        }

        var taskName = $"{taskPrefix}_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        return CreateRunDelete(taskName, username, command);
    }

    /// <summary>
    /// Registers a trigger-less task for <paramref name="username"/>, runs it,
    /// and deletes it. The task carries no trigger, so a leftover one (when the
    /// /Delete fails) can only ever be started by an explicit /Run.
    /// </summary>
    private static bool CreateRunDelete(string taskName, string username, string command)
    {
        try { return CreateAndRun(taskName, username, command); }
        finally { Schtasks("/Delete", "/TN", taskName, "/F"); }
    }

    /// <summary>
    /// Registers and runs the task; the caller owns the /Delete, so a launch
    /// that must be verified first can keep the task alive while it waits.
    /// </summary>
    private static bool CreateAndRun(string taskName, string username, string command)
    {
        try
        {
            if (CreateFromXml(taskName, username, command))
            {
                return Schtasks("/Run", "/TN", taskName);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[user-session-task] XML registration threw: {ex.Message}");
        }

        // Fall back to the schedule-type form. Registering from XML is the
        // preferred path, but a host that rejects it must still get its tray
        // helper and overlay, so the legacy command line stands behind it.
        // /ST 00:00 is already past, so a leftover task has a spent trigger.
        Console.Error.WriteLine($"[user-session-task] {taskName}: XML registration failed, using schedule-type form");
        if (!Schtasks("/Create", "/TN", taskName, "/TR", command,
                      "/SC", "ONCE", "/ST", "00:00", "/RU", username, "/IT", "/F"))
        {
            return false;
        }
        return Schtasks("/Run", "/TN", taskName);
    }

    // Registers taskName from a trigger-less task XML. False when the XML could
    // not be staged or schtasks rejected it.
    private static bool CreateFromXml(string taskName, string username, string command)
    {
        var xmlPath = UserSessionTaskXml.WriteTempFile(UserSessionTaskXml.Build(username, command));
        try
        {
            return Schtasks("/Create", "/TN", taskName, "/XML", xmlPath, "/F");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best-effort */ }
        }
    }

    // Machine environment variable changes made after this service last
    // started do not reach a process schtasks spawns in another session, so
    // NEXUS_WINDOW_DIAG=1 set on the service side never carried into the
    // helper. An argv value survives the schtasks hop; the helper turns it
    // back into the env var WindowSetPoller already reads (WindowsUserHelper.Run).
    private static string HelperSpawnArg()
    {
        var arg = "--helper";
        if (Nexus.Service.Activity.WindowDiagnostics.IsEnabled(
                Environment.GetEnvironmentVariable(Nexus.Service.Activity.WindowDiagnostics.EnvVarName)))
        {
            arg += $" {Nexus.Service.Activity.WindowDiagnostics.HelperArgName}";
        }

        // Poller disable list takes the same hop, for the same reason. Spaces
        // would split the argv the task XML carries, so only the comma form
        // survives - Parse accepts both and the env var is documented with commas.
        var disable = Environment.GetEnvironmentVariable(Nexus.Service.Helper.HelperPollerDiagnostics.EnvVarName);
        if (!string.IsNullOrWhiteSpace(disable))
        {
            var normalized = string.Join(",", Nexus.Service.Helper.HelperPollerDiagnostics.Parse(disable));
            if (normalized.Length > 0)
            {
                arg += $" {Nexus.Service.Helper.HelperPollerDiagnostics.HelperArgPrefix}{normalized}";
            }
        }

        return arg;
    }

    private static void SpawnInUserSession(string nexusArg, string logTag, string taskPrefix)
    {
        var username = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            Console.WriteLine($"[{logTag}] no active console user; skipping");
            return;
        }

        var exePath = Path.Combine(AppContext.BaseDirectory, "Nexus.exe");
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"[{logTag}] Nexus.exe not found at {exePath}");
            return;
        }

        var taskName = $"{taskPrefix}_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        if (!CreateRunDelete(taskName, username, $"\"{exePath}\" {nexusArg}"))
        {
            return;
        }

        Console.WriteLine($"[{logTag}] launched {nexusArg} as {username}");
    }

    internal static string ResolveActiveConsoleUsername()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return string.Empty;
        var buf = IntPtr.Zero;
        try
        {
            // WTSUserName = 5
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5, out buf, out _) || buf == IntPtr.Zero)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private static bool Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
#endif
