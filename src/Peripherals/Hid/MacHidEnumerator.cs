using System;
using System.Collections.Generic;
using System.Globalization;
using static Nexus.Service.Peripherals.Hid.MacHidNative;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// macOS <see cref="IHidEnumerator"/> over IOHIDManager. A device's path is
/// its IORegistry entry id ("iokit:&lt;hex&gt;"), which changes on re-plug -
/// callers re-enumerate per tick and key their own state by serial. Open
/// creates a fresh IOHIDDeviceRef from that service each time, so a control
/// handle and an input-reader handle on the same deck are independent.
/// </summary>
public sealed unsafe class MacHidEnumerator : IHidEnumerator, IHidWholeBusEnumerator
{
    private const string PathPrefix = "iokit:";

    public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
    {
        var all = FindAllForDiagnostics();
        var matches = new List<HidDeviceInfo>();
        foreach (var info in all)
        {
            if (info.VendorId == vendorId && info.ProductId == productId)
            {
                matches.Add(info);
            }
        }
        return matches;
    }

    public IReadOnlyList<HidDeviceInfo> FindAllForDiagnostics()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return Array.Empty<HidDeviceInfo>();
        }
        var result = new List<HidDeviceInfo>();
        // A fresh manager per call: one never scheduled on a run loop keeps its
        // first device set, so a replugged device (new registry id) never shows.
        var manager = IOHIDManagerCreate(IntPtr.Zero, 0);
        if (manager == IntPtr.Zero)
        {
            return result;
        }
        try
        {
            IOHIDManagerSetDeviceMatching(manager, IntPtr.Zero);
            var set = IOHIDManagerCopyDevices(manager);
            if (set == IntPtr.Zero)
            {
                return result;
            }
            try
            {
                var count = (int)CFSetGetCount(set);
                if (count <= 0)
                {
                    return result;
                }
                var devices = new IntPtr[count];
                fixed (IntPtr* p = devices)
                {
                    CFSetGetValues(set, p);
                }
                foreach (var device in devices)
                {
                    var path = PathFor(device);
                    if (path is null)
                    {
                        continue;
                    }
                    result.Add(ReadInfo(device, path));
                }
            }
            finally
            {
                CFRelease(set);
            }
        }
        finally
        {
            CFRelease(manager);
        }
        return result;
    }

    public IHidDevice? Open(string path, bool forInput = false)
    {
        if (!OperatingSystem.IsMacOS() || !TryParseEntryId(path, out var entryId))
        {
            return null;
        }
        var service = IOServiceGetMatchingService(0, IORegistryEntryIDMatching(entryId));
        if (service == 0)
        {
            return null;
        }
        IntPtr device;
        try
        {
            device = IOHIDDeviceCreate(IntPtr.Zero, service);
        }
        finally
        {
            IOObjectRelease(service);
        }
        if (device == IntPtr.Zero)
        {
            return null;
        }
        // Callers log their own open failure per retry; a TCC-denied keyboard-class device would otherwise warn every second.
        if (IOHIDDeviceOpen(device, 0) != KIoReturnSuccess)
        {
            CFRelease(device);
            return null;
        }
        // forInput needs no distinct open mode here: the input run loop starts on the first Read.
        return new MacHidDevice(device, ReadInfo(device, path));
    }

    private static string? PathFor(IntPtr device)
    {
        var service = IOHIDDeviceGetService(device);
        if (service == 0)
        {
            return null;
        }
        ulong entryId;
        return IORegistryEntryGetRegistryEntryID(service, &entryId) == KIoReturnSuccess
            ? PathPrefix + entryId.ToString("x", CultureInfo.InvariantCulture)
            : null;
    }

    internal static bool TryParseEntryId(string path, out ulong entryId)
    {
        entryId = 0;
        return path.StartsWith(PathPrefix, StringComparison.Ordinal)
            && ulong.TryParse(path.AsSpan(PathPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out entryId);
    }
}
