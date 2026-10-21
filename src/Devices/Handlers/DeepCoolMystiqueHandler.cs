using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.DeepCool;

namespace Nexus.Service.Devices.Handlers;

/// <summary>Device-list row for the Mystique AIO screen, so it has its own Nexus Control switch.</summary>
public sealed class DeepCoolMystiqueHandler : IDeviceHandler
{
    private readonly MystiqueWorker _worker;

    public DeepCoolMystiqueHandler(MystiqueWorker worker)
    {
        _worker = worker;
    }

    public string Id => MystiqueWorker.HandlerId;
    public string Name => "DeepCool Mystique";
    public string Category => "cooler";

    public IReadOnlyList<UsbId> Identifiers { get; } = new[] { new UsbId(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId) };

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) =>
        _worker.IsConnected || detectedDevices.Any(d => d.VendorId == MystiqueProtocol.VendorId && d.ProductId == MystiqueProtocol.ProductId);

    public string GetFirmwareVersion() => "";
}
