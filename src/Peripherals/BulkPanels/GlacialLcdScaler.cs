using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Bilinear resize of tightly packed BGRA frames.</summary>
public static class GlacialLcdScaler
{
    public static void Resize(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            src[..(dstWidth * dstHeight * 4)].CopyTo(dst);
            return;
        }
        for (int y = 0; y < dstHeight; y++)
        {
            float fy = (y + 0.5f) * srcHeight / dstHeight - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, srcHeight - 1);
            int y1 = Math.Min(y0 + 1, srcHeight - 1);
            float wy = Math.Clamp(fy - y0, 0f, 1f);
            for (int x = 0; x < dstWidth; x++)
            {
                float fx = (x + 0.5f) * srcWidth / dstWidth - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, srcWidth - 1);
                int x1 = Math.Min(x0 + 1, srcWidth - 1);
                float wx = Math.Clamp(fx - x0, 0f, 1f);
                int o = (y * dstWidth + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = src[(y0 * srcWidth + x0) * 4 + c] * (1 - wx) + src[(y0 * srcWidth + x1) * 4 + c] * wx;
                    float bottom = src[(y1 * srcWidth + x0) * 4 + c] * (1 - wx) + src[(y1 * srcWidth + x1) * 4 + c] * wx;
                    dst[o + c] = (byte)(top * (1 - wy) + bottom * wy + 0.5f);
                }
            }
        }
    }

    /// <summary>
    /// Size of a frame scaled by <paramref name="percent"/>, which snaps to the nearest step that
    /// keeps the frame's exact aspect ratio on whole 8-pixel blocks: for 9:16 that is a multiple of
    /// 72 by 128, so 10 percent steps. At least two steps.
    /// </summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int percent)
    {
        int g = Gcd(width, height);
        int unitW = width / g;
        int unitH = height / g;
        // The smallest unit that is a whole number of 8-pixel blocks on both sides.
        int step = 1;
        while ((unitW * step) % 8 != 0 || (unitH * step) % 8 != 0)
        {
            step++;
        }
        int units = Math.Max(1, width / (unitW * step));
        int chosen = Math.Clamp((int)Math.Round(units * percent / 100.0, MidpointRounding.AwayFromZero), Math.Min(2, units), units);
        return (unitW * step * chosen, unitH * step * chosen);
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
