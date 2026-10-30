using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Panel;

/// <summary>
/// Spawns and supervises nexus-overlay.exe, the WebView2 host that
/// hosts the panel kiosks on the Windows desktop. The host lives in the
/// `overlay/` subdirectory next to the service exe; that layout is enforced
/// by the PublishOverlayHost MSBuild target. The overlay is a raw-Win32 +
/// direct WebView2 C-API P/Invoke binary published with PublishAot=true -
/// the only files in overlay/ are nexus-overlay.exe and WebView2Loader.dll.
///
/// Started and restarted by <see cref="OverlaySupervisor"/>; killed on service
/// shutdown.
/// </summary>
public sealed class PanelOverlayHostLauncher : IOverlayHost
{
    /// <summary>No-op: nexus-overlay re-polls assignments on the PrefsChanged push.</summary>
    public void NotifyDisplayAssignmentsChanged() { }

    private Process? _process;
    private readonly IntPtr _jobHandle = IntPtr.Zero;
    private readonly object _lock = new();
    /// <summary>Stop() sets this so an in-flight spawn does not adopt the process it started. Re-armed by the next Start().</summary>
    private volatile bool _stopRequested;
    /// <summary>
    /// True while a background spawn is in flight. We dispatch the
    /// schtasks dance to a Task so the HTTP request that triggered the
    /// reconcile doesn't wait the 1-3 s it takes for the task scheduler
    /// to materialize a new process. Subsequent Start() calls during
    /// this window are no-ops to avoid stacking multiple in-flight
    /// schtasks tasks.
    /// </summary>
    private volatile bool _starting;
    private bool _waitingForLogon;
    private bool _missingHostLogged;
    /// <summary>Bumped by every Stop so an in-flight spawn abandons the process it started instead of adopting it.</summary>
    private int _spawnGeneration;
    private static readonly string PidFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "panel-desktop-pid.txt");

    public PanelOverlayHostLauncher()
    {
        // Create a Windows Job Object with KILL_ON_JOB_CLOSE so any
        // child we assign to it is killed when this handle closes - i.e.
        // when the service process exits, including via taskkill /F or
        // a hard crash that ApplicationStopping can't react to.
        if (OperatingSystem.IsWindows())
        {
            try { _jobHandle = CreateChildKillJob(); }
            catch (Exception ex) { Console.Error.WriteLine($"[overlay-host] job-object init failed: {ex.Message}"); }
        }
    }

    // Also true for an overlay this instance did not spawn: the tray starts one
    // for the dashboard, and a second copy would only lose the singleton mutex.
    public bool IsRunning => _process is { HasExited: false } || _starting
        || (OperatingSystem.IsWindows() && HostInConsoleSession());

    private static long _sessionScanTick;
    private static bool _sessionScanResult;

    /// <summary>Any nexus-overlay alive in the console session. Cached briefly:
    /// this is the steady state when the tray owns the host, so it is read on
    /// every supervisor tick and every settings change.</summary>
    [SupportedOSPlatform("windows")]
    private static bool HostInConsoleSession()
    {
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _sessionScanTick) < 2000) return Volatile.Read(ref _sessionScanResult);
        var session = WTSGetActiveConsoleSessionId();
        var found = false;
        if (session != 0xFFFFFFFF)
        {
            foreach (var p in Process.GetProcessesByName("nexus-overlay"))
            {
                using (p)
                {
                    try { if (!found && (uint)p.SessionId == session && !p.HasExited) found = true; }
                    catch { }
                }
            }
        }
        Volatile.Write(ref _sessionScanResult, found);
        Volatile.Write(ref _sessionScanTick, now);
        return found;
    }

    public bool Start()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (IsRunning) return true;

        var hostPath = ResolveHostPath();
        if (hostPath is null || !File.Exists(hostPath))
        {
            if (!_missingHostLogged)
            {
                _missingHostLogged = true;
                Console.Error.WriteLine($"[overlay-host] nexus-overlay.exe not found at expected path '{hostPath ?? "<null>"}'; the PublishOverlayHost target must populate <publish>/overlay/. Panel kiosks disabled.");
            }
            return false;
        }
        if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem
            && string.IsNullOrEmpty(ResolveActiveConsoleUsername())
            && Nexus.Service.Lifecycle.OobePanelPolicy.Decide(
                string.Empty, Nexus.Service.Lifecycle.OobeSessionLaunch.ConsoleSessionExists(),
                Nexus.Service.Lifecycle.OobeDetector.IsInOobe) != Nexus.Service.Lifecycle.ConsoleLaunchPlan.OobeNoUser)
        {
            if (!_waitingForLogon) Console.WriteLine("[overlay-host] no active console user; waiting for logon");
            _waitingForLogon = true;
            return false;
        }
        _waitingForLogon = false;

        int generation;
        lock (_lock)
        {
            if (_starting) return true;
            _starting = true;
            _stopRequested = false;
            generation = _spawnGeneration;
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (OperatingSystem.IsWindows()) SpawnHostBlocking(hostPath, generation);
        });
        return true;
    }

    [SupportedOSPlatform("windows")]
    private void SpawnHostBlocking(string hostPath, int generation)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(hostPath)!;
            var proc = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem
                ? StartInActiveUserSession(hostPath, workingDir)
                : Process.Start(new ProcessStartInfo
                {
                    FileName = hostPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir,
                });
            if (proc is null) return;
            lock (_lock)
            {
                if (_stopRequested || generation != _spawnGeneration)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    try { proc.Dispose(); } catch { }
                    return;
                }
                _process = proc;
                WritePidFile(proc.Id);
                proc.EnableRaisingEvents = true;
                proc.Exited += OnExited;
                if (_jobHandle != IntPtr.Zero)
                {
                    try { AssignProcessToJobObject(_jobHandle, proc.Handle); }
                    catch (Exception ex) { Console.Error.WriteLine($"[overlay-host] job-object assign failed: {ex.Message}"); }
                }
            }
            Console.WriteLine($"[overlay-host] started pid {proc.Id}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-host] failed to start: {ex.Message}");
        }
        finally
        {
            lock (_lock) { if (generation == _spawnGeneration) _starting = false; }
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        lock (_lock)
        {
            _spawnGeneration++;
            _starting = false;
        }
        var proc = _process;
        _process = null;
        if (proc is not null)
        {
            try { proc.Exited -= OnExited; } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                // Access denied / process already gone / job-object death
                // races us. Log so a leaked host shows up in service logs
                // and the next reconcile pass can deal with it.
                Console.Error.WriteLine($"[overlay-host] kill failed: {ex.Message}");
            }
            try { proc.Dispose(); } catch { }
        }
        // The in-memory handle is null after a crash-respawn cycle or when a
        // prior service instance spawned the live host, so also kill whatever
        // PID we last tracked - shutdown must reliably reap nexus-overlay.exe,
        // not just the host this instance happens to hold a Process for.
        KillTrackedHost();
        try { DeletePidFile(); } catch { }
    }

    /// <summary>
    /// Kill any orphaned host process from a previous crash. Called on
    /// service startup before spawning a new instance. Mirrors
    /// <see cref="PanelKioskLauncher.CleanupOrphans"/>.
    /// </summary>
    public static void CleanupOrphans()
    {
        KillTrackedHost();
        try { DeletePidFile(); } catch { }
        if (OperatingSystem.IsWindows()) Nexus.Service.Lifecycle.OobeSessionLaunch.ReapLeftovers();
    }

    /// <summary>
    /// Kill the nexus-overlay.exe recorded in the PID file if it is still
    /// alive. The ProcessName guard rejects a recycled PID. Shared by the
    /// startup orphan sweep and shutdown; the PID file is deleted by the
    /// callers, not here.
    /// </summary>
    private static void KillTrackedHost()
    {
        try
        {
            if (!File.Exists(PidFilePath)) return;
            var text = File.ReadAllText(PidFilePath).Trim();
            if (!int.TryParse(text, out var pid)) return;
            var proc = Process.GetProcessById(pid);
            if (proc.ProcessName.Contains("nexus-overlay", StringComparison.OrdinalIgnoreCase))
            {
                proc.Kill(entireProcessTree: true);
                Console.WriteLine($"[overlay-host] killed tracked host (pid {pid})");
            }
            proc.Dispose();
        }
        catch { /* no pid file / already gone / recycled pid */ }
    }

    // -----------------------------------------------------------------------
    // Cross-session spawn: when the service runs as LocalSystem in Session 0,
    // we need to land the overlay in the user's interactive session so its
    // windows actually render on the desktop. Implementation uses schtasks
    // (see comment inside StartInActiveUserSession for why).
    // -----------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static Process? StartInActiveUserSession(string exePath, string workingDir)
    {
        // Use schtasks instead of CreateProcessAsUser / CreateProcessWithTokenW
        // for cross-session spawning. Direct Win32 paths repeatedly hit
        // STATUS_DLL_INIT_FAILED (0xC0000142) and ERROR_INVALID_PARAMETER (87)
        // because the user's profile + window-station / desktop ACLs need
        // bespoke setup the Task Scheduler service already handles for us.
        // Tradeoff: we lose direct parent-child handle tracking, but we
        // recover the PID by polling nexus-overlay.exe after Run completes.
        var username = ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            // Only while Windows is in OOBE with nobody signed in; Start() has already gated on it.
            if (Nexus.Service.Lifecycle.OobePanelPolicy.Decide(
                    username, Nexus.Service.Lifecycle.OobeSessionLaunch.ConsoleSessionExists(),
                    Nexus.Service.Lifecycle.OobeDetector.IsInOobe) == Nexus.Service.Lifecycle.ConsoleLaunchPlan.OobeNoUser)
            {
                return Nexus.Service.Lifecycle.OobeSessionLaunch.Start(exePath, string.Empty, workingDir, "overlay");
            }
            Console.Error.WriteLine("[overlay-host] no active console user; deferring");
            return null;
        }
        // Unique task name so concurrent spawns or stale tasks don't collide.
        var taskName = $"{LaunchTaskPrefix}{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        try
        {
            // /F overwrites if it collides. The XML form is trigger-less, but
            // the schedule-type fallback below carries a real ONCE trigger, so a
            // leftover of that form does fire once and launch the overlay
            // unprompted - SweepStaleLaunchTasks is what bounds it.
            if (!CreateTask(taskName, username, exePath))
            {
                return null;
            }
            // Snapshot existing nexus-overlay PIDs before /Run so we can detect
            // the new one by set difference.
            var before = Process.GetProcessesByName("nexus-overlay").Select(p => p.Id).ToHashSet();
            if (!Schtasks("/Run", "/TN", taskName))
            {
                return null;
            }
            // The task creates the process asynchronously; poll briefly.
            Process? spawned = null;
            for (var attempt = 0; attempt < 30 && spawned is null; attempt++)
            {
                System.Threading.Thread.Sleep(100);
                foreach (var p in Process.GetProcessesByName("nexus-overlay"))
                {
                    if (!before.Contains(p.Id)) { spawned = p; break; }
                    p.Dispose();
                }
            }
            if (spawned is null)
            {
                Console.Error.WriteLine("[overlay-host] schtasks /Run did not produce a nexus-overlay process");
                return null;
            }
            Console.WriteLine($"[overlay-host] spawned in user session via schtasks pid {spawned.Id}");
            return spawned;
        }
        finally
        {
            // Cannot run when the service is force-killed or crashes, which is
            // why the sweep exists.
            Schtasks("/Delete", "/TN", taskName, "/F");
        }
    }

    private const string LaunchTaskPrefix = "NexusOverlayLaunch_";
    private static bool _sweptStaleTasks;

    /// <summary>Deletes launch tasks left by a process whose delete never ran; runs at service start, since a user with no widgets and no panel never spawns an overlay and it is their leftover ONCE-trigger tasks that keep launching one.</summary>
    [SupportedOSPlatform("windows")]
    internal static void SweepStaleLaunchTasks()
    {
        if (_sweptStaleTasks)
        {
            return;
        }
        _sweptStaleTasks = true;

        foreach (var name in QueryLaunchTaskNames())
        {
            // A task whose owning process is still alive may be mid-spawn -
            // this one's own, or another Nexus instance's on a lab box.
            if (OwnerProcessAlive(name))
            {
                continue;
            }
            Schtasks("/Delete", "/TN", name, "/F");
        }
    }

    /// <summary>Whether the pid embedded in a launch task name is still running.</summary>
    private static bool OwnerProcessAlive(string taskName)
    {
        var rest = taskName.Substring(LaunchTaskPrefix.Length);
        var cut = rest.IndexOf('_');
        if (cut <= 0 || !int.TryParse(rest.AsSpan(0, cut), out var pid))
        {
            // An unparseable name is not ours to reason about; leave it.
            return true;
        }
        if (pid == Environment.ProcessId)
        {
            return true;
        }
        try
        {
            using var owner = Process.GetProcessById(pid);
            return !owner.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Cannot tell; deleting could strand a live spawn, so do not.
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> QueryLaunchTaskNames()
    {
        var names = new List<string>();
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "/Query", "/FO", "CSV", "/NH" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return names;
            // Both streams are drained concurrently: schtasks prints a line per
            // unreadable task, and a full stderr pipe would block the child
            // forever while this waits on stdout.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                Console.Error.WriteLine("[overlay-host] stale-task query timed out");
                return names;
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();
            foreach (var line in stdout.Split('\n'))
            {
                // CSV row: "\TaskName","Next Run Time","Status"
                var start = line.IndexOf('"');
                if (start < 0) continue;
                var end = line.IndexOf('"', start + 1);
                if (end <= start) continue;
                var name = line.Substring(start + 1, end - start - 1).TrimStart('\\');
                if (name.StartsWith(LaunchTaskPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-host] stale-task query failed: {ex.Message}");
        }
        return names;
    }

    private static string ResolveActiveConsoleUsername()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return string.Empty;
        var buf = IntPtr.Zero;
        try
        {
            // WTSUserName = 5
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5, out buf, out var bytes) || buf == IntPtr.Zero)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    // Registers taskName from a trigger-less task XML, falling back to the
    // schedule-type command line when the host rejects it.
    [SupportedOSPlatform("windows")]
    private static bool CreateTask(string taskName, string username, string exePath)
    {
        var (userId, runAs) = Nexus.Service.Lifecycle.UserSessionTaskXml.TaskPrincipal(
            username, Environment.MachineName, Nexus.Service.Lifecycle.ConsoleSessionAccount.TryResolve);
        try
        {
            var xmlPath = Nexus.Service.Lifecycle.UserSessionTaskXml.WriteTempFile(
                Nexus.Service.Lifecycle.UserSessionTaskXml.Build(userId, $"\"{exePath}\""));
            try
            {
                if (Schtasks("/Create", "/TN", taskName, "/XML", xmlPath, "/F"))
                {
                    return true;
                }
            }
            finally
            {
                try { System.IO.File.Delete(xmlPath); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-host] XML registration threw: {ex.Message}");
        }

        Console.Error.WriteLine("[overlay-host] XML registration failed, using schedule-type form");
        return Schtasks("/Create", "/TN", taskName, "/TR", $"\"{exePath}\"",
                        "/SC", "ONCE", "/ST", "00:00", "/RU", runAs, "/IT", "/F");
    }

    private static bool Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"[overlay-host] schtasks {args[0]} exit {p.ExitCode}: {stderr.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[overlay-host] schtasks {args[0]} failed: {ex.Message}");
            return false;
        }
    }

    private static string? ResolveHostPath()
    {
        // AppContext.BaseDirectory is single-file-safe and AOT-safe; both
        // Process.MainModule.FileName and Assembly.Location have edge cases
        // under publish modes we use.
        var serviceDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(serviceDir)) return null;
        return Path.Combine(serviceDir, "overlay", "nexus-overlay.exe");
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var exited = sender as Process;
        var exitCode = "?";
        try { if (exited is not null) exitCode = exited.ExitCode.ToString(); }
        catch { }
        Console.WriteLine($"[overlay-host] exited (code {exitCode})");
        lock (_lock)
        {
            if (ReferenceEquals(exited, _process)) _process = null;
        }
    }

    private static void WritePidFile(int pid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            File.WriteAllText(PidFilePath, pid.ToString());
        }
        catch { }
    }

    private static void DeletePidFile()
    {
        try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { }
    }

    // ── Windows Job Object plumbing ──
    // CreateJobObject + JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE makes every
    // process assigned to the job die when this handle closes (= when
    // Nexus.exe exits, including taskkill /F or hard crash).

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    // ── Cross-session spawn plumbing (just enough to identify the active user) ──

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateChildKillJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        return handle;
    }
}
