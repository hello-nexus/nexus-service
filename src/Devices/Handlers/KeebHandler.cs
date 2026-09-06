using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// HYTE Keeb TKL. Only the 0x0300 board speaks the documented HYTE protocol
/// (<see cref="Peripherals.Hyte.Keeb.KeebProtocol"/>); the iBUYPOWER
/// keyboards on the same vendor id (KM7 / KM10 / MK9 / MEK 4) are
/// <see cref="IbpKeyboardHandler"/>'s, with their own lighting stack.
/// </summary>
public sealed class KeebHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "keeb";
    public string Name => "Keeb";
    public string Category => "keyboard";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, Peripherals.Hyte.Keeb.KeebProtocol.ProductId), // Keeb TKL (Suoai)
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
