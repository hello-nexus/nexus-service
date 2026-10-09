using System;

namespace Nexus.Service.Cooling;

/// <summary>
/// Curve math for ADLX manual fan tuning, which has no direct duty write: a
/// duty is applied as a flat curve with every point at the same speed.
/// </summary>
public static class AdlxFanCurve
{
    public readonly record struct Range(int Min, int Max, int Step);

    /// <summary>The curve speed (%) for a duty: clamped to the card's range and snapped to its step.</summary>
    public static int Speed(int duty, Range range)
    {
        var step = Math.Max(1, range.Step);
        var clamped = Math.Clamp(duty, range.Min, Math.Max(range.Min, range.Max));
        var snapped = range.Min + (int)Math.Round((clamped - range.Min) / (double)step) * step;
        return Math.Min(snapped, Math.Max(range.Min, range.Max));
    }

    /// <summary>Every point at <paramref name="speed"/>, temperatures spread evenly from the range minimum.</summary>
    public static (int Speed, int Temp)[] Flat(int speed, Range temp, int count)
    {
        var points = new (int Speed, int Temp)[count];
        var step = Math.Max(1, temp.Step);
        var stride = count > 0 ? (temp.Max - temp.Min) / count / step * step : 0;
        for (var i = 0; i < count; i++)
            points[i] = (speed, temp.Min + stride * i);
        return points;
    }
}
