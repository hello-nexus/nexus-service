using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Nexus.Service.Devices.Detection;

/// <summary>
/// macOS USB enumeration: IOKit's IOUSBHostDevice registry entries first
/// (idVendor/idProduct/USB Product Name/USB Serial Number/locationID), with
/// the system_profiler SPUSBDataType -json parser as the fallback - on macOS
/// 26 system_profiler prints an empty USB tree.
/// </summary>
public sealed unsafe class MacUsbEnumerator : IUsbEnumerator
{
    public List<UsbDeviceEntry> Enumerate()
    {
        try
        {
            var native = EnumerateIoKit();
            if (native.Count > 0)
            {
                return native;
            }
            var json = ShellOut("/usr/sbin/system_profiler", "SPUSBDataType", "-json");
            return ParseJson(json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usb-enum] macOS enumeration failed: {ex.Message}");
            return new List<UsbDeviceEntry>();
        }
    }

    private static List<UsbDeviceEntry> EnumerateIoKit()
    {
        var result = new List<UsbDeviceEntry>();
        if (!OperatingSystem.IsMacOS())
        {
            return result;
        }
        var matching = IOServiceMatching("IOUSBHostDevice");
        if (matching == IntPtr.Zero)
        {
            return result;
        }
        uint iterator;
        // IOServiceGetMatchingServices consumes the matching dictionary.
        if (IOServiceGetMatchingServices(0, matching, &iterator) != 0)
        {
            return result;
        }
        try
        {
            for (var service = IOIteratorNext(iterator); service != 0; service = IOIteratorNext(iterator))
            {
                try
                {
                    var vid = IntProperty(service, "idVendor");
                    var pid = IntProperty(service, "idProduct");
                    if (vid is null || pid is null || vid <= 0 || pid <= 0)
                    {
                        continue;
                    }
                    var location = IntProperty(service, "locationID");
                    result.Add(new UsbDeviceEntry
                    {
                        VendorId = vid.Value,
                        ProductId = pid.Value,
                        Name = StringProperty(service, "USB Product Name"),
                        Manufacturer = StringProperty(service, "USB Vendor Name"),
                        Serial = StringProperty(service, "USB Serial Number"),
                        Location = location is null ? "" : $"0x{location.Value:x8}",
                        Class = "",
                        Speed = "",
                        Driver = "",
                        HardwareId = $"USB\\VID_{vid.Value:X4}&PID_{pid.Value:X4}",
                    });
                }
                finally
                {
                    IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }
        return result;
    }

    private static int? IntProperty(uint service, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, KCfStringEncodingUtf8);
        try
        {
            var value = IORegistryEntryCreateCFProperty(service, cfKey, IntPtr.Zero, 0);
            if (value == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                if (CFGetTypeID(value) != CFNumberGetTypeID())
                {
                    return null;
                }
                int n;
                return CFNumberGetValue(value, KCfNumberSInt32Type, &n) ? n : null;
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private static string StringProperty(uint service, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, KCfStringEncodingUtf8);
        try
        {
            var value = IORegistryEntryCreateCFProperty(service, cfKey, IntPtr.Zero, 0);
            if (value == IntPtr.Zero)
            {
                return "";
            }
            try
            {
                if (CFGetTypeID(value) != CFStringGetTypeID())
                {
                    return "";
                }
                var buffer = stackalloc byte[512];
                if (!CFStringGetCString(value, buffer, 512, KCfStringEncodingUtf8))
                {
                    return "";
                }
                return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buffer)?.Trim() ?? "";
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCfStringEncodingUtf8 = 0x08000100;
    private const int KCfNumberSInt32Type = 3;

    [System.Runtime.InteropServices.DllImport(IOKit)] private static extern IntPtr IOServiceMatching([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string name);
    [System.Runtime.InteropServices.DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, uint* iterator);
    [System.Runtime.InteropServices.DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
    [System.Runtime.InteropServices.DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [System.Runtime.InteropServices.DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string str, uint encoding);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, nint type, int* valuePtr);
    [System.Runtime.InteropServices.DllImport(CoreFoundation)] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);

    /// <summary>Pure parser exposed for unit tests.</summary>
    internal static List<UsbDeviceEntry> ParseJson(string json)
    {
        var result = new List<UsbDeviceEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("SPUSBDataType", out var root))
        {
            foreach (var controller in root.EnumerateArray())
            {
                CollectDevices(controller, result);
            }
        }
        return result;
    }

    private static void CollectDevices(JsonElement element, List<UsbDeviceEntry> result)
    {
        if (element.TryGetProperty("vendor_id", out var vidEl) &&
            element.TryGetProperty("product_id", out var pidEl))
        {
            var vid = ParseHexId(vidEl.GetString());
            var pid = ParseHexId(pidEl.GetString());
            if (vid > 0 && pid > 0)
            {
                var name = TryGetString(element, "_name");
                var manufacturer = ExtractManufacturer(vidEl.GetString(), element);
                var serial = TryGetString(element, "serial_num");
                var location = TryGetString(element, "location_id");
                var speed = TryGetString(element, "speed");

                result.Add(new UsbDeviceEntry
                {
                    VendorId = vid,
                    ProductId = pid,
                    Name = name,
                    Manufacturer = manufacturer,
                    Serial = serial,
                    Location = location,
                    Class = "",
                    Speed = speed,
                    Driver = "",
                    HardwareId = $"USB\\VID_{vid:X4}&PID_{pid:X4}",
                });
            }
        }

        if (element.TryGetProperty("_items", out var items))
        {
            foreach (var child in items.EnumerateArray())
            {
                CollectDevices(child, result);
            }
        }
    }

    /// <summary>
    /// system_profiler puts manufacturer inline in vendor_id: "0x3402 (HYTE)".
    /// Fall back to a "manufacturer" key when present (some entries have both).
    /// </summary>
    private static string ExtractManufacturer(string? vendorRaw, JsonElement element)
    {
        var mfg = TryGetString(element, "manufacturer");
        if (!string.IsNullOrEmpty(mfg))
        {
            return mfg;
        }
        if (string.IsNullOrEmpty(vendorRaw))
        {
            return "";
        }
        var openParen = vendorRaw.IndexOf('(');
        var closeParen = vendorRaw.IndexOf(')');
        if (openParen > 0 && closeParen > openParen)
        {
            return vendorRaw.Substring(openParen + 1, closeParen - openParen - 1).Trim();
        }
        return "";
    }

    private static string TryGetString(JsonElement element, string key)
    {
        return element.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
    }

    private static int ParseHexId(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }
        var hex = raw.Trim();
        var spaceIdx = hex.IndexOf(' ');
        if (spaceIdx > 0)
        {
            hex = hex.Substring(0, spaceIdx);
        }
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var val) ? val : 0;
    }

    private static string ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "";
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
