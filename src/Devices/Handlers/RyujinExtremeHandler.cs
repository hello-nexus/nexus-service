using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Ryujin;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// The ROG Ryujin III Extreme: pump and fan control plus its built-in hardware-monitor screen.
/// Its own handler id, apart from the 360's live-panel one, because the Extreme takes no live frames.
/// </summary>
public sealed class RyujinExtremeHandler : IDeviceHandler
{
    private readonly RyujinCoolingHub _hub;

    public RyujinExtremeHandler(RyujinCoolingHub hub)
    {
        _hub = hub;
        Identifiers = new[] { new UsbId(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId) };
    }

    public string Id => RyujinCoolingProtocol.HandlerId;
    public string Name => "ASUS ROG Ryujin III Extreme";
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _hub.IsConnected
        || detectedDevices.Any(d => d.VendorId == RyujinCoolingProtocol.VendorId && d.ProductId == RyujinCoolingProtocol.ProductId);

    public string GetFirmwareVersion() => "";
}
