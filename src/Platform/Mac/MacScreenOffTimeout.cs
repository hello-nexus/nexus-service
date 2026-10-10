using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// The display-sleep timeout from IOKit's active power-management preferences:
/// a dictionary keyed by the power source in use, holding the "Display Sleep
/// Timer" in minutes.
/// </summary>
internal static partial class MacScreenOffTimeout
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const nint SInt64Type = 4;

    /// <summary>Seconds; 0 is Never; null when unreadable.</summary>
    public static int? Read()
    {
        var prefs = IOPMCopyActivePMPreferences();
        if (prefs == IntPtr.Zero)
        {
            return null;
        }
        var key = CFStringCreateWithCString(IntPtr.Zero, "Display Sleep Timer", Utf8);
        try
        {
            var count = (int)CFDictionaryGetCount(prefs);
            if (count < 1 || key == IntPtr.Zero)
            {
                return null;
            }
            var keys = new IntPtr[count];
            var values = new IntPtr[count];
            CFDictionaryGetKeysAndValues(prefs, keys, values);
            foreach (var source in values)
            {
                var number = CFDictionaryGetValue(source, key);
                long minutes = 0;
                if (number != IntPtr.Zero && CFNumberGetValue(number, SInt64Type, ref minutes))
                {
                    return (int)Math.Clamp(minutes * 60, 0, int.MaxValue);
                }
            }
            return null;
        }
        finally
        {
            if (key != IntPtr.Zero)
            {
                CFRelease(key);
            }
            CFRelease(prefs);
        }
    }

    [LibraryImport(IOKit, EntryPoint = "IOPMCopyActivePMPreferences")]
    private static partial IntPtr IOPMCopyActivePMPreferences();

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string str, uint encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetCount")]
    private static partial nint CFDictionaryGetCount(IntPtr dict);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetKeysAndValues")]
    private static partial void CFDictionaryGetKeysAndValues(IntPtr dict, [Out] IntPtr[] keys, [Out] IntPtr[] values);

    [LibraryImport(CoreFoundation, EntryPoint = "CFDictionaryGetValue")]
    private static partial IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [LibraryImport(CoreFoundation, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CFNumberGetValue(IntPtr number, nint type, ref long value);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(IntPtr obj);
}
