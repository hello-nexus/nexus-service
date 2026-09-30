using System.Collections.Generic;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// Single source of truth for "is this device on the USB bus right now," used to
/// gate per-device background workers so they don't poll - and log - for hardware
/// that isn't attached. On a host with none of a given device, its worker would
/// otherwise re-run discovery every 2-3 s and emit a status line each tick.
///
/// Descriptor-queried, not a fixed device list: a query is a vendor id plus an
/// optional product-id set, matched against the live enumeration. A third-party
/// app's worker gates through the same call with its cert-granted VID/PIDs - the
/// enumeration "expands" for free because presence is a filter over whatever is on
/// the bus, not a hard-coded table. PluginProcessSupervisor is the intended
/// plugin hook.
///
/// Backed by the shared single-flight <see cref="CachingUsbEnumerator"/>
/// (event-invalidated on Windows), so a per-tick presence check adds no
/// bus-scan cost over the device detection already running.
/// Note: the monitor channel (<see cref="Platform.MonitorEnumerator"/>) carries no
/// EDID/vendor, so it cannot identify a specific product (e.g. a Y70) and is not a
/// gate source; a Y70 is gated on its serial controller's VID/PID instead.
/// </summary>
public sealed class HardwarePresence
{
    private readonly IUsbEnumerator _usb;

    public HardwarePresence(IUsbEnumerator usb) { _usb = usb; }

    /// <summary>True when the enumerator returned at least one device this cycle.</summary>
    public bool AnyUsbEnumerated() => _usb.Enumerate().Count > 0;

    /// <summary>
    /// True when a USB device with <paramref name="vendorId"/> is currently
    /// enumerated. When <paramref name="productIds"/> is non-empty the product id
    /// must also be one of them; an empty set matches any product under the vendor.
    /// </summary>
    public bool UsbPresent(int vendorId, params int[] productIds) => UsbPresent(vendorId, (IReadOnlyList<int>)productIds);

    /// <inheritdoc cref="UsbPresent(int, int[])"/>
    public bool UsbPresent(int vendorId, IReadOnlyList<int> productIds)
    {
        foreach (var d in _usb.Enumerate())
        {
            if (d.VendorId != vendorId)
                continue;
            if (productIds.Count == 0 || Contains(productIds, d.ProductId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Currently enumerated devices under <paramref name="vendorId"/>. On
    /// Windows entries are unique per vid:pid:name, so identical units share one.
    /// </summary>
    public List<UsbDeviceEntry> UsbEntriesFor(int vendorId)
    {
        var result = new List<UsbDeviceEntry>();
        foreach (var d in _usb.Enumerate())
        {
            if (d.VendorId == vendorId)
                result.Add(d);
        }
        return result;
    }

    private static bool Contains(IReadOnlyList<int> ids, int value)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == value)
                return true;
        }
        return false;
    }
}
