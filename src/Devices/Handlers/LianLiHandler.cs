using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Devices.Handlers;

public sealed class LianLiHandler : IDeviceHandler
{
    private readonly LianLiHub _hub;

    public LianLiHandler(LianLiHub hub)
    {
        _hub = hub;
        var pids = LianLiFanProfiles.AllProductIds;
        var ids = new UsbId[pids.Length];
        for (var i = 0; i < pids.Length; i++)
        {
            ids[i] = new UsbId(LianLiProtocol.VendorId, pids[i]);
        }
        Identifiers = ids;
    }

    public string Id => "lianli";

    public string Name
    {
        get
        {
            var model = _hub.ModelName;
            return string.IsNullOrEmpty(model) ? "Lian Li Uni Fan" : $"Lian Li {model}";
        }
    }

    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected) return true;
        return detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;
}
