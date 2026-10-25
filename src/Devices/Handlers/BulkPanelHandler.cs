using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Device-list row for one bulk-pipe cooler LCD, so each gets its own Nexus Control toggle
/// and Experimental badge. It keeps a page while unconnected, so its row stays listed; once
/// connected the panel record carries that page.
/// </summary>
public sealed class BulkPanelHandler : IDeviceHandler
{
    private readonly BulkPanelHub _hub;

    public BulkPanelHub Hub => _hub;

    public BulkPanelHandler(BulkPanelHub hub)
    {
        _hub = hub;
        Identifiers = hub.Driver.ProductIds
            .Select(pid => new UsbId(hub.Driver.VendorId, pid))
            .ToArray();
    }

    public string Id => _hub.Driver.HandlerId;
    public string Name => _hub.Driver.Name;
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        var driver = _hub.Driver;
        return detectedDevices.Any(d =>
            d.VendorId == driver.VendorId && driver.ProductIds.Contains(d.ProductId));
    }

    /// <summary>None of these panels exposes a firmware query this driver reads.</summary>
    public string GetFirmwareVersion() => "";

    /// <summary>
    /// Present but unopenable is the common case: these bulk endpoints are reachable only
    /// where Windows has bound WinUSB, and a cooler still owned by its vendor driver
    /// enumerates without ever opening here.
    /// </summary>
    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected || !IsConnected(detectedDevices))
        {
            return null;
        }
        return _hub.OpenAccessDenied ? "access-denied" : "bulk-pipe-unavailable";
    }
}
