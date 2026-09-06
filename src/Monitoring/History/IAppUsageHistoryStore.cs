using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One app's window-aggregate stat for a metric: SQL-side AVG/MAX
/// over the requested [from, to] range, already limited and ordered by the
/// store (top apps by Avg descending).</summary>
public sealed record AppWindowStat(string Name, double Avg, double Max);

/// <summary>One raw (ts, value) reading for a single app within a metric,
/// feeding MetricsDecimation for that app's sparkline points. VramMb is
/// populated only for gpu metrics.</summary>
public sealed record AppRawPoint(long TsSec, double? Value, double? VramMb);

/// <summary>QuerySampledTicks and QueryTopApps answered together - see
/// IAppUsageHistoryStore.QueryWindow.</summary>
public sealed record AppUsageWindow(IReadOnlyList<long> SampledTicks, IReadOnlyList<AppWindowStat> TopApps)
{
    public static readonly AppUsageWindow Empty = new(Array.Empty<long>(), Array.Empty<AppWindowStat>());
}

/// <summary>
/// Persistent store for per-app usage history. MetricsSampler is the only
/// writer (one Append per flush, every MetricsHistory.FlushSeconds ticks,
/// carrying every AppSampleBuffer tick since the last flush); GET
/// /monitoring/history/apps is the reader.
///
/// Metric ids match the scalar wire convention: "cpu", "memory", "storage",
/// "net", "storage-read", "storage-write", "net-down", "net-up",
/// "gpu:&lt;gid&gt;", "vram:&lt;gid&gt;" - see ProcessAppUsageSource for why
/// "net" (and its down/up split) is not always-on the way the others are.
/// </summary>
public interface IAppUsageHistoryStore
{
    /// <summary>Batches every metric sample across every tick in one
    /// transaction. When pruneCutoffSec is not null, also deletes every row
    /// older than it, inside that same transaction.</summary>
    void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec);

    /// <summary>Top maxApps apps by window average for metric, descending.
    /// Aggregation runs in SQL (GROUP BY), not in the caller.</summary>
    IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps);

    /// <summary>Raw (ts, value, vramMb) rows for one app within metric,
    /// ascending by ts - the input MetricsDecimation.Decimate expects.</summary>
    IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec);

    /// <summary>Distinct ts values metric was sampled at within [fromSec,
    /// toSec], ascending - the persisted-side half of the route's db+tail
    /// union used to compute a window's expected sample count.</summary>
    IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec);

    /// <summary>Earliest ts (epoch seconds) this app name appears under any
    /// metric, or null when the app has never been recorded. Retention
    /// pruning can push this later over time as older rows age out.</summary>
    long? QueryFirstSeen(string appName);

    /// <summary>QuerySampledTicks and QueryTopApps for the same window in
    /// one call; a store that scans files answers both from a single pass.
    /// Same results as the two calls made separately.</summary>
    AppUsageWindow QueryWindow(string metric, long fromSec, long toSec, int maxApps) =>
        new(QuerySampledTicks(metric, fromSec, toSec), QueryTopApps(metric, fromSec, toSec, maxApps));

    /// <summary>QueryAppSeries for every name in appNames, keyed
    /// case-insensitively by the requested name; a store that scans files
    /// answers all of them from a single pass. Same per-name results as the
    /// individual calls.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<AppRawPoint>> QueryAppSeriesBatch(
        string metric, IReadOnlyCollection<string> appNames, long fromSec, long toSec)
    {
        var result = new Dictionary<string, IReadOnlyList<AppRawPoint>>(appNames.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var name in appNames)
        {
            result[name] = QueryAppSeries(metric, name, fromSec, toSec);
        }
        return result;
    }
}
