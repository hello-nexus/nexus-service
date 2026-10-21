using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// With the Nexus panel mode off, an install that had it on is moved to the display's native
/// modes: the saved mode is no longer the panel, the display is told its mode, brightness and
/// on state once more through the normal native path, and the streamed panel record for the
/// display is removed so the device page is the native one again.
/// </summary>
public static class GlacialLcdNexusPanelMigration
{
    /// <summary>True when anything was changed.</summary>
    public static bool Run(IConfigStore store, StreamedPanelStore streams, Func<string, PanelDeviceRecord?> getPanel, Func<string, bool> removePanel)
    {
        var records = streams.Load();
        var doomed = new List<string>();
        foreach (var pair in records)
        {
            bool owned = pair.Key == GlacialLcdWorker.HandlerId;
            if (!owned && !string.IsNullOrEmpty(pair.Value.PanelDeviceId))
            {
                var panel = getPanel(pair.Value.PanelDeviceId);
                owned = string.Equals(panel?.Capabilities?.Family, GlacialLcdWorker.HandlerId, StringComparison.Ordinal);
            }
            if (owned)
            {
                doomed.Add(pair.Key);
            }
        }
        bool wasPanel = false;
        store.Update(s =>
        {
            var g = s.Devices.GlacialLcd;
            wasPanel = g.ModeChosen && g.Mode == GlacialLcdSettings.ModePanel;
            if (wasPanel)
            {
                g.Mode = GlacialLcdSettings.ModeMonitor;
            }
            if (wasPanel || doomed.Count > 0)
            {
                // Reselect the mode and resend brightness and display state, in case the last upload left the display unresponsive.
                if (g.ModeChosen)
                {
                    g.ModePending = true;
                }
                if (g.UserEdited)
                {
                    g.BrightnessEdited = true;
                    g.DisplayEdited = true;
                }
            }
        });
        if (doomed.Count == 0)
        {
            return wasPanel;
        }
        foreach (var key in doomed)
        {
            string panelId = records[key].PanelDeviceId;
            records.Remove(key);
            if (!string.IsNullOrEmpty(panelId))
            {
                try
                {
                    removePanel(panelId);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[{GlacialLcdWorker.HandlerId}] panel record removal failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        streams.Save(records);
        ServiceLog.Info($"[{GlacialLcdWorker.HandlerId}] Nexus panel mode is off: streamed panel record removed, native modes only");
        return true;
    }
}

/// <summary>Runs the migration at start, before the streamed-panel coordinator's first pass.</summary>
public sealed class GlacialLcdNexusPanelMigrationService : IHostedService
{
    private readonly Action _run;

    public GlacialLcdNexusPanelMigrationService(Action run) => _run = run;

    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken)
    {
        try
        {
            _run();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{GlacialLcdWorker.HandlerId}] migration failed: {ex.GetType().Name}: {ex.Message}");
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
}
