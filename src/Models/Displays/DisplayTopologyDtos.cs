using System.Collections.Generic;

namespace Nexus.Service.Models.Displays;

/// <summary>
/// OS-level facts about one attached monitor, as reported by the platform
/// topology provider. Coordinates are virtual-desktop units in whatever space
/// the OS lays monitors out in (physical px on Windows under a per-monitor
/// DPI-aware thread, points on macOS); positions are only comparable within
/// one snapshot. <c>X</c>/<c>Y</c> are null when the platform cannot report
/// layout (Linux DRM sysfs), in which case <c>Width</c>/<c>Height</c> still
/// carry the monitor's size for aspect-correct rendering.
/// </summary>
public sealed class RawDisplayInfo
{
    /// <summary>Stable id, byte-identical to the id space of GET /displays.</summary>
    public string Id { get; set; } = "";
    /// <summary>
    /// The monitor number Windows Settings shows (DISPLAYCONFIG source id + 1
    /// on Windows); 0 = unknown. Falls back to the GDI \\.\DISPLAYn ordinal
    /// when DISPLAYCONFIG can't resolve the adapter.
    /// </summary>
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Native pixels of the current (or preferred) mode.</summary>
    public int ResolutionWidth { get; set; }
    public int ResolutionHeight { get; set; }
    /// <summary>Current mode's refresh rate in Hz; 0 when unknown.</summary>
    public int RefreshHz { get; set; }
    /// <summary>OS scale factor (1.5 = 150%); null when unknown.</summary>
    public double? Scale { get; set; }
    /// <summary>Physical dots-per-inch estimate; null when unknown.</summary>
    public double? Dpi { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsInternal { get; set; }
    /// <summary>An integrated touch digitizer targets this monitor (Windows pointer-device association).</summary>
    public bool IsTouch { get; set; }
    /// <summary>"Landscape" | "Portrait" | "LandscapeFlipped" | "PortraitFlipped"; "" when unknown.</summary>
    public string Orientation { get; set; } = "";
    /// <summary>
    /// Un-sanitized PnP/EDID hardware id (e.g. \\?\DISPLAY#RTK0004#...) used
    /// for Y70 controller-name matching. Never sent to clients.
    /// </summary>
    public string RawHardwareId { get; set; } = "";
    /// <summary>GPU adapter DeviceString driving this monitor (Windows); "" elsewhere or when unknown.</summary>
    public string AdapterName { get; set; } = "";
    /// <summary>EDID-derived hardware identity that survives a changed <see cref="Id"/>; null when unreadable.</summary>
    public DisplayFingerprint? Fingerprint { get; set; }
}

/// <summary>
/// Hardware identity of a monitor independent of its OS display id (which
/// embeds the connection instance on Windows and falls back to an index).
/// Manufacturer is the three-letter EISA id and Product the hex product
/// code, the same text the display id carries.
/// </summary>
public sealed class DisplayFingerprint
{
    public string Manufacturer { get; set; } = "";
    public string Product { get; set; } = "";
    /// <summary>Numeric EDID serial; 0 when the monitor reports none.</summary>
    public long Serial { get; set; }
    /// <summary>EDID serial descriptor string; null when absent.</summary>
    public string? SerialText { get; set; }
}

public sealed class DisplayBoundsDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class DisplaySizeDto
{
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>One monitor in GET /displays/topology, merged with panel state.</summary>
public sealed class DisplayTopologyEntryDto
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Null when the platform cannot report monitor positions.</summary>
    public DisplayBoundsDto? Bounds { get; set; }
    public DisplaySizeDto Resolution { get; set; } = new();
    public double? ScaleFactor { get; set; }
    public double? Dpi { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsInternal { get; set; }
    /// <summary>An integrated touch digitizer targets this monitor.</summary>
    public bool IsTouch { get; set; }
    /// <summary>Current OS rotation; "" when unknown.</summary>
    public string Orientation { get; set; } = "";
    /// <summary>The Y70 panel's own monitor: auto-managed, never promotable here.</summary>
    public bool IsY70 { get; set; }
    /// <summary>Whether this display can host a Nexus panel kiosk.</summary>
    public bool HostingSupported { get; set; }
    /// <summary>False when the user turned brightness control off for this display; Nexus then sends it no DDC/CI at all.</summary>
    public bool DdcEnabled { get; set; } = true;
    public string? AssignedPanelDeviceId { get; set; }
    public string? AssignedPanelName { get; set; }
    /// <summary>Server-side only: lets a promote record the fingerprint at once instead of waiting for a relink scan.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DisplayFingerprint? Fingerprint { get; set; }
}

public sealed class DisplayTopologyResponse
{
    /// <summary>Kiosk hosting availability on this host OS.</summary>
    public bool HostingSupported { get; set; }
    /// <summary>Promoted-monitor rotation availability on this host OS.</summary>
    public bool RotationSupported { get; set; }
    /// <summary>"Keep panel clear of other windows" availability on this host OS.</summary>
    public bool ReserveSupported { get; set; }
    /// <summary>False when monitors carry no positions (web lays them out in a row).</summary>
    public bool PositionsAvailable { get; set; }
    public long Revision { get; set; }
    public List<DisplayTopologyEntryDto> Displays { get; set; } = new();
    /// <summary>Non-empty when enumeration is degraded (e.g. helper not connected).</summary>
    public string Hint { get; set; } = "";
}

/// <summary>
/// Multiplex frame: display topology or panel assignment changed. Subscribers
/// refetch GET /displays/topology.
/// </summary>
public sealed class DisplaysChangedFrame
{
    public long Revision { get; set; }
}

/// <summary>Body for POST /displays/{id}/panel (promote a monitor to a panel).</summary>
public sealed class PanelPromoteBody
{
    /// <summary>Panel name override; defaults to the monitor's name.</summary>
    public string? DisplayName { get; set; }
}

public sealed class DisplayAssignmentDto
{
    public string DisplayId { get; set; } = "";
    public string PanelDeviceId { get; set; } = "";
    /// <summary>Per-panel "keep panel clear of other windows" (record setting; default true).</summary>
    public bool ReserveMonitor { get; set; } = true;
    /// <summary>Panel backdrop; "desktop" tells the overlay to host this kiosk
    /// as a transparent window so the live desktop shows through.</summary>
    public string Backdrop { get; set; } = "";
    /// <summary>Per-panel "keep the mouse off the panel" (record setting; default false).</summary>
    public bool KeepCursorOff { get; set; }
    /// <summary>Per-panel "keep the panel above other windows" (record setting; default true).</summary>
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>Per-panel click-through on empty space (record setting; default false). Only meaningful with Backdrop "desktop".</summary>
    public bool ClickThrough { get; set; }
    /// <summary>The bound display is the OS primary display. Hosts never apply the keep-off guards there.</summary>
    public bool IsPrimary { get; set; }
}

/// <summary>Body for POST /displays/{id}/rotation.</summary>
public sealed class DisplayRotationBody
{
    /// <summary>"Landscape" | "Portrait" | "LandscapeFlipped" | "PortraitFlipped".</summary>
    public string Orientation { get; set; } = "";
}

/// <summary>Body for POST /displays/touch-routing (macOS overlay helper).</summary>
public sealed class TouchRoutingReportBody
{
    /// <summary>Stable id of the display touch is routed to; "" when idle.</summary>
    public string DisplayId { get; set; } = "";
    /// <summary>"active" | "permission-needed" | "idle".</summary>
    public string State { get; set; } = "";
}

/// <summary>GET /displays/assignments - the overlay's kiosk reconcile input.</summary>
public sealed class DisplayAssignmentsResponse
{
    public List<DisplayAssignmentDto> Assignments { get; set; } = new();
    /// <summary>Backdrop of the Y70's own panel record. The Y70 kiosk opens
    /// from hardware detection rather than a display assignment, so the host
    /// has no entry above to read it from.</summary>
    public string PanelBackdrop { get; set; } = "";
}
