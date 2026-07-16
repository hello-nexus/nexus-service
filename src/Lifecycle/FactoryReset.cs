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
    private sealed record Root(string Path, string[] Preserve);

    private static string UserPath(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    /// Every directory tree Nexus writes user data into, per OS. Mirrors the
    /// path resolvers in JsonConfigStore / MediaLibrary / AppInstallPaths /
    /// ServiceLog / FirmwareStore / SqliteScreenTimeStore / LocalHttpsCertificate.
    private static List<Root> Roots()
    {
        var roots = new List<Root>();

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            // %ProgramData%\Nexus: settings, profiles, screentime.db, devices/
            // (device media + records), media/ (lighting content), drivers/, firmware,
            // logs (nexus-service/overlay/tray/helper/volume/gpu/pawnio),
            // https cert, ffmpeg-pids, DesktopWebView2, openrgb-config. Whole-tree
            // wipe, so the grouped subdirs need no per-name upkeep here.
            // Preserve PawnIO\ (kernel driver).
            roots.Add(new Root(System.IO.Path.Combine(programData, "Nexus"), new[] { "PawnIO" }));
            // Per-user data the daemon can't reach via GetFolderPath: it runs as
            // LocalSystem, so LocalApplicationData resolves to the SYSTEM profile.
            // dashboard-bounds.json (written by the user-session overlay) lands in
            // a real user's Local\Nexus; current SDK-app installs go to
            // %ProgramData%\Nexus\apps (wiped above), but older installs may
            // linger in a profile's Roaming\Nexus. WindowsUserProfiles() returns
            // BOTH every C:\Users\* profile AND the system profile, so each one's
            // AppData\{Local,Roaming,LocalLow}\Nexus
            // gets wiped.
            foreach (var profile in WindowsUserProfiles())
            {
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "Local", "Nexus"), Array.Empty<string>()));
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "Roaming", "Nexus"), Array.Empty<string>()));
                roots.Add(new Root(System.IO.Path.Combine(profile, "AppData", "LocalLow", "Nexus"), Array.Empty<string>()));
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
            // Linux splits user data across the XDG base dirs.
            roots.Add(new Root(XdgRoot("XDG_CONFIG_HOME", ".config"), Array.Empty<string>()));    // settings, screentime, cert, ffmpeg-pids
            roots.Add(new Root(XdgRoot("XDG_DATA_HOME", ".local", "share"), Array.Empty<string>())); // devices, media, widgets
            roots.Add(new Root(XdgRoot("XDG_CACHE_HOME", ".cache"), Array.Empty<string>()));      // firmware, drivers
            // logs: ~/.local/state/nexus/logs (lowercase, no XDG override in ServiceLog).
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
    private static IEnumerable<string> WindowsUserProfiles()
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
    public static void Begin(bool wipe = true)
    {
        var selfExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfExe))
        {
            Console.Error.WriteLine("[factory-reset] cannot resolve own exe path; aborting");
            return;
        }
        var psi = new ProcessStartInfo
        {
            FileName = selfExe,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(wipe ? FinalizeFlag : RestartFlag);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            Process.Start(psi);
            Console.Error.WriteLine("[factory-reset] finalizer spawned");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[factory-reset] failed to spawn finalizer: {ex.Message}");
        }
    }

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
#if !WINDOWS
        // Leave the service's session/process group before anything else, so
        // tearing the service down can't take this finalizer with it mid-wipe:
        // `launchctl bootout` (macOS) signals the whole job process group, and a
        // systemd cgroup stop (Linux) does the same. setsid() makes us a new
        // session leader; it fails harmlessly if we're already a group leader.
        setsid();
#endif
        Console.Error.WriteLine($"[factory-reset] finalize: waiting for pid {parentPid} to exit");
        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));

        // macOS only: launchd would relaunch the service the instant it exits
        // (KeepAlive=true), and that new instance could flush its in-memory
        // (old) settings back to disk after we delete. bootout tears the agent
        // down AND waits for the process to exit, so nothing can resurrect the
        // config mid-wipe. No-op when the service was started manually (dev).
#if MACOS
        if (wipe)
        {
            BootoutMacAgent();
        }
#endif

#if WINDOWS
        // A wipe deletes %ProgramData%\Nexus\DesktopWebView2 /
        // \DashboardEdge (the dashboard's WebView2/Edge profiles, holding
        // localStorage's cached auth token) out from under the overlay and
        // helper if either is still running - they survive a plain service
        // stop/start, so a cold reopen needs them gone first: kill them
        // before the wipe so nothing holds the profile dirs open, and so the
        // next boot's UserHelperBootstrapper.EnsureLaunched spawn is not
        // blocked by the still-running helper's Local\NexusHelper mutex.
        // Excludes this finalizer's own pid - it also runs as Nexus.exe.
        if (wipe)
        {
            ShellExecutor.RunExit("taskkill.exe", ShellExecutor.DefaultTimeoutMs,
                "/F", "/T", "/IM", "nexus-overlay.exe");
            ShellExecutor.RunExit("taskkill.exe", ShellExecutor.DefaultTimeoutMs,
                "/F", "/FI", $"PID ne {Environment.ProcessId}", "/IM", "Nexus.exe");
        }
#endif

        if (wipe)
        {
            DeleteRoots();
        }
#if WINDOWS
        // The reopen flag lives under %ProgramData%\Nexus, so it must be
        // written after the wipe or DeleteRoots would remove it immediately.
        // WindowsUserHelper.Run checks for it on every helper startup - the
        // fresh helper EnsureLaunched spawns after RestartService below picks
        // it up and reopens a cold dashboard once the overlay it also starts
        // is ready.
        if (wipe)
        {
            DashboardReopenFlag.Write();
        }
#endif
        RestartService();
#if WINDOWS
        // A plain restart (e.g. applying the render-GPU choice) was triggered
        // from the open dashboard, but the overlay idle-exits across the restart,
        // so the window would not come back on its own. Re-show it; the web
        // auto-reconnects to the service while it finishes booting.
        if (!wipe)
        {
            try { UserHelperBootstrapper.LaunchOpenApp(); } catch { }
        }
#endif
        return 0;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
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

    private static void DeleteRoots()
    {
        foreach (var root in Roots())
        {
            if (!Directory.Exists(root.Path)) continue;
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
    private static void BootoutMacAgent()
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
