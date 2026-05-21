using System.Collections.Generic;

namespace Qos.Service.Models.Panel;

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
    public double? BackgroundOpacity { get; set; }
    public long FirstSeenAt { get; set; }
    public long LastSeenAt { get; set; }
    public PanelDeviceCapabilities? Capabilities { get; set; }
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
    public bool? Dock { get; set; }
    public string? Orientation { get; set; }
    // Viewport hints reported by the kiosk SPA itself - the kiosk knows its
    // own CSS viewport and devicePixelRatio (== Windows display scaling on
    // the panel monitor). The dashboard reads these to size the simulator
    // iframe to match the actual hardware instead of the hardcoded profile.
    public int? CssWidth { get; set; }
    public int? CssHeight { get; set; }
    public double? Dpr { get; set; }
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
    public double? BackgroundOpacity { get; set; }
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

public sealed class CoolingChangedFrame
{
    public long Revision { get; set; }
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
