using System;
using System.Collections.Generic;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hyte.Y70Display;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Merge layer for GET /displays/topology: raw OS facts from the platform
/// provider, stamped with Nexus panel state (Y70 auto-panel detection via
/// the DDC controller-name fragments, hosting support, monitor-panel
/// assignments). Also keeps a short-lived attached-id cache so record
/// serialization can stamp displayAttached without a helper RPC per request.
/// </summary>
public sealed class DisplayTopologyService
{
    /// <summary>Kiosk hosting: nexus-overlay on Windows, the overlay-helper
    /// sidecar on macOS, a user-session Chromium kiosk on Linux.</summary>
    public static bool HostingSupportedOnHost => HostingSupportedOverrideForTests
        ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    /// <summary>Promoted-monitor rotation goes through ChangeDisplaySettingsEx;
    /// macOS has no public rotation API and Linux layout is compositor-owned.</summary>
    public static bool RotationSupportedOnHost => OperatingSystem.IsWindows();

    /// <summary>"Keep panel clear of other windows" is the overlay's
    /// PanelMonitorGuard, Windows-only. macOS kiosks sit above app windows
    /// by level; Linux kiosks rely on the compositor.</summary>
    public static bool ReserveSupportedOnHost => OperatingSystem.IsWindows();

    /// <summary>Lets the promote/demote integration tests run on any host OS.</summary>
    internal static bool? HostingSupportedOverrideForTests;

    private const string HelperUnavailableHint =
        "Displays are enumerated in the desktop session; the Nexus helper is not connected yet.";
    private const int AttachedIdsMaxAgeMs = 5000;

    private readonly IDisplayTopologyProvider _provider;
    private readonly PanelDeviceRegistry _panelRegistry;
    private readonly Nexus.Service.Persistence.IConfigStore? _store;
    private readonly object _cacheLock = new();
    private HashSet<string>? _attachedIds;
    // Display id of the attached Y70 panel monitor ("" when none); the
    // brightness controller routes that id through the Y70 provider.
    private string _y70DisplayId = "";
    private string _ddcOnlyY70Variant = "";
    private long _attachedIdsAtMs;
    private bool _attachedIdsValid;

    public DisplayTopologyService(IDisplayTopologyProvider provider, PanelDeviceRegistry panelRegistry, Nexus.Service.Persistence.IConfigStore? store = null)
    {
        _provider = provider;
        _panelRegistry = panelRegistry;
        _store = store;
    }

    /// <summary>Display ids with brightness control turned off. Read per
    /// topology build rather than cached: the list changes from a toggle, not
    /// on a hot path.</summary>
    private HashSet<string> DdcDisabled()
        => new(_store?.Load().Devices.DdcDisabledDisplays ?? new List<string>(), StringComparer.Ordinal);

    public DisplayTopologyResponse GetTopology()
    {
        var raw = _provider.Enumerate();
        CacheAttachedIds(raw);
        var response = new DisplayTopologyResponse
        {
            HostingSupported = HostingSupportedOnHost,
            RotationSupported = RotationSupportedOnHost,
            ReserveSupported = ReserveSupportedOnHost,
            PositionsAvailable = _provider.PositionsAvailable,
            Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Hint = raw is null ? HelperUnavailableHint : "",
        };
        if (raw is null) return response;

        var ddcDisabled = DdcDisabled();
        foreach (var info in raw)
        {
            var isY70 = IsY70Display(info.RawHardwareId);
            // A disabled (turned-off) panel keeps its record but the display
            // reads as unassigned: the UI offers "Use as Nexus panel", which
            // re-activates the same record.
            var assigned = _panelRegistry.FindByDisplayId(info.Id);
            if (assigned?.Enabled == false) assigned = null;
            response.Displays.Add(new DisplayTopologyEntryDto
            {
                Id = info.Id,
                Number = info.Number,
                Name = info.Name,
                Manufacturer = info.Manufacturer,
                Model = info.Model,
                Bounds = info.X is int x && info.Y is int y
                    ? new DisplayBoundsDto { X = x, Y = y, Width = info.Width, Height = info.Height }
                    : null,
                Resolution = new DisplaySizeDto { Width = info.ResolutionWidth, Height = info.ResolutionHeight },
                ScaleFactor = info.Scale,
                Dpi = info.Dpi,
                IsPrimary = info.IsPrimary,
                IsInternal = info.IsInternal,
                IsTouch = info.IsTouch,
                Orientation = info.Orientation,
                IsY70 = isY70,
                DdcEnabled = !ddcDisabled.Contains(info.Id),
                HostingSupported = HostingSupportedOnHost && !isY70,
                AssignedPanelDeviceId = assigned?.Id,
                AssignedPanelName = assigned?.DisplayName,
            });
        }
        return response;
    }

    /// <summary>One entry from the current topology, or null when absent/unknown.</summary>
    public DisplayTopologyEntryDto? FindDisplay(string displayId)
    {
        foreach (var entry in GetTopology().Displays)
        {
            if (string.Equals(entry.Id, displayId, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Ids of currently-attached displays for displayAttached stamping. Null
    /// = topology unknown (no helper). Served from a short-lived cache; a
    /// stale cache re-enumerates inline.
    /// </summary>
    public HashSet<string>? GetAttachedIds()
    {
        if (TryGetCached(out var ids, out _, out _)) return ids;
        return CacheAttachedIds(_provider.Enumerate());
    }

    /// <summary>
    /// Whether any currently-attached display matches the Y70's DDC/EDID
    /// hardware-id fragments. Served from the same short-lived cache as
    /// <see cref="GetAttachedIds"/> so device-list polling never issues a
    /// helper RPC per call.
    /// </summary>
    public bool HasY70Display() => Y70DisplayId().Length > 0;

    /// <summary>
    /// Id of the attached Y70 panel monitor in the /displays id space, or
    /// empty. Same cache as <see cref="HasY70Display"/>.
    /// </summary>
    public string Y70DisplayId()
    {
        if (TryGetCached(out _, out var y70Id, out _)) return y70Id;
        CacheAttachedIds(_provider.Enumerate());
        lock (_cacheLock) return _y70DisplayId;
    }

    /// <summary>
    /// Variant key of the attached DDC-only Y70 panel (GW / Ina, matched by
    /// EDID fragment), or empty. These panels expose no USB serial function,
    /// so their handler must not treat a missing serial connection as a
    /// fault, and the EDID match is their only variant signal.
    /// </summary>
    public string DdcOnlyY70Variant()
    {
        if (TryGetCached(out _, out _, out var ddcOnly)) return ddcOnly;
        CacheAttachedIds(_provider.Enumerate());
        lock (_cacheLock) return _ddcOnlyY70Variant;
    }

    private bool TryGetCached(out HashSet<string>? ids, out string y70DisplayId, out string ddcOnlyVariant)
    {
        lock (_cacheLock)
        {
            var now = Environment.TickCount64;
            if (_attachedIdsValid && now - _attachedIdsAtMs <= AttachedIdsMaxAgeMs)
            {
                ids = _attachedIds;
                y70DisplayId = _y70DisplayId;
                ddcOnlyVariant = _ddcOnlyY70Variant;
                return true;
            }
        }
        ids = null;
        y70DisplayId = "";
        ddcOnlyVariant = "";
        return false;
    }

    private HashSet<string>? CacheAttachedIds(IReadOnlyList<RawDisplayInfo>? raw)
    {
        HashSet<string>? ids = null;
        var y70DisplayId = "";
        var ddcOnlyVariant = "";
        if (raw is not null)
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in raw)
            {
                ids.Add(info.Id);
                if (y70DisplayId.Length == 0 && IsY70Display(info.RawHardwareId)) y70DisplayId = info.Id;
                if (ddcOnlyVariant.Length == 0)
                    ddcOnlyVariant = Y70DisplayProtocol.DdcOnlyVariantForHardwareId(info.RawHardwareId);
            }
        }
        lock (_cacheLock)
        {
            _attachedIds = ids;
            _y70DisplayId = y70DisplayId;
            _ddcOnlyY70Variant = ddcOnlyVariant;
            _attachedIdsAtMs = Environment.TickCount64;
            _attachedIdsValid = true;
        }
        if (raw is not null) SyncPromotedPanelCapabilities(raw);
        return ids;
    }

    /// <summary>
    /// Fires with the record ids whose capabilities were refreshed by
    /// <see cref="SyncPromotedPanelCapabilities"/>, so a hub-owning listener
    /// can broadcast panel/device (this service has no hub reference). The
    /// Windows and macOS topology watchers subscribe; on Linux records still
    /// refresh but clients pick the change up on their next fetch instead of
    /// a push.
    /// </summary>
    public event Action<IReadOnlyList<string>>? PromotedPanelCapabilitiesChanged;

    /// <summary>
    /// Re-derives promote-time capabilities for every enabled display-bound
    /// record from the current OS facts. Promote stamps them once and the
    /// kiosk never self-reports, so rotation, scaling changes, and newly
    /// curated KnownPanelDisplays facts (dpi/family) would otherwise stay
    /// stale forever. Writes only when something differs. Best-effort: a
    /// store fault must not fail the topology read it rides on.
    /// </summary>
    private void SyncPromotedPanelCapabilities(IReadOnlyList<RawDisplayInfo> raw)
    {
        try
        {
            List<string>? changed = null;
            foreach (var info in raw)
            {
                var record = _panelRegistry.FindByDisplayId(info.Id);
                if (record is null || record.Enabled == false) continue;
                if (record.Capabilities?.Surface is { } surface && surface != PanelSurfaces.Monitor) continue;
                var caps = BuildPromotedCapabilities(
                    info.Manufacturer, info.Model, info.Name,
                    info.ResolutionWidth, info.ResolutionHeight,
                    info.Scale, info.IsTouch, info.Orientation);
                // Grid is kiosk-reported on self-registered panels; carry any
                // stored value so the rebuild never clears it.
                caps.Grid = record.Capabilities?.Grid;
                if (_panelRegistry.RefreshDisplayCapabilities(info.Id, caps))
                    (changed ??= new List<string>()).Add(record.Id);
            }
            if (changed is not null) PromotedPanelCapabilitiesChanged?.Invoke(changed);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[displays] promoted capability sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Viewport + identity hints for a promoted-monitor record, derived from
    /// OS facts. The kiosk loads /panel/{id} directly and never runs the
    /// self-report path, so these values are what the dashboard editor and
    /// the kiosk grid read.
    /// </summary>
    internal static PanelDeviceCapabilities BuildPromotedCapabilities(
        string? manufacturer,
        string? model,
        string? name,
        int resolutionWidth,
        int resolutionHeight,
        double? scaleFactor,
        bool isTouch,
        string? orientation)
    {
        var scale = scaleFactor is > 0 ? scaleFactor.Value : 1.0;
        var known = KnownPanelDisplays.Match(manufacturer, model, name);
        return new PanelDeviceCapabilities
        {
            Surface = PanelSurfaces.Monitor,
            // Touch widgets are placeable when an integrated touch digitizer
            // targets this monitor (Windows pointer-device association), or when
            // a curated known display asserts touch - the association is
            // unreliable and misses some panels (e.g. the Xeneon Edge, a touch
            // strip). Plain monitors with neither behave like the Q-series.
            Touch = isTouch || (known?.Touch ?? false),
            Orientation = string.IsNullOrEmpty(orientation) ? null : orientation,
            CssWidth = (int)Math.Round(resolutionWidth / scale),
            CssHeight = (int)Math.Round(resolutionHeight / scale),
            Dpr = scale,
            Dpi = known?.Dpi,
            Family = known?.Family,
        };
    }

    internal static bool IsY70Display(string rawHardwareId)
    {
        if (string.IsNullOrEmpty(rawHardwareId)) return false;
        foreach (var fragment in Y70DisplayProtocol.DdcPanelHardwareNames)
        {
            if (rawHardwareId.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
