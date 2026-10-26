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
    internal const int MinScalePercent = 50;
    internal const int MaxScalePercent = 200;
    /// <summary>How long a legacy monitor index may stay absent (a sleeping or unplugged display) before the primary stands in.</summary>
    internal const long MissingMonitorGraceMs = 24L * 60 * 60 * 1000;

    private readonly IConfigStore _store;
    private readonly OverlayMonitorMap _monitors;
    private readonly DisplayTopologyService? _topology;
    private readonly MultiplexHub? _hub;
    private readonly object _lock = new();

    internal Func<long> NowMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
            // The monitor map is how a new Windows overlay host announces itself. An older host keeps
            // drawing the legacy widgets, so nothing may be handed over or hidden until the map arrives.
            if (Os == HostOs.Windows && !_monitors.HasMap) return null;
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
            var target = ResolveTarget(settings.Overlay, topology, out var indexMissing);
            if (indexMissing) NoteMissingMonitor(settings.Overlay);
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
                    CellPx = existing?.CellPx ?? LegacyCellPx * Math.Clamp(overlay.Scale, MinScalePercent, MaxScalePercent) / 100.0,
                    CellUnits = existing?.CellUnits ?? (Os == HostOs.Windows ? "device" : "css"),
                    Merge = merge || existing?.Merge == true,
                    Widgets = existing is null ? widgets : existing.Widgets.Concat(widgets).ToList(),
                };
                overlay.MigratedToPanels = true;
                overlay.MigratedRecordId = record.Id;
                overlay.MonitorMissingSinceMs = null;
                recordId = record.Id;
            });
            Finish(recordId);
            return recordId;
        }
    }

    // Windows only reaches here with a posted map. A legacy index the map or the topology does not hold
    // yet (a secondary asleep at boot) waits, so the widgets do not land on the wrong display; the primary
    // stands in once the index has been missing for the grace period.
    private DisplayTopologyEntryDto? ResolveTarget(OverlaySettings overlay, DisplayTopologyResponse topology, out bool indexMissing)
    {
        indexMissing = false;
        var displays = topology.Displays;
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? (displays.Count == 1 ? displays[0] : null);
        DisplayTopologyEntryDto? target;
        if (Os == HostOs.MacOS || overlay.Monitor < 0)
        {
            target = primary;
        }
        else
        {
            var id = _monitors.DisplayIdForIndex(overlay.Monitor);
            target = id is null ? null : displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            if (target is null)
            {
                indexMissing = true;
                var since = overlay.MonitorMissingSinceMs;
                if (since is null || NowMs() - since.Value < MissingMonitorGraceMs) return null;
                indexMissing = false;
                target = primary;
            }
        }

        if (target is { IsY70: true } || target is { HostingSupported: false }) target = primary;
        return target is null || target.IsY70 || !target.HostingSupported ? null : target;
    }

    private void NoteMissingMonitor(OverlaySettings overlay)
    {
        if (overlay.MonitorMissingSinceMs is not null) return;
        var now = NowMs();
        _store.Update(s => s.Overlay.MonitorMissingSinceMs ??= now);
        _store.FlushNow();
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
