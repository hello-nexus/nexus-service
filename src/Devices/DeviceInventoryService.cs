using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Peripherals.JpegPanels;
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
    private readonly IUsbEnumerator _usb;
    private readonly RgbBridge _rgb;
    private readonly TlLcdPanels _tlLcd;

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
        IConfigStore store,
        IUsbEnumerator usb,
        RgbBridge rgb,
        TlLcdPanels tlLcd)
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
        _usb = usb;
        _rgb = rgb;
        _tlLcd = tlLcd;
    }

    public SystemDevicesResponse Build()
    {
        var rows = new List<SystemDeviceDto>();
        // Handler ids whose hardware a dedicated row above stands for, so the USB pass skips them.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Claim(string handlerId, bool listed)
        {
            if (listed)
            {
                claimed.Add(handlerId);
            }
        }
        void AddClaimed(string handlerId, SystemDeviceDto? row)
        {
            Claim(handlerId, row is not null);
            Add(rows, row);
        }

        var wireless = _wireless.State;
        rows.AddRange(DeviceInventoryMapper.FromWireless(wireless));

        var devices = _store.Load().Devices;
        foreach (var hub in _uni.Hubs)
        {
            AddClaimed(hub.DeviceId, DeviceInventoryMapper.FromUniHub(hub.DeviceId, hub.IsConnected, hub.Profile, LianLiHubSet.FansOf(devices, hub.DeviceId)));
        }
        AddClaimed("lianli-tl", DeviceInventoryMapper.FromTlHub(_tl.IsConnected, _tl.Snapshot));
        foreach (var hub in _corsair.All)
        {
            var linked = DeviceInventoryMapper.FromCorsairLink(hub.HubId, hub.State);
            Claim(hub.DeviceId, linked.Count > 0);
            rows.AddRange(linked);
        }
        AddClaimed(KrakenHub.DeviceId, DeviceInventoryMapper.FromKraken(_kraken.IsConnected, _kraken.Model, _kraken.Serial));
        AddClaimed("lianli-aio", DeviceInventoryMapper.FromGalahad2(_galahad.IsConnected, _galahad.ConnectedProductId));
        AddClaimed(NzxtFanControllerHub.DeviceId, DeviceInventoryMapper.FromNzxtFanController(_nzxtFans.IsConnected, _nzxtFans.Fans));
        AddClaimed("qseries", DeviceInventoryMapper.FromQSeries(_qSeries.IsConnected, _qSeries.Variant, _qSeries.State.Serial));
        Add(rows, DeviceInventoryMapper.FromTlLcd(_tlLcd.Attached.Count));

        // A HydroShift II on both the radio and USB is one unit, listed once from the radio (see RadioCoversUsbHydroShift2).
        var hydroShiftOnRadio = DeviceInventoryMapper.RadioCoversUsbHydroShift2(wireless);
        foreach (var handler in _handlers)
        {
            if (handler is JpegPanelHandler jpeg)
            {
                var hub = jpeg.Hub;
                AddClaimed(hub.Model.HandlerId, DeviceInventoryMapper.FromJpegPanel(hub.Model, hub.IsConnected, hub.AttachedProductId, hub.Serial));
            }
            else if (handler is BulkPanelHandler bulk)
            {
                var hub = bulk.Hub;
                var driver = hub.Driver;
                if (driver is HydroShift2LcdDriver hs2)
                {
                    Claim(driver.HandlerId, hub.IsConnected && hydroShiftOnRadio);
                    if (!hydroShiftOnRadio)
                    {
                        AddClaimed(driver.HandlerId, DeviceInventoryMapper.FromHydroShift2Usb(hub.IsConnected, hs2.Round, hs2.VendorId, hub.Serial));
                    }
                    continue;
                }
                AddClaimed(driver.HandlerId, DeviceInventoryMapper.FromRyujin(driver.HandlerId, hub.IsConnected, driver.VendorId, driver.ProductIds.Count > 0 ? driver.ProductIds[0] : 0, hub.Serial));
            }
        }

        // A unit with a row above also covers its VID:PID, however many handler slots share it.
        var claimedUsb = new HashSet<(int Vid, int Pid)>();
        foreach (var row in rows)
        {
            if (row.Vid is { } vid && row.Pid is { } pid)
            {
                claimedUsb.Add((vid, pid));
            }
        }

        // The shared cached USB list, the same one the Devices tab reads.
        rows.AddRange(DeviceInventoryMapper.FromUsb(_usb.Enumerate(), _handlers, claimed, claimedUsb, AllSupportedDevices.All));

        rows = DeviceInventoryMapper.MergeOpenRgb(rows, DeviceInventoryMapper.FromOpenRgb(_rgb.Devices));
        DeviceInventoryMapper.HashKeys(rows);

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
