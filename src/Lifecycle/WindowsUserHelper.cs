#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
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
    private const string SessionMutexName = @"Local\NexusHelper";
    private const uint WM_CLOSE = 0x0010;
    private const string UpdaterWindowTitle = "Nexus Updater";

    private static readonly CancellationTokenSource s_exit = new();

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
            try { CloseUpdaterWindow(); } catch { /* best-effort */ }
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
                try { Platform.Windows.TrayIcon.OpenLocalWindow(); } catch { /* best-effort */ }
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

        // User-session providers. Each one owns its own polling/listening
        // and pushes envelopes through the shared outbound. Adding a new
        // domain = a new helper-side class + a service-side subscriber;
        // nothing else here changes.
        using var screenTime = new ScreenTimePoller(outbound);
        using var media = new MediaPusher(outbound);
        using var screenCapture = new ScreenCapturePusher(outbound);
        var brightness = new Platform.Displays.WindowsDisplayBrightnessProvider();

        // Each domain registers its own envelope handler against this
        // registry. Adding a new domain = create the handler class in
        // Helper/Domains/ and call .Register(handlerRegistry) here.
        var handlerRegistry = new HelperHandlerRegistry();
        new TrayHandler(
            Platform.Windows.TrayIcon.SetVisible,
            Platform.Windows.TrayIcon.ShowPairBalloon,
            Platform.Windows.TrayIcon.ClearPairBalloon,
            Platform.Windows.TrayIcon.ShowNoticeBalloon,
            Platform.Windows.TrayIcon.ShowUpdateReadyBalloon,
            () => Platform.Windows.TrayIcon.OpenLocalWindow(),
            ShowUpdaterWindow,
            CloseUpdaterWindow).Register(handlerRegistry);
        // Runs in the user session, so this set lands on the clipboard the
        // user actually pastes from (the service's Session-0 one is invisible).
        new ClipboardHandler(new Platform.Clipboard.WindowsClipboardProvider().SetText).Register(handlerRegistry);
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
        new MediaHandler(media.Control, media.GetAlbumArt).Register(handlerRegistry);
        new BrightnessHandler(brightness).Register(handlerRegistry);
        new ShortcutsHandler(new Nexus.Service.Activity.WindowsShortcutsProvider()).Register(handlerRegistry);
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
        new OrientationHandler(new Platform.Displays.WindowsDisplayOrientationProvider()).Register(handlerRegistry);
        new ScreenMirrorHandler(screenCapture.Start, screenCapture.Stop).Register(handlerRegistry);
        // Foregrounded variant of LogsFolder.Open: the helper is a background
        // process, so a plain explorer spawn lands behind the app window.
        new DiagnosticsHandler(
            onOpenLogs: () => Platform.Windows.ForegroundNudge.OpenFolderOverApp(
                Nexus.Service.Platform.ServiceLog.LogsDirectory),
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
            onDisconnected: () => Platform.Windows.TrayIcon.SetVisible(false));
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

    // HTA caption is a known constant so the helper can close it by title.
    // The HTA content is English-only; HTA files cannot use the service i18n bundle.
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

            var from = HtmlEscape(fromVersion);
            var to = HtmlEscape(toVersion);
            // Verbatim string (not a raw/interpolated literal): doubled quotes,
            // single braces, and __FROM__/__TO__ placeholders substituted below.
            // Avoids interpolated-raw-string parsing differences across compilers.
            var hta = @"<html>
<head>
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"" />
<hta:application
  id=""nexusUpdater""
  applicationname=""Nexus Updater""
  caption=""no""
  border=""none""
  sysmenu=""no""
  maximizebutton=""no""
  minimizebutton=""no""
  showintaskbar=""no""
  singleinstance=""yes""
  scroll=""no"" />
<title>Nexus Updater</title>
<style>
* { margin:0; padding:0; box-sizing:border-box; user-select:none; -ms-user-select:none; -webkit-user-select:none; }
html, body { height:100%; overflow:hidden; }
body { font-family:'Segoe UI',sans-serif; display:flex; flex-direction:column; align-items:center; justify-content:center; }
body.dark { background:#202024; color:#e8e8ea; }
body.dark .ver { color:#9a9a9e; }
body.dark .track { background:#3a3a3e; }
body.dark .base { background:#5a5a5e; }
body.dark .shine { background:#a6a6aa; }
body.dark .note { color:#76767a; }
body.dark .mark { fill:#c2c2c6; }
body.light { background:#f3f3f4; color:#202024; }
body.light .ver { color:#6e6e72; }
body.light .track { background:#dcdcde; }
body.light .base { background:#bcbcc0; }
body.light .shine { background:#86868a; }
body.light .note { color:#9a9a9e; }
body.light .mark { fill:#5a5a5e; }
.mark { margin-bottom:12px; }
.title { font-size:16px; font-weight:600; }
.ver { font-size:12px; margin-top:6px; margin-bottom:20px; }
.track { position:relative; width:220px; height:5px; border-radius:99px; overflow:hidden; }
.base { position:absolute; top:0; left:0; height:100%; width:100%; }
.shine { position:absolute; top:0; left:-45%; height:100%; width:45%; }
.note { font-size:11px; margin-top:18px; }
</style>
</head>
<body class=""dark"">
  <script language=""JavaScript"">
    try { if ((new ActiveXObject(""WScript.Shell"")).RegRead(""HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\\AppsUseLightTheme"") == 1) { document.body.className = ""light""; } } catch (e) {}
  </script>
  <svg class=""mark"" width=""34"" height=""36"" viewBox=""0 0 706 741"" preserveAspectRatio=""xMidYMid meet""><g transform=""translate(-155.5,882.838615) scale(0.1,-0.1)""><path d=""M2955 8815 c-701 -98 -1238 -603 -1377 -1295 l-23 -115 0 -2285 0 -2285 23 -115 c244 -1226 1727 -1728 2634 -892 115 106 301 354 493 655 l87 138 -33 42 c-30 39 -248 304 -363 443 -28 33 -103 124 -166 202 -63 78 -117 142 -120 142 -4 0 -69 -84 -146 -187 -78 -104 -210 -280 -294 -392 -190 -255 -249 -305 -407 -346 -244 -64 -520 113 -573 368 -7 36 -10 708 -8 2257 l3 2205 22 54 c102 248 352 363 591 271 110 -42 152 -85 415 -416 122 -155 322 -404 444 -555 121 -151 283 -350 358 -444 76 -93 208 -255 294 -360 87 -104 222 -271 301 -370 79 -99 169 -210 199 -246 31 -36 153 -184 271 -329 118 -144 241 -295 273 -334 33 -39 79 -97 104 -128 57 -72 47 -74 140 28 375 412 437 928 166 1398 -51 89 -77 123 -363 465 -107 129 -303 366 -435 526 -132 160 -285 345 -340 412 -55 66 -156 190 -225 274 -240 295 -554 653 -696 794 -322 319 -808 482 -1249 420z""/><path d=""M6850 8815 c-454 -64 -782 -260 -1065 -638 -147 -196 -362 -551 -347 -570 94 -119 408 -507 446 -552 29 -33 91 -108 140 -167 l87 -108 103 143 c209 291 454 614 502 661 246 244 644 129 752 -217 21 -66 32 -4305 12 -4412 -43 -220 -207 -366 -412 -365 -141 1 -224 58 -372 252 -310 410 -638 829 -858 1098 -16 19 -117 145 -226 280 -108 134 -242 299 -297 366 -55 66 -149 183 -210 260 -60 76 -155 193 -210 260 -55 66 -143 173 -195 238 -81 101 -320 392 -439 535 l-36 44 -109 -119 c-477 -520 -484 -1066 -23 -1612 139 -163 739 -912 1068 -1332 85 -107 185 -233 224 -280 38 -47 135 -168 215 -270 198 -254 316 -393 397 -471 755 -728 1978 -490 2451 477 68 139 100 236 139 421 21 99 33 4479 13 4658 -99 882 -907 1537 -1750 1420z""/></g></svg>
  <div class=""title"">Updating Nexus</div>
  <div class=""ver"">__FROM__ &#x2192; __TO__</div>
  <div class=""track""><div class=""base""></div><div id=""shine"" class=""shine""></div></div>
  <div class=""note"">Nexus will reopen automatically.</div>
  <script language=""JavaScript"">
    var p = -45;
    setInterval(function(){ p += 2; if (p >= 100) { p = -45; } document.getElementById(""shine"").style.left = p + ""%""; }, 16);
    setTimeout(function(){ window.close(); }, 300000);
  </script>
</body>
</html>".Replace("__FROM__", from).Replace("__TO__", to);
            var htaPath = Path.Combine(Path.GetTempPath(), "nexus-updating.hta");
            File.WriteAllText(htaPath, hta, Encoding.UTF8);
            // Full path: the helper's spawned environment may not have System32
            // on PATH, so a bare "mshta.exe" Start can fail silently.
            var mshta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mshta.exe");
            var psi = new ProcessStartInfo(mshta, $"\"{htaPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false,
            };
            var proc = Process.Start(psi);
            Diag($"ShowUpdaterWindow: mshta started pid={proc?.Id.ToString() ?? "null"} hta={htaPath}");
            // mshta spawns the window behind the dashboard. Poll for it (it
            // appears a moment after launch) and force it topmost + foreground
            // so it is visible over everything during the install.
            _ = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    var hwnd = FindWindowW(null, UpdaterWindowTitle);
                    if (hwnd != IntPtr.Zero)
                    {
                        // mshta ignores caption="no" on Win11 and forces a title bar.
                        // Strip the caption/border, then resize: the resize re-flows
                        // the body into the reclaimed client area (no white gap) and
                        // centers the now-borderless window, topmost.
                        var style = GetWindowLong(hwnd, GwlStyle);
                        SetWindowLong(hwnd, GwlStyle, style & ~WsChrome);
                        const int w = 330, h = 220;
                        var x = (GetSystemMetrics(SmCxScreen) - w) / 2;
                        var y = (GetSystemMetrics(SmCyScreen) - h) / 2;
                        SetWindowPos(hwnd, HwndTopmost, x, y, w, h, SwpFrameChanged | SwpShowWindow);
                        SetForegroundWindow(hwnd);
                        return;
                    }
                    Thread.Sleep(100);
                }
            });
        }
        catch (Exception ex) { Diag($"ShowUpdaterWindow failed: {ex.Message}"); }
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    [SupportedOSPlatform("windows")]
    internal static void CloseUpdaterWindow()
    {
        try
        {
            var hwnd = FindWindowW(null, UpdaterWindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040, SwpFrameChanged = 0x0020;
    private const int GwlStyle = -16;
    // WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
    private const int WsChrome = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
    private const int SmCxScreen = 0, SmCyScreen = 1;

    private static void Diag(string msg) => Nexus.Service.Platform.HelperLog.Write(msg);
}
#endif
