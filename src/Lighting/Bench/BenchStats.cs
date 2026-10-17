using System;

namespace Nexus.Service.Lighting.Bench;

/// <summary>Distribution summary of one metric across frames.</summary>
public sealed class Percentiles
{
    public int Count { get; set; }
    public double Mean { get; set; }
    public double Min { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Max { get; set; }
}

/// <summary>Pure statistics for the lighting bench.</summary>
internal static class BenchStats
{
    /// <summary>Summary of <paramref name="samples"/>. Percentiles use linear interpolation between
    /// the two nearest ranks. An empty input yields all zeros.</summary>
    public static Percentiles Summarize(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0)
        {
            return new Percentiles();
        }
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        double sum = 0;
        foreach (var v in sorted)
        {
            sum += v;
        }
        return new Percentiles
        {
            Count = sorted.Length,
            Mean = sum / sorted.Length,
            Min = sorted[0],
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
            P99 = Percentile(sorted, 0.99),
            Max = sorted[^1],
        };
    }

    /// <summary>Percentile of an ascending-sorted array; <paramref name="q"/> in the unit interval.</summary>
    public static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }
        if (sorted.Length == 1)
        {
            return sorted[0];
        }
        var pos = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

/// <summary>Coarse canvas fingerprint used to compare backends for output correctness.</summary>
internal static class BenchFingerprint
{
    public const int Grid = 8;

    /// <summary>Mean color of each cell of a Grid x Grid partition of a top-left-origin RGB888
    /// buffer, rounded to the nearest integer. Layout is row-major cells, three bytes each.</summary>
    public static byte[] Compute(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width < Grid || height < Grid || rgb.Length != width * height * 3)
        {
            throw new ArgumentException("buffer does not match the stated size");
        }
        var sums = new long[Grid * Grid * 3];
        var counts = new int[Grid * Grid];
        for (var y = 0; y < height; y++)
        {
            var cy = y * Grid / height;
            for (var x = 0; x < width; x++)
            {
                var cell = cy * Grid + x * Grid / width;
                var o = (y * width + x) * 3;
                sums[cell * 3] += rgb[o];
                sums[cell * 3 + 1] += rgb[o + 1];
                sums[cell * 3 + 2] += rgb[o + 2];
                counts[cell]++;
            }
        }
        var result = new byte[Grid * Grid * 3];
        for (var c = 0; c < Grid * Grid; c++)
        {
            for (var k = 0; k < 3; k++)
            {
                result[c * 3 + k] = (byte)((sums[c * 3 + k] + counts[c] / 2) / counts[c]);
            }
        }
        return result;
    }

    /// <summary>Largest absolute per-channel difference between two fingerprints.</summary>
    public static int MaxChannelDelta(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("fingerprints differ in length");
        }
        var max = 0;
        for (var i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }
        return max;
    }

    /// <summary>Mean absolute per-channel difference between two fingerprints.</summary>
    public static double MeanChannelDelta(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("fingerprints differ in length");
        }
        if (a.Length == 0)
        {
            return 0;
        }
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }
        return (double)sum / a.Length;
    }
}
