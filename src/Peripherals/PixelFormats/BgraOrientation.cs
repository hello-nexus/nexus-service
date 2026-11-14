using System;

namespace Nexus.Service.Peripherals.PixelFormats;

/// <summary>
/// Re-orients a 4-byte-per-pixel frame for glass mounted turned or read through a reflection.
/// The result is the source mirrored left to right when asked, then turned clockwise by the
/// rotation, so a mount turn always acts on the content as authored. A quarter turn needs a
/// square frame unless the caller opts in: turning a rectangle swaps its dimensions, which only
/// a transport that renders the swapped size expects.
/// </summary>
public static class BgraOrientation
{
    /// <summary>True when <see cref="Apply"/> would do anything.</summary>
    public static bool IsIdentity(int rotation, bool mirror) => Normalize(rotation) == 0 && !mirror;

    /// <summary>Folds any whole-degree turn into 0, 90, 180 or 270 clockwise.</summary>
    public static int Normalize(int rotation) => (((rotation % 360) + 360) % 360) / 90 * 90;

    /// <summary>
    /// The turn a frame of this size can really take: a rectangle keeps only the half turn
    /// component, since a quarter turn would change its dimensions.
    /// </summary>
    public static int Effective(int rotation, int width, int height)
    {
        var normalized = Normalize(rotation);
        return width == height || normalized % 180 == 0 ? normalized : (normalized >= 180 ? 180 : 0);
    }

    /// <summary>
    /// Writes the re-oriented frame into <paramref name="dest"/>. With <paramref name="quarterOnRectangle"/> a
    /// quarter turn of a rectangle is honored and <paramref name="dest"/> holds the frame with its sides swapped.
    /// </summary>
    public static void Apply(
        ReadOnlySpan<byte> src, int width, int height, int rotation, bool mirror, Span<byte> dest,
        bool quarterOnRectangle = false)
    {
        int stride = width * 4;
        if (src.Length < stride * height)
        {
            throw new ArgumentException("frame is shorter than width * height * 4", nameof(src));
        }
        if (dest.Length < stride * height)
        {
            throw new ArgumentException("destination is too small", nameof(dest));
        }

        rotation = quarterOnRectangle ? Normalize(rotation) : Effective(rotation, width, height);
        if (rotation % 180 != 0)
        {
            ApplyQuarterTurn(src, width, height, rotation, mirror, dest);
            return;
        }

        bool flip180 = rotation == 180;
        // A 180 turn and a mirror both flip x, so together they cancel to a y-only flip.
        bool flipX = flip180 ^ mirror;
        for (int y = 0; y < height; y++)
        {
            int sy = flip180 ? height - 1 - y : y;
            var srcRow = src.Slice(sy * stride, stride);
            var destRow = dest.Slice(y * stride, stride);
            if (!flipX)
            {
                srcRow.CopyTo(destRow);
                continue;
            }
            for (int x = 0; x < width; x++)
            {
                srcRow.Slice((width - 1 - x) * 4, 4).CopyTo(destRow.Slice(x * 4, 4));
            }
        }
    }

    private static void ApplyQuarterTurn(
        ReadOnlySpan<byte> src, int width, int height, int rotation, bool mirror, Span<byte> dest)
    {
        if (rotation == 90)
        {
            BgraQuarterTurn.RotateCw(src, width, height, dest);
        }
        else
        {
            BgraQuarterTurn.RotateCcw(src, width, height, dest);
        }
        if (!mirror)
        {
            return;
        }
        // Mirroring first and turning after is the turn followed by a flip of the turned rows.
        int stride = height * 4;
        for (int top = 0, bottom = width - 1; top < bottom; top++, bottom--)
        {
            var a = dest.Slice(top * stride, stride);
            var b = dest.Slice(bottom * stride, stride);
            for (int i = 0; i < stride; i++)
            {
                (a[i], b[i]) = (b[i], a[i]);
            }
        }
    }

    /// <summary>
    /// The source pixel that <see cref="Apply"/> placed at (<paramref name="x"/>, <paramref name="y"/>)
    /// of the oriented frame; the inverse a touch point needs to land back on the content.
    /// <paramref name="width"/> and <paramref name="height"/> are the source's, which a quarter turn of a
    /// rectangle (<paramref name="quarterOnRectangle"/>) swaps on the oriented side.
    /// </summary>
    public static (int X, int Y) MapToSource(
        int x, int y, int width, int height, int rotation, bool mirror, bool quarterOnRectangle = false)
    {
        switch (quarterOnRectangle ? Normalize(rotation) : Effective(rotation, width, height))
        {
            case 90:
                (x, y) = (y, height - 1 - x);
                break;
            case 180:
                (x, y) = (width - 1 - x, height - 1 - y);
                break;
            case 270:
                (x, y) = (width - 1 - y, x);
                break;
        }
        return mirror ? (width - 1 - x, y) : (x, y);
    }
}
