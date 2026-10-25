using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Nzxt;

namespace Nexus.Service.Devices.Handlers;

/// <summary>NZXT RGB & Fan Controller, fan channels only; its lighting belongs to OpenRGB.</summary>
public sealed class NzxtFanControllerHandler : IDeviceHandler
{
    private readonly NzxtFanControllerHub _hub;

    public NzxtFanControllerHandler(NzxtFanControllerHub hub)
    {
        _hub = hub;
    }

    public string Id => NzxtFanControllerHub.DeviceId;
    public string Name => NzxtFanControllerHub.ProductName;
    public string Category => "hub";

    /// <summary>Fans are configured on Cooling, lighting belongs to OpenRGB.</summary>
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = NzxtFanControllerProtocol.ProductIds
        .Select(pid => new UsbId(NzxtFanControllerProtocol.VendorId, pid))
        .ToArray();

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected)
        {
            return true;
        }
        return detectedDevices.Any(d =>
            d.VendorId == NzxtFanControllerProtocol.VendorId &&
            NzxtFanControllerProtocol.ProductIds.Contains(d.ProductId));
    }

    public string GetFirmwareVersion() => "";
}
