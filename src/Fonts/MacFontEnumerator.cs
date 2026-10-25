#if MACOS
using System.Runtime.InteropServices;

namespace Nexus.Service.Fonts;

internal static class MacFontEnumerator
{
    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    internal static List<string> Enumerate()
    {
        var names = new List<string>();
        var array = CTFontManagerCopyAvailableFontFamilyNames();
        if (array == IntPtr.Zero)
        {
            return names;
        }
        try
        {
            var count = CFArrayGetCount(array);
            var buffer = new byte[1024];
            for (nint i = 0; i < count; i++)
            {
                var str = CFArrayGetValueAtIndex(array, i);
                if (str != IntPtr.Zero && CFStringGetCString(str, buffer, buffer.Length, KCFStringEncodingUtf8))
                {
                    var len = Array.IndexOf(buffer, (byte)0);
                    names.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len));
                }
            }
        }
        finally
        {
            CFRelease(array);
        }
        return names;
    }

    [DllImport(CoreText)]
    private static extern IntPtr CTFontManagerCopyAvailableFontFamilyNames();

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
#endif
