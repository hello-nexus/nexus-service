using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// iBUYPOWER AW5 AIO cooler. Nexus drives the pump display itself
/// (<see cref="Nexus.Service.Peripherals.Aw5.Aw5PanelWorker"/>); the vendor driver
/// .exe the app manifest still describes is dormant (see DriverExePolicy). Presence
/// comes from USB enumeration rather than the panel handle, so a cooler shows in the
/// device list whether or not its display is being written.
/// </summary>
public sealed class Aw5Handler : IDeviceHandler
{
    private const int IbpVid = 0x3402;

    /// <summary>Shared with the panel blanker. The app manifest's deviceId must match it by hand - that one lives in nexus-apps.</summary>
    public const string HandlerId = "aw5";

    public string Id => HandlerId;
    public string Name => "iBUYPOWER AW5";
    public string Category => "cooler";

    /// <summary>
    /// One PID per ODM variant. Apaltek (0x0405) is deliberately absent: no
    /// driver binary is published for it, so recognizing it would list a cooler
    /// Nexus cannot drive.
    /// </summary>
    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(IbpVid, 0x0406), // Levelplay
        new UsbId(IbpVid, 0x0407), // CoolerMaster
    };

    /// <summary>
    /// Off stops Nexus writing to the panels and blanks what it can; on resumes.
    /// Keyed on this handler id, so one toggle covers both variants.
    /// </summary>
    public bool SupportsNexusControl => true;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    /// <summary>Empty: neither variant's panel report carries a firmware version.</summary>
    public string GetFirmwareVersion() => "";
}
