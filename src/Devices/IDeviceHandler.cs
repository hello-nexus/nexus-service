using System.Collections.Generic;

namespace Nexus.Service.Devices;

/// <summary>
/// Defines a modular device handler. Each supported device type implements this interface.
/// Handlers are self-contained - removing a handler file and its DI registration
/// has zero impact on the rest of the application.
/// </summary>
public interface IDeviceHandler
{
    /// <summary>Unique identifier (e.g., "cnvs", "q60").</summary>
    string Id { get; }

    /// <summary>Display name (e.g., "HYTE CNVS", "HYTE Q60").</summary>
    string Name { get; }

    /// <summary>Device category for grouping (e.g., "controller", "display", "hub", "keyboard").</summary>
    string Category { get; }

    /// <summary>USB VID/PID pairs this handler recognizes.</summary>
    IReadOnlyList<UsbId> Identifiers { get; }

    /// <summary>Returns true if any of the enumerated USB devices match this handler's identifiers.</summary>
    bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices);

    /// <summary>
    /// False for a handler that only reports presence and never claims the
    /// device, so the Nexus Control on/off gate would have nothing to gate and
    /// the UI hides the switch. Defaults true: a first-party handler drives its
    /// hardware through a gate-honoring connection worker.
    /// </summary>
    bool SupportsNexusControl => true;

    /// <summary>False for a device whose controls live on shared pages, so the
    /// sidebar omits its row instead of linking to an empty placeholder.</summary>
    bool HasPage => true;

    /// <summary>Current firmware version, or empty string if unavailable.</summary>
    string GetFirmwareVersion();

    /// <summary>
    /// Firmware-catalog key - the bundled-.hex directory name used to look up
    /// the available firmware version. Defaults to <see cref="Id"/>. Handlers
    /// that cover several firmware variants under one id (Q-series → "q60" /
    /// "q80") override this with the connected variant so the right image is
    /// offered. Returns <see cref="Id"/> (no bundled firmware) when the variant
    /// isn't yet known.
    /// </summary>
    string FirmwareType => Id;

    /// <summary>
    /// Short code describing a partial-detection issue (e.g. a control channel
    /// is down while another connection path still sees the device), or null
    /// when there is nothing to flag. Surfaced as DeviceListItem.Warning.
    /// </summary>
    string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices) => null;
}

/// <summary>USB Vendor ID + Product ID pair.</summary>
public readonly struct UsbId
{
    public int VendorId { get; }
    public int ProductId { get; }

    public UsbId(int vendorId, int productId)
    {
        VendorId = vendorId;
        ProductId = productId;
    }
}

/// <summary>A USB device detected on the system.</summary>
public sealed class UsbDeviceEntry
{
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public string Class { get; init; } = "";
    public string Speed { get; init; } = "";
    public string Driver { get; init; } = "";
    public string HardwareId { get; init; } = "";
}
