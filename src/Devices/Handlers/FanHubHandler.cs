using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.MiniHub;

namespace Nexus.Service.Devices.Handlers;

/// <summary>iBUYPOWER fan and ARGB hubs.</summary>
public sealed class FanHubHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    private readonly MiniHubHub _hub;

    public FanHubHandler(MiniHubHub hub)
    {
        _hub = hub;
    }

    public string Id => "fan-hub";
    public string Name => "iBUYPOWER MiniHub";
    public string Category => "hub";

    /// <summary>Fans are configured on Cooling, ARGB on Lighting.</summary>
    public bool HasPage => false;

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, MiniHubProtocol.ProductId), // IBP Mini Hub
        new UsbId(HyteVid, 0x0A04), // PWM Fan + ARGB Hub
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        // Prefer the hub's live opinion (it has actually opened the serial
        // port) over USB enumeration - the MiniHub talks over a serial bridge
        // and doesn't always surface under its USB VID/PID. Mirrors Np50Handler.
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;
}
