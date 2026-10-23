using System;

namespace Nexus.Service.Sensors.Astral;

/// <summary>X, Y and Z tilt angles in degrees.</summary>
public readonly record struct AstralAngles(float X, float Y, float Z);

/// <summary>
/// G-sensor block read from the card's power-monitor chip: big-endian X, Y, Z words
/// clamped to one g of full scale, then a status byte. Angles are asin(x), asin(y),
/// acos(z) of the normalized vector.
/// </summary>
internal static class AstralGSensorParser
{
    public const byte RegisterAddress = 0x70;
    public const int BlockSize = 7;
    private const int FullScale = 4096;

    public static AstralAngles? Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length != BlockSize)
        {
            return null;
        }
        double x = Axis(block[0], block[1]);
        double y = Axis(block[2], block[3]);
        double z = Axis(block[4], block[5]);
        var total = Math.Sqrt(x * x + y * y + z * z);
        // A card at rest reads gravity alone; anything far from 1 g is a missing sensor or a bump.
        if (total < FullScale * 0.5 || total > FullScale * 1.5)
        {
            return null;
        }
        return new AstralAngles(
            (float)Deg(Math.Asin(x / total)), (float)Deg(Math.Asin(y / total)), (float)Deg(Math.Acos(z / total)));
    }

    private static int Axis(byte hi, byte lo) => Math.Clamp((int)(short)((hi << 8) | lo), -FullScale, FullScale);
    private static double Deg(double rad) => rad * 180 / Math.PI;
}

/// <summary>
/// Level Sense for one card. Readings are smoothed (half old, half new); a calibration
/// keeps the most frequent smoothed value per axis over <see cref="CalibrationSamples"/>
/// readings (smallest on a tie). X and Y deviations keep the vendor's rule of comparing
/// magnitudes across zero; Sag and Lean carry direction (+ sag: free end lower, + lean: top
/// edge toward the fans), with +X running from the PCIe fingers to the top edge and +Z out
/// through the fans. Not thread-safe; the owner locks.
/// </summary>
internal sealed class AstralLevelSense
{
    public const int CalibrationSamples = 5;
    private const float ModeTolerance = 1e-5f;

    private readonly float[] _x = new float[CalibrationSamples];
    private readonly float[] _y = new float[CalibrationSamples];
    private readonly float[] _z = new float[CalibrationSamples];
    private int _pending = -1;
    private AstralAngles? _smoothed;

    public AstralAngles? Calibration { get; private set; }
    public bool IsCalibrating => _pending >= 0;

    public void Load(AstralAngles calibration) => Calibration = calibration;

    public void StartCalibration() => _pending = 0;

    /// <summary>Adds a reading; returns the calibration it completed, else null.</summary>
    public AstralAngles? Add(AstralAngles sample)
    {
        var s = _smoothed is { } p
            ? new AstralAngles((p.X + sample.X) / 2, (p.Y + sample.Y) / 2, (p.Z + sample.Z) / 2)
            : sample;
        _smoothed = s;
        if (_pending < 0)
        {
            return null;
        }
        (_x[_pending], _y[_pending], _z[_pending]) = (s.X, s.Y, s.Z);
        if (++_pending < CalibrationSamples)
        {
            return null;
        }
        _pending = -1;
        Calibration = new AstralAngles(Mode(_x), Mode(_y), Mode(_z));
        return Calibration;
    }

    /// <summary>X and Y in the vendor's convention: how far each moved from the calibration.</summary>
    public (float X, float Y)? Deviation => _smoothed is { } n && Calibration is { } c
        ? (Delta(n.X, c.X), Delta(n.Y, c.Y))
        : null;

    public (float Sag, float Lean)? Posture => _smoothed is { } n && Calibration is { } c
        ? (n.Y - c.Y, -Wrap(Roll(n) - Roll(c)))
        : null;

    private static float Delta(float now, float cal) => now * cal >= 0 ? now - cal : now + cal;

    // Where up sits around the card's length, from the height (X) and fan (Z) components.
    private static double Roll(AstralAngles a) =>
        Math.Atan2(Math.Cos(a.Z * Math.PI / 180), Math.Sin(a.X * Math.PI / 180)) * 180 / Math.PI;

    private static float Wrap(double d)
    {
        d %= 360;
        return (float)(d > 180 ? d - 360 : d <= -180 ? d + 360 : d);
    }

    internal static float Mode(ReadOnlySpan<float> samples)
    {
        var best = float.NaN;
        var bestCount = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var count = 0;
            for (var j = 0; j < samples.Length; j++)
            {
                if (Math.Abs(samples[j] - samples[i]) <= ModeTolerance)
                {
                    count++;
                }
            }
            if (count > bestCount || (count == bestCount && samples[i] < best))
            {
                (best, bestCount) = (samples[i], count);
            }
        }
        return best;
    }
}
