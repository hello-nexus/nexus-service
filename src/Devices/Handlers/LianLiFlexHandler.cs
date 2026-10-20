using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Lian Li TL LCD Flex and SL-INF LCD Flex fans: receivers and screens behind one hub each.</summary>
public sealed class LianLiFlexHandler : IDeviceHandler
{
    private readonly FlexHub _hub;

    public LianLiFlexHandler(FlexHub hub)
    {
        _hub = hub;
        Identifiers = new[]
        {
            new UsbId(FlexProtocol.ReceiverVendorId, FlexProtocol.TlReceiverProductId),
            new UsbId(FlexProtocol.ReceiverVendorId, FlexProtocol.SlInfReceiverProductId),
            new UsbId(FlexProtocol.ScreenVendorId, FlexProtocol.TlScreenProductId),
            new UsbId(FlexProtocol.ScreenVendorId, FlexProtocol.SlInfScreenProductId),
        };
    }

    public string Id => "lianli-flex";

    public string Name => "Lian Li LCD Flex Fans";

    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _hub.IsConnected || detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public bool DrivesDisplay(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _hub.Units.Any(u => u.ScreenSerial.Length > 0);

    public string GetFirmwareVersion()
    {
        var first = _hub.Units.FirstOrDefault();
        return first is { FirmwareVersion: > 0 } ? first.FirmwareVersion.ToString() : "";
    }
}
