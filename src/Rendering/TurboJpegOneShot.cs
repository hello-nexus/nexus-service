using System;
using Nexus.Service.Platform;

namespace Nexus.Service.Rendering;

/// <summary>
/// JPEG compression for callers that render a fresh bitmap each time (deck keys, deck
/// monitoring and weather tiles, SL-LCD content) rather than driving a fixed-geometry
/// stream - <see cref="Nexus.Service.Peripherals.JpegPanels.BgraJpegEncoder"/> covers that
/// case and keeps its own handle.
///
/// One process-wide handle behind a lock, not one per thread: these callers run on
/// thread-pool threads, and a per-thread handle would leak a native compressor for every
/// pool thread that ever encoded. An encode here is tens of microseconds, so the lock is
/// never the bottleneck.
/// </summary>
internal static unsafe class TurboJpegOneShot
{
    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static int _quality;
    private static byte[] _out = Array.Empty<byte>();
    private static bool _failureLogged;

    /// <summary>
    /// Encodes <paramref name="pixels"/> (4 bytes per pixel, <paramref name="pixelFormat"/>
    /// one of the TJPF_* values on <see cref="TurboJpeg"/>). Returns null when turbojpeg is
    /// unavailable or the compress call fails, so the caller uses its managed path.
    /// </summary>
    public static byte[]? TryCompress(ReadOnlySpan<byte> pixels, int width, int height, int pixelFormat, int quality)
    {
        if (width <= 0 || height <= 0 || pixels.Length < (long)width * height * 4)
        {
            return null;
        }

        lock (Gate)
        {
            var handle = HandleLocked(quality);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            // Worst case for the geometry; TJPARAM_NOREALLOC means the library will not grow
            // it, and a smaller guess would fail on noisy content. Kept between calls so a
            // 480x480 tile does not put a ~340 KB array on the large-object heap every frame.
            var needed = checked((int)TurboJpeg.tj3JPEGBufSize(width, height, TurboJpeg.Subsamp420));
            if (_out.Length < needed)
            {
                _out = new byte[needed];
            }

            nuint size = (nuint)needed;
            fixed (byte* src = pixels)
            fixed (byte* dst = _out)
            {
                var outPtr = dst;
                if (TurboJpeg.tj3Compress8(handle, src, width, width * 4, height, pixelFormat, &outPtr, &size) != 0)
                {
                    if (!_failureLogged)
                    {
                        _failureLogged = true;
                        ServiceLog.Warn($"[jpeg] turbojpeg compress failed ({TurboJpeg.ErrorString(handle)}); "
                            + "using the managed encoder. Logged once.");
                    }
                    return null;
                }
            }

            _failureLogged = false;
            return _out.AsSpan(0, (int)size).ToArray();
        }
    }

    private static IntPtr HandleLocked(int quality)
    {
        if (_handle == IntPtr.Zero)
        {
            if (!TurboJpeg.IsAvailable)
            {
                return IntPtr.Zero;
            }
            _handle = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
            if (_handle == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            TurboJpeg.tj3Set(_handle, TurboJpeg.ParamSubsamp, TurboJpeg.Subsamp420);
            TurboJpeg.tj3Set(_handle, TurboJpeg.ParamNoRealloc, 1);
            _quality = -1;
        }
        if (_quality != quality)
        {
            TurboJpeg.tj3Set(_handle, TurboJpeg.ParamQuality, quality);
            _quality = quality;
        }
        return _handle;
    }

    /// <summary>True when compression goes through libjpeg-turbo; drives the caller's hoist.</summary>
    public static bool IsAvailable => TurboJpeg.IsAvailable;
}
