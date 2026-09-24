using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// HYTE NP50 Nexus Link fan + lighting hub. Connection state and firmware
/// version come from the singleton <see cref="Np50Hub"/>; this handler is just
/// the integration point with <c>DeviceManager</c> / <c>DeviceBroadcaster</c>.
/// </summary>
public sealed class Np50Handler : IDeviceHandler
{
    private readonly Np50Hub _hub;

    public Np50Handler(Np50Hub hub)
    {
        _hub = hub;
    }

    public string Id => "np50";
    public string Name => Np50Hub.ProductName;
    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(Np50Protocol.VendorId, Np50Protocol.ProductId),
        // DFU mode (post-firmware-jump). Surfaced so the UI can show "device
        // is in bootloader mode" rather than "device missing" when a flash
        // is in progress. Phase 2 wires the actual flasher.
        new UsbId(Np50Protocol.VendorId, Np50Protocol.DfuProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Prefer the hub's live opinion (it has actually opened the port) over
        // pure USB enumeration. Fall back to USB-only when the hub hasn't
        // initialized yet (e.g. during the first poll cycle).
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;
}
