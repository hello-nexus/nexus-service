using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Devices.Handlers;

public sealed class LianLiHandler : IDeviceHandler
{
    private readonly LianLiHubSet _hubs;
    private readonly LianLiHub _hub;
    // The first hub stands in for a Uni hub USB enumeration sees before the connection worker has pinned any.
    private readonly bool _primary;

    public LianLiHandler(LianLiHubSet hubs, int slot)
    {
        _hubs = hubs;
        _hub = hubs.Hubs[slot];
        _primary = slot == 0;
        var pids = LianLiFanProfiles.AllProductIds;
        var ids = new UsbId[pids.Length];
        for (var i = 0; i < pids.Length; i++)
        {
            ids[i] = new UsbId(LianLiProtocol.VendorId, pids[i]);
        }
        Identifiers = ids;
    }

    public string Id => _hub.DeviceId;

    public string Name
    {
        get
        {
            var model = _hub.ModelName;
            var name = string.IsNullOrEmpty(model) ? "Lian Li Uni Fan" : $"Lian Li {model}";
            var slot = LianLiHubSet.SlotOf(_hub.DeviceId);
            return slot > 0 ? $"{name} {slot + 1}" : name;
        }
    }

    public string Category => "hub";

    public IReadOnlyList<UsbId> Identifiers { get; }

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_hub.IsConnected || _hub.Present) return true;
        return _primary && !_hubs.AnyPresent && detectedDevices.Any(d =>
            Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));
    }

    public string GetFirmwareVersion() => _hub.State.FirmwareVersion;
}
