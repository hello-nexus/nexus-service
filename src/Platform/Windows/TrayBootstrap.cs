using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Serialization;
#endif

namespace Nexus.Service.Platform.Windows;

// Windows-only post-Build wiring for the tray icon, helper-pipe sync, and
// the app-window auto-launch on startup. Each piece is gated by the calling
// context (--service vs interactive) so the Session-0 daemon never tries
// to materialise an interactive NotifyIcon.
[SupportedOSPlatform("windows")]
internal static class TrayBootstrap
{
    // Interactive Windows session: hides console, shows tray with right-click menu.
    // Skipped under --service: Session 0 cannot show UI, so the tray must be a
    // separate user-session process (Nexus.exe --helper). Leaving the
    // tray init in here would create a stale NotifyIcon in Session 0.
    public static void ConfigureTray(WebApplication app)
    {
        var panelLauncher = app.Services.GetRequiredService<PanelKioskLauncher>();
        var store = app.Services.GetRequiredService<IConfigStore>();
        var desktopHostForTray = app.Services.GetRequiredService<PanelOverlayHostLauncher>();
        TrayIcon.Configure(
            9400,
            onExit: () =>
            {
                panelLauncher.Close();
                try { desktopHostForTray.Stop(); } catch { /* best-effort */ }
                Environment.Exit(0);
            },
            onTogglePanel: () =>
            {
                // Flip the AutoLaunch / Show Panel setting; the overlay-host
                // watcher mirrors it onto the nexus-overlay kiosk window. The
                // OnChanged cascade broadcasts /prefs on its own.
                store.Update(s => s.Panel.AutoLaunch = !s.Panel.AutoLaunch);
            },
            // Tray checkmark reflects actual kiosk-window state via FindWindow,
            // not the persisted setting - that way a dead/crashed overlay
            // shows unchecked even if AutoLaunch is still true.
            isPanelRunning: () => panelLauncher.IsRunning);

        var hub = app.Services.GetRequiredService<MultiplexHub>();
        TrayIcon.ConfigureDesktop(
            onToggleOverlayTopmost: () =>
            {
                store.Update(s => s.Overlay.AlwaysOnTop = !s.Overlay.AlwaysOnTop);
                PanelTopics.BroadcastPrefs(hub);
            },
            isOverlayTopmost: () => store.Load().Overlay.AlwaysOnTop,
            hasOverlayWidgets: () => store.Load().Overlay.Layout.Count > 0);

        var pm = app.Services.GetRequiredService<ProfileManager>();
        TrayIcon.ConfigureProfiles(
            getProfiles: () =>
            {
                var manifest = pm.GetManifest();
                var items = manifest.Profiles
                    .Select(p => (p.Id, p.Name))
                    .ToList();
                return (items, manifest.ActiveProfileId);
            },
            onSwitchProfile: id =>
            {
                try
                {
                    pm.SwitchProfile(id);
                    PanelTopics.BroadcastPrefs(hub);
                    PanelTopics.BroadcastLighting(hub);
                    PanelTopics.BroadcastCooling(hub);
                }
                catch (KeyNotFoundException) { /* stale id; ignore */ }
            });

        TrayIcon.SetVisible(store.Load().Monitoring.ShowWindowsTrayIcon);

        store.OnChanged += () =>
        {
            try { TrayIcon.SetVisible(store.Load().Monitoring.ShowWindowsTrayIcon); }
            catch { /* best-effort */ }
        };

        // Surface a tray balloon when a phone pair request arrives with no
        // dashboard open. Interactive mode hosts the tray in-process, so the
        // notification calls TrayIcon directly (service mode routes the same
        // events down the helper pipe - see WireHelperPipe).
        var pairing = app.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.PairRequestNeedsAttention += notice =>
        {
            try { TrayIcon.ShowPairBalloon(notice.DeviceLabel); }
            catch { /* best-effort */ }
        };
        pairing.PairRequestResolved += () =>
        {
            try { TrayIcon.ClearPairBalloon(); }
            catch { /* best-effort */ }
        };

        // Same dashboard-closed gate, transfer flavor: a phone→PC item landed
        // with nobody subscribed to the WS toast.
        var inbox = app.Services.GetRequiredService<Nexus.Service.Transfer.TransferInbox>();
        inbox.TransferNeedsAttention += notice =>
        {
            try { TrayIcon.ShowNoticeBalloon(notice.Title, notice.Text, notice.FolderPath); }
            catch { /* best-effort */ }
        };

        // Diagnostics alerts: same generic notice balloon, gated by the
        // user's notification settings inside DiagnosticsAlertService itself.
        var diagAlerts = app.Services.GetRequiredService<Nexus.Service.Diagnostics.DiagnosticsAlertService>();
        diagAlerts.AlertNeedsAttention += notice =>
        {
            try { TrayIcon.ShowNoticeBalloon(notice.Title, notice.Text, null); }
            catch { /* best-effort */ }
        };
    }

#if WINDOWS
    // Service mode: the helper is a separate long-lived user-session process
    // connected over a named pipe. ShowWindowsTrayIcon does not control the
    // helper's existence (it always runs so providers like screen-time stay
    // alive); we push the visibility flip down the pipe and let the helper
    // hide/show its NotifyIcon in place. Also wires the "Shut down" item back
    // through the helper-pipe - the service's SCM DACL only grants
    // Authenticated Users QUERY_STATUS + START, not STOP, so the helper
    // can't sc.exe-stop us itself.
    public static void WireHelperPipe(WebApplication app)
    {
        var trayStore = app.Services.GetRequiredService<IConfigStore>();
        var helperRegistry = app.Services.GetRequiredService<HelperRegistry>();
        var pm = app.Services.GetRequiredService<ProfileManager>();
        var hub = app.Services.GetRequiredService<MultiplexHub>();
        var lastVisible = trayStore.Load().Monitoring.ShowWindowsTrayIcon;

        void PushProfiles()
        {
            var m = pm.GetManifest();
            _ = ProfileCommands.PushListAsync(helperRegistry, m.Profiles, m.ActiveProfileId);
        }

        // Surface a native tray balloon when a phone pair request arrives and
        // no dashboard is open to show the Allow/Deny modal. The service runs
        // in Session 0 and can't draw UI, so the notification is pushed down
        // the pipe to the user-session helper, which owns the tray icon.
        // Clicking the balloon opens the dashboard; the snapshot provider then
        // replays the pending request so the modal pops.
        var pairing = app.Services.GetRequiredService<PanelPhonePairingService>();
        pairing.PairRequestNeedsAttention += notice =>
        {
            try { _ = TrayCommands.PairNoticeAsync(helperRegistry, true, notice.DeviceLabel); }
            catch (Exception ex) { Console.Error.WriteLine($"[pair-notify] show failed: {ex.Message}"); }
        };
        pairing.PairRequestResolved += () =>
        {
            try { _ = TrayCommands.PairNoticeAsync(helperRegistry, false, ""); }
            catch (Exception ex) { Console.Error.WriteLine($"[pair-notify] dismiss failed: {ex.Message}"); }
        };

        // Same dashboard-closed gate, transfer flavor - pushed down the pipe
        // because Session 0 can't draw UI.
        var inbox = app.Services.GetRequiredService<Nexus.Service.Transfer.TransferInbox>();
        inbox.TransferNeedsAttention += notice =>
        {
            try { _ = TrayCommands.NoticeAsync(helperRegistry, notice.Title, notice.Text, notice.FolderPath); }
            catch (Exception ex) { Console.Error.WriteLine($"[transfer-notify] show failed: {ex.Message}"); }
        };

        // Diagnostics alerts - pushed down the pipe, same as pairing/transfer.
        var diagAlerts = app.Services.GetRequiredService<Nexus.Service.Diagnostics.DiagnosticsAlertService>();
        diagAlerts.AlertNeedsAttention += notice =>
        {
            try { _ = TrayCommands.NoticeAsync(helperRegistry, notice.Title, notice.Text, null); }
            catch (Exception ex) { Console.Error.WriteLine($"[diagnostics-notify] show failed: {ex.Message}"); }
        };

        // Push current state on every fresh helper connect: first bootstrap,
        // service restart, helper crash-and-respawn.
        helperRegistry.Connected += conn =>
        {
            try
            {
                var current = trayStore.Load().Monitoring.ShowWindowsTrayIcon;
                _ = TrayCommands.SetVisibleAsync(helperRegistry, current);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] initial state failed: {ex.Message}"); }
        };

        // Re-assert the Y70 panel's display orientation on every fresh helper
        // connect. Windows defaults a freshly attached portrait panel to
        // landscape; this drives it to the stored orientation (PortraitFlipped
        // by default) so the panel never comes up sideways. The Win32
        // ChangeDisplaySettingsEx call runs in the helper (user session, where
        // it can see the monitors); a no-op when Windows is already in the
        // target orientation.
        helperRegistry.Connected += async conn =>
        {
            try
            {
                var y70 = trayStore.Load().Y70;
                var orientation = y70.ForceOrientation ? "PortraitFlipped" : y70.Orientation;
                var res = await OrientationCommands.SetAsync(helperRegistry, orientation);
                ServiceLog.Info($"[y70-sync] orientation re-assert requested='{orientation}' ok={res.Ok} detail='{res.Error}'");
            }
            catch (Exception ex) { Console.Error.WriteLine($"[y70-sync] initial orientation failed: {ex.Message}"); }
        };

        helperRegistry.Connected += _ =>
        {
            try { PushProfiles(); }
            catch (Exception ex) { Console.Error.WriteLine($"[profiles-sync] push on connect failed: {ex.Message}"); }
        };

        pm.OnProfileSwitched += () =>
        {
            try { PushProfiles(); }
            catch (Exception ex) { Console.Error.WriteLine($"[profiles-sync] push on switch failed: {ex.Message}"); }
        };

        trayStore.OnChanged += () =>
        {
            try
            {
                var nowVisible = trayStore.Load().Monitoring.ShowWindowsTrayIcon;
                if (nowVisible == lastVisible) return;
                lastVisible = nowVisible;
                _ = TrayCommands.SetVisibleAsync(helperRegistry, nowVisible);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] {ex.Message}"); }
        };

        helperRegistry.InboundEnvelope += (_, env) =>
        {
            if (env.Type != "service.requestStop") return;
            try { app.Lifetime.StopApplication(); }
            catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] requestStop failed: {ex.Message}"); }
        };

        helperRegistry.InboundEnvelope += (_, env) =>
        {
            if (env.Type != "profiles.switch") return;
            if (env.Payload is null) return;
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize(
                    env.Payload.Value,
                    AppJsonContext.Default.ProfileSwitchPayload);
                if (p is null) return;
                pm.SwitchProfile(p.Id);
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
            }
            catch (KeyNotFoundException) { /* stale id; ignore */ }
            catch (Exception ex) { Console.Error.WriteLine($"[profiles-switch] failed: {ex.Message}"); }
        };

        // Quitting must take the user-session UI with it: close the --app
        // window and exit the helper so the tray icon disappears. Without
        // this, "Stop Nexus" leaves an orphaned Edge --app window pointing at
        // a dead port and a stale tray icon in the user session.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                LifecycleCommands.SendShutdownAsync(helperRegistry, cts.Token).GetAwaiter().GetResult();
            }
            catch { /* helper may not be connected */ }
        });
    }
#endif

    // Wires the on-Started auto-launch of the dashboard window (or helper in
    // service mode), the on-Stopping panel-kiosk close + state flush, and
    // the background PawnIO install. Windows-only.
    public static void WireAppWindowAndPawnIo(WebApplication app, bool serviceMode, bool suppressStartupWindow)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (serviceMode)
            {
                Console.WriteLine("[nexus-service] startup window suppressed (LocalSystem session 0 has no interactive desktop)");
#if WINDOWS
                // Scrub a stray autostart Run key from LocalSystem's own hive
                // (older SYSTEM-context installs misfiled it there); self-heals.
                Nexus.Service.Lifecycle.WindowsStartupProvider.ScrubSystemHiveAutostart();
                // Always launch the user-session helper. Its lifetime is decoupled
                // from any pref - the helper hosts the tray icon, screen-time
                // poller, media/brightness providers, etc.
                Nexus.Service.Lifecycle.UserHelperBootstrapper.EnsureLaunched();
#endif
            }
            else if (suppressStartupWindow)
            {
                Console.WriteLine("[nexus-service] startup window suppressed (--no-window)");
            }
            else
            {
                TrayIcon.OpenLocalWindow();
                Console.WriteLine("[nexus-service] app window launched");
            }
        });

        // Kill panel kiosk webview AND the overlay host (nexus-overlay.exe) on
        // any shutdown (Ctrl+C, Task Manager, Stop-Process, the tray "Shut
        // down", /service/stop, etc.) and flush in-memory settings + active
        // profile to disk so recent mutations survive a graceful stop. Without
        // the overlay Stop() the host - spawned cross-session via schtasks, so
        // the KILL_ON_JOB_CLOSE job never holds it - outlives the service: the
        // service stops and the tray goes, but nexus-overlay.exe (and any
        // dashboard/widget window) lingers. The interactive tray's onExit kills
        // it directly; service mode (the shipped path) only runs this handler.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var launcher = app.Services.GetRequiredService<PanelKioskLauncher>();
            launcher.Close();
            try { app.Services.GetRequiredService<PanelOverlayHostLauncher>().Stop(); }
            catch { /* best-effort */ }
            try { app.Services.GetRequiredService<IConfigStore>().FlushNow(); }
            catch { /* best-effort */ }
            try { app.Services.GetRequiredService<ProfileManager>().SaveActiveProfile(); }
            catch { /* best-effort */ }
        });

        // Auto-install the bundled PawnIO kernel driver if not already
        // installed, and repair a registered driver whose device is dead.
        // Fire-and-forget; on unelevated interactive hosts an install or
        // repair prompts UAC (the LocalSystem service elevates silently).
        // LhmComputer's background Open waits on PawnIoBootGate so a driver
        // installed or repaired here is enumerable in the same boot - signal
        // it on every outcome or LHM waits out the full cap.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Nexus.Service.Lifecycle.PawnIoInstaller.EnsureInstalledAsync();
                ServiceLog.Info($"[pawnio] driver state: {result}");
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[pawnio] install check failed: {ex.Message}");
            }
            finally
            {
                Nexus.Service.Lifecycle.PawnIoBootGate.Signal();
            }
        });
    }
}
