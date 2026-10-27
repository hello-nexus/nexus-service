using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Persistence;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
#endif

namespace Nexus.Service.Panel;

// Settings-change edges the overlay host learns about from the service:
// the prefs push, the Y70 kiosk toggle, and the monitor-assignment set.
// Whether the host process runs at all is OverlaySupervisor's job.
internal static class OverlayHostBootstrap
{
    public static void Wire(WebApplication app)
    {
#if WINDOWS
        System.Threading.Tasks.Task.Run(PanelOverlayHostLauncher.SweepStaleLaunchTasks);
#endif
        var overlayHost = app.Services.GetRequiredService<IOverlayHost>();
        var store = app.Services.GetRequiredService<IConfigStore>();
        var panelKioskLauncher = app.Services.GetRequiredService<PanelKioskLauncher>();
        var edgeLock = new object();
        var lastShowPanel = store.Load().Panel.AutoLaunch;
        var lastAssignments = AssignmentsSignature(store.Load());
#if WINDOWS
        var helperRegistry = app.Services.GetService<HelperRegistry>();
#endif
#if LINUX
        var linuxKiosks = app.Services.GetService<Platform.Linux.LinuxPanelKioskHost>();
#endif
        store.OnChanged += () =>
        {
            try
            {
                var snapshot = store.Load();
#if WINDOWS
                if (helperRegistry is not null)
                    LifecycleCommands.NotifyOverlayPrefsChangedAsync(helperRegistry).ObserveFaults();
#endif
                bool showPanelChanged, assignmentsChanged;
                var assignments = AssignmentsSignature(snapshot);
                lock (edgeLock)
                {
                    showPanelChanged = snapshot.Panel.AutoLaunch != lastShowPanel;
                    lastShowPanel = snapshot.Panel.AutoLaunch;
                    assignmentsChanged = assignments != lastAssignments;
                    lastAssignments = assignments;
                }
                if (showPanelChanged)
                {
                    if (snapshot.Panel.AutoLaunch) panelKioskLauncher.Launch();
                    else panelKioskLauncher.Close();
                }
                if (assignmentsChanged)
                {
                    overlayHost.NotifyDisplayAssignmentsChanged();
#if LINUX
                    linuxKiosks?.Reconcile();
#endif
                }
            }
            catch { }
        };

#if LINUX
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                linuxKiosks?.Reconcile();
            });
        });
#endif
#if LINUX
        // Kiosk teardown is a browser kill plus waits. It runs beside the hosted-service
        // stops, so a slow browser cannot delay the fan release or the Lian Li disconnect;
        // ApplicationStopped joins it, bounded, before the DI container disposes (which kills OpenRGB).
        Task? kioskStop = null;
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            kioskStop = Task.Run(() =>
            {
                try { linuxKiosks?.Shutdown(); } catch { }
            });
        });
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            try { kioskStop?.Wait(Platform.Linux.LinuxPanelKioskHost.ShutdownBudgetMs); } catch { }
        });
#else
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { overlayHost.Stop(); } catch { }
        });
#endif
    }

    private static string AssignmentsSignature(NexusSettings snapshot)
    {
        var parts = new List<string>();
        foreach (var record in snapshot.PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && record.Enabled != false)
                parts.Add($"{record.DisplayId}|{record.Id}|{record.ReserveMonitor ?? true}|{record.KeepCursorOff ?? false}|{record.AlwaysOnTop ?? true}");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}
