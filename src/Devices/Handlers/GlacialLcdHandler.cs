using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Device-list row for the ROG Crosshair X870E Glacial LCD, so it has its own Nexus Control switch.</summary>
public sealed class GlacialLcdHandler : IDeviceHandler
{
    private readonly GlacialLcdWorker _worker;

    public GlacialLcdHandler(GlacialLcdWorker worker)
    {
        _worker = worker;
    }

    public string Id => GlacialLcdWorker.HandlerId;
    public string Name => "ROG Crosshair X870E Glacial LCD";
    public string Category => "light";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[] { new UsbId(0x0B05, GlacialLcdProtocol.ProductId) };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _worker.IsConnected || detectedDevices.Any(d => d.VendorId == 0x0B05 && d.ProductId == GlacialLcdProtocol.ProductId);

    public string GetFirmwareVersion() => "";
}
