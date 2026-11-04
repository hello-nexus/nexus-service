using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Corsair.Galleon;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Corsair Galleon 100 SD keyboard lighting (USB 1B1C:2B0C). The Stream Deck half (2B18) is <see cref="StreamDeckHandler"/>'s. No device page; the LEDs live on the Lighting page.</summary>
public sealed class GalleonSdHandler : IDeviceHandler
{
    public string Id => GalleonSdProtocol.HandlerId;
    public string Name => GalleonSdProtocol.ProductName;
    public string Category => "keyboard";
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(GalleonSdProtocol.VendorId, GalleonSdProtocol.ProductId),
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
