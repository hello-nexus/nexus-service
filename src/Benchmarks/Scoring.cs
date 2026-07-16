using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Benchmarks;

internal static class Scoring
{
    // v2.2-2026.07: primesieve / clpeak+vkpeak / STREAM / DiskSpd
    // Reference machine: Ryzen 7600 / RTX 4060 / DDR5-6000 CL30 / PCIe 4 NVMe
    // Bump on any tool, invocation, or baseline change so the leaderboard
    // partitions. The CPU baseline is tied to the all-core sieve size (4e11);
    // changing it shifts the rate and requires a new version. v2.2 adds
    // multi-trial median scoring for GPU/RAM and a wider clpeak time budget -
    // the invocation changed, not the baselines, but the raw distribution a
    // single run vs. a median draws from differs enough to partition.
    public const string ScoringVersion = "v2.2-2026.07";

    public const double BaselineCpuPrimesPerSec = 2_000_000_000d;
    public const double BaselineGpuGflops = 10_000d;
    public const double BaselineRamGbPerSec = 35d;
    public const double BaselineStorageMbPerSec = 3_000d;

    public static double Score(double raw, double baseline)
    {
        if (baseline <= 0 || raw <= 0)
            return 0;
        return Math.Round(raw / baseline * 1000d, 1);
    }

    public static double Composite(double cpuScore, double gpuScore, double ramScore, double storageScore)
    {
        static double Safe(double v) => v <= 0 ? 1 : v;
        double logSum =
            Math.Log(Safe(cpuScore)) +
            Math.Log(Safe(gpuScore)) +
            Math.Log(Safe(ramScore)) +
            Math.Log(Safe(storageScore));
        return Math.Round(Math.Exp(logSum / 4d), 1);
    }

    /// <summary>Middle value of a sorted copy of values; averages the two middle entries on an even count. Zero on an empty list.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2d : sorted[mid];
    }

    /// <summary>(max - min) / median, a scale-free trial-to-trial spread. Zero when fewer than two values or the median is non-positive.</summary>
    public static double RelativeSpread(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }
        double median = Median(values);
        if (median <= 0)
        {
            return 0;
        }
        return Math.Round((values.Max() - values.Min()) / median, 4);
    }
}
