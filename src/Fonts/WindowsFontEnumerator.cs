#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Lifecycle;
using Nexus.Service.Platform;

namespace Nexus.Service.Fonts;

[SupportedOSPlatform("windows")]
internal static class WindowsFontEnumerator
{
    private const byte DefaultCharset = 1;
    // LOGFONTW layout offsets and total size.
    private const int LogFontSize = 92;
    private const int CharsetOffset = 23;
    private const int FaceNameOffset = 28;
    private const uint FrPrivate = 0x10;
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];

    private delegate int EnumFontFamExProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr lParam);

    internal static List<string> Enumerate()
    {
        var names = new List<string>();
        EnumFontFamExProc callback = (logFont, _, _, _) =>
        {
            names.Add(Marshal.PtrToStringUni(logFont + FaceNameOffset) ?? string.Empty);
            return 1;
        };
        // The service runs as LocalSystem, so GDI misses the console user's per-user installs.
        // Load them privately into this process for the duration of the enumeration.
        var loaded = LoadConsoleUserFonts();
        var query = new byte[LogFontSize];
        query[CharsetOffset] = DefaultCharset;
        try
        {
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
        }
        finally
        {
            foreach (var path in loaded)
            {
                RemoveFontResourceExW(path, FrPrivate, IntPtr.Zero);
            }
        }
        return names;
    }

    private static List<string> LoadConsoleUserFonts()
    {
        var loaded = new List<string>();
        try
        {
            var profile = ConsoleUserSid.ResolveProfilePath();
            if (string.IsNullOrEmpty(profile))
            {
                return loaded;
            }
            var dir = Path.Combine(profile, "AppData", "Local", "Microsoft", "Windows", "Fonts");
            if (!Directory.Exists(dir))
            {
                return loaded;
            }
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                if (Array.IndexOf(FontExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0
                    && AddFontResourceExW(path, FrPrivate, IntPtr.Zero) > 0)
                {
                    loaded.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[system-fonts] per-user fonts skipped: {ex.Message}");
        }
        return loaded;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddFontResourceExW(string path, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveFontResourceExW(string path, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int EnumFontFamiliesExW(IntPtr dc, byte[] logFont, EnumFontFamExProc proc, IntPtr lParam, uint flags);
}
#endif
