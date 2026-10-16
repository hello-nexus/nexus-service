using System;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Parses the ROG Astral G-sensor block, read from the same ITE IT8915FN as the
/// 12VHPWR telemetry, into the tilt angles GPU Tweak III's "Level Sense" works
/// with. Mirrors ExpanModule.dll's GetSensorData: registers 0x70-0x75 hold
/// big-endian X, Y and Z words, each clamped to +-4096 (1 g), 0x76 holds status
/// flags. The vector is normalized and turned into X = asin(x), Y = asin(y),
/// Z = acos(z), in degrees. GPU Tweak first divides each axis by 4.1 (to
/// milli-g); that cancels out in the normalization, so it is left out here.
/// </summary>
internal static class AstralGSensorParser
{
    public const byte RegisterAddress = 0x70;
    public const int BlockSize = 7;

    private const int FullScale = 4096;

    // A card at rest measures gravity alone, about 1 g. GPU Tweak has no such
    // check; it keeps a card without the sensor, a reading taken mid-bump or a
    // garbage block from becoming a tilt.
    private const double MinMagnitude = FullScale * 0.5;
    private const double MaxMagnitude = FullScale * 1.5;

    public readonly record struct Angles(float X, float Y, float Z);

    /// <summary>The angles for <paramref name="block"/>, or null when it is not
    /// a G-sensor block or does not read as roughly 1 g.</summary>
    public static Angles? Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length != BlockSize)
        {
            return null;
        }

        double x = Axis(block[0], block[1]);
        double y = Axis(block[2], block[3]);
        double z = Axis(block[4], block[5]);
        var total = Math.Sqrt(x * x + y * y + z * z);
        if (total < MinMagnitude || total > MaxMagnitude)
        {
            return null;
        }

        return new Angles(
            (float)Degrees(Math.Asin(x / total)),
            (float)Degrees(Math.Asin(y / total)),
            (float)Degrees(Math.Acos(z / total)));
    }

    private static int Axis(byte high, byte low) => Math.Clamp((int)(short)((high << 8) | low), -FullScale, FullScale);

    private static double Degrees(double radians) => radians * 180 / Math.PI;
}
