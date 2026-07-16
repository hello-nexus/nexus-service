using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// One registered panel device. Keyed by an opaque <c>Id</c> the service
/// allocates on first connect; the device caches the id locally
/// (cookie + localStorage) so the URL <c>/panel/{Id}</c> stays stable
/// across reconnects. If the device clears its storage, it shows up as a
/// new record.
/// </summary>
public sealed class PanelDeviceRecord
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PanelLayoutDto? Layout { get; set; }
    public string? ThemeMode { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string? BackgroundMode { get; set; }
    public string? BackgroundEffect { get; set; }
    public int? BackgroundTemplate { get; set; }
    /// <summary>
    /// Per-shader preset selection for THIS panel (effect key → preset index).
    /// The panel remembers which of the universal presets it points at for each
    /// shader, independent of the LEDs and other panels. The preset *contents*
    /// stay central (Lighting.Animate.Templates); this only records the choice.
    /// Absent/unset shaders default to preset 0. <see cref="BackgroundTemplate"/>
    /// is the active shader's entry, kept for rendering + back-compat.
    /// </summary>
    public Dictionary<string, int>? BackgroundTemplates { get; set; }
    public double? BackgroundOpacity { get; set; }
    /// <summary>Background layer on/off. False renders the panel page fully
    /// transparent so a kiosk-hosted panel (y70 / promoted monitor) shows the
    /// Windows desktop through. Null = enabled.</summary>
    public bool? BackgroundEnabled { get; set; }
    /// <summary>Selected background-media asset id (PanelBgLibrary) for THIS
    /// panel; null = none. Only set for local panels (y70 / q-series).</summary>
    public string? BackgroundMediaId { get; set; }
    /// <summary>Type of the selected background-media asset: "static" or
    /// "animated". Null when no asset is selected. Stored so the render layer
    /// does not need to refetch the library to know whether to use an img or
    /// video element.</summary>
    public string? BackgroundMediaType { get; set; }
    /// <summary>Frosted-glass blur over the background layer (shader / media /
    /// desktop wallpaper): "light" or "heavy". Null = none.</summary>
    public string? BackgroundFrost { get; set; }
    public double? WidgetOpacity { get; set; }
    public bool? WidgetLabels { get; set; }
    /// <summary>Percent 0-100. Null is unset; the client applies its own default.</summary>
    public double? WidgetPadding { get; set; }
    public bool? ThemeSyncWithDesktop { get; set; }
    public bool? AccentSyncWithDesktop { get; set; }
    public long FirstSeenAt { get; set; }
    public long LastSeenAt { get; set; }
    public PanelDeviceCapabilities? Capabilities { get; set; }
    /// <summary>
    /// Stable display id (GET /displays id space) when this record was
    /// created by promoting an OS monitor to a panel. Mutated only via the
    /// /displays/{id}/panel endpoints, never via the patch route. The Y70's
    /// auto-managed record never carries one.
    /// </summary>
    public string? DisplayId { get; set; }
    /// <summary>
    /// Per-panel "keep panel clear of other windows": the overlay evicts
    /// foreign windows from this panel's monitor while its kiosk is up.
    /// Display-bound (monitor) records only; null = default (true). The Y70
    /// kiosk keeps using the global Panel.ReserveMonitor preference.
    /// </summary>
    public bool? ReserveMonitor { get; set; }
    /// <summary>
    /// Follow the panel's hardware orientation sensor and apply the matching
    /// Windows display rotation (currently the Corsair Xeneon Edge only).
    /// Display-bound records only; null = default (true).
    /// </summary>
    public bool? AutoOrient { get; set; }
    /// <summary>
    /// Last known Corsair Xeneon Edge native display settings (vendor HID),
    /// applied/read through /displays/{id}/xeneon-settings. Display-bound
    /// xeneon-edge records only; a control's field is null until it has been
    /// read or set at least once through Nexus.
    /// </summary>
    public XeneonEdgeSettingsDto? XeneonEdgeSettings { get; set; }
    /// <summary>
    /// Whether this display-bound panel is currently turned ON (kiosk
    /// hosted). Turning a monitor's panel off keeps the record - layout,
    /// theme, and settings persist through off/on cycles; promote
    /// re-activates the same record. Null = enabled (back-compat).
    /// </summary>
    public bool? Enabled { get; set; }
    /// <summary>
    /// Route-computed on GET /panel/devices responses for display-bound
    /// records: false when the bound monitor is currently absent, null when
    /// topology is unknown. Never persisted (null on stored records).
    /// </summary>
    public bool? DisplayAttached { get; set; }
}

/// <summary>
/// Last-reported viewport hints from the device. Stored alongside the
/// device record so the dashboard can show "this is a 2x4 portrait
/// touch panel" without polling the device. <c>Surface</c> is the
/// widget-filtering classification (y70 / q60 / phone) the SPA infers
/// from its own viewport on first connect; widget filtering and size
/// constraints continue to switch on it unchanged.
/// </summary>
public sealed class PanelDeviceCapabilities
{
    public string? Surface { get; set; }
    public string? Grid { get; set; }
    public bool? Touch { get; set; }
    public string? Orientation { get; set; }
    // Viewport hints reported by the kiosk SPA itself - the kiosk knows its
    // own CSS viewport and devicePixelRatio (== Windows display scaling on
    // the panel monitor). The dashboard reads these to size the simulator
    // iframe to match the actual hardware instead of the hardcoded profile.
    public int? CssWidth { get; set; }
    public int? CssHeight { get; set; }
    public double? Dpr { get; set; }
    // Physical density (native px/inch) of a curated known display (see
    // KnownPanelDisplays). The grid capacity math needs real density and
    // Windows only exposes the scaling DPI, so it cannot come from the OS.
    public double? Dpi { get; set; }
    /// <summary>Curated display family id (e.g. "xeneon-edge") driving
    /// web-side branding (sidebar name + icon).</summary>
    public string? Family { get; set; }
}

/// <summary>
/// Corsair Xeneon Edge native display settings. Used both as the persisted
/// snapshot on <see cref="PanelDeviceRecord.XeneonEdgeSettings"/> and as the
/// GET/POST body for /displays/{id}/xeneon-settings - a POST only carries
/// the fields being changed, the rest are left null and untouched.
/// </summary>
public sealed class XeneonEdgeSettingsDto
{
    public int? Brightness { get; set; }
    public int? Backlight { get; set; }
    public int? Contrast { get; set; }
    public int? Red { get; set; }
    public int? Green { get; set; }
    public int? Blue { get; set; }
}

/// <summary>
/// Partial update body for POST /panel/devices/{id}. Every field is
/// nullable so the handler can tell "client left this out" from "client
/// explicitly cleared this".
/// </summary>
public sealed class PanelDevicePatch
{
    public string? DisplayName { get; set; }
    public PanelLayoutDto? Layout { get; set; }
    public string? ThemeMode { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string? BackgroundColorLight { get; set; }
    public string? BackgroundMode { get; set; }
    public string? BackgroundEffect { get; set; }
    public int? BackgroundTemplate { get; set; }
    /// <summary>Full per-shader preset map to replace this panel's selection
    /// (effect key → preset index). The client sends the whole map.</summary>
    public Dictionary<string, int>? BackgroundTemplates { get; set; }
    public double? BackgroundOpacity { get; set; }
    public bool? BackgroundEnabled { get; set; }
    public string? BackgroundMediaId { get; set; }
    public string? BackgroundMediaType { get; set; }
    public string? BackgroundFrost { get; set; }
    public double? WidgetOpacity { get; set; }
    public bool? WidgetLabels { get; set; }
    public double? WidgetPadding { get; set; }
    public bool? ThemeSyncWithDesktop { get; set; }
    public bool? AccentSyncWithDesktop { get; set; }
    /// <summary>Display-bound records only; ignored for other panels.</summary>
    public bool? ReserveMonitor { get; set; }
    /// <summary>Display-bound records only; ignored for other panels.</summary>
    public bool? AutoOrient { get; set; }
    public PanelDeviceCapabilities? Capabilities { get; set; }
}

public sealed class PanelDeviceListResponse
{
    public List<PanelDeviceRecord> Devices { get; set; } = new();
}

public sealed class PanelDeviceCreateBody
{
    public string? DisplayName { get; set; }
    public PanelDeviceCapabilities? Capabilities { get; set; }
}

/// <summary>
/// Multiplex frame: control-state mutation happened. Subscribers refetch
/// the canonical resource. <c>Revision</c> is unix-ms so clients can
/// echo-suppress their own writes.
/// </summary>
public sealed class PrefsChangedFrame
{
    public long Revision { get; set; }
}

public sealed class LightingChangedFrame
{
    public long Revision { get; set; }
}

/// <summary>
/// Multiplex frame: a community LED mapping was auto-applied to a newly seen
/// device. Carries the payload directly (toast UX with one-click undo);
/// subscribers also refetch lighting state via the regular lighting topic.
/// </summary>
public sealed class MappingAutoAppliedFrame
{
    public long Revision { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string MappingId { get; set; } = "";
    public string MappingName { get; set; } = "";
    public int AdopterCount { get; set; }
}

public sealed class CoolingChangedFrame
{
    public long Revision { get; set; }
}

public sealed class VolumeChangedFrame
{
    public long Revision { get; set; }
}

public sealed class GalleryChangedFrame
{
    public long Revision { get; set; }
}

public sealed class MediaLibraryChangedFrame
{
    public long Revision { get; set; }
}

/// <summary>
/// Multiplex frame: the console user's desktop wallpaper changed. Panels in
/// wallpaper-background mode refetch <c>GET /panel/desktop-wallpaper</c>.
/// </summary>
public sealed class DesktopWallpaperChangedFrame
{
    public long Revision { get; set; }
}

/// <summary>
/// Multiplex frame: the host's network address changed (VPN toggle, Wi-Fi↔wired
/// switch, DHCP renew), so any displayed pairing QR now embeds a stale LAN IP.
/// Subscribers re-fetch <c>GET /panel/phone/pair-qr</c> to mint a fresh QR for
/// the current address. Content-less beyond the echo-suppress revision.
/// </summary>
public sealed class PairQrRefreshFrame
{
    public long Revision { get; set; }
}

/// <summary>
/// Multiplex frame: the OS accent colour changed. Only the Linux service emits
/// it - Windows/macOS push the accent straight from their native shell. The
/// dashboard applies <see cref="Hex"/> live when the accent source is "system",
/// matching how light/dark already tracks the OS in real time.
/// </summary>
public sealed class SystemAccentFrame
{
    public string Hex { get; set; } = "";
}

/// <summary>
/// Multiplex frame: cooling-device warning state changed (e.g. NP50 AmpScale
/// current overload, LED count exceeded). Subscribers refetch
/// <c>GET /cooling/warnings</c>; <c>DeviceId</c> lets a UI scope the refetch
/// or restrict toast firing.
/// </summary>
public sealed class CoolingWarningsChangedFrame
{
    public long Revision { get; set; }
    public string DeviceId { get; set; } = "";
}

public sealed class DevicesChangedFrame
{
    public long Revision { get; set; }
}

/// <summary>
/// Multiplex frame: a per-device record changed (layout, theme, name,
/// capabilities). <c>DeviceId</c> lets a panel filter to its own record
/// and ignore frames meant for other devices.
/// </summary>
public sealed class PanelDeviceChangedFrame
{
    public long Revision { get; set; }
    public string DeviceId { get; set; } = "";
}
