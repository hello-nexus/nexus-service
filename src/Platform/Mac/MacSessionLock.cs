using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Whether the console session's screen is locked, from the window server's
/// session dictionary (CGSSessionScreenIsLocked is present and true only while
/// locked). Reads session state, never input.
/// </summary>
internal static class MacSessionLock
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    /// <summary>Null when there is no session dictionary (no console session) or the call fails.</summary>
    internal static bool? IsLocked()
    {
        try
        {
            var dict = CGSessionCopyCurrentDictionary();
            if (dict == IntPtr.Zero)
            {
                return null;
            }
            var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", KCFStringEncodingUtf8);
            try
            {
                var value = CFDictionaryGetValue(dict, key);
                return value != IntPtr.Zero && CFBooleanGetValue(value);
            }
            finally
            {
                if (key != IntPtr.Zero)
                {
                    CFRelease(key);
                }
                CFRelease(dict);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
