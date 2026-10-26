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
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try { overlayHost.Stop(); } catch { }
        });
    }

    internal static string AssignmentsSignature(NexusSettings snapshot)
    {
        var parts = new List<string>();
        foreach (var record in snapshot.PanelDevices.Values)
        {
            if (!string.IsNullOrEmpty(record.DisplayId) && PanelHosting.IsOn(record))
                parts.Add($"{record.DisplayId}|{record.Id}|{record.ReserveMonitor ?? true}|{record.KeepCursorOff ?? false}|{record.AlwaysOnTop ?? true}|{record.Backdrop ?? ""}|{record.ClickThrough ?? false}");
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }
}
