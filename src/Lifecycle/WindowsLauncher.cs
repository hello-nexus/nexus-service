#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Update;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Default Nexus.exe no-args entrypoint. Detects current install state and
/// dispatches accordingly. The launcher NEVER starts the daemon in-process
/// and (other than the first-time install path) NEVER triggers a UAC
/// prompt - the SERVICE_START DACL granted to Authenticated Users at
/// install time lets us recover a stopped service without elevation.
///
/// State machine:
///
///   Query SCM: is NexusService registered?
///     No  -> self-elevate, run --install on self (the ONLY UAC path).
///     Yes -> Query status.
///            Running       -> spawn --helper if needed, open dashboard. If
///                             /ping stays silent and SCM no longer reports
///                             RUNNING, it stopped under us: wait for STOPPED
///                             and restart fresh (falls into Stopped).
///            StartPending  -> wait briefly, retry, then dashboard.
///            StopPending   -> a relaunch landed mid-shutdown; wait for STOPPED
///                             then start fresh, so we never open onto a dead
///                             port (the blank-dashboard bug).
///            Stopped       -> if a Pending always-mode update is staged, start
///                             the service (which applies the install), then
///                             wait for the install cycle to finish and open the
///                             dashboard on the what's-new view. Otherwise start
///                             the service and open the dashboard as usual.
///            Other         -> log the state and open the dashboard so the user
///                             sees the service-down banner; state is ambiguous
///                             so we don't try to start.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsLauncher
{
    private const int DefaultPort = 9400;

    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var state = QueryServiceState();
        switch (state)
        {
            case ServiceState.NotInstalled:
                // First-time install: this is the only path that prompts
                // UAC. --install handles the self-elevate internally.
                Console.WriteLine("[launcher] Nexus is not installed yet; running --install");
                return WindowsServiceInstaller.RunInstall(Array.Empty<string>());

            case ServiceState.Running:
                Console.WriteLine("[launcher] service is running; opening dashboard");
                EnsureHelperRunning();
                // Was RUNNING at query time but /ping never answered AND SCM no
                // longer reports RUNNING -> it shut down under us (the user hit
                // "Shut down" then immediately reopened). Opening now points Edge
                // at a dead port -> blank page; wait for the stop to finish and
                // restart instead. The state re-check keeps a slow-but-healthy
                // boot (ping slow, still RUNNING) on the plain open-dashboard path.
                if (!WaitForPing(TimeSpan.FromSeconds(10)) && QueryServiceState() != ServiceState.Running)
                {
                    Console.WriteLine("[launcher] service stopped under us; waiting for stop, then restarting");
                    WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(20));
                    goto case ServiceState.Stopped;
                }
                OpenDashboard();
                return 0;

            case ServiceState.StartPending:
                Console.WriteLine("[launcher] service is starting; waiting briefly");
                WaitForState(ServiceState.Running, TimeSpan.FromSeconds(10));
                EnsureHelperRunning();
                WaitForPing(TimeSpan.FromSeconds(10));
                OpenDashboard();
                return 0;

            case ServiceState.StopPending:
                // Relaunch landed mid-shutdown (tray "Shut down" then reopen).
                // Opening the dashboard now points Edge at a dying port -> blank
                // page. Wait for the stop to settle, then start fresh.
                Console.WriteLine("[launcher] service is stopping; waiting for stop, then restarting");
                WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(20));
                goto case ServiceState.Stopped;

            case ServiceState.Stopped:
                Console.WriteLine("[launcher] service is stopped; starting unprivileged (DACL grant)");
                // Check the marker before starting the service: ApplyPendingOnStartup
                // deletes it once applied, so this read must happen while stopped.
                // For a Pending always-mode marker, the service launches the NSIS
                // installer on startup, which issues net stop then restarts. Opening
                // the dashboard before that cycle completes would open against the
                // old version and then lose the connection mid-session.
                var pendingVersion = ReadPendingAlwaysModeVersion();
                if (TryStartService())
                {
                    EnsureHelperRunning();
                    if (pendingVersion is not null)
                    {
                        Console.WriteLine("[launcher] pending always-mode install detected; waiting for install cycle");
                        WindowsUserHelper.ShowUpdaterWindow(BuildInfo.Version, pendingVersion);
                        WaitForState(ServiceState.Stopped, TimeSpan.FromSeconds(120));
                        WaitForState(ServiceState.Running, TimeSpan.FromSeconds(60));
                        WaitForPing(TimeSpan.FromSeconds(20));
                        WindowsUserHelper.CloseUpdaterWindow();
                        Console.WriteLine("[launcher] install cycle complete; opening dashboard");
                    }
                    else
                    {
                        // sc.exe start returns when SCM accepts the request, and our
                        // ServiceMain sets RUNNING as soon as we hand the web app off
                        // to a worker thread - the port isn't actually bound yet. Wait
                        // for /ping so the browser opens against a ready service.
                        WaitForPing(TimeSpan.FromSeconds(15));
                    }
                    OpenDashboard();
                    return 0;
                }
                Console.Error.WriteLine("[launcher] could not start service; opening dashboard anyway");
                OpenDashboard();
                return 1;

            default:
                Console.Error.WriteLine($"[launcher] unexpected service state: {state}");
                OpenDashboard();
                return 1;
        }
    }

    // Returns the pending version string when a Pending always-mode update is
    // ready to install on the next service start, null otherwise. Reads the
    // marker and settings from disk directly because DI is not available in
    // the pre-daemon launcher.
    private static string? ReadPendingAlwaysModeVersion()
    {
        try
        {
            var marker = StagedInstallMarkerStore.Read();
            if (marker is null || marker.State != StagedInstallMarkerStore.StatePending)
            {
                return null;
            }

            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Nexus",
                "settings.json");

            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("update", out var updateEl)
                && updateEl.TryGetProperty("updateMode", out var modeEl)
                && modeEl.GetString() == "always")
            {
                return marker.Version;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private enum ServiceState
    {
        NotInstalled,
        Stopped,
        StartPending,
        StopPending,
        Running,
        Other,
    }

    private static ServiceState QueryServiceState()
    {
        return WindowsServiceInstaller.QueryCurrentServiceState(WindowsServiceInstaller.ServiceName) switch
        {
            0 => ServiceState.NotInstalled,
            1 => ServiceState.Stopped,
            2 => ServiceState.StartPending,
            3 => ServiceState.StopPending,
            4 => ServiceState.Running,
            _ => ServiceState.Other,
        };
    }

    private static bool TryStartService()
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add(WindowsServiceInstaller.ServiceName);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10000);
            if (p.ExitCode != 0) return false;
            return WaitForState(ServiceState.Running, TimeSpan.FromSeconds(15));
        }
        catch { return false; }
    }

    private static bool WaitForState(ServiceState target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceState() == target) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool WaitForPing(TimeSpan timeout)
    {
        // Even when SCM reports RUNNING, our ServiceMain set that state right
        // after handing off the web app to a worker thread - Kestrel may not
        // have bound :9400 yet. Hit /ping until we get a 2xx (or give up).
        var deadline = DateTime.UtcNow + timeout;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = http.GetAsync($"http://localhost:{DefaultPort}/ping").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(300);
        }
        return false;
    }

    private static void OpenDashboard()
    {
        // Open the dashboard in an Edge --app frameless window (the same
        // path the tray's "Open Nexus" menu uses). Falls back to the user's
        // default browser if Edge isn't found.
        try
        {
            Platform.Windows.TrayIcon.OpenLocalWindow(DefaultPort);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[launcher] OpenLocalWindow failed, falling back to browser: {ex.Message}");
            try
            {
                var psi = new ProcessStartInfo($"http://localhost:{DefaultPort}/")
                {
                    UseShellExecute = true,
                };
                Process.Start(psi);
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"[launcher] failed to open dashboard: {ex2.Message}");
            }
        }
    }

    private static void EnsureHelperRunning()
    {
        // Best-effort: spawn Nexus.exe --helper if no helper is alive in this
        // session. The helper's per-session mutex (Local\NexusHelper) handles
        // deduplication, so a racing second spawn just exits silently.
        try
        {
            // A spawn while a helper holds the mutex only starts a process that exits at once.
            if (Mutex.TryOpenExisting(WindowsUserHelper.SessionMutexName, out var running))
            {
                running.Dispose();
                return;
            }
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--helper");
            Process.Start(psi);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
#endif
