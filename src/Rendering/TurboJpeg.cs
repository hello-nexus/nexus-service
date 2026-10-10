using System;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Rendering;

/// <summary>
/// libjpeg-turbo's TurboJPEG 3 API: the SIMD baseline JPEG encoder behind every
/// server-rendered device bitmap. It is compiled into the bundled libSkiaSharp
/// (<c>Bundled/skia</c>), whose JPEG codec uses the same copy.
///
/// Faster than Skia's encoder at the same quality: Skia's sets optimize_coding, a second
/// Huffman pass. Every caller keeps the Skia fallback, so nothing here throws on load.
/// </summary>
internal static unsafe class TurboJpeg
{
    private const string Library = "libSkiaSharp";

    // enum TJINIT / TJPARAM / TJPF / TJSAMP, turbojpeg.h 3.x.
    public const int InitCompress = 0;
    public const int ParamNoRealloc = 2;
    public const int ParamQuality = 3;
    public const int ParamSubsamp = 4;
    public const int PixelFormatRgbx = 2;
    public const int PixelFormatBgrx = 3;
    public const int Subsamp420 = 2;

    private static volatile int _available = -1;

    /// <summary>
    /// True once one call into the library has succeeded; false after a load failure.
    /// A false here is nearly always a packaging fault - the DLL missing from a publish -
    /// so it is logged at Warn: the symptom otherwise is a silent 3-4x slowdown.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available < 0)
            {
                try
                {
                    var handle = tj3Init(InitCompress);
                    _available = handle == IntPtr.Zero ? 0 : 1;
                    if (handle != IntPtr.Zero)
                    {
                        tj3Destroy(handle);
                    }
                    else
                    {
                        ServiceLog.Warn("[jpeg] turbojpeg loaded but tj3Init failed; using the Skia encoder");
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    ServiceLog.Warn($"[jpeg] turbojpeg unavailable, falling back to the Skia encoder ({ex.GetType().Name}). "
                        + "Device bitmaps will encode through the slower Skia encoder; check that the bundled libSkiaSharp is beside the service binary.");
                    _available = 0;
                }
            }
            return _available == 1;
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr tj3Init(int initType);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void tj3Destroy(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tj3Set(IntPtr handle, int param, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint tj3JPEGBufSize(int width, int height, int jpegSubsamp);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int tj3Compress8(
        IntPtr handle, byte* srcBuf, int width, int pitch, int height, int pixelFormat,
        byte** jpegBuf, nuint* jpegSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr tj3GetErrorStr(IntPtr handle);

    public static string ErrorString(IntPtr handle) =>
        Marshal.PtrToStringAnsi(tj3GetErrorStr(handle)) ?? "unknown turbojpeg error";
}
