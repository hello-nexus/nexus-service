using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Factory reset: wipe every Nexus data directory and restart the service from
/// a clean slate. Runs in two halves so no live file handle blocks the wipe:
///
///   1. <see cref="Begin"/> - called inside the running service from
///      POST /service/factory-reset. Spawns a detached copy of this binary as
///      <c>--factory-reset-finalize &lt;pid&gt;</c>; the caller then stops the
///      service (StopApplication).
///   2. <see cref="Finalize"/> - the detached child. Waits for the old process
///      to exit (so Windows file locks are released), deletes the data roots,
///      then restarts the service via the platform service manager
///      (sc / launchctl / systemctl), falling back to re-exec of the binary.
///
/// The child is spawned by the live service, so it inherits the service's
/// identity and environment - including the user-session HOME/XDG_* the Linux
/// daemon adopts at startup - which is exactly why its path resolution lands on
/// the same directories the individual stores wrote to.
///
/// The install/binary dir is never listed: it holds the running exe plus the
/// bundled widgets and firmware. On Windows the PawnIO kernel-driver subfolder
/// is preserved (it is re-extracted on boot anyway; no need to churn a loaded
/// driver). The local HTTPS cert IS wiped, so the service mints a fresh one on
/// next boot - a true clean-slate identity.
/// </summary>
internal static class FactoryReset
{
    public const string FinalizeFlag = "--factory-reset-finalize";
    // Same two-phase mechanism, but restart-only (no data wipe). Used by
    // POST /service/restart to apply restart-to-apply settings like the
    // lighting render-GPU choice.
    public const string RestartFlag = "--restart-finalize";

    // A data tree to wipe, plus the names of any immediate children to skip.
    private sealed record Root(string Path, string[] Preserve, bool PerUser = false);

    /// Entries under %ProgramData%\Nexus a wipe leaves alone: the kernel driver,
    /// and the remembered render GPU (see Roots).
    internal static readonly string[] PreservedProgramDataEntries = { "PawnIO", "gpu-render-state" };

    private static string UserPath(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    /// Every directory tree Nexus writes user data into, per OS. Mirrors the
    /// path resolvers in JsonConfigStore / MediaLibrary / AppInstallPaths /
    /// ServiceLog / FirmwareStore / NexusDataPaths / LocalHttpsCertificate.
    private static List<Root> Roots()
    {
        var roots = new List<Root>();

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            // %ProgramData%\Nexus: settings, profiles, db/ (metrics/screentime/
            // ai-history binary stores), devices/ (device media + records),
            // media/ (lighting content), drivers/, firmware,
            // logs (nexus-service/overlay/tray/helper/volume/gpu/pawnio),
            // https cert, ffmpeg-pids, openrgb-config, the legacy DesktopWebView2. Whole-tree
            // wipe, so the grouped subdirs need no per-name upkeep here.
            // Preserve PawnIO\ (kernel driver) and gpu-render-state (which card
            // yields a working GL context - a hardware fact, not user data; a
            // wrong one still self-corrects through the crash guard and the
            // re-probe timer, while wiping it costs every reset a fresh probe).
            roots.Add(new Root(System.IO.Path.Combine(programData, "Nexus"), PreservedProgramDataEntries));
            // Per-user data the daemon can't reach via GetFolderPath: it runs as
            // LocalSystem, so LocalApplicationData resolves to the SYSTEM profile.
            // dashboard-bounds.json (written by the user-session overlay) lands in
            // a real user's Local\Nexus, as does the per-user DesktopWebView2
            // profile (localStorage, cached auth token); current SDK-app installs go to
            // %ProgramData%\Nexus\apps (wiped above), but older installs may
            // linger in a profile's Roaming\Nexus. WindowsUserProfiles() returns
            // BOTH every C:\Users\* profile AND the system profile, so each one's
            // AppData\{Local,Roaming,LocalLow}\Nexus
            // gets wiped.
            foreach (var profile in WindowsUserProfiles())
            {
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "Local", "Nexus"), Array.Empty<string>(), PerUser: true));
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "Roaming", "Nexus"), Array.Empty<string>(), PerUser: true));
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "LocalLow", "Nexus"), Array.Empty<string>(), PerUser: true));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            roots.Add(new Root(UserPath("Library", "Application Support", "Nexus"), Array.Empty<string>()));
            roots.Add(new Root(UserPath("Library", "Logs", "Nexus"), Array.Empty<string>()));
            roots.Add(new Root(XdgRoot("XDG_CACHE_HOME", ".cache"), Array.Empty<string>())); // firmware
        }
        else
        {
            // Both Linux layouts, unconditionally. The finalizer is a detached
            // child that returns from CommandLineEntry.TryEarlyExit before
            // AdoptActiveSessionEnv ever runs, so it cannot ask whether this
            // install is the root daemon - branching here would wipe one layout
            // and leave the live store, auth token and all, fully intact.
            roots.Add(new Root(Persistence.NexusDataPaths.LinuxSystemRoot, Array.Empty<string>()));
            // Linux splits user data across the XDG base dirs.
            roots.Add(new Root(XdgRoot("XDG_CONFIG_HOME", ".config"), Array.Empty<string>()));    // settings, db/ (metrics/screentime/ai-history), cert, ffmpeg-pids
            roots.Add(new Root(XdgRoot("XDG_DATA_HOME", ".local", "share"), Array.Empty<string>())); // devices, media, widgets
            roots.Add(new Root(XdgRoot("XDG_CACHE_HOME", ".cache"), Array.Empty<string>()));      // firmware, drivers
            // logs: ~/.local/state/nexus/logs (lowercase, no XDG override in
            // ServiceLog). The daemon branch above covers its own logs/ dir.
            roots.Add(new Root(UserPath(".local", "state", "nexus"), Array.Empty<string>()));
        }
        return roots;
    }

    // <env>/Nexus if the XDG var is set, else <home>/<fallback…>/Nexus.
    private static string XdgRoot(string env, params string[] fallback)
    {
        var v = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrEmpty(v)) return System.IO.Path.Combine(v, "Nexus");
        var parts = new string[fallback.Length + 1];
        Array.Copy(fallback, parts, fallback.Length);
        parts[^1] = "Nexus";
        return UserPath(parts);
    }

    // Every user profile on the machine (C:\Users\* plus the LocalSystem
    // profile), so the LocalSystem finalizer can wipe per-user AppData\Nexus.
    internal static IEnumerable<string> WindowsUserProfiles()
    {
        var profiles = new List<string>();
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var usersDir = System.IO.Path.Combine(systemDrive + "\\", "Users");
        try
        {
            if (Directory.Exists(usersDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(usersDir))
                {
                    if (System.IO.Path.GetFileName(dir).Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;
                    profiles.Add(dir);
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[factory-reset] enum user profiles failed: {ex.Message}"); }
        profiles.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return profiles;
    }

    /// Spawn the detached finalizer and return. The caller stops the service
    /// next; the finalizer waits for that, optionally wipes, and restarts.
    /// <paramref name="wipe"/> false = plain restart (no data wipe).
    /// Returns false without spawning when a finalizer was already claimed by
    /// this process: two live finalizers are both same-path kill targets of
    /// each other's macOS survivor sweep, and a near-simultaneous mutual
    /// SIGKILL would leave nothing to run RestartService.
    public static bool Begin(bool wipe = true, string? reopenRoute = null)
    {
        if (Interlocked.Exchange(ref _finalizerClaimed, 1) != 0)
        {
            Nexus.Service.Platform.ServiceLog.Warn("[factory-reset] finalizer already claimed; ignoring repeat request");
            return false;
        }
#if WINDOWS
        // We are about to stop a process that may be mid-GL-init, and the guard
        // it left behind would read as "this card killed us" on the next boot,
        // demoting a working card into a fresh probe.
        Nexus.Service.Lighting.Engine.Gpu.GpuRenderSelect.ClearCrashGuardOnStop();
#endif
        var selfExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfExe))
        {
            Console.Error.WriteLine("[factory-reset] cannot resolve own exe path; aborting");
            Interlocked.Exchange(ref _finalizerClaimed, 0);
            return false;
        }
        var psi = new ProcessStartInfo
        {
            FileName = selfExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(wipe ? FinalizeFlag : RestartFlag);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        if (ReopenRoute.Validate(reopenRoute) is { } route)
        {
            psi.ArgumentList.Add(route);
        }
        try
        {
            Process.Start(psi);
            Nexus.Service.Platform.ServiceLog.Info("[factory-reset] finalizer spawned");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[factory-reset] failed to spawn finalizer: {ex.Message}");
            // Release so a later retry is possible; nothing was spawned.
            Interlocked.Exchange(ref _finalizerClaimed, 0);
            return false;
        }
    }

    private static int _finalizerClaimed;

    internal static void ResetFinalizerClaimForTests() => Interlocked.Exchange(ref _finalizerClaimed, 0);

    internal static void ClaimFinalizerForTests() => Interlocked.Exchange(ref _finalizerClaimed, 1);

    /// True once this process has successfully spawned a finalizer.
    internal static bool FinalizerClaimed => Interlocked.CompareExchange(ref _finalizerClaimed, 0, 0) != 0;

#if !WINDOWS
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int setsid();
#endif

    /// Detached child entry point (argv[0] == --factory-reset-finalize or
    /// --restart-finalize). The restart variant skips the data wipe.
    public static int Finalize(string[] args)
    {
        var wipe = args.Length > 0 && args[0] == FinalizeFlag;
        var parentPid = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 0;
        var reopenRoute = args.Length > 2 ? ReopenRoute.Validate(args[2]) : null;
#if !WINDOWS
        // Leave the service's session/process group before anything else, so
        // tearing the service down can't take this finalizer with it mid-wipe:
        // `launchctl bootout` (macOS) signals the whole job process group, and a
        // systemd cgroup stop (Linux) does the same. setsid() makes us a new
        // session leader; it fails harmlessly if we're already a group leader.
        setsid();
#endif
        Console.Error.WriteLine($"[factory-reset] finalize: waiting for pid {parentPid} to exit");
#if MACOS
        // The parent frequently survives StopApplication (the NSApplication shell stays resident) and the
        // kill below handles it, so a long wait only stalls the relaunch.
        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(5));
#else
        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));
#endif

        // macOS only: launchd would relaunch the service the instant it exits
        // (KeepAlive=true), and that new instance could flush its in-memory
        // (old) settings back to disk after we delete, or steal the
        // single-instance handshake from the relaunch below. bootout tears the
        // agent down AND waits for the process to exit; the survivor kill is
        // the backstop for an instance that outlived StopApplication. The OTA
        // swap (UnixUpdateApplier.FinalizeMacSwap) runs the same two steps.
        // No-op when the service was started manually (dev).
#if MACOS
        BootoutMacAgent();
        KillOtherInstancesOfThisBinary();
#endif

#if WINDOWS
        // The user-session helper/tray and the overlay survive a plain service stop/start. The OTA install
        // kills them before it swaps files, and the next boot's UserHelperBootstrapper.EnsureLaunched spawns
        // fresh ones; a restart does the same so it is the same full shutdown and start. A wipe also needs
        // them gone first, since they hold the %LocalAppData%\Nexus WebView2 profile dirs it deletes.
        SweepUserSessionProcesses();
#endif

        if (wipe)
        {
            // Nothing in the wipe may skip RestartService below, or the service stays down until reboot.
            try { DeleteRoots(); }
            catch (Exception ex) { Console.Error.WriteLine($"[factory-reset] wipe aborted: {ex.Message}"); }
        }
#if WINDOWS
        // The reopen flag lives under %ProgramData%\Nexus, so it must be written after the wipe or
        // DeleteRoots would remove it immediately. WindowsUserHelper.Run checks for it on every helper
        // startup - the fresh helper EnsureLaunched spawns after RestartService below picks it up and
        // reopens a cold dashboard once the overlay it also starts is ready. The OTA install uses the
        // same flag.
        try { DashboardReopenFlag.Write(reopenRoute); }
        catch (Exception ex) { Console.Error.WriteLine($"[factory-reset] reopen flag failed: {ex.Message}"); }
#endif
#if MACOS
        if (reopenRoute is not null)
        {
            ReopenRoute.WriteFile(reopenRoute);
        }
#endif
        RestartService();
        return 0;
    }

#if WINDOWS
    /// <summary>Kills the overlay and every other own Nexus.exe (helper, tray), by pid so another product's Nexus.exe survives.</summary>
    private static void SweepUserSessionProcesses()
    {
        ShellExecutor.RunExit("taskkill.exe", ShellExecutor.DefaultTimeoutMs,
            "/F", "/T", "/IM", "nexus-overlay.exe");
        // Never let the sweep abort Finalize: a throw here would skip the wipe and the service restart.
        try
        {
            var pids = new List<int>();
            foreach (var sibling in OwnNexusExe.Siblings(OwnNexusExe.Known()))
            {
                pids.Add(sibling.Id);
                sibling.Dispose();
            }
            var args = SiblingKillArgs(pids);
            if (args.Length > 1)
            {
                ShellExecutor.RunExit("taskkill.exe", ShellExecutor.DefaultTimeoutMs, args);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[factory-reset] Nexus.exe sweep failed: {ex.Message}");
        }
    }
#endif

    /// <summary>taskkill arguments forcing exactly <paramref name="pids"/>; excludes this finalizer's own pid, which also runs as Nexus.exe.</summary>
    internal static string[] SiblingKillArgs(IEnumerable<int> pids)
    {
        var args = new List<string> { "/F" };
        foreach (var pid in pids)
        {
            if (pid == Environment.ProcessId)
            {
                continue;
            }
            args.Add("/PID");
            args.Add(pid.ToString());
        }
        return args.ToArray();
    }

    internal static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex) { Console.Error.WriteLine($"[factory-reset] wait failed: {ex.Message}"); }
    }

    /// <summary>Removes the .bak before settings.json so a partial wipe never leaves a backup that the next boot would restore.</summary>
    internal static void DeleteSettingsFiles(string rootPath)
    {
        var settings = System.IO.Path.Combine(rootPath, "settings.json");
        foreach (var file in new[] { Nexus.Service.Persistence.AtomicJsonFile.BackupPathFor(settings), settings })
        {
            try { File.Delete(file); } catch { /* the directory wipe retries it */ }
        }
    }

    private static void DeleteRoots()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root.Path)) continue;
            if (root.PerUser)
            {
                // The finalizer runs as SYSTEM on folders a user controls: a junction in the way would
                // redirect the delete, so such roots are skipped (static junctions only; see UserDataSafety).
                // IsSafeUserTree never throws, so one hostile root cannot abort the sweep.
                if (!UserDataSafety.IsSafeUserTree(root.Path))
                {
                    Console.Error.WriteLine($"[factory-reset] skipping {root.Path}: unreadable or behind a reparse point");
                    continue;
                }
            }
            DeleteSettingsFiles(root.Path);
            try
            {
                if (root.Preserve.Length == 0)
                {
                    Directory.Delete(root.Path, recursive: true);
                }
                else
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(root.Path))
                    {
                        var name = System.IO.Path.GetFileName(entry);
                        if (root.Preserve.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                }
                Console.Error.WriteLine($"[factory-reset] wiped {root.Path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[factory-reset] could not wipe {root.Path}: {ex.Message}");
            }
        }
    }

#if MACOS
    /// SIGKILL (no flush, no teardown) every other live process running this
    /// exact binary. Same-path match only, so a differently-located dev build
    /// is left alone.
    internal static void KillOtherInstancesOfThisBinary()
    {
        // Never let the sweep abort Finalize: after BootoutMacAgent a throw
        // here would strand the agent booted out with no RestartService.
        try
        {
            var selfPath = Environment.ProcessPath ?? "";
            if (selfPath.Length == 0) return;
            foreach (var proc in Process.GetProcessesByName("Nexus"))
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    if (!string.Equals(OwnNexusExe.ImagePathOf(proc.Id), selfPath, StringComparison.Ordinal)) continue;
                    proc.Kill();
                    Console.Error.WriteLine($"[factory-reset] killed surviving instance pid {proc.Id}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[factory-reset] survivor check pid {proc.Id}: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[factory-reset] survivor sweep failed: {ex.Message}");
        }
    }

    internal static void BootoutMacAgent()
    {
        var uid = ShellExecutor.Run("/usr/bin/id", "-u").Trim();
        if (uid.Length == 0)
        {
            Console.Error.WriteLine("[factory-reset] could not resolve uid; skipping launchd bootout");
            return;
        }
        ShellExecutor.RunExit("/bin/launchctl", ShellExecutor.DefaultTimeoutMs,
            "bootout", $"gui/{uid}/{MacStartupProvider.PlistLabel}");
    }
#endif

    private static void RestartService()
    {
#if WINDOWS
        // SCM owns the daemon. StopApplication left it STOPPED - a clean stop,
        // not a failure, so the sc.exe recovery policy didn't fire. Start it
        // again, retrying while SCM transitions out of STOP_PENDING.
        for (var i = 0; i < 10; i++)
        {
            if (ShellExecutor.RunExit("sc.exe", ShellExecutor.DefaultTimeoutMs,
                    "start", WindowsServiceInstaller.ServiceName) == 0)
            {
                return;
            }
            System.Threading.Thread.Sleep(500);
        }
        Console.Error.WriteLine("[factory-reset] sc start failed after retries");
#elif MACOS
        var uid = ShellExecutor.Run("/usr/bin/id", "-u").Trim();
        if (uid.Length > 0 && ShellExecutor.RunExit("/bin/launchctl", ShellExecutor.DefaultTimeoutMs,
                "bootstrap", $"gui/{uid}", MacStartupProvider.PlistPath) == 0)
        {
            return;
        }
        // Fallback (started manually, no launchd agent): re-exec the binary.
        ReExec();
#elif LINUX
        if (ShellExecutor.RunExit("systemctl", ShellExecutor.DefaultTimeoutMs,
                "restart", LinuxStartupProvider.UnitName) == 0)
        {
            return;
        }
        // Fallback (dev --user run, no installed unit): re-exec the binary.
        ReExec();
#else
        ReExec();
#endif
    }

#if !WINDOWS
    private static void ReExec()
    {
        var selfExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfExe))
        {
            Console.Error.WriteLine("[factory-reset] cannot re-exec: own exe path unknown");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = selfExe,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Console.Error.WriteLine("[factory-reset] re-exec fallback launched");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[factory-reset] re-exec failed: {ex.Message}");
        }
    }
#endif
}
