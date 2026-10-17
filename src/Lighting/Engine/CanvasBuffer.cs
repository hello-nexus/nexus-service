using System;

namespace Nexus.Service.Lighting.Engine;

public sealed class CanvasBuffer
{
    private readonly byte[] _pixels;

    /// <summary>The engine's render canvas size; the output stream and the screen preview share it.</summary>
    public const int DefaultWidth = 160;
    public const int DefaultHeight = 90;

    public CanvasBuffer(int width = DefaultWidth, int height = DefaultHeight)
    {
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 3];
    }

    public int Width { get; }
    public int Height { get; }
    public int ByteCount => _pixels.Length;

    public void SetPixel(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            return;
        }

        var off = (y * Width + x) * 3;
        _pixels[off] = r;
        _pixels[off + 1] = g;
        _pixels[off + 2] = b;
    }

    public (byte r, byte g, byte b) GetPixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var off = (y * Width + x) * 3;
        return (_pixels[off], _pixels[off + 1], _pixels[off + 2]);
    }

    public void Fill(byte r, byte g, byte b)
    {
        for (int i = 0; i < _pixels.Length; i += 3)
        { _pixels[i] = r; _pixels[i + 1] = g; _pixels[i + 2] = b; }
    }

    public void Clear() => Array.Clear(_pixels, 0, _pixels.Length);
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    /// Bulk write from a top-left-origin RGB888 buffer sized exactly
    /// Width * Height * 3. Used by the GPU readback path.
    /// </summary>
    public void WriteFromRgb(ReadOnlySpan<byte> src)
    {
        if (src.Length != _pixels.Length)
        {
            return;
        }
        src.CopyTo(_pixels);
    }

    /// <summary>
    /// Bulk write from a bottom-up glReadPixels block (RGBA when <paramref name="rgba"/>, else RGB) sized
    /// Width * Height pixels. Flips the rows and drops alpha in one pass.
    /// </summary>
    internal void WriteFromGl(ReadOnlySpan<byte> src, bool rgba)
    {
        if (src.Length < Width * Height * (rgba ? 4 : 3))
        {
            return;
        }
        Gpu.GlReadbackConvert.FlipRows(src, _pixels, Width, Height, rgba);
    }

    /// <summary>
    /// Applies the shared hue / colorize / saturation / contrast post-process
    /// to every pixel in place. Called by Screen Mirror and Media effects so
    /// their output responds to the same four sliders the animate shaders do.
    /// </summary>
    public void ApplyPostProcess(PostProcessState state) => RgbPostProcess.Apply(_pixels, state);

    /// <summary>
    /// In-place geometric flip of the canvas pixels. Used by the Mirror-mode
    /// filter set so the LED canvas can display a mirrored copy of the source
    /// frame without round-tripping pixels to the client. Both flags can be set
    /// at once for a 180-degree flip.
    /// </summary>
    public void ApplyFlip(bool flipX, bool flipY)
    {
        if (!flipX && !flipY) return;
        if (flipX)
        {
            for (int y = 0; y < Height; y++)
            {
                int rowOff = y * Width * 3;
                for (int x = 0; x < Width / 2; x++)
                {
                    int a = rowOff + x * 3;
                    int b = rowOff + (Width - 1 - x) * 3;
                    (_pixels[a], _pixels[b]) = (_pixels[b], _pixels[a]);
                    (_pixels[a + 1], _pixels[b + 1]) = (_pixels[b + 1], _pixels[a + 1]);
                    (_pixels[a + 2], _pixels[b + 2]) = (_pixels[b + 2], _pixels[a + 2]);
                }
            }
        }
        if (flipY)
        {
            int rowBytes = Width * 3;
            var tmp = new byte[rowBytes];
            for (int y = 0; y < Height / 2; y++)
            {
                int top = y * rowBytes;
                int bot = (Height - 1 - y) * rowBytes;
                Buffer.BlockCopy(_pixels, top, tmp, 0, rowBytes);
                Buffer.BlockCopy(_pixels, bot, _pixels, top, rowBytes);
                Buffer.BlockCopy(tmp, 0, _pixels, bot, rowBytes);
            }
        }
    }
}
