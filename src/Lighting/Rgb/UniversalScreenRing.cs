namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// The 60 LEDs round the Lian Li Universal Screen 8.8's bezel, which OpenRGB reports as one
/// linear zone. Camera-mapped on a landscape mount, counter-clockwise seen from the front:
/// 0-14 right to left along the top from 60% across, 15-20 down the left edge, 21-44 left to
/// right along the bottom, 45-50 up the right edge, 51-59 along the top back to 0.
/// </summary>
public static class UniversalScreenRing
{
    public const int LedCount = 60;

    private const int LongEdge = 24;
    private const int ShortEdge = 6;

    /// <summary>The OpenRGB controller name for the bezel's 0416:8050 LED board.</summary>
    public static bool Matches(RgbDevice device) =>
        device.LedCount == LedCount && device.Name == "Lian Li Universal Screen";

    public static (float[] U, float[] V) Positions()
    {
        var u = new float[LedCount];
        var v = new float[LedCount];
        for (int i = 0; i < LedCount; i++)
        {
            (u[i], v[i]) = Position(i);
        }
        return (u, v);
    }

    internal static (float U, float V) Position(int index)
    {
        static float Spread(int i, int count) => (i + 0.5f) / count;
        return index switch
        {
            < 15 => (1f - Spread(index + 9, LongEdge), 0f),
            < 21 => (0f, Spread(index - 15, ShortEdge)),
            < 45 => (Spread(index - 21, LongEdge), 1f),
            < 51 => (1f, 1f - Spread(index - 45, ShortEdge)),
            _ => (1f - Spread(index - 51, LongEdge), 0f),
        };
    }
}
