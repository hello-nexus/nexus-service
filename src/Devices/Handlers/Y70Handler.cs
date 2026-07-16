using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Y70 touch display - multiple panel variants (Touch, Infinite, Truly, GW, Ina).</summary>
public sealed class Y70Handler : IDeviceHandler
{
    private const string UsbDisconnectedWarning = "usb-disconnected";
    private const string DisplayDisconnectedWarning = "display-disconnected";

    private readonly Y70DisplayHub _hub;
    private readonly DisplayTopologyService _topology;

    public Y70Handler(Y70DisplayHub hub, DisplayTopologyService topology)
    {
        _hub = hub;
        _topology = topology;
    }

    public string Id => "y70";

    // Every panel variant surfaces under the family name; the specific variant
    // is carried by FirmwareType (the OTA catalog key for the serial models,
    // a diagnostics label for the DDC-only ones), not the label.
    public string Name => "Y70 Touch";

    public string Category => "display";

    // PID set per HYTE's reference controllers (Y70TouchController /
    // Y70TouchInfiniteController / Y70TouchTrulyController: VID_3402 &
    // PID_0C00/0C01/0C02) - the same constants the heartbeat worker gates on.
    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(Y70DisplayProtocol.VendorId, Y70DisplayProtocol.Y70TouchProductId),
        new UsbId(Y70DisplayProtocol.VendorId, Y70DisplayProtocol.Y70InfiniteProductId),
        new UsbId(Y70DisplayProtocol.VendorId, Y70DisplayProtocol.Y70TrulyProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Trust the display controller's live serial connection (it has opened
        // the COM port), then the cached topology's Y70 EDID match (the Y70 is
        // also a Windows display, reachable even with the serial channel
        // unplugged), then fall back to USB enumeration.
        if (_hub.IsConnected) return true;
        if (_topology.HasY70Display()) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    // Known Y70 touch digitizers - the panel's USB touch function. Y70ti
    // enumerates an ILITEK digitizer with no 0x3402 serial function at all,
    // so the digitizer is the USB-cable presence signal for those units.
    // Internal (not private): TouchPanelCatalog references this set directly
    // rather than duplicating the VID/PID pairs.
    internal static readonly UsbId[] TouchDigitizers =
    {
        new(0x222A, 0x0001), // ILITEK (Y70ti)
        new(0x27C0, 0x0859), // Y70 Touch (bench Y70, serial variant)
    };

    /// <summary>
    /// Flags a half-connected Y70: "usb-disconnected" when the monitor is present
    /// but no USB function of the panel is, and "display-disconnected" when the
    /// serial channel is up but no video display is attached (the panel can't
    /// render); null when both or neither are present. A touch digitizer with NO
    /// 0x3402 serial function on the bus is a Y70ti (its cable carries touch but
    /// no serial), so "connect the USB cable" would be false there; when a serial
    /// function IS enumerated but the hub can't connect (COM port held, driver
    /// failure), the warning stays - that is a real degraded state. The
    /// DDC-only panels (GW / Ina) expose no USB serial function at all, so a
    /// missing serial connection is their normal state, not a fault.
    /// </summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => ComputeWarning(
            _hub.IsConnected,
            _topology.HasY70Display(),
            touchOnlyUsb: HasTouchDigitizer(detectedDevices) && !HasSerialFunction(detectedDevices),
            ddcOnlyPanel: _topology.DdcOnlyY70Variant().Length > 0);

    internal static bool HasTouchDigitizer(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => detectedDevices.Any(d => TouchDigitizers.Any(t => t.VendorId == d.VendorId && t.ProductId == d.ProductId));

    internal bool HasSerialFunction(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    internal static string? ComputeWarning(bool serialConnected, bool hasDisplay, bool touchOnlyUsb, bool ddcOnlyPanel)
    {
        if (serialConnected) return hasDisplay ? null : DisplayDisconnectedWarning;
        if (!hasDisplay) return null;
        if (ddcOnlyPanel) return null;
        return touchOnlyUsb ? null : UsbDisconnectedWarning;
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;

    // Serial-reported variant once the controller identifies itself; the
    // DDC-only panels (GW / Ina) have no serial function, so their EDID match
    // is the only variant signal. Otherwise the keyless Id ("y70", no bundled
    // firmware) so the device isn't offered an update before the applicable
    // image is known - which also covers GW / Ina permanently (they have no
    // host-updatable firmware).
    public string FirmwareType
    {
        get
        {
            if (!string.IsNullOrEmpty(_hub.Variant)) return _hub.Variant;
            var ddcOnly = _topology.DdcOnlyY70Variant();
            return ddcOnly.Length > 0 ? ddcOnly : Id;
        }
    }
}
