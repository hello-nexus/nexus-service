using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.JpegPanels;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Device-list row for one JPEG-over-HID cooler LCD. One handler per
/// <see cref="JpegPanelModel"/>, so each model gets its own Nexus Control toggle and its
/// own Experimental badge rather than the family sharing one switch.
///
/// It keeps a page while unconnected, so its row stays listed; once connected the panel record
/// carries that page (the glass is what the user configures), the way a Kraken panel claims its row.
/// </summary>
public sealed class JpegPanelHandler : IDeviceHandler
{
    private readonly JpegPanelHub _hub;

    public JpegPanelHub Hub => _hub;

    public JpegPanelHandler(JpegPanelHub hub)
    {
        _hub = hub;
        Identifiers = hub.Model.ProductIds
            .Select(pid => new UsbId(hub.Model.VendorId, pid))
            .ToArray();
    }

    public string Id => _hub.Model.HandlerId;
    public string Name => _hub.Model.Name;
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        var model = _hub.Model;
        return detectedDevices.Any(d =>
            d.VendorId == model.VendorId && model.ProductIds.Contains(d.ProductId));
    }

    /// <summary>These panels expose no firmware query of any kind.</summary>
    public string GetFirmwareVersion() => "";
}
