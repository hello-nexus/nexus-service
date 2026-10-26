using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

/// <summary>
/// Hands the legacy desktop-widget layout (<c>Overlay.Layout</c>) to a per-display panel record as a
/// <see cref="PanelDeviceRecord.DesktopImport"/>, which the panel's own web client places on its grid.
/// The Overlay block is never rewritten except for its marker, so a downgraded build still finds its widgets.
/// </summary>
public sealed class DesktopWidgetsMigration
{
    public enum HostOs { Windows, MacOS, Linux }

    /// <summary>The legacy overlay cell edge before <c>Overlay.Scale</c>.</summary>
    internal const double LegacyCellPx = 86;

    private readonly IConfigStore _store;
    private readonly OverlayMonitorMap _monitors;
    private readonly DisplayTopologyService? _topology;
    private readonly MultiplexHub? _hub;
    private readonly object _lock = new();

    internal HostOs Os { get; init; } =
        OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;

    public DesktopWidgetsMigration(IConfigStore store, OverlayMonitorMap monitors, DisplayTopologyService? topology, MultiplexHub? hub, ProfileManager? profiles = null)
    {
        _store = store;
        _monitors = monitors;
        _topology = topology;
        _hub = hub;
        profiles?.ProfileLoaded += OnProfileLoaded;
    }

    // Off the profile manager's lock: Run may wait on a display helper round trip.
    private void OnProfileLoaded() => _ = System.Threading.Tasks.Task.Run(() =>
    {
        try { Run(); }
        catch (Exception ex) { ServiceLog.Error($"[desktop-migration] {ex.GetType().Name}: {ex.Message}"); }
    });

    /// <summary>
    /// A profile was loaded over live settings. When another profile's widgets already moved to panels and
    /// Panels are shared, this profile's widgets are not merged in (its looks would not follow), so only the
    /// marker is set and its legacy overlay stays hidden.
    /// </summary>
    internal static void MarkLaterProfile(NexusSettings s, bool priorMigrated)
    {
        if (!priorMigrated || s.Overlay.MigratedToPanels || s.Overlay.Layout.Count == 0) return;
        if (!s.SharedCategories.Contains(ProfileSharing.Panels)) return;
        s.Overlay.MigratedToPanels = true;
    }

    /// <summary>
    /// Runs one pass. Returns the record id that received an import, or null when nothing was handed over
    /// (done earlier, nothing to migrate, or deferred until the topology or monitor map is known).
    /// </summary>
    public string? Run(DisplayTopologyResponse? topology = null)
    {
        lock (_lock)
        {
            if (Os == HostOs.Linux) return null;
            var settings = _store.Load();
            if (settings.Overlay.MigratedToPanels) return null;

            if (settings.Overlay.Layout.Count == 0)
            {
                _store.Update(s => s.Overlay.MigratedToPanels = true);
                Finish(null);
                return null;
            }

            topology ??= _topology?.GetTopology();
            if (topology is null || topology.Displays.Count == 0) return null;
            var target = ResolveTarget(settings.Overlay, topology);
            if (target is null) return null;

            string? recordId = null;
            _store.Update(s =>
            {
                if (s.Overlay.MigratedToPanels) return;
                var overlay = s.Overlay;
                var widgets = overlay.Layout
                    .Where(w => !string.Equals(w.Type, "discord", StringComparison.Ordinal))
                    .Select(CopyWidget)
                    .ToList();
                if (widgets.Count == 0)
                {
                    overlay.MigratedToPanels = true;
                    return;
                }

                var record = s.PanelDevices.Values.FirstOrDefault(r => string.Equals(r.DisplayId, target.Id, StringComparison.Ordinal));
                var merge = record is not null;
                if (record is null)
                {
                    var caps = DisplayTopologyService.BuildPromotedCapabilities(
                        target.Manufacturer, target.Model, target.Name,
                        target.Resolution.Width, target.Resolution.Height,
                        target.ScaleFactor, target.IsTouch, target.Orientation, target.Dpi);
                    record = PanelDeviceRegistry.NewDisplayRecord(target.Id, target.Name, caps, target.Fingerprint);
                    record.Layout = EmptyLayout();
                    ApplyDesktopFlags(record, overlay);
                    s.PanelDevices[record.Id] = record;
                }
                else if (!PanelHosting.IsOn(record))
                {
                    ApplyDesktopFlags(record, overlay);
                }

                var existing = record.DesktopImport;
                record.DesktopImport = new DesktopImportDto
                {
                    Version = 1,
                    CellPx = existing?.CellPx ?? LegacyCellPx * Math.Clamp(overlay.Scale, 50, 200) / 100.0,
                    CellUnits = existing?.CellUnits ?? (Os == HostOs.Windows ? "device" : "css"),
                    Merge = merge || existing?.Merge == true,
                    Widgets = existing is null ? widgets : existing.Widgets.Concat(widgets).ToList(),
                };
                overlay.MigratedToPanels = true;
                overlay.MigratedRecordId = record.Id;
                recordId = record.Id;
            });
            Finish(recordId);
            return recordId;
        }
    }

    private DisplayTopologyEntryDto? ResolveTarget(OverlaySettings overlay, DisplayTopologyResponse topology)
    {
        var displays = topology.Displays;
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? (displays.Count == 1 ? displays[0] : null);
        DisplayTopologyEntryDto? target;
        if (Os == HostOs.MacOS || overlay.Monitor < 0)
        {
            target = primary;
        }
        else if (_monitors.HasMap)
        {
            var id = _monitors.DisplayIdForIndex(overlay.Monitor);
            target = id is null ? primary : displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)) ?? primary;
        }
        else if (displays.Count == 1)
        {
            target = displays[0];
        }
        else
        {
            return null;
        }

        if (target is { IsY70: true } || target is { HostingSupported: false }) target = primary;
        return target is null || target.IsY70 || !target.HostingSupported ? null : target;
    }

    private static void ApplyDesktopFlags(PanelDeviceRecord record, OverlaySettings overlay)
    {
        record.Backdrop = "desktop";
        record.ClickThrough = true;
        record.AlwaysOnTop = overlay.AlwaysOnTop;
        record.ReserveMonitor = false;
        record.KeepCursorOff = false;
        record.WidgetOpacity = Math.Clamp(overlay.Opacity, 0, 1);
        record.ThemeSyncWithDesktop = true;
        record.AccentSyncWithDesktop = true;
        record.Hosted = overlay.Enabled;
        record.Enabled = false;
    }

    // A null layout would seed the starter widgets underneath the imported ones.
    private static PanelLayoutDto EmptyLayout() => new()
    {
        LayoutSchemaVersion = 2,
        Surface = PanelSurfaces.Monitor,
        Pages = new List<PanelPageDto> { new() { Id = Guid.NewGuid().ToString() } },
    };

    private static OverlayWidgetDto CopyWidget(OverlayWidgetDto w) => new()
    {
        Id = w.Id,
        Type = w.Type,
        Size = w.Size,
        Monitor = w.Monitor,
        Col = w.Col,
        Row = w.Row,
        Locked = w.Locked,
        Config = w.Config is null ? null : new Dictionary<string, System.Text.Json.JsonElement>(w.Config),
    };

    private void Finish(string? recordId)
    {
        _store.FlushNow();
        if (_hub is null) return;
        if (recordId is not null) PanelTopics.BroadcastPanelDevice(_hub, recordId);
        PanelTopics.BroadcastDisplays(_hub);
        PanelTopics.BroadcastPrefs(_hub);
    }
}
