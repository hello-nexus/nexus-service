using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Lian Li L-Wireless controller (SLV3 2.4GHz dongle). Surfaces the wireless
/// fans in the device list; connected state comes from the hub, since the
/// TX/RX dongles bind WinUSB and the general USB enumeration may not list them.
/// </summary>
public sealed class LianLiWirelessHandler : IDeviceHandler
{
    private readonly Slv3Hub _hub;

    public LianLiWirelessHandler(Slv3Hub hub)
    {
        _hub = hub;
        Identifiers = new[]
        {
            new UsbId(Slv3Protocol.TxVendorId, Slv3Protocol.TxProductId),
            new UsbId(Slv3Protocol.RxVendorId, Slv3Protocol.RxProductId),
            new UsbId(Slv3Protocol.WchVendorId, Slv3Protocol.TxProductIdWch),
            new UsbId(Slv3Protocol.WchVendorId, Slv3Protocol.RxProductIdWch),
        };
    }

    public string Id => "lianli-wireless";

    public string Name => "Lian Li L-Wireless Controller";

    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() =>
        _hub.State.TxFirmwareVersion > 0 ? _hub.State.TxFirmwareVersion.ToString() : "";
}
