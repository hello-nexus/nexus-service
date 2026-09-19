using System;

namespace Nexus.Service.Rendering;

/// <summary>
/// One-shot JPEG compression for callers that render a fresh bitmap each time (deck keys,
/// monitoring and weather tiles, SL-LCD content) rather than driving a fixed-geometry
/// stream - <see cref="Nexus.Service.Peripherals.JpegPanels.BgraJpegEncoder"/> covers that
/// case and keeps its own handle.
///
/// The compressor handle is thread-static: these renderers run repeatedly on a worker
/// thread, and at a 72x72 deck key the per-call init would cost more than the encode.
/// </summary>
internal static unsafe class TurboJpegOneShot
{
    [ThreadStatic]
    private static IntPtr _handle;

    [ThreadStatic]
    private static int _quality;

    /// <summary>
    /// Encodes <paramref name="pixels"/> (4 bytes per pixel, <paramref name="pixelFormat"/>
    /// one of the TJPF_* values on <see cref="TurboJpeg"/>). Returns null when turbojpeg is
    /// unavailable or the compress call fails, so the caller uses its managed path.
    /// </summary>
    public static byte[]? TryCompress(ReadOnlySpan<byte> pixels, int width, int height, int pixelFormat, int quality)
    {
        if (!TurboJpeg.IsAvailable || width <= 0 || height <= 0
            || pixels.Length < (long)width * height * 4)
        {
            return null;
        }

        var handle = Handle(quality);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        // Worst case for the geometry; with TJPARAM_NOREALLOC set the library will not
        // grow it, and a smaller guess would fail on noisy content.
        var capacity = checked((int)TurboJpeg.tj3JPEGBufSize(width, height, TurboJpeg.Subsamp420));
        var buffer = new byte[capacity];
        nuint size = (nuint)capacity;
        fixed (byte* src = pixels)
        fixed (byte* dst = buffer)
        {
            var outPtr = dst;
            if (TurboJpeg.tj3Compress8(handle, src, width, width * 4, height, pixelFormat, &outPtr, &size) != 0)
            {
                Nexus.Service.Platform.ServiceLog.Warn(
                    $"[jpeg] turbojpeg compress failed ({TurboJpeg.ErrorString(handle)}); using the managed encoder");
                return null;
            }
        }

        var jpeg = new byte[(int)size];
        Array.Copy(buffer, jpeg, jpeg.Length);
        return jpeg;
    }

    private static IntPtr Handle(int quality)
    {
        if (_handle != IntPtr.Zero && _quality == quality)
        {
            return _handle;
        }
        if (_handle == IntPtr.Zero)
        {
            _handle = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
            if (_handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            TurboJpeg.tj3Set(_handle, TurboJpeg.ParamSubsamp, TurboJpeg.Subsamp420);
            TurboJpeg.tj3Set(_handle, TurboJpeg.ParamNoRealloc, 1);
        }
        TurboJpeg.tj3Set(_handle, TurboJpeg.ParamQuality, quality);
        _quality = quality;
        return _handle;
    }
}
