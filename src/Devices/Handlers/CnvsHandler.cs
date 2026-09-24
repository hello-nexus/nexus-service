using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Cnvs;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// CNVS RGB controller - the main iBUYPOWER case lighting controller.
/// Multiple hardware revisions share the same handler.
/// </summary>
public sealed class CnvsHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    private readonly CnvsHub _hub;

    public CnvsHandler(CnvsHub hub)
    {
        _hub = hub;
    }

    public string Id => "cnvs";
    public string Name => "HYTE CNVS";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0BFF), // CNVS
        new UsbId(HyteVid, 0x0B00), // CNVS Left
        new UsbId(HyteVid, 0x0B01), // CNVS v1
        new UsbId(HyteVid, 0x0B02), // CNVS White
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => _hub.FirmwareVersion;

    // CNVS firmware is variant-specific (Left/v1/White ship different images),
    // so the catalog key is the connected variant - never the bare "cnvs" id,
    // which has no bundled image. Empty/"cnvs" when not connected.
    public string FirmwareType => _hub.Variant;
}
