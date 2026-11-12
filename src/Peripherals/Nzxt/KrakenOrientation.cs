using System;
using System.Collections.Concurrent;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// The LCD orientation as the user sees it is degrees. The cooler only knows quarter turns, so a
/// 45 degree step splits into the firmware's quarter turns plus a host-side extra eighth turn
/// (135 is one quarter turn and the extra). Only round panels take the extra: the corners it
/// clips sit outside the glass there, and a square panel would show them.
/// </summary>
public static class KrakenOrientation
{
    /// <summary>Splits degrees into firmware quarter turns and the extra 45 degree step.</summary>
    public static (int QuarterTurns, bool Extra45) Split(int degrees)
    {
        int d = ((degrees % 360) + 360) % 360;
        return (d / 90, d % 90 == 45);
    }

    public static int ToDegrees(int quarterTurns, bool extra45) => ((quarterTurns & 0x03) * 90) + (extra45 ? 45 : 0);

    /// <summary>Whether <paramref name="degrees"/> is a step this panel may be set to.</summary>
    public static bool IsAllowed(int degrees, bool round) =>
        degrees is >= 0 and < 360 && (degrees % 90 == 0 || (round && degrees % 45 == 0));

    // Source index per destination pixel for a clockwise 45 degree turn, -1 where the turned frame
    // has nothing (the clipped corners). Built once per panel size; the per-frame cost is a gather.
    private static readonly ConcurrentDictionary<int, int[]> Maps = new();

    private static int[] MapFor(int size) => Maps.GetOrAdd(size, static n =>
    {
        var map = new int[n * n];
        double c = (n - 1) / 2.0;
        double k = Math.Sqrt(0.5);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                double dx = x - c, dy = y - c;
                int sx = (int)Math.Round(c + (k * dx) + (k * dy));
                int sy = (int)Math.Round(c - (k * dx) + (k * dy));
                map[(y * n) + x] = sx < 0 || sy < 0 || sx >= n || sy >= n ? -1 : (sy * n) + sx;
            }
        }
        return map;
    });

    /// <summary>
    /// Turns a square 4-byte-per-pixel frame a further 45 degrees clockwise into
    /// <paramref name="dst"/>, keeping the source's channel order. Pixels with no source are opaque
    /// black. Allocation free after the first call for a size.
    /// </summary>
    public static void Rotate45(ReadOnlySpan<byte> src, int size, Span<byte> dst)
    {
        var map = MapFor(size);
        for (int i = 0; i < map.Length; i++)
        {
            int to = i * 4;
            int from = map[i];
            if (from < 0)
            {
                dst[to] = 0;
                dst[to + 1] = 0;
                dst[to + 2] = 0;
                dst[to + 3] = 0xFF;
                continue;
            }
            from *= 4;
            dst[to] = src[from];
            dst[to + 1] = src[from + 1];
            dst[to + 2] = src[from + 2];
            dst[to + 3] = src[from + 3];
        }
    }
}
