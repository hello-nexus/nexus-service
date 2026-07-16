using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Panel;

/// <summary>
/// Spawns and supervises nexus-overlay.exe, the WebView2 host that
/// renders floating widgets on the Windows desktop. The host lives in the
/// `overlay/` subdirectory next to the service exe; that layout is enforced
/// by the PublishOverlayHost MSBuild target. The overlay is a raw-Win32 +
/// direct WebView2 C-API P/Invoke binary published with PublishAot=true -
/// the only files in overlay/ are nexus-overlay.exe and WebView2Loader.dll.
///
/// Lifecycle:
/// - Started at service boot when <c>UiSettings.OverlayWidgetsEnabled</c>
///   is true (set automatically the first time the user pins a widget).
/// - Restarted on crash with simple linear backoff.
/// - Killed on service shutdown.
/// </summary>
public sealed class PanelOverlayHostLauncher : IOverlayHost
{
    /// <summary>
    /// Always-on-top is pushed to the Windows sidecar via the SPA WebMessage
    /// bridge and via a 5s prefs poll. The service-side abstraction is a
    /// no-op so tray and reconcile callers can target IOverlayHost uniformly.
    /// </summary>
    public void SetAlwaysOnTop(bool value) { }

    /// <summary>No-op: nexus-overlay re-polls assignments on the PrefsChanged push.</summary>
    public void NotifyDisplayAssignmentsChanged() { }

    private Process? _process;
    private DateTime _lastSpawnUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private readonly IntPtr _jobHandle = IntPtr.Zero;
    private readonly object _lock = new();
    /// <summary>
    /// Stop() sets this true so a queued OnExited callback - already on
    /// the threadpool when Stop ran - won't respawn the host. Re-armed on
    /// the next Start().
    /// </summary>
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
    /// <summary>
    /// Bumped (under _lock) by every Start() and Stop(). An in-flight spawn
    /// task carries the generation it was started with and abandons itself
    /// when a newer Start/Stop has bumped it - otherwise a Stop-then-Start
    /// during the console-user wait would revive the ordered-dead loop and
    /// two spawn tasks would race to write _process and the PID file.
    /// </summary>
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

    public bool IsRunning => _process is { HasExited: false } || _starting;

    /// <summary>
    /// Request the host process be started if it isn't already running or
    /// being started. Non-blocking: the actual schtasks dance runs on a
    /// background Task so the HTTP request thread that triggered the
    /// reconcile (profile switch, widget add, etc.) doesn't wait for
    /// Task Scheduler to materialize the new process. Returns true if a
    /// spawn was requested or one was already in flight.
    /// </summary>
    public bool Start()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (IsRunning) return true;

        var hostPath = ResolveHostPath();
        if (hostPath is null || !File.Exists(hostPath))
        {
            Console.Error.WriteLine($"[overlay-host] nexus-overlay.exe not found at expected path '{hostPath ?? "<null>"}'; the PublishOverlayHost target must populate <publish>/overlay/. Desktop widgets disabled.");
            return false;
        }

        // Dedupe concurrent Start calls. A reconcile that fires Start()
        // back-to-back (e.g., the widget-add path that flips both
        // OverlayLayout and OverlayWidgetsEnabled in the same Update)
        // would otherwise queue multiple background tasks.
        int generation;
        lock (_lock)
        {
            if (_starting) return true;
            if (IsRunning) return true;
            _starting = true;
            _stopRequested = false;
            _lastSpawnUtc = DateTime.UtcNow;
            generation = ++_spawnGeneration;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            SpawnHostBlocking(hostPath, generation);
        });
        return true;
    }

    /// <summary>
    /// The actual spawn path - blocking. Always runs on a background
    /// thread via <see cref="Start"/>. Sets <c>_process</c> on success,
    /// wires the kill-on-close job + exit handler, and clears
    /// <c>_starting</c> before returning so that an OnExited-triggered
    /// respawn (process died immediately after spawn) doesn't see a
    /// stale "starting in progress" flag and short-circuit.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void SpawnHostBlocking(string hostPath, int generation)
    {
        try
        {
            // Spawn in the active console user session when we're running
            // as LocalSystem (the service case). Without this the overlay
            // process lands in Session 0, where (a) no windows it draws are
            // ever visible to the user, and (b) WebView2 refuses to use the
            // SYSTEM profile's AppData path. When running in user mode
            // (e.g., a future broker split, or dev / standalone runs), fall
            // back to a plain Process.Start.
            var workingDir = Path.GetDirectoryName(hostPath)!;
            var proc = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem
                ? StartInActiveUserSessionWhenReady(hostPath, workingDir, generation)
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
                // A Stop() (or a Stop-then-Start) can land mid-spawn - the
                // cross-session dance can block on the console-user wait.
                // A superseded task must not adopt the process it spawned:
                // kill it instead of clobbering the current generation's
                // _process / PID file.
                if (_stopRequested || generation != _spawnGeneration)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* gone */ }
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
            // Clear before returning so a same-thread OnExited that
            // synchronously queued its respawn Task (with 2s delay) sees
            // _starting=false by the time the respawn runs. A superseded
            // task leaves the flag alone - it belongs to the newer spawn.
            lock (_lock)
            {
                if (generation == _spawnGeneration) _starting = false;
            }
        }
    }

    public void Stop()
    {
        // Latch the stop intent BEFORE attempting Kill so that a queued
        // OnExited (already on the threadpool) sees it and skips respawn.
        _stopRequested = true;
        lock (_lock)
        {
            // Supersede any in-flight spawn task so it abandons itself even
            // if a later Start() clears _stopRequested.
            _spawnGeneration++;
            // Also clear the spawn-in-flight flag; otherwise IsRunning would
            // keep returning true until the background SpawnHostBlocking
            // finishes, blocking a subsequent Start() during a quick stop /
            // re-enable cycle.
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

    /// <summary>
    /// On a cold boot the service's ApplicationStarted fires before the
    /// auto-login console session exists, so a cross-session spawn has no
    /// launch target yet. Wait for the console session first, the same
    /// shape as UserHelperBootstrapper.EnsureLaunched. Runs on the Start()
    /// background task; a Stop() or superseding Start() abandons the wait.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private Process? StartInActiveUserSessionWhenReady(string exePath, string workingDir, int generation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var waitLogged = false;
        while (!_stopRequested && generation == Volatile.Read(ref _spawnGeneration))
        {
            if (!string.IsNullOrEmpty(ResolveActiveConsoleUsername()))
            {
                return StartInActiveUserSession(exePath, workingDir);
            }
            if (!waitLogged)
            {
                waitLogged = true;
                Console.WriteLine("[overlay-host] no active console user; waiting for logon");
            }
            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine("[overlay-host] no active console user after 5 min; giving up");
                return null;
            }
            Thread.Sleep(2000);
        }
        return null;
    }

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
            Console.Error.WriteLine("[overlay-host] no active console user; deferring");
            return null;
        }
        // Unique task name so concurrent spawns or stale tasks don't collide.
        var taskName = $"NexusOverlayLaunch_{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        try
        {
            // /IT = interactive. /SC ONCE + an already-past /ST 00:00 so the task
            // only ever fires from our explicit /Run: a leftover task (if the
            // /Delete below fails) has a spent trigger and cannot auto-run on a
            // wall clock. /F overwrites if collides.
            if (!Schtasks("/Create", "/TN", taskName, "/TR", $"\"{exePath}\"",
                          "/SC", "ONCE", "/ST", "00:00", "/RU", username, "/IT", "/F"))
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
            // Best-effort cleanup - leaves no schtasks residue.
            Schtasks("/Delete", "/TN", taskName, "/F");
        }
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
        int? rawExitCode = null;
        try { if (exited is not null) { rawExitCode = exited.ExitCode; exitCode = rawExitCode.Value.ToString(); } }
        catch { /* handle already gone */ }
        Console.WriteLine($"[overlay-host] OnExited fired; exit code {exitCode}");

        // Stop() was called; this callback was already in flight on the
        // threadpool when Stop ran (handler unsubscribe doesn't drain
        // pending invocations). Don't respawn.
        if (_stopRequested) return;

        lock (_lock)
        {
            // A stale callback from a superseded process (a Stop-then-Start
            // adopted a newer host while this one's Exited was already
            // queued) must not detach or respawn over the current
            // generation's process.
            if (!ReferenceEquals(exited, _process)) return;
            _process = null;
        }

        // Exit code 0 = deliberate self-shutdown (overlay idled out: no
        // widgets, no dashboard). The reconcile and the tray's
        // EnsureOverlayRunning re-spawn the host when something actually
        // needs it again, so we deliberately do NOT restart here.
        if (rawExitCode == 0)
        {
            Console.WriteLine("[overlay-host] clean exit; not respawning");
            _consecutiveFailures = 0;
            return;
        }

        // Linear backoff cap: don't restart more than 3 times in a row
        // within 30s. Prevents tight crash-loop hammering.
        var since = DateTime.UtcNow - _lastSpawnUtc;
        if (since < TimeSpan.FromSeconds(30))
        {
            _consecutiveFailures++;
            if (_consecutiveFailures > 3)
            {
                Console.Error.WriteLine($"[overlay-host] giving up after {_consecutiveFailures} rapid failures (last exit code {exitCode})");
                return;
            }
        }
        else
        {
            _consecutiveFailures = 0;
        }
        Console.WriteLine($"[overlay-host] exited (code {exitCode}); restarting");
        // Give the host a moment before respawning. Run on the default
        // scheduler with explicit error handling so a Start() throw is
        // reported instead of disappearing into an unobserved task.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(2000);
                if (_stopRequested) return;
                Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[overlay-host] respawn task failed: {ex.Message}");
            }
        });
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
