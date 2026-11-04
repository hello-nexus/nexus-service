#if MACOS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Sums cumulative disk bytes read/written across every IOBlockStorageDriver
/// registry entry's Statistics dictionary - the same counters Activity
/// Monitor's disk history uses. Stateless: every read matches, fetches the
/// statistics dictionary, and releases everything; DiskRateReader diffs consecutive
/// reads into a byte rate.
/// </summary>
internal static partial class MacDiskStats
{
    private const string IOKitFramework = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint KCfStringEncodingUtf8 = 0x08000100;
    private const nint KCfNumberSInt64Type = 4;

    // IOBlockStorageDriver.h: kIOBlockStorageDriverStatistics{,BytesRead,BytesWritten}Key.
    private const string StatisticsKey = "Statistics";
    private const string BytesReadKey = "Bytes (Read)";
    private const string BytesWrittenKey = "Bytes (Write)";

    /// <summary>False when no block-storage driver could be read this pass;
    /// a drive that unmounts between reads shrinks the sums, which the
    /// caller's negative-delta guard discards.</summary>
    public static bool TryReadCumulativeBytes(out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        try
        {
            var matching = IOServiceMatching("IOBlockStorageDriver");
            if (matching == IntPtr.Zero)
            {
                return false;
            }

            // IOServiceGetMatchingServices consumes one reference to the
            // matching dictionary; it must not be released here.
            if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
            {
                return false;
            }

            var anyRead = false;
            try
            {
                uint service;
                while ((service = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        if (TryReadDriverBytes(service, out var read, out var written))
                        {
                            bytesRead += read;
                            bytesWritten += written;
                            anyRead = true;
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

            return anyRead;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadDriverBytes(uint service, out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        var statsKey = CFStringCreateWithCString(IntPtr.Zero, StatisticsKey, KCfStringEncodingUtf8);
        var readKey = CFStringCreateWithCString(IntPtr.Zero, BytesReadKey, KCfStringEncodingUtf8);
        var writtenKey = CFStringCreateWithCString(IntPtr.Zero, BytesWrittenKey, KCfStringEncodingUtf8);
        var stats = IntPtr.Zero;
        try
        {
            if (statsKey == IntPtr.Zero || readKey == IntPtr.Zero || writtenKey == IntPtr.Zero)
            {
                return false;
            }

            stats = IORegistryEntryCreateCFProperty(service, statsKey, IntPtr.Zero, 0);

            // A wrong CF type handed to the typed CF calls crashes in
            // native code where managed catch cannot reach - gate each
            // value on CFGetTypeID before use. CFDictionaryGetValue
            // returns unretained references owned by the statistics dictionary.
            if (stats == IntPtr.Zero || CFGetTypeID(stats) != CFDictionaryGetTypeID())
            {
                return false;
            }

            return TryReadNumber(stats, readKey, out bytesRead) &
                   TryReadNumber(stats, writtenKey, out bytesWritten);
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
            if (readKey != IntPtr.Zero)
            {
                CFRelease(readKey);
            }
            if (writtenKey != IntPtr.Zero)
            {
                CFRelease(writtenKey);
            }
        }
    }

    private static bool TryReadNumber(IntPtr dict, IntPtr key, out long value)
    {
        value = 0;
        var number = CFDictionaryGetValue(dict, key);
        if (number == IntPtr.Zero || CFGetTypeID(number) != CFNumberGetTypeID())
        {
            return false;
        }
        return CFNumberGetValue(number, KCfNumberSInt64Type, ref value);
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
#endif
