using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Devices;
using Nexus.Service.Plugins;

namespace Nexus.Service.Devices;

/// <summary>
/// Aggregates all registered IDeviceHandler instances and coordinates USB enumeration
/// to produce the unified device list. This is the single entry point for device queries.
/// </summary>
public sealed class DeviceManager
{
    private readonly IReadOnlyList<IDeviceHandler> _handlers;
    private readonly IUsbEnumerator _enumerator;
    private readonly PluginProviderRegistry _registry;
    private readonly DeviceControlGate _gate;

    public DeviceManager(IEnumerable<IDeviceHandler> handlers, IUsbEnumerator enumerator, PluginProviderRegistry registry, DeviceControlGate gate)
    {
        _handlers = handlers.ToList();
        _enumerator = enumerator;
        _registry = registry;
        _gate = gate;
    }

    // First-party handlers (static DI) + any plugin handlers (registry snapshot,
    // read fresh each call so a plugin registered at runtime is detected).
    private IEnumerable<IDeviceHandler> AllHandlers => _handlers.Concat(_registry.Handlers);

    /// <summary>
    /// Enumerates USB devices, checks each handler, and returns the full device list.
    /// </summary>
    public List<DeviceListItem> GetAll()
    {
        var usbDevices = _enumerator.Enumerate();
        // Only first-party handlers have a gate-honoring connection worker; the
        // on/off switch is a no-op for plugin handlers, so don't advertise it.
        // A first-party handler that only reports presence opts out the same way.
        var firstParty = new HashSet<IDeviceHandler>(_handlers);
        return AllHandlers.Select(h =>
        {
            var supportsControl = firstParty.Contains(h) && h.SupportsNexusControl;
            return new DeviceListItem
            {
                Id = h.Id,
                Name = h.Name,
                Category = h.Category,
                Connected = h.IsConnected(usbDevices),
                FirmwareVersion = h.GetFirmwareVersion(),
                FirmwareType = h.FirmwareType,
                SupportsNexusControl = supportsControl,
                Experimental = supportsControl && DeviceControlPolicy.IsExperimental(h.Id),
                NexusControlEnabled = _gate.IsEnabled(h.Id),
                Warning = h.GetWarning(usbDevices),
                ConflictAppId = DeviceControlPolicy.ConflictAppFor(h.Id),
            };
        }).ToList();
    }

    /// <summary>
    /// Returns every USB device the OS reports, with full details. Backs /devices/usb/all.
    /// Dedupes by (VID, PID, Name, Manufacturer) so composite-device interface rows that
    /// share a name don't appear multiple times. Handler matching still runs against the
    /// full raw enumerator list, so dedupe here has no effect on recognition.
    /// </summary>
    public List<UsbDeviceDetail> GetUsbDevices()
    {
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var result = new List<UsbDeviceDetail>();
        foreach (var e in _enumerator.Enumerate())
        {
            var key = $"{e.VendorId:X4}:{e.ProductId:X4}:{e.Name}:{e.Manufacturer}";
            if (!seen.Add(key))
            {
                continue;
            }
            result.Add(new UsbDeviceDetail
            {
                VendorId = $"0x{e.VendorId:X4}",
                ProductId = $"0x{e.ProductId:X4}",
                Name = e.Name,
                Manufacturer = e.Manufacturer,
                Serial = e.Serial,
                Location = e.Location,
                Class = e.Class,
                Speed = e.Speed,
                Driver = e.Driver,
                HardwareId = e.HardwareId,
            });
        }
        return result;
    }
}
