using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Shared constants and id conventions for the metrics history pipeline
/// (MetricsSampler -> MetricsSampleBuffer -> IMetricsHistoryStore -> GET
/// /monitoring/history), so the sampler, store, and route agree on retention,
/// flush cadence, and the decimation step ladder without re-deriving them.
/// </summary>
public static class MetricsHistory
{
    /// <summary>Days of 1Hz metric_seconds/gpu_seconds/fan_seconds rows kept
    /// in metrics.db before pruning.</summary>
    public const int RetentionDays = 7;

    /// <summary>Days of temp_buckets rollup rows (cpu/gpu/storage/ram
    /// temperature, one row per component per bucket) kept before pruning.
    /// Wider than RetentionDays because temperature is the one series
    /// diagnostics needs a long history for (sustained-high episodes,
    /// day-by-day review); load/net/fan stay at RetentionDays.</summary>
    public const int TempRetentionDays = 90;

    /// <summary>Bucket width (minutes) of the temp_buckets rollup - wider than
    /// the metric_minutes/gpu_minutes/fan_minutes rollup so a
    /// TempRetentionDays-wide query scans proportionally fewer rows, since no
    /// temperature chart tier ever requests a narrower resolution (see
    /// TemperatureInsights.TierWidthMinutesFor's floor). Mirrored as
    /// TemperatureInsights.NativeBucketMinutes for callers outside this
    /// namespace.</summary>
    public const int TempBucketMinutes = 5;

    /// <summary>Ticks (seconds, MetricsSampler runs at 1Hz) between store
    /// flushes of the buffered tail.</summary>
    public const int FlushSeconds = 30;

    /// <summary>Ticks between per-app usage sampling sub-cadence, a divisor
    /// of FlushSeconds so every flush carries a whole number of app ticks.</summary>
    public const int AppSampleIntervalSeconds = 5;

    /// <summary>Apps recorded per metric per app-usage sampling tick: every
    /// app with a reading above AppUsageEpsilon, ranked by usage and capped
    /// at this many. Sized so a mid-ranked, intermittently-fluctuating app
    /// stays inside the cap on effectively every tick instead of dropping in
    /// and out around a narrower one (see AppUsageStorageEstimateTests for
    /// the measured RetentionDays-wide metrics.db footprint this cap
    /// implies).</summary>
    public const int TopAppsPerSample = 64;

    /// <summary>Floor an app's reading must exceed to be recorded at all.
    /// Zero: excludes only an exact-zero reading (the common case for an
    /// idle background process at 1-second CPU sampling), so the cap above
    /// is spent on apps with any measured activity rather than padding out
    /// with true zeros.</summary>
    public const double AppUsageEpsilon = 0.0;

    /// <summary>Default points per app returned by GET /monitoring/history/apps
    /// when maxPoints is not specified.</summary>
    public const int DefaultMaxAppPoints = 100;

    /// <summary>Decimation step widths in seconds, narrowest first. A query
    /// picks the first step wide enough to keep its point count under the
    /// requested maxPoints (see MetricsDecimation.StepSecondsFor).</summary>
    public static readonly IReadOnlyList<int> StepLadderSeconds =
        new[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    /// <summary>Sanitizes an LHM-style hardware identifier (e.g.
    /// "/gpu-nvidia/0") into a wire-safe series id fragment ("gpu-nvidia-0"):
    /// strips one leading slash, then replaces any remaining slash with a
    /// hyphen. Applied identically to GPU and fan channel ids so
    /// "gpu:&lt;gid&gt;"/"gpu-temp:&lt;gid&gt;" and
    /// "fan:&lt;fid&gt;"/"fan-duty:&lt;fid&gt;" ids round-trip the same way on
    /// both the write and read paths.</summary>
    public static string SanitizeId(string rawId)
    {
        if (string.IsNullOrEmpty(rawId))
        {
            return rawId;
        }
        var trimmed = rawId[0] == '/' ? rawId[1..] : rawId;
        return trimmed.Replace('/', '-');
    }

    /// <summary>Sums two nullable readings: null only when both are null,
    /// otherwise the non-null side(s) added (a null side contributes zero).
    /// Shared by InMemoryMetricsHistoryStore and MonitoringHistoryRoutes'
    /// tail-side bare-gpu aggregation so both agree with AppUsageStore's
    /// persisted-side vram summation.</summary>
    public static double? SumNullable(double? a, double? b) =>
        a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
