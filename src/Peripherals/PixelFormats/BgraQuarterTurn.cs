using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.PixelFormats;

/// <summary>
/// Quarter-turns a BGRA frame for panels whose glass is mounted sideways.
/// </summary>
public static class BgraQuarterTurn
{
    /// <summary>
    /// Square block the transpose walks in. A quarter turn reads rows and writes columns,
    /// so one of the two sides is always strided; blocking keeps both inside L2 instead of
    /// touching a fresh cache line per pixel across a multi-megabyte frame.
    /// </summary>
    private const int Tile = 64;

    /// <summary>
    /// Rotates <paramref name="src"/> a quarter turn counter-clockwise into
    /// <paramref name="dest"/>, which must hold width x height pixels with the sides swapped.
    /// </summary>
    public static void RotateCcw(ReadOnlySpan<byte> src, int width, int height, Span<byte> dest)
    {
        int pixels = width * height;
        if (src.Length < pixels * 4)
        {
            throw new ArgumentException("source is too small", nameof(src));
        }
        if (dest.Length < pixels * 4)
        {
            throw new ArgumentException("destination is too small", nameof(dest));
        }

        // One uint per pixel rather than a 4-byte span copy: the layout is unchanged, so
        // the channels ride along in whatever order the caller had them.
        var s = MemoryMarshal.Cast<byte, uint>(src);
        var d = MemoryMarshal.Cast<byte, uint>(dest);

        // Counter-clockwise sends the source's top-right corner to the destination's
        // top-left, so a source row walks up a destination column.
        for (int ty = 0; ty < height; ty += Tile)
        {
            int yEnd = Math.Min(ty + Tile, height);
            for (int tx = 0; tx < width; tx += Tile)
            {
                int xEnd = Math.Min(tx + Tile, width);
                for (int sy = ty; sy < yEnd; sy++)
                {
                    int srcIndex = sy * width + tx;
                    int destIndex = (width - 1 - tx) * height + sy;
                    for (int sx = tx; sx < xEnd; sx++)
                    {
                        d[destIndex] = s[srcIndex++];
                        destIndex -= height;
                    }
                }
            }
        }
    }
}
