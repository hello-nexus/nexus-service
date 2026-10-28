using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.SteelSeries;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// Device-list row for the Apex Pro TKL keyboards' OLED. It owns the Nexus Control switch the
/// streamed-panel discovery is gated on; the screen itself is a panel record, which carries the page.
/// </summary>
public sealed class ApexOledHandler : IDeviceHandler
{
    public string Id => Nexus.Service.Panel.Streams.ApexOledPanelDiscovery.Id;
    public string Name => "SteelSeries Apex Pro TKL OLED";
    public string Category => "keyboard";

    public IReadOnlyList<UsbId> Identifiers { get; } = ApexOledProtocol.ProductIds
        .Select(pid => new UsbId(ApexOledProtocol.VendorId, pid))
        .ToArray();

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => d.VendorId == ApexOledProtocol.VendorId && ApexOledProtocol.ProductIds.Contains(d.ProductId));

    /// <summary>The screen interface exposes no firmware query.</summary>
    public string GetFirmwareVersion() => "";
}
