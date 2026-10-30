#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Entry point for <c>Nexus.exe --helper</c> (and the legacy <c>--tray</c>
/// alias). Long-lived user-session companion process that owns the system
/// tray icon and acts as the named-pipe client to the LocalSystem service.
/// All Session 0-blind operations (foreground-window polling, SMTC media,
/// brightness control, etc.) run here and report to the service via the
/// pipe.
///
/// Single-instance per logon session via <c>Local\NexusHelper</c>. Lifetime
/// is decoupled from <c>ShowWindowsTrayIcon</c> (which controls icon
/// visibility only); the process keeps running so the providers it hosts
/// stay alive.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsUserHelper
{
    private const int DefaultPort = 9400;
    internal const string SessionMutexName = @"Local\NexusHelper";

    private static readonly CancellationTokenSource s_exit = new();

    // Splash this process spawned; a close that beats the splash window still ends it.
    private static int s_splashPid;

    // Immutable snapshot of the latest profiles.list push. Written by the pipe
    // handler thread; read on the tray message-pump thread. Volatile reference
    // swap to an immutable object is the safe cross-thread publish pattern.
    private sealed class ProfileSnapshot
    {
        public IReadOnlyList<(string Id, string Name)> Items { get; }
        public string ActiveId { get; }

        public ProfileSnapshot(IReadOnlyList<(string Id, string Name)> items, string activeId)
        {
            Items = items;
            ActiveId = activeId;
        }
    }

    private static volatile ProfileSnapshot s_profiles = new(Array.Empty<(string, string)>(), "");

    public static int Run(string[] args)
    {
        Diag("WindowsUserHelper.Run entered");
        if (!OperatingSystem.IsWindows()) return 0;

        using var mutex = new Mutex(initiallyOwned: true, name: SessionMutexName, out var isFirst);
        if (!isFirst)
        {
            Console.WriteLine("[helper] another helper is already running in this session; exiting");
            return 0;
        }

        // The helper outlives transient service stops. We only exit when the
        // service is uninstalled (no point staying), the session ends, or
        // we're explicitly told to stop. A stopped-but-installed service
        // means the pipe client will sit in its reconnect loop until SCM
        // brings the daemon back up.
        if (QueryServiceState() == ServiceState.NotInstalled)
        {
            Console.WriteLine("[helper] NexusService not installed; helper exits");
            return 0;
        }

        // Sync the tray-autostart Run key to the service start type, in the
        // user's hive (HKCU here is the real user). Mirrors "start on boot":
        // present when the service auto-starts, removed when it is demand.
        WindowsStartupProvider.SyncHelperAutostart(Environment.ProcessPath ?? string.Empty);

        // Pipe client to the service. Reconnects with backoff on drop.
        // Outbound is shared with the helper-side providers (screen-time
        // poller, media pusher, etc.) so they can emit envelopes without
        // touching the pipe directly. Constructed before the tray so the
        // "Shut down" closure can capture it for the stop-request send.
        var outbound = new HelperOutbound();

        // Tray icon: hidden until the service is connected. The pump thread
        // starts now (so WM_DISPLAYCHANGE / TaskbarCreated still work while the
        // icon is hidden), but the icon itself is added
        // only when the service pushes the current ShowWindowsTrayIcon value on
        // connect (TrayBootstrap.WireHelperPipe). A service that never comes up
        // (SCM start type = demand, "launch at startup" off) never sends that
        // push, so no icon appears while Nexus is not running; a service crash
        // hides it via onDisconnected below.
        //
        // "Shut down" in the tray menu must match the settings "Stop Nexus"
        // UX: service stopped, --app window closed, tray gone. The stop
        // request rides the existing pipe (already authenticated) so the
        // service runs its graceful StopApplication path; sc.exe stop
        // would 5 (Access Denied) here because the SCM DACL only grants
        // Authenticated Users START + QUERY, not STOP.
        Platform.Windows.TrayIcon.Configure(
            DefaultPort,
            onExit: () =>
            {
                try { Platform.Windows.TrayIcon.CloseAppWindow(); } catch { }
                try { RequestServiceStop(outbound); } catch { }
                s_exit.Cancel();
            });

        Platform.Windows.TrayIcon.EnsurePumpStarted();

        // Close any updater splash from a prior install attempt (whether
        // succeeded, failed, or interrupted). Must run unconditionally so a
        // stale splash from a failed install is also dismissed.
        _ = Task.Run(() =>
        {
            try { UpdaterSplashWindow.CloseStale(); } catch { /* best-effort */ }
        });

        // If a reopen flag was written before the installer launched (or by a
        // factory reset that just wiped the overlay/helper), open the
        // dashboard now that the helper owns the tray.
        if (File.Exists(DashboardReopenFlag.Path))
        {
            _ = Task.Run(() =>
            {
                // Open the dashboard FIRST: it is the goal and must not be
                // blocked by the flag delete, which throws when the helper
                // (user session) cannot remove the LocalSystem-written flag.
                // The route is read before the delete, and the URL is built from the local base by OpenLocalWindow.
                var route = DashboardReopenFlag.ReadRoute();
                try
                {
                    if (route is null) Platform.Windows.TrayIcon.OpenLocalWindow();
                    else Platform.Windows.TrayIcon.OpenLocalWindow(path: route);
                }
                catch { /* best-effort */ }
                try { File.Delete(DashboardReopenFlag.Path); } catch { /* service grants the user delete; ignore if it fails */ }
                // Dashboard window appears after WebView2 init in the overlay
                // process; nudge it foreground once visible so it opens on top.
                try { NudgeDashboardToForeground(); } catch { /* best-effort */ }
            });
        }

        // Watchdog: only exits the helper when the service is uninstalled.
        // Transient stopped states are fine; the pipe client handles them.
        var watchdog = new Thread(WatchdogLoop) { IsBackground = true };
        watchdog.Start();

        // UserHelperBootstrapper forwards NEXUS_WINDOW_DIAG as this argv flag
        // instead of the env var itself - a machine env var change on the
        // service side does not reach a process schtasks spawns in another
        // session. Re-hydrate it as the env var WindowSetPoller reads.
        if (Array.IndexOf(args, Nexus.Service.Activity.WindowDiagnostics.HelperArgName) >= 0)
        {
            Environment.SetEnvironmentVariable(Nexus.Service.Activity.WindowDiagnostics.EnvVarName, "1");
        }

        // Same schtasks hop as the window-diag flag above: the disable list is
        // forwarded as argv, with the service-side env var as the fallback for
        // a helper started by hand. Configure before any poller is constructed.
        var disableArg = Array.Find(args, a => a.StartsWith(Helper.HelperPollerDiagnostics.HelperArgPrefix, StringComparison.Ordinal));
        var disableFile = Path.Combine(
            Nexus.Service.Platform.ServiceLog.LogsDirectory,
            Helper.HelperPollerDiagnostics.DisableFileName);
        Helper.HelperPollerDiagnostics.Configure(
            disableArg is not null
                ? disableArg.Substring(Helper.HelperPollerDiagnostics.HelperArgPrefix.Length)
                : Environment.GetEnvironmentVariable(Helper.HelperPollerDiagnostics.EnvVarName),
            disableFile);
        Helper.HelperPollerDiagnostics.Log = Nexus.Service.Platform.HelperLog.Write;
        Diag($"[helper-perf] switch file: {disableFile}");
        Diag(Helper.HelperPollerDiagnostics.FormatStartupLine(Helper.HelperPollerDiagnostics.Current));

        // User-session providers. Each one owns its own polling/listening
        // and pushes envelopes through the shared outbound. Adding a new
        // domain = a new helper-side class + a service-side subscriber;
        // nothing else here changes.
        using var screenTime = new ScreenTimePoller(outbound);
        using var media = new MediaPusher(outbound);
        using var screenCapture = new ScreenCapturePusher(outbound);
        using var windowSet = new WindowSetPoller(outbound);
        using var audioMixer = new AudioSessionPusher(outbound);
        // Idle until the service arms it, which it does only while a lock
        // blackout is engaged.
        using var lockInput = new Helper.LockInputPoller(outbound);
        // Idle until the service arms it, which it does only while idle dim is on with a fixed timeout.
        using var idleDimPoller = new Helper.IdleDimPoller(outbound);
        // Idle until the service arms it, which it does only while a keyboard has key reactions on.
        using var keyPresses = new Helper.KeyPressWatcher(outbound);
        // Idle until the service pushes combos; registers specific combos only (RegisterHotKey), nothing else is observed.
        using var globalHotkeys = new Helper.GlobalHotkeys();
        globalHotkeys.Fired += (id, combo) => _ = outbound.SendAsync(
            type: HotkeyCommands.FiredType,
            payload: new HotkeyFiredPayload { Id = id, Combo = combo },
            payloadType: AppJsonContext.Default.HotkeyFiredPayload);
        var brightness = new Platform.Displays.WindowsDisplayBrightnessProvider();
        Platform.Windows.TrayIcon.SessionLockChanged += Platform.Displays.DdcGate.OnSessionLockChanged;
        Platform.Displays.DdcGate.Start(() => Platform.Windows.TrayIcon.SessionLockWatchActive);

        // Each domain registers its own envelope handler against this
        // registry. Adding a new domain = create the handler class in
        // Helper/Domains/ and call .Register(handlerRegistry) here.
        // Register the toast identity up front rather than on first use: the
        // attribution name and icon are read from that registration, and a
        // notice can be the very first toast this install ever raises.
        Platform.Windows.ToastNotifications.EnsureAppUserModelId();

        var handlerRegistry = new HelperHandlerRegistry();
        new TrayHandler(
            Platform.Windows.TrayIcon.SetVisible,
            Platform.Windows.TrayIcon.ShowPairBalloon,
            Platform.Windows.TrayIcon.ClearPairBalloon,
            ShowNoticeOrToast,
            Platform.Windows.TrayIcon.ShowUpdateReadyBalloon,
            () => Platform.Windows.TrayIcon.OpenLocalWindow(),
            ShowUpdaterWindow,
            CloseUpdaterWindow).Register(handlerRegistry);
        new ConflictNoticeHandler(p => ShowConflictLaunchNotice(p, outbound)).Register(handlerRegistry);
        new AstralNoticeHandler(p => ShowAstralNotice(p, outbound)).Register(handlerRegistry);
        // Runs in the user session, so this set lands on the clipboard the
        // user actually pastes from (the service's Session-0 one is invisible).
        new ClipboardHandler(new Platform.Clipboard.WindowsClipboardProvider().SetText).Register(handlerRegistry);
        new LockLightingHandler(lockInput.SetArmed).Register(handlerRegistry);
        // Idle dim: the input poll arms with the threshold; the display power-state
        // notification rides the tray window, which already pumps messages.
        new IdleDimHandler((threshold, display) =>
        {
            idleDimPoller.SetThreshold(threshold);
            Platform.Windows.TrayIcon.SetDisplayStateWatch(display);
        }).Register(handlerRegistry);
        Platform.Windows.TrayIcon.DisplayStateChanged += off => _ = outbound.SendAsync(
            IdleDimCommands.DisplayStateType,
            new DisplayStatePayload { Off = off },
            Nexus.Service.Serialization.AppJsonContext.Default.DisplayStatePayload);
        Platform.Windows.TrayIcon.ScreenOffTimeoutMayHaveChanged += () => _ = outbound.SendAsync(
            IdleDimCommands.ScreenOffTimeoutChangedType,
            new ScreenOffTimeoutChangedPayload(),
            Nexus.Service.Serialization.AppJsonContext.Default.ScreenOffTimeoutChangedPayload);
        new KeyReactiveHandler(keyPresses.SetArmed).Register(handlerRegistry);
        new HotkeysHandler(globalHotkeys.ApplyAsync, Platform.Windows.TrayIcon.ToggleLocalWindow).Register(handlerRegistry);
        new LifecycleHandler(
            onShutdown: () =>
            {
                // Service-driven teardown: it's stopping (e.g. user hit
                // "Stop Nexus" in settings), so close the --app window and
                // exit the helper so the tray icon goes too. We do NOT
                // also send service.requestStop here - that would echo
                // the very stop the service has already initiated. Tray
                // onExit is the symmetric path that pushes the stop the
                // other way; do not "fix" this asymmetry.
                try { Platform.Windows.TrayIcon.CloseAppWindow(); } catch { }
                s_exit.Cancel();
            },
            onOverlayPrefsChanged: () =>
            {
                // Wake the overlay's marshaler so it repolls preferences
                // immediately (e.g. the user toggled widgets off). We run
                // in the same Windows session as nexus-overlay, so FindWindow
                // can see the marshaler that the service-side cannot.
                try
                {
                    var hwnd = FindWindowW(OverlayMarshalerClassName, null);
                    if (hwnd != IntPtr.Zero)
                    {
                        var msg = RegisterWindowMessageW(OverlayPrefsChangedMessageName);
                        if (msg != 0) PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch { /* best-effort wake; 5 s poll is the safety net */ }
            }).Register(handlerRegistry);
        new MediaHandler(media.Control, media.Seek, media.GetAlbumArt).Register(handlerRegistry);
        new BrightnessHandler(brightness).Register(handlerRegistry);
        Platform.Windows.LaunchFocus.Start((hwnd, focusedAtArm) => _ = outbound.SendAsync(
            SystemCommands.FocusWindowElevatedType,
            new FocusWindowPayload { Hwnd = hwnd.ToInt64(), ExpectedForeground = focusedAtArm.ToInt64() },
            Nexus.Service.Serialization.AppJsonContext.Default.FocusWindowPayload));
        new ShortcutsHandler(new Nexus.Service.Activity.WindowsShortcutsProvider()).Register(handlerRegistry);
        new ProcessIconHandler(new Nexus.Service.Activity.WindowsIconExtractor()).Register(handlerRegistry);
        new ProcessActionsHandler().Register(handlerRegistry);
        new FileDialogHandler().Register(handlerRegistry);
        new MonitorsHandler().Register(handlerRegistry);
        new DisplaysHandler().Register(handlerRegistry);
        // WM_DISPLAYCHANGE lands on the tray's hidden top-level window; push
        // it to the service so dashboards refetch /displays/topology. Fired
        // on the message-pump thread, so the send is fire-and-forget.
        Platform.Windows.TrayIcon.DisplayChanged += () =>
        {
            _ = outbound.SendAsync(
                type: DisplayTopologyCommands.ChangedType,
                payload: new DisplaysChangedPayload(),
                payloadType: Serialization.AppJsonContext.Default.DisplaysChangedPayload);
        };
        // Windows light/dark for panels synced to the desktop. The dashboard
        // page reports it only while its window is open; resent on every
        // connect so a switch while the pipe was down is not lost.
        void PushSystemTheme() => _ = outbound.SendAsync(
            type: SystemCommands.ThemeChangedType,
            payload: new SystemThemePayload { Dark = Platform.Windows.TrayIcon.IsSystemDarkMode() },
            payloadType: AppJsonContext.Default.SystemThemePayload);
        Platform.Windows.TrayIcon.SystemThemeChanged += PushSystemTheme;
        new OrientationHandler(new Platform.Displays.WindowsDisplayOrientationProvider()).Register(handlerRegistry);
        new ScreenMirrorHandler(screenCapture.Start, screenCapture.Stop).Register(handlerRegistry);
        new WindowSetHandler(windowSet.SetWanted).Register(handlerRegistry);
        // The default-endpoint switch is per-user, so it runs here rather than in
        // the Session-0 service.
        var audioDevices = new Nexus.Service.Activity.WindowsAudioDeviceProvider();
        new AudioMixerHandler(
            audioMixer.SetStreaming,
            audioMixer.Apply,
            audioDevices.SetDefaultDirect).Register(handlerRegistry);
        // Foregrounded variant of LogsFolder.Open: the helper is a background
        // process, so a plain explorer spawn lands behind the app window.
        new DiagnosticsHandler(
            onOpenLogs: () => Platform.Windows.ForegroundNudge.OpenFolderOverApp(
                Nexus.Service.Persistence.NexusDataPaths.NexusRoot()),
            onOpenEventViewer: () => Platform.Windows.ForegroundNudge.OpenFileOverApp("eventvwr.msc"),
            onOpenDeviceManager: () => Platform.Windows.ForegroundNudge.OpenFileOverApp("devmgmt.msc")
        ).Register(handlerRegistry);
        new SystemHandler().Register(handlerRegistry);
        // Runs in the user session, so SendInput reaches the interactive
        // desktop (the deck hotkey/hotkeySwitch actions and the
        // /system/input/keys route otherwise land on the Session-0 desktop).
        new InputHandler().Register(handlerRegistry);
        new ProfileListHandler(payload =>
        {
            var items = new List<(string Id, string Name)>(payload.Profiles.Count);
            foreach (var p in payload.Profiles)
            {
                items.Add((p.Id, p.Name));
            }
            s_profiles = new ProfileSnapshot(items, payload.ActiveId);
        }).Register(handlerRegistry);

        Platform.Windows.TrayIcon.ConfigureProfiles(
            getProfiles: () =>
            {
                var s = s_profiles;
                return (s.Items, s.ActiveId);
            },
            onSwitchProfile: id =>
            {
                try
                {
                    _ = outbound.SendAsync(
                        type: "profiles.switch",
                        payload: new ProfileSwitchPayload { Id = id },
                        payloadType: AppJsonContext.Default.ProfileSwitchPayload);
                }
                catch { /* best-effort */ }
            });

        // Hide the tray icon whenever the service pipe drops (crash or stop);
        // the service re-pushes ShowWindowsTrayIcon on reconnect. Keeps the
        // icon absent while Nexus is not running.
        var client = new HelperClientLoop(
            handlerRegistry,
            outbound,
            onDisconnected: () =>
            {
                Platform.Windows.TrayIcon.SetVisible(false);
                // With no service there is nothing to act on a press, so give the combos back.
                globalHotkeys.Clear();
            },
            onConnected: PushSystemTheme);
        var pipeTask = Task.Run(() => client.RunAsync(s_exit.Token));

        try { s_exit.Token.WaitHandle.WaitOne(); }
        catch { }

        try { pipeTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        Platform.Windows.TrayIcon.SetVisible(false);
        return 0;
    }

    private static void WatchdogLoop()
    {
        while (!s_exit.IsCancellationRequested)
        {
            try { Thread.Sleep(5000); } catch { return; }
            if (s_exit.IsCancellationRequested) return;
            if (Helper.HelperPollerDiagnostics.IsDisabled(Helper.HelperPollerDiagnostics.Watchdog)) continue;
            if (QueryServiceState() == ServiceState.NotInstalled)
            {
                Console.WriteLine("[helper] NexusService uninstalled; helper exits");
                s_exit.Cancel();
                return;
            }
        }
    }

    /// <summary>
    /// Ask the service to stop, over the pipe. The service handler calls
    /// IHostApplicationLifetime.StopApplication so the daemon runs the
    /// same graceful path /service/stop uses. We briefly block to make
    /// sure the frame actually leaves the wire before the caller cancels
    /// s_exit (which tears down the pipe loop); a timeout falls through
    /// if the service isn't currently connected, in which case there's
    /// no daemon to stop anyway.
    /// </summary>
    private static void RequestServiceStop(HelperOutbound outbound)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            outbound.SendAsync(
                type: "service.requestStop",
                payload: new ServiceRequestStopPayload(),
                payloadType: Nexus.Service.Serialization.AppJsonContext.Default.ServiceRequestStopPayload,
                ct: cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[helper] pipe stop request failed: {ex.Message}");
        }
    }

    private enum ServiceState { NotInstalled, Running, Other }

    private static ServiceState QueryServiceState()
    {
        return WindowsServiceInstaller.QueryCurrentServiceState(WindowsServiceInstaller.ServiceName) switch
        {
            0 => ServiceState.NotInstalled,
            4 => ServiceState.Running,
            _ => ServiceState.Other,
        };
    }

    /// <summary>
    /// One notice, two transports. A notice asking for an action button goes
    /// out as an interactive toast; everything else - and any toast Windows
    /// refuses - falls back to the tray balloon, whose whole surface still
    /// routes to the same place.
    /// </summary>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void ShowNoticeOrToast(string title, string text, string? folderPath, string? windowPath, string? buttonLabel)
    {
        if (!string.IsNullOrEmpty(buttonLabel)
            && Platform.Windows.ToastNotifications.TryShowActionToast(title, text, buttonLabel, windowPath ?? "/"))
        {
            return;
        }
        Platform.Windows.TrayIcon.ShowNoticeBalloon(title, text, folderPath, windowPath);
    }

    /// <summary>
    /// End task goes back to the service rather than ending the app here: the
    /// helper runs as the user and cannot end an elevated vendor app. The
    /// balloon fallback has no button; a click opens the conflict settings.
    /// </summary>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void ShowConflictLaunchNotice(ConflictLaunchNoticePayload p, HelperOutbound outbound)
    {
        var appId = p.AppId;
        var shown = Platform.Windows.ToastNotifications.TryShowConflictLaunchToast(
            p.Title, p.Text, p.EndLabel, p.WindowPath,
            onEnd: () => _ = outbound.SendAsync(
                type: ConflictNoticeCommands.EndType,
                payload: new ConflictEndPayload { AppId = appId },
                payloadType: AppJsonContext.Default.ConflictEndPayload));
        if (!shown) Platform.Windows.TrayIcon.ShowNoticeBalloon(p.Title, p.Text, null, p.WindowPath);
    }

    /// <summary>The countdown toast's button sends the cancel back to the service, which owns the countdown; the balloon fallback has no button and its click opens the Devices page.</summary>
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void ShowAstralNotice(AstralNoticePayload p, HelperOutbound outbound)
    {
        if (p.CancelId is not { } cancelId)
        {
            Platform.Windows.TrayIcon.ShowNoticeBalloon(p.Title, p.Text, null, p.WindowPath);
            return;
        }
        var shown = Platform.Windows.ToastNotifications.TryShowConflictLaunchToast(
            p.Title, p.Text, p.CancelLabel, p.WindowPath,
            onEnd: () => _ = outbound.SendAsync(
                type: AstralNoticeCommands.CancelType,
                payload: new AstralCancelPayload { Id = cancelId },
                payloadType: AppJsonContext.Default.AstralCancelPayload));
        if (!shown) Platform.Windows.TrayIcon.ShowNoticeBalloon(p.Title, p.Text, null, p.WindowPath);
    }

    [SupportedOSPlatform("windows")]
    internal static void ShowUpdaterWindow(string fromVersion, string toVersion)
    {
        try
        {
            // Close the dashboard before the splash appears so the user
            // never sees them overlap. The later service-shutdown path also
            // calls CloseAppWindow; that second call is a harmless no-op
            // because FindExistingNexusAppWindow returns Zero once the
            // window is already gone.
            try { Platform.Windows.TrayIcon.CloseAppWindow(); } catch { /* best-effort */ }

            var pid = UpdaterSplashWindow.Spawn(fromVersion, toVersion);
            if (pid != 0) Volatile.Write(ref s_splashPid, pid);
            Diag($"ShowUpdaterWindow: native splash started pid={pid}");
        }
        catch (Exception ex) { Diag($"ShowUpdaterWindow failed: {ex.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    internal static void CloseUpdaterWindow()
    {
        try { UpdaterSplashWindow.Dismiss(Interlocked.Exchange(ref s_splashPid, 0)); }
        catch { /* best-effort */ }
    }

    // Poll for the overlay dashboard HWND (created after WebView2 init in the
    // overlay process) and call TryForeground so the helper, running in the
    // background without the foreground lock, can still raise the window.
    private static void NudgeDashboardToForeground()
    {
        for (var i = 0; i < 40; i++)
        {
            var hwnd = FindWindowW(OverlayDashboardClassName, null);
            if (hwnd != IntPtr.Zero)
            {
                Platform.Windows.ForegroundNudge.TryForeground(hwnd);
                return;
            }
            Thread.Sleep(100);
        }
    }

    // Win32 plumbing for the cross-process overlay marshaler wake. Lives
    // here rather than in a shared file because this is the only consumer.
    private const string OverlayMarshalerClassName = "Nexus.Overlay.Marshaler";
    private const string OverlayDashboardClassName = "Nexus.Overlay.Dashboard";
    private const string OverlayPrefsChangedMessageName = "Nexus.Overlay.PrefsChanged";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static void Diag(string msg) => Nexus.Service.Platform.HelperLog.Write(msg);
}
#endif
