using System.Collections.Generic;
using Nexus.Service.Peripherals.Nollie;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Lists every Nollie controller under one device entry; the boards themselves are cards on the Lighting page.</summary>
public sealed class NollieHandler : IDeviceHandler
{
    private readonly NollieHub _hub;

    public NollieHandler(NollieHub hub)
    {
        _hub = hub;
        var ids = new UsbId[NollieProtocol.Devices.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = new UsbId(NollieProtocol.Devices[i].VendorId, NollieProtocol.Devices[i].ProductId);
        }
        Identifiers = ids;
    }

    public string Id       => NollieConnectionWorker.HandlerId;
    public string Name     => "Nollie";
    public string Category => "controller";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return true;
        foreach (var d in detectedDevices)
        {
            if (NollieProtocol.Lookup(d.VendorId, d.ProductId) is not null) return true;
        }
        return false;
    }

    public string GetFirmwareVersion() => "";
}
