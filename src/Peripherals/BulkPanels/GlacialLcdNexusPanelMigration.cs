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
/// modes: the saved mode is no longer the panel, the display is told its mode once more through the
/// normal native path, and the streamed panel record for the
/// display is removed so the device page is the native one again.
/// </summary>
public static class GlacialLcdNexusPanelMigration
{
    /// <summary>True when anything was changed.</summary>
    public static bool Run(IConfigStore store, StreamedPanelStore streams, Func<string, PanelDeviceRecord?> getPanel, Func<string, bool> removePanel)
    {
        var records = streams.Load();
        var doomed = new List<string>();
        var panelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in records)
        {
            bool owned = pair.Key == GlacialLcdWorker.HandlerId;
            if (!string.IsNullOrEmpty(pair.Value.PanelDeviceId))
            {
                var panel = getPanel(pair.Value.PanelDeviceId);
                owned |= string.Equals(panel?.Capabilities?.Family, GlacialLcdWorker.HandlerId, StringComparison.Ordinal);
            }
            if (owned)
            {
                doomed.Add(pair.Key);
                if (!string.IsNullOrEmpty(pair.Value.PanelDeviceId))
                {
                    panelIds.Add(pair.Value.PanelDeviceId);
                }
            }
        }
        // A family record with no stream entry is swept too.
        store.Read(s =>
        {
            foreach (var panel in s.PanelDevices.Values)
            {
                if (string.Equals(panel.Capabilities?.Family, GlacialLcdWorker.HandlerId, StringComparison.Ordinal))
                {
                    panelIds.Add(panel.Id);
                }
            }
        });
        bool wasPanel = false;
        store.Update(s =>
        {
            var g = s.Devices.GlacialLcd;
            wasPanel = g.ModeChosen && g.Mode == GlacialLcdSettings.ModePanel;
            if (wasPanel)
            {
                g.Mode = GlacialLcdSettings.ModeMonitor;
            }
            // Only the mode is re-sent, in case the last upload left the display unresponsive.
            if ((wasPanel || doomed.Count > 0 || panelIds.Count > 0) && g.ModeChosen)
            {
                g.ModePending = true;
            }
        });
        if (doomed.Count == 0 && panelIds.Count == 0)
        {
            return wasPanel;
        }
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in panelIds)
        {
            try
            {
                removePanel(id);
            }
            catch (Exception ex)
            {
                failed.Add(id);
                ServiceLog.Warn($"[{GlacialLcdWorker.HandlerId}] panel record removal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // A stream entry stays while its panel record could not be removed, so the next start retries.
        foreach (var key in doomed)
        {
            if (!failed.Contains(records[key].PanelDeviceId))
            {
                records.Remove(key);
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
