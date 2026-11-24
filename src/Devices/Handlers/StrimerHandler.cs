using System.Collections.Generic;
using Nexus.Service.Peripherals.Strimer;

namespace Nexus.Service.Devices.Handlers;

public sealed class StrimerHandler : IDeviceHandler
{
    private readonly StrimerUnits _units;

    public StrimerHandler(StrimerUnits units)
    {
        _units = units;
        Identifiers = new[] { new UsbId(StrimerProtocol.VendorId, StrimerProtocol.ProductId) };
    }

    public string Id       => "strimer";
    public string Name     => "Lian Li Strimer";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_units.IsConnected) return true;
        foreach (var d in detectedDevices)
        {
            if (d.VendorId == StrimerProtocol.VendorId && d.ProductId == StrimerProtocol.ProductId) return true;
        }
        return false;
    }

    public string GetFirmwareVersion() => "";
}
