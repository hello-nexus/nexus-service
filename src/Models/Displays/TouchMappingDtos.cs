using System.Collections.Generic;

namespace Nexus.Service.Models.Displays;

/// <summary>
/// One touch digitizer as Windows currently reports it: its raw-input device
/// interface path (GetRawInputDeviceInfoW RIDI_DEVICENAME - byte-identical to
/// the string Windows itself uses as the Digimon registry value name) and
/// which display id it is presently associated with.
/// </summary>
public sealed class TouchMapDigitizerInfo
{
    public string InterfacePath { get; set; } = "";
    public string ProductString { get; set; } = "";
    /// <summary>Display id currently associated by Windows; "" when unassociated.</summary>
    public string AssociatedDisplayId { get; set; } = "";
    /// <summary>
    /// True when the digitizer's devnode has a USB ancestor (CfgMgr32
    /// devnode-parentage walk). False for a laptop-integrated I2C/ACPI
    /// digitizer, and false when the walk itself fails - the generic
    /// touch-mapping tier treats both the same (fail closed).
    /// </summary>
    public bool IsUsbAttached { get; set; }
    /// <summary>
    /// Instance ids of the devices sharing a USB hub with this digitizer's
    /// nearest hub-port ancestor (CfgMgr32.GetUsbHubSiblingInstanceIds).
    /// Disambiguates descriptor-identical digitizers on different panels by
    /// what else shares their internal hub (see TouchPanelCatalogEntry.CompanionUsbIds).
    /// Empty when the walk fails or the digitizer has no such sibling.
    /// </summary>
    public List<string> CompanionHardwareIds { get; set; } = new();
}

/// <summary>
/// One display, keyed the same way GET /displays/topology keys it, plus its
/// monitor device interface path (EnumDisplayDevicesW
/// EDD_GET_DEVICE_INTERFACE_NAME) - the exact string Windows expects as the
/// Digimon registry value data.
/// </summary>
public sealed class TouchMapDisplayInfo
{
    public string Id { get; set; } = "";
    public string MonitorInterfacePath { get; set; } = "";
    /// <summary>
    /// EDID PnP identity split the same way WindowsDisplayIdentity.ResolveIdentity
    /// does (3-letter EISA manufacturer id, hex model code); "" when
    /// unresolved. Lets the generic touch-mapping tier match KnownPanelDisplays
    /// without re-parsing MonitorInterfacePath.
    /// </summary>
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>Digitizer/display association snapshot for the touch-mapping guard.</summary>
public sealed class TouchMapSnapshot
{
    public List<TouchMapDigitizerInfo> Digitizers { get; set; } = new();
    public List<TouchMapDisplayInfo> Displays { get; set; } = new();
}

/// <summary>Response for POST /displays/touch-mapping/repair.</summary>
public sealed class TouchMappingRepairResponse
{
    /// <summary>"repaired" | "alreadyCorrect" | "noPanel" | "noDigitizer" | "noHelper" | "failed".</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}
