using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SkiaSharp;

namespace Nexus.Service.Activity;

/// <summary>Resolves a shortcut or file path to a PNG-encoded icon. Test seam for <see cref="WindowsShortcutsProvider"/>.</summary>
public interface IWindowsIconExtractor
{
    /// <summary>
    /// Returns a square PNG of the icon for a .lnk (resolved to its real
    /// target first, so the shell's link-arrow overlay never appears) or a
    /// direct exe/dll/ico path, sized as close to sizePx as the source icon
    /// resource allows. Empty array on any failure.
    /// </summary>
    byte[] ExtractPng(string lnkOrTargetPath, int sizePx);

    /// <summary>
    /// Resolves a .lnk to the executable it launches. Unlike the icon path
    /// this ignores IconLocation, which frequently points at a resource DLL
    /// rather than the exe. Empty string on any failure.
    /// </summary>
    string ResolveLinkTargetPath(string lnkPath);
}

/// <summary>
/// Manual-vtable COM (IShellLink/IPersistFile) plus GDI (SHDefExtractIcon +
/// GetDIBits) icon extraction. IntPtr + function pointers throughout, no
/// ComWrappers or reflection marshalling, so this is fully NativeAOT-safe -
/// same shape as <see cref="WindowsVolumeProvider"/> and
/// <see cref="Platform.Windows.NativeFileDialog"/>.
///
/// SHDefExtractIcon (not IShellItemImageFactory) is the extraction API: a
/// .lnk's explicit IconLocation can point at an index inside a resource
/// container (e.g. "imageres.dll,-15"), and only the ExtractIcon family can
/// select that index - IShellItemImageFactory resolves a single file's own
/// icon with no index concept.
///
/// Shell-namespace calls run on a dedicated STA worker thread, matching
/// NativeFileDialog's choice for the same category of API (IShellLink is a
/// sibling of IFileOpenDialog's IShellItem, not the MTA-only Core Audio
/// interfaces WindowsVolumeProvider isolates for a different reason).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsIconExtractor : IWindowsIconExtractor, IDisposable
{
    private const int MaxPath = 260;

    private readonly Thread _comThread;
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());

    public WindowsIconExtractor()
    {
        _comThread = new Thread(RunComLoop)
        {
            IsBackground = true,
            Name = "NexusIconExtractCOM",
        };
        _comThread.SetApartmentState(ApartmentState.STA);
        _comThread.Start();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _comThread.Join(1000); } catch { }
        _queue.Dispose();
    }

    public byte[] ExtractPng(string lnkOrTargetPath, int sizePx) =>
        RunOnComThread(() => ExtractPngCore(lnkOrTargetPath, sizePx), Array.Empty<byte>());

    public string ResolveLinkTargetPath(string lnkPath) =>
        RunOnComThread(() => TryResolveLnkTarget(lnkPath, out var target) ? target : "", "");

    private void RunComLoop()
    {
        var hr = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE)
        {
            Console.Error.WriteLine($"[icon-extract] CoInitializeEx failed: 0x{hr:X8}");
            return;
        }

        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Console.Error.WriteLine($"[icon-extract] worker exception: {ex.Message}"); }
            }
        }
        catch (InvalidOperationException) { /* queue completed */ }
    }

    private T RunOnComThread<T>(Func<T> work, T fallback)
    {
        var result = fallback;
        Exception? err = null;
        using var done = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            });
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
        if (!done.Wait(5000))
        {
            Console.Error.WriteLine("[icon-extract] extraction timed out after 5 s");
            return fallback;
        }
        if (err != null)
        {
            Console.Error.WriteLine($"[icon-extract] extraction failed: {err.Message}");
            return fallback;
        }
        return result;
    }

    private static byte[] ExtractPngCore(string lnkOrTargetPath, int sizePx)
    {
        var iconPath = lnkOrTargetPath;
        var iconIndex = 0;
        if (lnkOrTargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            TryResolveLnk(lnkOrTargetPath, out var resolvedPath, out var resolvedIndex))
        {
            iconPath = resolvedPath;
            iconIndex = resolvedIndex;
        }

        var hIcon = ExtractHIcon(iconPath, iconIndex, sizePx);
        if (hIcon == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        try
        {
            return IconToPng(hIcon);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Resolves a .lnk to the icon it should be drawn with: its explicit
    /// IconLocation if set, else the shortcut's own resolved target. Both are
    /// the .lnk's real payload, never the .lnk file itself, which is what
    /// keeps the shell's shortcut-arrow overlay out of the result.
    /// </summary>
    private static bool TryResolveLnk(string lnkPath, out string targetPath, out int iconIndex)
    {
        targetPath = "";
        iconIndex = 0;

        var clsid = ClsidShellLink;
        var linkIid = IidIShellLinkW;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref linkIid, out var link) < 0 || link == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var persistIid = IidIPersistFile;
            if (QueryInterface(link, ref persistIid, out var persistFile) < 0 || persistFile == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var lnkPtr = Marshal.StringToHGlobalUni(lnkPath);
                try
                {
                    if (Load(persistFile, lnkPtr, StgmRead) < 0)
                    {
                        return false;
                    }
                }
                finally { Marshal.FreeHGlobal(lnkPtr); }

                var iconBuf = Marshal.AllocHGlobal(MaxPath * 2);
                var pathBuf = Marshal.AllocHGlobal(MaxPath * 2);
                try
                {
                    if (GetIconLocation(link, iconBuf, MaxPath, out var idx) >= 0)
                    {
                        // A shortcut's stored IconLocation is often unexpanded
                        // (e.g. "%SystemRoot%\System32\imageres.dll").
                        var explicitIcon = Environment.ExpandEnvironmentVariables(Marshal.PtrToStringUni(iconBuf) ?? "");
                        if (explicitIcon.Length > 0 && File.Exists(explicitIcon))
                        {
                            targetPath = explicitIcon;
                            iconIndex = idx;
                            return true;
                        }
                    }

                    if (GetPath(link, pathBuf, MaxPath, IntPtr.Zero, 0) >= 0)
                    {
                        targetPath = Marshal.PtrToStringUni(pathBuf) ?? "";
                        iconIndex = 0;
                        return targetPath.Length > 0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(iconBuf);
                    Marshal.FreeHGlobal(pathBuf);
                }
            }
            finally { Release(persistFile); }
        }
        finally { Release(link); }

        return false;
    }

    /// <summary>Resolves a .lnk to its target path only - <see cref="TryResolveLnk"/>
    /// answers a different question (what to draw) and returns an icon resource
    /// when the shortcut carries one.</summary>
    private static bool TryResolveLnkTarget(string lnkPath, out string targetPath)
    {
        targetPath = "";

        var clsid = ClsidShellLink;
        var linkIid = IidIShellLinkW;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref linkIid, out var link) < 0 || link == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var persistIid = IidIPersistFile;
            if (QueryInterface(link, ref persistIid, out var persistFile) < 0 || persistFile == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var lnkPtr = Marshal.StringToHGlobalUni(lnkPath);
                try
                {
                    if (Load(persistFile, lnkPtr, StgmRead) < 0)
                    {
                        return false;
                    }
                }
                finally { Marshal.FreeHGlobal(lnkPtr); }

                var pathBuf = Marshal.AllocHGlobal(MaxPath * 2);
                try
                {
                    if (GetPath(link, pathBuf, MaxPath, IntPtr.Zero, 0) >= 0)
                    {
                        targetPath = Marshal.PtrToStringUni(pathBuf) ?? "";
                        return targetPath.Length > 0;
                    }
                }
                finally { Marshal.FreeHGlobal(pathBuf); }
            }
            finally { Release(persistFile); }
        }
        finally { Release(link); }

        return false;
    }

    private static IntPtr ExtractHIcon(string iconPath, int iconIndex, int sizePx)
    {
        // nIconSize packs the large-icon size in the low word, the small-icon
        // size in the high word. phiconSmall is IntPtr.Zero (NULL) - the small
        // size value is unused since nothing retrieves it.
        var nIconSize = (uint)((sizePx & 0xFFFF) | (16 << 16));
        var hr = SHDefExtractIconW(iconPath, iconIndex, 0, out var hIconLarge, IntPtr.Zero, nIconSize);
        return hr >= 0 ? hIconLarge : IntPtr.Zero;
    }

    /// <summary>
    /// Reads the icon's color bitmap as 32bpp top-down BGRA and encodes it as
    /// PNG. Older icons (no PNG-compressed frame) carry no real alpha in the
    /// color bitmap - every byte reads 0 even for opaque pixels - so when the
    /// whole buffer comes back with zero alpha, opacity is instead read from
    /// the icon's 1bpp AND mask (0 = opaque, 1 = transparent).
    /// </summary>
    private static byte[] IconToPng(IntPtr hIcon)
    {
        if (GetIconInfo(hIcon, out var info) == 0)
        {
            return Array.Empty<byte>();
        }

        try
        {
            if (info.hbmColor == IntPtr.Zero || GetObjectW(info.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
            {
                return Array.Empty<byte>();
            }

            var width = bmp.bmWidth;
            var height = bmp.bmHeight;
            if (width <= 0 || height <= 0)
            {
                return Array.Empty<byte>();
            }

            var pixels = new byte[width * height * 4];
            if (!ReadColorDib(info.hbmColor, width, height, pixels))
            {
                return Array.Empty<byte>();
            }

            if (!HasAnyAlpha(pixels) && info.hbmMask != IntPtr.Zero)
            {
                ApplyMaskAlpha(info.hbmMask, width, height, pixels);
            }

            BgraToRgba(pixels);

            fixed (byte* rgba = pixels)
            {
                using var image = new SKPixmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), (IntPtr)rgba, width * 4);
                using var png = image.Encode(SKPngEncoderOptions.Default);
                return png?.ToArray() ?? Array.Empty<byte>();
            }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>32bpp BI_RGB has no color table, so a bare header is the whole BITMAPINFO.</summary>
    private static bool ReadColorDib(IntPtr hbmp, int width, int height, byte[] buffer)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            fixed (byte* p = buffer)
            {
                return GetDIBits(hdc, hbmp, 0, (uint)height, (IntPtr)p, ref bmi, 0) != 0;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// 1bpp targets need trailing color-table space in the BITMAPINFO buffer -
    /// GetDIBits writes its synthesized 2-entry black/white palette there, and
    /// a bare BITMAPINFOHEADER (no room after it) lets that write corrupt
    /// adjacent memory.
    /// </summary>
    private static bool ReadMaskDib(IntPtr hbmp, int width, int height, byte[] buffer)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var bmi = new BITMAPINFO_1BPP
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 1,
                    biCompression = 0,
                },
            };
            fixed (byte* p = buffer)
            {
                return GetDIBitsMasked(hdc, hbmp, 0, (uint)height, (IntPtr)p, ref bmi, 0) != 0;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static bool HasAnyAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void ApplyMaskAlpha(IntPtr hbmMask, int width, int height, byte[] bgra)
    {
        // 1bpp AND mask rows are DWORD-aligned.
        var maskStride = ((width + 31) / 32) * 4;
        var mask = new byte[maskStride * height];
        if (!ReadMaskDib(hbmMask, width, height, mask))
        {
            for (var i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }
            return;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var byteIndex = y * maskStride + x / 8;
                var bit = 7 - (x % 8);
                var transparent = ((mask[byteIndex] >> bit) & 1) != 0;
                bgra[(y * width + x) * 4 + 3] = transparent ? (byte)0 : (byte)255;
            }
        }
    }

    private static void BgraToRgba(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
    }

    // ── IShellLinkW / IPersistFile vtable (manual, no ComWrappers) ──────────

    private static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref Guid, out IntPtr, int>)GetVTableSlot(obj, 0);
        return fn(obj, ref iid, out result);
    }

    // IPersistFile vtable: IUnknown 0-2; IPersist [3]=GetClassID; [4]=IsDirty; [5]=Load.
    private static int Load(IntPtr persistFile, IntPtr pszFileName, uint mode)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)GetVTableSlot(persistFile, 5);
        return fn(persistFile, pszFileName, mode);
    }

    // IShellLinkW vtable: IUnknown 0-2; [3]=GetPath ... [16]=GetIconLocation.
    private static int GetPath(IntPtr link, IntPtr pszFile, int cch, IntPtr findData, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr, uint, int>)GetVTableSlot(link, 3);
        return fn(link, pszFile, cch, findData, flags);
    }

    private static int GetIconLocation(IntPtr link, IntPtr pszIconPath, int cch, out int index)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, out int, int>)GetVTableSlot(link, 16);
        return fn(link, pszIconPath, cch, out index);
    }

    private static IntPtr GetVTableSlot(IntPtr instance, int slot)
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void Release(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)GetVTableSlot(ptr, 2);
        fn(ptr);
    }

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidIPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private const int ClsCtxInprocServer = 0x1;
    private const uint StgmRead = 0;
    private const uint CoInitApartmentThreaded = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>BITMAPINFOHEADER plus the 2-entry RGBQUAD color table a 1bpp DIB needs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO_1BPP
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColor0;
        public uint bmiColor1;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(IntPtr h, int c, out BITMAP pv);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbi, uint usage);

    /// <summary>Same native GetDIBits; a distinct overload so the &lt;=8bpp caller passes a buffer with color-table room.</summary>
    [DllImport("gdi32.dll", ExactSpelling = true, EntryPoint = "GetDIBits")]
    private static extern int GetDIBitsMasked(IntPtr hdc, IntPtr hbmp, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO_1BPP lpbi, uint usage);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(IntPtr ho);
}
