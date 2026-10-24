using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;

namespace Nexus.Service.Devices;

/// <summary>
/// Builds the detected-device inventory from the hubs' cached state. It never
/// probes hardware and sends no USB traffic.
/// </summary>
public sealed class DeviceInventoryService
{
    private readonly Slv3Hub _wireless;
    private readonly LianLiHubSet _uni;
    private readonly TlFanHub _tl;
    private readonly CorsairLinkHubs _corsair;
    private readonly KrakenHub _kraken;
    private readonly Galahad2Hub _galahad;
    private readonly NzxtFanControllerHub _nzxtFans;
    private readonly QSeriesCoolerHub _qSeries;
    private readonly IReadOnlyList<IDeviceHandler> _handlers;
    private readonly IConfigStore _store;

    public DeviceInventoryService(
        Slv3Hub wireless,
        LianLiHubSet uni,
        TlFanHub tl,
        CorsairLinkHubs corsair,
        KrakenHub kraken,
        Galahad2Hub galahad,
        NzxtFanControllerHub nzxtFans,
        QSeriesCoolerHub qSeries,
        IEnumerable<IDeviceHandler> handlers,
        IConfigStore store)
    {
        _wireless = wireless;
        _uni = uni;
        _tl = tl;
        _corsair = corsair;
        _kraken = kraken;
        _galahad = galahad;
        _nzxtFans = nzxtFans;
        _qSeries = qSeries;
        _handlers = handlers.ToList();
        _store = store;
    }

    public SystemDevicesResponse Build()
    {
        var rows = new List<SystemDeviceDto>();
        var wireless = _wireless.State;
        rows.AddRange(DeviceInventoryMapper.FromWireless(wireless));

        var devices = _store.Load().Devices;
        foreach (var hub in _uni.Hubs)
        {
            Add(rows, DeviceInventoryMapper.FromUniHub(hub.DeviceId, hub.IsConnected, hub.Profile, LianLiHubSet.FansOf(devices, hub.DeviceId)));
        }
        Add(rows, DeviceInventoryMapper.FromTlHub(_tl.IsConnected, _tl.Snapshot));
        foreach (var hub in _corsair.All)
        {
            rows.AddRange(DeviceInventoryMapper.FromCorsairLink(hub.HubId, hub.State));
        }
        Add(rows, DeviceInventoryMapper.FromKraken(_kraken.IsConnected, _kraken.Model, _kraken.Serial));
        Add(rows, DeviceInventoryMapper.FromGalahad2(_galahad.IsConnected, _galahad.ConnectedProductId));
        Add(rows, DeviceInventoryMapper.FromNzxtFanController(_nzxtFans.IsConnected, _nzxtFans.Fans));
        Add(rows, DeviceInventoryMapper.FromQSeries(_qSeries.IsConnected, _qSeries.Variant, _qSeries.State.Serial));

        // A HydroShift II on both the radio and USB is one unit, listed once from the radio (see RadioCoversUsbHydroShift2).
        var hydroShiftOnRadio = DeviceInventoryMapper.RadioCoversUsbHydroShift2(wireless);
        foreach (var handler in _handlers)
        {
            if (handler is JpegPanelHandler jpeg)
            {
                var hub = jpeg.Hub;
                Add(rows, DeviceInventoryMapper.FromJpegPanel(hub.Model, hub.IsConnected, hub.AttachedProductId, hub.Serial));
            }
            else if (handler is BulkPanelHandler bulk)
            {
                var hub = bulk.Hub;
                var driver = hub.Driver;
                if (driver is HydroShift2LcdDriver hs2)
                {
                    if (!hydroShiftOnRadio)
                    {
                        Add(rows, DeviceInventoryMapper.FromHydroShift2Usb(hub.IsConnected, hs2.Round, hs2.VendorId, hub.Serial));
                    }
                    continue;
                }
                Add(rows, DeviceInventoryMapper.FromRyujin(driver.HandlerId, hub.IsConnected, driver.VendorId, driver.ProductIds.Count > 0 ? driver.ProductIds[0] : 0, hub.Serial));
            }
        }

        return new SystemDevicesResponse { Devices = rows.ToArray() };
    }

    private static void Add(List<SystemDeviceDto> rows, SystemDeviceDto? row)
    {
        if (row is not null)
        {
            rows.Add(row);
        }
    }
}
