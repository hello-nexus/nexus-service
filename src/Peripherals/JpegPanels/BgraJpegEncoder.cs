using System;
using System.IO;
using Nexus.Service.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Turns one captured BGRA frame into the JPEG these panels take.
///
/// The overlay hands frames back in capture order (BGRA, <c>DXGI_FORMAT_B8G8R8A8_UNORM</c>),
/// which both encoders take as-is, so there is no channel swap here - the bytes are
/// reinterpreted, not rearranged. Encoding lives on this side rather than in the overlay
/// because the overlay has no image library at all (WebView2 only), and adding one to a
/// native-AOT Windows binary would make this path untestable off Windows.
///
/// libjpeg-turbo (4:2:0, SIMD) drives it where the library loaded, ImageSharp otherwise -
/// which picks 4:2:0 itself at this quality, so the two produce near-identical output.
/// Not thread-safe: one instance per stream transport, which is the only caller.
/// </summary>
public sealed unsafe class BgraJpegEncoder : IDisposable
{
    /// <summary>
    /// Matches <c>RenderKit.JpegQuality</c>. These panels are small and the wire is a
    /// 1 KB-at-a-time HID pipe, so quality trades directly against frame time.
    /// </summary>
    public const int Quality = 85;

    private readonly int _width;
    private readonly int _height;
    private readonly JpegEncoder _encoder;
    private readonly MemoryStream _buffer;
    private readonly IntPtr _turbo;
    private readonly byte[] _turboOut;
    private bool _disposed;

    public BgraJpegEncoder(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "panel size must be positive");
        }
        _width = width;
        _height = height;
        _encoder = new JpegEncoder { Quality = Quality };
        // Grows to whatever the busiest frame needs and then stops reallocating.
        _buffer = new MemoryStream(64 * 1024);
        _turboOut = Array.Empty<byte>();
        if (TurboJpeg.IsAvailable)
        {
            _turbo = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
            if (_turbo != IntPtr.Zero
                && (TurboJpeg.tj3Set(_turbo, TurboJpeg.ParamQuality, Quality) != 0
                    || TurboJpeg.tj3Set(_turbo, TurboJpeg.ParamSubsamp, TurboJpeg.Subsamp420) != 0
                    || TurboJpeg.tj3Set(_turbo, TurboJpeg.ParamNoRealloc, 1) != 0))
            {
                TurboJpeg.tj3Destroy(_turbo);
                _turbo = IntPtr.Zero;
            }
            if (_turbo != IntPtr.Zero)
            {
                // Worst case for the geometry, so the library never reallocates per frame.
                _turboOut = new byte[checked((int)TurboJpeg.tj3JPEGBufSize(width, height, TurboJpeg.Subsamp420))];
            }
        }
    }

    public int FrameBytes => _width * _height * 4;

    /// <summary>True when frames go through libjpeg-turbo rather than ImageSharp.</summary>
    public bool IsNative => _turbo != IntPtr.Zero;

    /// <summary>
    /// Encodes one frame. The returned span points into this encoder's own buffer and is
    /// valid only until the next call - callers write it to the wire before encoding again.
    /// </summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> bgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bgra.Length < FrameBytes)
        {
            throw new ArgumentException($"frame must be at least {FrameBytes} bytes", nameof(bgra));
        }
        if (_turbo != IntPtr.Zero)
        {
            nuint size = (nuint)_turboOut.Length;
            fixed (byte* src = bgra)
            fixed (byte* dst = _turboOut)
            {
                var outPtr = dst;
                if (TurboJpeg.tj3Compress8(_turbo, src, _width, _width * 4, _height, TurboJpeg.PixelFormatBgrx, &outPtr, &size) != 0)
                {
                    throw new InvalidOperationException("turbojpeg: " + TurboJpeg.ErrorString(_turbo));
                }
            }
            return _turboOut.AsSpan(0, checked((int)size));
        }
        using var image = Image.LoadPixelData<Bgra32>(bgra[..FrameBytes], _width, _height);
        _buffer.SetLength(0);
        image.Save(_buffer, _encoder);
        return _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _buffer.Dispose();
        if (_turbo != IntPtr.Zero)
        {
            TurboJpeg.tj3Destroy(_turbo);
        }
    }
}
