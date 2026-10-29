using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Panel;

/// <summary>
/// Hands the legacy desktop-widget layout (<c>Overlay.Layout</c>) to a per-display panel record as a
/// <see cref="PanelDeviceRecord.DesktopImport"/>, which the panel's own web client places on its grid.
/// Once the widgets are handed over, the Overlay block's legacy fields are cleared and only its markers stay,
/// so a pre-migration build opened on the same settings sees no widgets.
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

    /// <summary>Monotonic, so a wall-clock jump neither shortens nor stretches the grace period.</summary>
    internal Func<long> MonoMs { get; set; } = () => Environment.TickCount64;

    /// <summary>How often a deferral on a missing index re-checks on its own, so the fallback needs no display change.</summary>
    internal int RecheckIntervalMs { get; set; } = 60 * 60 * 1000;

    internal bool RecheckArmed { get; private set; }

    private System.Threading.Timer? _recheck;
    private long? _lastMissingCheckMs;

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
        if (!priorMigrated || s.Overlay.MigratedToPanels || !s.Overlay.HasWidgets) return;
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
            if (Os == HostOs.Linux)
            {
                // No host here, so widgets stay for a Windows or macOS machine that opens the same profile.
                // A block without any holds nothing worth keeping.
                if (_store.Load().Overlay is { HasWidgets: false } idle && HasLegacyFields(idle))
                {
                    // A profile switch may have swapped the block since the snapshot.
                    _store.Update(s =>
                    {
                        if (!s.Overlay.HasWidgets) s.Overlay.ClearLegacy();
                    });
                    _store.FlushNow();
                }
                return null;
            }
            // The monitor map is how a new Windows overlay host announces itself. An older host keeps
            // drawing the legacy widgets, so nothing may be handed over or hidden until the map arrives.
            if (Os == HostOs.Windows && !_monitors.HasMap) return null;
            var settings = _store.Load();
            if (settings.Overlay.MigratedToPanels)
            {
                ClearMigratedLegacy(settings.Overlay);
                return null;
            }

            if (!settings.Overlay.HasWidgets)
            {
                _store.Update(s =>
                {
                    // Re-checked against the live block: a profile switch may have loaded one holding widgets.
                    if (s.Overlay.MigratedToPanels || s.Overlay.HasWidgets) return;
                    s.Overlay.MigratedToPanels = true;
                    s.Overlay.ClearLegacy();
                });
                Finish(null);
                return null;
            }

            topology ??= _topology?.GetTopology();
            if (topology is null || topology.Displays.Count == 0) return null;
            var target = ResolveTarget(settings, topology, out var indexMissing);
            if (indexMissing)
            {
                ArmRecheck();
            }
            else
            {
                DisarmRecheck();
                ClearMissing(settings.Overlay);
            }
            if (target is null) return null;

            string? recordId = null;
            var y70RecordGone = false;
            _store.Update(s =>
            {
                if (s.Overlay.MigratedToPanels) return;
                var overlay = s.Overlay;
                var widgets = Migratable(overlay).ToList();
                if (widgets.Count == 0)
                {
                    overlay.MigratedToPanels = true;
                    overlay.MonitorMissingForMs = null;
                    overlay.ClearLegacy();
                    return;
                }

                // A Y70 is a known panel: its own record takes the widgets and keeps every setting it has.
                var onY70 = target.IsY70;
                var record = onY70
                    ? PanelDeviceRegistry.FindNewestY70(s)
                    : s.PanelDevices.Values.FirstOrDefault(r => string.Equals(r.DisplayId, target.Id, StringComparison.Ordinal));
                if (onY70 && record is null)
                {
                    y70RecordGone = true;
                    return;
                }
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
                else if (!onY70 && !PanelHosting.IsOn(record))
                {
                    ApplyDesktopFlags(record, overlay);
                }

                AddImport(record, overlay, merge, widgets);
                overlay.MigratedToPanels = true;
                overlay.MigratedRecordId = record.Id;
                overlay.MonitorMissingForMs = null;
                overlay.ClearLegacy();
                recordId = record.Id;
            });
            if (y70RecordGone)
            {
                // The record vanished after the pre-check; keep the recheck armed so a later pass retries.
                ArmRecheck();
                return null;
            }
            Finish(recordId);
            return recordId;
        }
    }

    // Read under the store lock: the Y70 kiosk adds its record concurrently.
    private bool HasY70Record()
    {
        var found = false;
        _store.Read(s => found = PanelDeviceRegistry.FindNewestY70(s) is not null);
        return found;
    }

    /// <summary>
    /// Whether a host is worth running for the pending widgets: the Windows monitor map comes from the host itself,
    /// and a target display that exists or may still appear can take them. False when no pass could ever resolve one.
    /// </summary>
    public bool HostCanAdvance(NexusSettings s, DisplayTopologyResponse? topology = null)
    {
        if (Os == HostOs.Linux) return true;
        if (Os == HostOs.Windows && !_monitors.HasMap) return true;
        var displays = (topology ?? _topology?.GetTopology())?.Displays;
        if (displays is null || displays.Count == 0) return true;
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? (displays.Count == 1 ? displays[0] : null);
        // A Y70 takes the widgets on its own record, which may still register, so it stays a usable target.
        var overlay = s.Overlay;
        // Once the grace has elapsed with no Y70 record, no pass can ever use it, so it is not worth a host.
        var y70Waitable = HasY70Record() || (overlay.MonitorMissingForMs ?? 0) < MissingMonitorGraceMs;
        bool Usable(DisplayTopologyEntryDto? d) => d is not null && (d.IsY70 ? y70Waitable : d.HostingSupported);
        if (Os == HostOs.MacOS || (overlay.Monitor ?? -1) < 0) return Usable(primary);
        var id = _monitors.DisplayIdForIndex(overlay.Monitor ?? -1);
        var target = id is null ? null : displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        // A legacy index not mapped yet may still appear, and the primary stands in after the grace.
        return target is null || Usable(target) || Usable(primary);
    }

    // A migrated block still holds legacy fields when an older build migrated without clearing them, or when
    // a build that kept the markers wrote widgets after the migration. Widgets the record holds by id neither
    // on its layout nor in a pending import are handed over first, so only copies are cleared. A missing record
    // keeps the block, and the live block is re-checked so a profile switch cannot lose another profile's.
    private void ClearMigratedLegacy(LegacyOverlaySettings snapshot)
    {
        if (snapshot.MigratedRecordId is null || !HasLegacyFields(snapshot)) return;
        var cleared = false;
        string? imported = null;
        _store.Update(s =>
        {
            var o = s.Overlay;
            if (!o.MigratedToPanels || o.MigratedRecordId is not { } id || !HasLegacyFields(o)) return;
            if (!s.PanelDevices.TryGetValue(id, out var record)) return;
            var held = (record.Layout?.Pages ?? new List<PanelPageDto>())
                .SelectMany(p => p.Widgets.Select(w => w.Id))
                .Concat(record.DesktopImport?.Widgets.Select(w => w.Id) ?? Enumerable.Empty<string>())
                .ToHashSet(StringComparer.Ordinal);
            var missing = Migratable(o).Where(w => !held.Contains(w.Id)).ToList();
            if (missing.Count > 0)
            {
                AddImport(record, o, merge: true, missing);
                imported = id;
            }
            o.ClearLegacy();
            cleared = true;
        });
        if (imported is not null)
        {
            ServiceLog.Info($"[desktop-migration] handed legacy widgets missing from {imported} to it after the migration");
            Finish(imported);
        }
        else if (cleared)
        {
            _store.FlushNow();
        }
    }

    private static IEnumerable<LegacyOverlayWidget> Migratable(LegacyOverlaySettings overlay) =>
        (overlay.Layout ?? new List<LegacyOverlayWidget>())
            .Where(w => !string.Equals(w.Type, "discord", StringComparison.Ordinal))
            .Select(CopyWidget);

    private void AddImport(PanelDeviceRecord record, LegacyOverlaySettings overlay, bool merge, List<LegacyOverlayWidget> widgets)
    {
        var existing = record.DesktopImport;
        record.DesktopImport = new DesktopImportDto
        {
            Version = 1,
            CellPx = existing?.CellPx ?? LegacyCellPx * Math.Clamp(overlay.Scale ?? 100, MinScalePercent, MaxScalePercent) / 100.0,
            CellUnits = existing?.CellUnits ?? (Os == HostOs.Windows ? "device" : "css"),
            Merge = merge || existing?.Merge == true,
            Widgets = existing is null ? widgets : existing.Widgets.Concat(widgets).ToList(),
        };
    }

    // Windows only reaches here with a posted map. A legacy index the map or the topology does not hold
    // yet (a secondary asleep at boot) waits, so the widgets do not land on the wrong display; the primary
    // stands in once the index has been missing for the grace period.
    private DisplayTopologyEntryDto? ResolveTarget(NexusSettings settings, DisplayTopologyResponse topology, out bool indexMissing)
    {
        var overlay = settings.Overlay;
        indexMissing = false;
        var displays = topology.Displays;
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? (displays.Count == 1 ? displays[0] : null);
        DisplayTopologyEntryDto? target;
        if (Os == HostOs.MacOS || (overlay.Monitor ?? -1) < 0)
        {
            target = primary;
        }
        else
        {
            var id = _monitors.DisplayIdForIndex(overlay.Monitor ?? -1);
            target = id is null ? null : displays.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            if (target is null)
            {
                indexMissing = true;
                if (!MissingGraceElapsed(overlay)) return null;
                indexMissing = false;
                target = primary;
            }
        }

        if (target is { IsY70: true })
        {
            if (HasY70Record()) return target;
            // The Y70 kiosk registers its record on first run; wait for it, then let the primary stand in.
            indexMissing = true;
            if (!MissingGraceElapsed(overlay)) return null;
            // The primary stands in; a Y70 primary still has no record, so keep waiting on the recheck.
            if (primary is { IsY70: true }) return null;
            indexMissing = false;
            target = primary;
        }
        else if (target is { HostingSupported: false })
        {
            target = primary;
        }
        return target is null || target.IsY70 || !target.HostingSupported ? null : target;
    }

    // Missing time accumulates from monotonic deltas between checks and is persisted at each one. A restart
    // adds nothing for the time it was down, so the grace can only run long, never short.
    private bool MissingGraceElapsed(LegacyOverlaySettings overlay)
    {
        var mono = MonoMs();
        var delta = _lastMissingCheckMs is { } last ? Math.Max(0, mono - last) : 0;
        _lastMissingCheckMs = mono;
        var total = (overlay.MonitorMissingForMs ?? 0) + delta;
        if (total != overlay.MonitorMissingForMs)
        {
            _store.Update(s => s.Overlay.MonitorMissingForMs = total);
            _store.FlushNow();
        }
        return total >= MissingMonitorGraceMs;
    }

    private void ClearMissing(LegacyOverlaySettings overlay)
    {
        _lastMissingCheckMs = null;
        if (overlay.MonitorMissingForMs is null) return;
        _store.Update(s => s.Overlay.MonitorMissingForMs = null);
    }

    private void ArmRecheck()
    {
        RecheckArmed = true;
        _recheck ??= new System.Threading.Timer(_ => OnRecheck(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _recheck.Change(RecheckIntervalMs, System.Threading.Timeout.Infinite);
    }

    private void DisarmRecheck()
    {
        RecheckArmed = false;
        _recheck?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    private void OnRecheck()
    {
        try { Run(); }
        catch (Exception ex) { ServiceLog.Error($"[desktop-migration] {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void ApplyDesktopFlags(PanelDeviceRecord record, LegacyOverlaySettings overlay)
    {
        record.Backdrop = "desktop";
        record.ClickThrough = true;
        record.AlwaysOnTop = overlay.AlwaysOnTop ?? false;
        record.ReserveMonitor = false;
        record.KeepCursorOff = false;
        record.WidgetOpacity = Math.Clamp(overlay.Opacity ?? PanelDeviceRegistry.FullWidgetOpacity, 0, 1);
        record.ThemeSyncWithDesktop = true;
        record.AccentSyncWithDesktop = true;
        record.Hosted = overlay.Enabled ?? false;
        record.Enabled = false;
    }

    // A null layout would seed the starter widgets underneath the imported ones.
    private static PanelLayoutDto EmptyLayout() => new()
    {
        LayoutSchemaVersion = 2,
        Surface = PanelSurfaces.Monitor,
        Pages = new List<PanelPageDto> { new() { Id = Guid.NewGuid().ToString() } },
    };

    private static LegacyOverlayWidget CopyWidget(LegacyOverlayWidget w) => new()
    {
        Id = w.Id,
        Type = AppPrefixMigration.Rewrite(w.Type),
        Size = w.Size,
        Monitor = w.Monitor,
        Col = w.Col,
        Row = w.Row,
        Locked = w.Locked,
        Config = w.Config is null ? null : new Dictionary<string, System.Text.Json.JsonElement>(w.Config),
    };

    private static bool HasLegacyFields(LegacyOverlaySettings o) =>
        o.Enabled is not null || o.AlwaysOnTop is not null || o.Scale is not null || o.Opacity is not null
        || o.Monitor is not null || o.Layout is not null;

    private void Finish(string? recordId)
    {
        _store.FlushNow();
        if (_hub is null) return;
        if (recordId is not null) PanelTopics.BroadcastPanelDevice(_hub, recordId);
        PanelTopics.BroadcastDisplays(_hub);
        PanelTopics.BroadcastPrefs(_hub);
    }
}
