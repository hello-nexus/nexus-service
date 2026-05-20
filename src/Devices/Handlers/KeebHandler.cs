using System.Collections.Generic;
using System.Linq;

namespace Qos.Service.Devices.Handlers;

/// <summary>HYTE Keeb TKL (Suoai-built).</summary>
public sealed class KeebHandler : IDeviceHandler
{
    private const int HyteVid = 0x3402;

    public string Id => "keeb";
    public string Name => "Keeb TKL";
    public string Category => "keyboard";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(HyteVid, 0x0300), // HYTE Keeb TKL (Suoai)
    };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    public string GetFirmwareVersion() => "";
}
