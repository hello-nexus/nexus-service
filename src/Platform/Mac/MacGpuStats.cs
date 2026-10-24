using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Reads GPU utilization from the IOAccelerator registry entry's
/// PerformanceStatistics dictionary ("Device Utilization %"), the same
/// counter Activity Monitor's GPU history uses. Stateless: every read
/// matches, fetches the statistics dictionary, and releases everything. On machines
/// with several accelerators the busiest one is reported.
/// </summary>
internal static partial class MacGpuStats
{
    private const string IOKitFramework = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint KCfStringEncodingUtf8 = 0x08000100;
    private const nint KCfNumberSInt64Type = 4;

    public static int? TryReadDeviceUtilization()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var matching = IOServiceMatching("IOAccelerator");
            if (matching == IntPtr.Zero)
            {
                return null;
            }

            // IOServiceGetMatchingServices consumes one reference to the
            // matching dictionary; it must not be released here.
            if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
            {
                return null;
            }

            int? best = null;
            try
            {
                uint service;
                while ((service = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        // Range-gated per accelerator so one driver's garbage
                        // reading cannot discard a valid sibling's value.
                        var value = ReadUtilization(service);
                        if (value is >= 0 and <= 100 && (!best.HasValue || value.Value > best.Value))
                        {
                            best = value;
                        }
                    }
                    finally
                    {
                        _ = IOObjectRelease(service);
                    }
                }
            }
            finally
            {
                _ = IOObjectRelease(iterator);
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadUtilization(uint service)
    {
        var statsKey = CFStringCreateWithCString(IntPtr.Zero, "PerformanceStatistics", KCfStringEncodingUtf8);
        var utilKey = CFStringCreateWithCString(IntPtr.Zero, "Device Utilization %", KCfStringEncodingUtf8);
        // Owned by the caller (create rule); released below.
        var stats = IntPtr.Zero;
        try
        {
            if (statsKey == IntPtr.Zero || utilKey == IntPtr.Zero)
            {
                return null;
            }

            // Fetching the one dictionary avoids copying the accelerator's whole
            // property table, which costs far more than the single read needs.
            stats = IORegistryEntryCreateCFProperty(service, statsKey, IntPtr.Zero, 0);

            // The generic IOAccelerator match covers every vendor's
            // driver, and a wrong CF type handed to the typed CF calls
            // crashes in native code where managed catch cannot reach -
            // gate each value on CFGetTypeID before use.
            // CFDictionaryGetValue returns unretained references owned by
            // the statistics dictionary.
            if (stats == IntPtr.Zero || CFGetTypeID(stats) != CFDictionaryGetTypeID())
            {
                return null;
            }

            var number = CFDictionaryGetValue(stats, utilKey);
            if (number == IntPtr.Zero || CFGetTypeID(number) != CFNumberGetTypeID())
            {
                return null;
            }

            long value = 0;
            return CFNumberGetValue(number, KCfNumberSInt64Type, ref value) ? (int)value : null;
        }
        finally
        {
            if (stats != IntPtr.Zero)
            {
                CFRelease(stats);
            }
            if (statsKey != IntPtr.Zero)
            {
                CFRelease(statsKey);
            }
            if (utilKey != IntPtr.Zero)
            {
                CFRelease(utilKey);
            }
        }
    }

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceMatching", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKitFramework, EntryPoint = "IOServiceGetMatchingServices")]
    private static partial int IOServiceGetMatchingServices(uint masterPort, IntPtr matching, out uint iterator);

    [LibraryImport(IOKitFramework, EntryPoint = "IOIteratorNext")]
    private static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKitFramework, EntryPoint = "IOObjectRelease")]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKitFramework, EntryPoint = "IORegistryEntryCreateCFProperty")]
    private static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string str, uint encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetValue")]
    private static partial IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFNumberGetValue(IntPtr number, nint type, ref long value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr obj);

    [LibraryImport(CoreFoundation, EntryPoint = "CFGetTypeID")]
    private static partial nuint CFGetTypeID(IntPtr obj);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetTypeID")]
    private static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetTypeID")]
    private static partial nuint CFNumberGetTypeID();
}
