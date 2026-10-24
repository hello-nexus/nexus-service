#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Fonts;

[SupportedOSPlatform("windows")]
internal static class WindowsFontEnumerator
{
    private const byte DefaultCharset = 1;
    // LOGFONTW: five LONGs, eight BYTEs, then the 32-char face name.
    private const int LogFontSize = 92;
    private const int CharsetOffset = 23;
    private const int FaceNameOffset = 28;

    private delegate int EnumFontFamExProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr lParam);

    internal static List<string> Enumerate()
    {
        var names = new List<string>();
        EnumFontFamExProc callback = (logFont, _, _, _) =>
        {
            names.Add(Marshal.PtrToStringUni(logFont + FaceNameOffset) ?? string.Empty);
            return 1;
        };
        var query = new byte[LogFontSize];
        query[CharsetOffset] = DefaultCharset;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return names;
        }
        try
        {
            EnumFontFamiliesExW(dc, query, callback, IntPtr.Zero, 0);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        GC.KeepAlive(callback);
        return names;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int EnumFontFamiliesExW(IntPtr dc, byte[] logFont, EnumFontFamExProc proc, IntPtr lParam, uint flags);
}
#endif
