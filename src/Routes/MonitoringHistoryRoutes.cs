using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>
/// GET /monitoring/history: the metrics history read path. Merges
/// IMetricsHistoryStore's persisted rows with MetricsSampleBuffer's
/// unflushed tail, decimates every requested series onto one shared time
/// grid, and converts internal epoch-seconds timestamps to the wire's UTC
/// milliseconds. Pinned cross-repo contract with nexus-web's
/// useMetricHistory/monitoringHistory.ts - series ids, kinds, and the
/// {t, avg, max} point shape must not change on one side alone.
///
/// GET /monitoring/privacy: which apps accessed microphone/webcam/location/
/// screen-capture, as sessions overlapping the requested window. Unsupported
/// off Windows (PrivacyAccessWatcher only runs there).
/// </summary>
public static class MonitoringHistoryRoutes
{
    private const int MinMaxPoints = 1;
    private const int MaxMaxPoints = 2000;
    private const int DefaultMaxPoints = 600;

    // Aligned with the minute-rollup rings' own eligibility
    // (BinaryMetricsHistoryStore.IsRollupEligible): below this,
    // QueryScalarsDecimated would fall back to the raw per-second decimation
    // path, which scans more rows than the raw-pull path at a narrow window,
    // so narrower requests stay on the untouched BuildHistoryResponse path;
    // at and above it every request is served from the rollup tier, which
    // scans a bounded number of pre-aggregated minute rows instead of
    // every raw second in the window.
    private const int DecimatedPathMinStepSeconds = 60;

    private const int MinMaxApps = 1;
    // Also bounds an absent maxApps (see clampedMaxApps below): every app
    // sampled in the window, not a top-N, since the process search and the
    // detail panel both read this list. TopAppsPerSample caps one tick's
    // ranked set, but distinct app names across a day-long window churn well
    // past that as different apps rank in and out over time, so this needs
    // headroom above it to hold a busy desktop's full set without silently
    // truncating. Internal so the test project can size a window against it
    // directly instead of duplicating the value.
    internal const int MaxMaxApps = 500;
    private const int MinMaxAppPoints = 1;
    private const int MaxMaxAppPoints = 2000;

    private const int MinEventsLimit = 1;
    private const int MaxEventsLimit = 2000;
    private const int DefaultEventsLimit = 500;
    internal const int MaxCustomEventLabelLength = 120;

    // POST /monitoring/events clamps a t further in the future than this to
    // now, per the wire contract - a client's clock skew should not park an
    // event ahead of every real one on the timeline.
    private const long CustomEventFutureClampMs = 60_000;

    // Query-timing log throttle: at most one line per route per this window,
    // regardless of request volume, so a live scrub session (many requests a
    // second while dragging) never floods the log.
    private static readonly TimeSpan TimingLogThrottle = TimeSpan.FromSeconds(5);
    private static long s_lastHistoryTimingLogTicks;
    private static long s_lastAppsTimingLogTicks;

    public static void MapMonitoringHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/monitoring/history", (
            long? from, long? to, int? maxPoints, string? series,
            IMetricsHistoryStore store, MetricsSampleBuffer buffer, ISensorProvider sensors) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            var fromSec = from.Value / 1000;
            var toSec = to.Value / 1000;
            var clampedMaxPoints = Math.Clamp(maxPoints ?? DefaultMaxPoints, MinMaxPoints, MaxMaxPoints);
            var seriesFilter = ParseSeriesFilter(series);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var adapterLuids = ResolveGpuAdapterLuids(sensors);
                var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), clampedMaxPoints);

                MetricsHistoryResponse response;
                if (stepSeconds >= DecimatedPathMinStepSeconds)
                {
                    // The GROUP BY's own overhead (temp b-tree sort, three
                    // separate round trips) measurably exceeds the raw path's
                    // cost at a moderate reduction ratio, and only wins once
                    // the window is wide enough - DecimatedPathMinStepSeconds
                    // is picked from that measured crossover, not from "any
                    // reduction is happening".
                    var tailSamples = buffer.SnapshotRange(fromSec, toSec);
                    var dbScalars = store.QueryScalarsDecimated(fromSec, toSec, stepSeconds);
                    var dbGpu = store.QueryGpuDecimated(fromSec, toSec, stepSeconds);
                    var dbFan = store.QueryFanDecimated(fromSec, toSec, stepSeconds);
                    var dbComponentTemps = store.QueryComponentTempDecimated(fromSec, toSec, stepSeconds);
                    response = BuildDecimatedHistoryResponse(
                        dbScalars, dbGpu, dbFan, dbComponentTemps, tailSamples, fromSec, toSec, stepSeconds, seriesFilter, adapterLuids);
                }
                else
                {
                    // Buffer read first: a flush landing between the two calls
                    // commits its samples to the store and then RemoveThroughs
                    // them out of the buffer, so querying the store first could
                    // miss those seconds in both reads. Reading the tail before
                    // the store guarantees any sample dropped from the tail by
                    // an intervening flush is already visible in the store read
                    // that follows; MergeSamples's tail-wins-by-ts dedup handles
                    // the overlap either way.
                    var tailSamples = buffer.SnapshotRange(fromSec, toSec);
                    var dbSamples = store.Query(fromSec, toSec);
                    response = BuildHistoryResponse(
                        dbSamples, tailSamples, fromSec, toSec, clampedMaxPoints, seriesFilter, adapterLuids);
                }

                LogTimingThrottled(
                    ref s_lastHistoryTimingLogTicks, "monitoring-history",
                    stopwatch.ElapsedMilliseconds, toSec - fromSec, stepSeconds);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-history] query failed: {ex.Message}");
                return Results.Ok(new MetricsHistoryResponse { Supported = false });
            }
        }).AllowPanel();

        // Resets every ring/segment store IMetricsHistoryStore owns (scalars
        // incl fps, gpu, fan, component temps, temp buckets, app usage).
        // Screen time and fps session history live in separate stores and
        // are untouched; recording continues on the next sampler tick.
        app.MapDelete("/monitoring/history", (IMetricsHistoryStore store) =>
            Results.Ok(new DeleteResponse { Deleted = store.ResetAll() }));

        app.MapGet("/monitoring/privacy", (long? from, long? to, IPrivacySessionStore store) =>
        {
            if (from is null || to is null || to < from)
            {
                return Results.BadRequest(ApiResponse.Fail("from and to are required and to must be >= from"));
            }

            if (!OperatingSystem.IsWindows())
            {
                return Results.Ok(new PrivacyAccessResponse { Supported = false });
            }

            try
            {
                var fromSec = from.Value / 1000;
                var toSec = to.Value / 1000;
                var sessions = store.Query(fromSec, toSec);
                return Results.Ok(BuildPrivacyResponse(sessions, fromSec, toSec));
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-privacy] query failed: {ex.Message}");
                return Results.Ok(new PrivacyAccessResponse { Supported = false });
            }
        }).AllowPanel();

        app.MapGet("/monitoring/events", (long? from, long? to, int? limit, IMonitoringEventStore store) =>
        {
            if (from is null)
            {
                return Results.BadRequest(ApiResponse.Fail("from is required"));
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toMs = to ?? nowMs;
            if (toMs < from.Value)
            {
                return Results.BadRequest(ApiResponse.Fail("to must be >= from"));
            }

            var clampedLimit = Math.Clamp(limit ?? DefaultEventsLimit, MinEventsLimit, MaxEventsLimit);
            var events = store.Query(from.Value, toMs, clampedLimit);
            return Results.Ok(new MonitoringEventsResponse(events.Select(ToEventDto).ToList()));
        }).AllowPanel();

        app.MapPost("/monitoring/events", (MonitoringEventCreateRequest body, IMonitoringEventStore store) =>
        {
            var labelError = NormalizeCustomEventLabel(body.Label, out var label);
            if (labelError is not null)
            {
                return Results.Json(ApiResponse.Fail(labelError), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.T <= 0)
            {
                return Results.Json(ApiResponse.Fail("t must be positive"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var t = ClampFutureEventTime(body.T, nowMs);
            var created = store.Append(t, MonitoringEventKinds.Custom, label, null, custom: true);
            return Results.Json(
                new MonitoringEventCreateResponse(ToEventDto(created)),
                AppJsonContext.Default.MonitoringEventCreateResponse,
                statusCode: 201);
        }).AllowPanel();

        app.MapDelete("/monitoring/events/{id:long}", (long id, IMonitoringEventStore store) =>
        {
            // No lookup-by-id on the store, so the widest possible window
            // finds it - events are low-volume (see MonitoringEventLog's
            // class doc), so this scan costs nothing worth a dedicated
            // store method for a user-triggered, infrequent delete.
            var existing = store.Query(long.MinValue, long.MaxValue, int.MaxValue).FirstOrDefault(e => e.Id == id);
            if (existing is null)
            {
                return Results.NotFound();
            }
            if (!existing.Custom)
            {
                return Results.Json(ApiResponse.Fail("event is not custom"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            store.DeleteCustom(id);
            return Results.NoContent();
        }).AllowPanel();

        app.MapGet("/monitoring/history/apps", (
            long? from, long? to, string? series, string? process, int? maxApps, int? maxPoints,
            IAppUsageHistoryStore appStore, AppSampleBuffer appBuffer, ProcessMonitor processes) =>
        {
            if (from is null || to is null || to < from || string.IsNullOrWhiteSpace(series))
            {
                return Results.BadRequest(ApiResponse.Fail("from, to, and series are required and to must be >= from"));
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var fromSec = from.Value / 1000;
                var toSec = to.Value / 1000;
                // Absent maxApps: every app up to MaxMaxApps, not a top-N (see its doc).
                var clampedMaxApps = maxApps is { } requestedMaxApps
                    ? Math.Clamp(requestedMaxApps, MinMaxApps, MaxMaxApps)
                    : MaxMaxApps;
                var clampedMaxPoints = Math.Clamp(maxPoints ?? MetricsHistory.DefaultMaxAppPoints, MinMaxAppPoints, MaxMaxAppPoints);

                // Buffer read first, matching the sibling /monitoring/history
                // route: a flush landing between the two reads commits its
                // samples to the store and then RemoveThroughs them out of
                // the buffer, so reading the store first can miss those
                // ticks in both reads. Reading the tail first guarantees
                // anything a subsequent flush drops from the buffer is
                // already visible in every store read below; MergeAppTail's
                // tail-wins-by-ts dedup handles the overlap either way.
                // Apps.Count > 0 matches what the store persists: a tick's
                // AppMetricSample only ever produces app_*_seconds rows when
                // it has apps, so a tail tick with none would stop counting
                // as sampled the moment it flushed - counting it here too
                // keeps expectedTicks stable across that boundary.
                var tailForMetric = appBuffer.SnapshotRange(fromSec, toSec)
                    .Select(t => (t.TsSec, Metric: ResolveTailMetric(t, series)))
                    .Where(t => t.Metric is not null && t.Metric.Apps.Count > 0)
                    .Select(t => (t.TsSec, Metric: t.Metric!))
                    .ToList();

                List<AppWindowStat> topApps;
                var seriesByApp = new Dictionary<string, IReadOnlyList<AppRawPoint>>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(process))
                {
                    // The slideout asks for one specific process, which need
                    // not be in the metric's top-N - bypass ranking entirely
                    // and answer only for the requested name. QueryAppSeries
                    // matches case-insensitively but returns no name of its
                    // own, so the response echoes the requested casing
                    // rather than the store's canonical (first-seen) casing;
                    // resolving that would cost a dedicated lookup this path
                    // exists to avoid.
                    var expectedTicks = CountSampledTicks(appStore.QuerySampledTicks(series, fromSec, toSec), tailForMetric);
                    topApps = new List<AppWindowStat>();
                    if (expectedTicks > 0)
                    {
                        var merged = MergeAppTail(appStore.QueryAppSeries(series, process, fromSec, toSec), tailForMetric, process);
                        if (merged.Count > 0)
                        {
                            var sum = merged.Sum(p => p.Value ?? 0);
                            var max = merged.Max(p => p.Value ?? double.MinValue);
                            topApps.Add(new AppWindowStat(process, sum / expectedTicks, max));
                            seriesByApp[process] = merged;
                        }
                    }
                }
                else
                {
                    // Db-only ranking is a candidate pool (the tail is at most a
                    // few AppSampleIntervalSeconds ticks, negligible against any
                    // window wide enough for the db side to matter); every
                    // candidate's final avg/max/points is recomputed below from
                    // the db+tail merge, so a db-side ranking miss only costs a
                    // wasted series, never a wrong number. One store pass
                    // ranks and counts sampled ticks, a second fetches every
                    // candidate's series, so this scales with the window's day
                    // files, not with maxApps.
                    var window = appStore.QueryWindow(series, fromSec, toSec, clampedMaxApps);
                    var expectedTicks = CountSampledTicks(window.SampledTicks, tailForMetric);

                    var candidateNames = new HashSet<string>(window.TopApps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, metric) in tailForMetric)
                    {
                        foreach (var a in metric.Apps)
                        {
                            candidateNames.Add(a.Name);
                        }
                    }

                    var mergedApps = new List<AppWindowStat>();
                    if (expectedTicks > 0)
                    {
                        var dbSeries = appStore.QueryAppSeriesBatch(series, candidateNames, fromSec, toSec);
                        foreach (var name in candidateNames)
                        {
                            var db = dbSeries.TryGetValue(name, out var s) ? s : Array.Empty<AppRawPoint>();
                            var merged = MergeAppTail(db, tailForMetric, name);
                            if (merged.Count == 0)
                            {
                                continue;
                            }
                            var sum = merged.Sum(p => p.Value ?? 0);
                            var max = merged.Max(p => p.Value ?? double.MinValue);
                            mergedApps.Add(new AppWindowStat(name, sum / expectedTicks, max));
                            seriesByApp[name] = merged;
                        }
                    }
                    topApps = mergedApps.OrderByDescending(a => a.Avg).Take(clampedMaxApps).ToList();
                }

                var liveStartedAt = ResolveLiveStartedAtByName(processes.GetProcesses());

                var response = BuildAppsHistoryResponse(
                    topApps, seriesByApp, liveStartedAt,
                    isGpuMetric: series == "gpu" || series.StartsWith("gpu:", StringComparison.Ordinal),
                    fromSec, toSec, clampedMaxPoints);

                LogTimingThrottled(
                    ref s_lastAppsTimingLogTicks, "monitoring-history-apps",
                    stopwatch.ElapsedMilliseconds, toSec - fromSec, topApps.Count);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[monitoring-history-apps] query failed: {ex.Message}");
                return Results.Ok(new AppUsageHistoryResponse { Supported = false });
            }
        }).AllowPanel();

        app.MapGet("/monitoring/process-icon", (
            string? name, HttpContext ctx, ProcessMonitor processes,
            IProcessIconProvider iconProvider, ProcessIconCache cache) =>
        {
            if (string.IsNullOrEmpty(name))
            {
                return Results.BadRequest();
            }

            var exePath = processes.ResolveExecutablePath(name);
            if (exePath is null)
            {
                return Results.NotFound();
            }

            byte[] bytes;
            if (cache.TryGet(exePath, out var cached))
            {
                bytes = cached;
            }
            else
            {
                var extracted = iconProvider.GetIcon(exePath);
                if (extracted is null)
                {
                    // Extraction could not even be attempted (helper not
                    // connected yet, RPC timeout) - a transient state, not
                    // a verdict on this exe; do not cache it as empty.
                    return Results.NotFound();
                }
                bytes = extracted;
                cache.Set(exePath, bytes);
            }

            if (bytes.Length == 0)
            {
                return Results.NotFound();
            }

            // Content hash as the ETag, same pattern as GET /shortcuts/icon:
            // Results.File's entityTag drives the framework's conditional-GET
            // handling, so a matching If-None-Match short-circuits to a
            // bodyless 304. Icons are immutable per path for this service's
            // lifetime, so the cache is long-lived.
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
            var etag = new EntityTagHeaderValue($"\"{hash}\"");
            ctx.Response.Headers.CacheControl = "private, max-age=86400, immutable";
            return Results.File(bytes, "image/png", entityTag: etag);
        }).AllowPanel();
    }

    // Ticks the metric was sampled across db+tail, ts-deduped so a tick
    // straddling a flush boundary counts once - the same window-average
    // denominator QueryTopApps uses, extended to cover ticks the tail has
    // that the db doesn't have yet.
    private static int CountSampledTicks(
        IReadOnlyList<long> dbSampledTicks, IReadOnlyList<(long TsSec, AppMetricSample Metric)> tailForMetric)
    {
        var sampledTicks = new HashSet<long>(dbSampledTicks);
        foreach (var (ts, _) in tailForMetric)
        {
            sampledTicks.Add(ts);
        }
        return sampledTicks.Count;
    }

    private static IReadOnlyDictionary<string, long> ResolveLiveStartedAtByName(IReadOnlyList<ProcessInfo> procs)
    {
        // OrdinalIgnoreCase: the app name stored in app_series (the key
        // BuildAppsHistoryResponse looks up against) keeps whatever casing
        // was first observed, which need not match the live snapshot's
        // current casing.
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, agg) in ProcessAggregation.GroupByName(procs))
        {
            if (agg.StartedAtMs is { } started)
            {
                result[name] = started;
            }
        }
        return result;
    }

    // "cpu"/"memory" (and a specific "gpu:<id>"/"vram:<id>") match the
    // tick's one sample with that exact metric id. Bare "gpu"/"vram" instead
    // aggregate every matching "gpu:<id>"/"vram:<id>" sample in the tick,
    // mirroring AppUsageStore/InMemoryMetricsHistoryStore's unfiltered
    // gpu/vram resolution on the persisted side.
    internal static AppMetricSample? ResolveTailMetric(AppUsageTick tick, string series) =>
        series switch
        {
            "gpu" => AggregateGpuMetrics(tick),
            "vram" => AggregateVramMetrics(tick),
            _ => tick.Metrics.FirstOrDefault(m => m.Metric == series),
        };

    // Sums each app's value (and vram) across every per-adapter gpu:<id>
    // sample in one tick, so a process using two adapters reads as one
    // combined value for that tick - the tail-side mirror of QueryTopApps/
    // QueryAppSeries' SQL pre-aggregation. Null when the tick carries no gpu
    // sample at all, distinct from an empty Apps list (no gpu-active
    // process that tick), matching the "no data this tick" vs "sampled with
    // nothing to report" distinction the store queries already draw.
    internal static AppMetricSample? AggregateGpuMetrics(AppUsageTick tick)
    {
        var gpuSamples = tick.Metrics.Where(m => m.Metric.StartsWith("gpu:", StringComparison.Ordinal)).ToList();
        if (gpuSamples.Count == 0)
        {
            return null;
        }

        var order = new List<string>();
        var sums = new Dictionary<string, (double Value, double? Vram)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in gpuSamples)
        {
            foreach (var a in sample.Apps)
            {
                if (sums.TryGetValue(a.Name, out var acc))
                {
                    sums[a.Name] = (acc.Value + a.Value, MetricsHistory.SumNullable(acc.Vram, a.VramMb));
                }
                else
                {
                    sums[a.Name] = (a.Value, a.VramMb);
                    order.Add(a.Name);
                }
            }
        }

        var points = order.Select(name => new AppUsagePoint(name, sums[name].Value, sums[name].Vram)).ToList();
        return new AppMetricSample("gpu", points);
    }

    // Sums each app's value across every per-adapter vram:<id> sample in one
    // tick, the vram counterpart of AggregateGpuMetrics. The reading itself
    // is the VRAM value (no side-channel VramMb to also sum), so each point
    // carries a null VramMb.
    internal static AppMetricSample? AggregateVramMetrics(AppUsageTick tick)
    {
        var vramSamples = tick.Metrics.Where(m => m.Metric.StartsWith("vram:", StringComparison.Ordinal)).ToList();
        if (vramSamples.Count == 0)
        {
            return null;
        }

        var order = new List<string>();
        var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in vramSamples)
        {
            foreach (var a in sample.Apps)
            {
                if (sums.TryGetValue(a.Name, out var acc))
                {
                    sums[a.Name] = acc + a.Value;
                }
                else
                {
                    sums[a.Name] = a.Value;
                    order.Add(a.Name);
                }
            }
        }

        var points = order.Select(name => new AppUsagePoint(name, sums[name], null)).ToList();
        return new AppMetricSample("vram", points);
    }

    // Db points and the buffered tail can overlap at the flush boundary;
    // the tail wins by ts (same precedence as MergeSamples), and a name
    // match is case-insensitive to match app_series' COLLATE NOCASE key.
    // One point per ts: a db batch re-flushed after a partial store failure
    // can hold a tick twice, and the later occurrence wins.
    internal static List<AppRawPoint> MergeAppTail(
        IReadOnlyList<AppRawPoint> dbPoints,
        IReadOnlyList<(long TsSec, AppMetricSample Metric)> tailForMetric,
        string name)
    {
        var merged = new List<AppRawPoint>(dbPoints.Count + tailForMetric.Count);
        merged.AddRange(dbPoints);
        var sorted = true;
        for (var i = 1; i < merged.Count; i++)
        {
            if (merged[i].TsSec < merged[i - 1].TsSec)
            {
                sorted = false;
                break;
            }
        }
        if (!sorted)
        {
            merged.Sort((a, b) => a.TsSec.CompareTo(b.TsSec));
        }
        var write = 0;
        for (var read = 0; read < merged.Count; read++)
        {
            if (write > 0 && merged[write - 1].TsSec == merged[read].TsSec)
            {
                merged[write - 1] = merged[read];
            }
            else
            {
                merged[write++] = merged[read];
            }
        }
        merged.RemoveRange(write, merged.Count - write);

        foreach (var (ts, metric) in tailForMetric)
        {
            var point = metric.Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (point is null)
            {
                continue;
            }
            var replacement = new AppRawPoint(ts, point.Value, point.VramMb);
            var index = LowerBound(merged, ts);
            if (index < merged.Count && merged[index].TsSec == ts)
            {
                merged[index] = replacement;
            }
            else
            {
                merged.Insert(index, replacement);
            }
        }
        return merged;
    }

    // First index whose ts is >= target in an ascending list (Count when none).
    private static int LowerBound(List<AppRawPoint> points, long ts)
    {
        var lo = 0;
        var hi = points.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (points[mid].TsSec < ts)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    // Pure and directly unit-tested: every input is plain data the route
    // handler above already fetched (store queries + the live process
    // snapshot), so this has no I/O of its own.
    internal static AppUsageHistoryResponse BuildAppsHistoryResponse(
        IReadOnlyList<AppWindowStat> topApps,
        IReadOnlyDictionary<string, IReadOnlyList<AppRawPoint>> seriesByApp,
        IReadOnlyDictionary<string, long> liveStartedAtByName,
        bool isGpuMetric,
        long fromSec, long toSec, int maxPoints)
    {
        var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), maxPoints);

        var apps = new List<AppHistoryEntryWire>(topApps.Count);
        foreach (var stat in topApps)
        {
            var raw = seriesByApp.TryGetValue(stat.Name, out var s) ? s : Array.Empty<AppRawPoint>();
            var points = MetricsDecimation.Decimate(
                raw.Select(p => new MetricSamplePoint(p.TsSec, p.Value)), fromSec, toSec, stepSeconds);

            double? vramAvgMb = null;
            if (isGpuMetric)
            {
                var vramValues = raw.Where(p => p.VramMb is not null).Select(p => p.VramMb!.Value).ToList();
                if (vramValues.Count > 0)
                {
                    vramAvgMb = Math.Round(vramValues.Average(), 1);
                }
            }

            apps.Add(new AppHistoryEntryWire
            {
                Name = stat.Name,
                StartedAtMs = liveStartedAtByName.TryGetValue(stat.Name, out var started) ? started : null,
                Avg = Math.Round(stat.Avg, 1),
                Max = Math.Round(stat.Max, 1),
                VramAvgMb = vramAvgMb,
                Points = points.Select(p => new AppHistoryPointWire { T = p.T * 1000, Avg = Math.Round(p.Avg, 1) }).ToList(),
            });
        }

        return new AppUsageHistoryResponse { Supported = true, Apps = apps };
    }

    private static void LogTimingThrottled(ref long lastLogTicks, string route, long elapsedMs, long windowSeconds, int contextCount)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastLogTicks);
        if (nowTicks - last < TimingLogThrottle.Ticks)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref lastLogTicks, nowTicks, last) != last)
        {
            return;
        }
        ServiceLog.Info($"[{route}] query took {elapsedMs}ms window={windowSeconds}s n={contextCount}");
    }

    // Wide-window fast path: db slots already carry avg/max per field (SQL
    // GROUP BY, see IMetricsHistoryStore.QueryScalarsDecimated/QueryGpuDecimated/
    // QueryFanDecimated), so only the small RAM tail needs C# decimation -
    // reusing MetricsDecimation.Decimate exactly as the narrow-window path
    // does. Same MetricsHistoryResponse shape as BuildHistoryResponse; kept
    // as a separate function rather than folded into it so the existing
    // raw-sample-based tests and call sites are untouched.
    internal static MetricsHistoryResponse BuildDecimatedHistoryResponse(
        IReadOnlyList<ScalarDecimatedSlot> dbScalars,
        IReadOnlyList<GpuDecimatedSlot> dbGpu,
        IReadOnlyList<FanDecimatedSlot> dbFan,
        IReadOnlyList<ComponentTempDecimatedSlot> dbComponentTemps,
        IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds,
        IReadOnlySet<string>? seriesFilter,
        IReadOnlyDictionary<string, string> gpuAdapterLuids)
    {
        var series = new List<MetricSeriesWire>();

        AddDecimatedScalarSeries(series, "cpu", "cpu", "CPU",
            dbScalars.Select(s => (s.Slot, s.CpuAvg, s.CpuMax)), s => s.CpuPercent,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddDecimatedScalarSeries(series, "memory", "memory", "Memory",
            dbScalars.Select(s => (s.Slot, s.MemAvg, s.MemMax)), s => s.MemoryPercent,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddDecimatedScalarSeries(series, "net-in", "net", "Network In",
            dbScalars.Select(s => (s.Slot, s.NetInAvg, s.NetInMax)), s => s.NetInBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "net-out", "net", "Network Out",
            dbScalars.Select(s => (s.Slot, s.NetOutAvg, s.NetOutMax)), s => s.NetOutBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "disk-read", "disk", "Disk Read",
            dbScalars.Select(s => (s.Slot, s.DiskReadAvg, s.DiskReadMax)), s => s.DiskReadBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "disk-write", "disk", "Disk Write",
            dbScalars.Select(s => (s.Slot, s.DiskWriteAvg, s.DiskWriteMax)), s => s.DiskWriteBytesPerSec,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddDecimatedScalarSeries(series, "cpu-temp", "cpu-temp", "CPU Temperature",
            dbScalars.Select(s => (s.Slot, s.CpuTempAvg, s.CpuTempMax)), s => s.CpuTempC,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddDecimatedScalarSeries(series, "fps", "fps", "FPS",
            dbScalars.Select(s => (s.Slot, s.FpsAvg, s.FpsMax)), s => (double?)s.Fps,
            tailSamples, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);

        AddDecimatedGpuSeries(series, dbGpu, tailSamples, fromSec, toSec, stepSeconds, seriesFilter, gpuAdapterLuids);
        AddDecimatedFanSeries(series, dbFan, tailSamples, fromSec, toSec, stepSeconds, seriesFilter);
        AddDecimatedComponentTempSeries(series, dbComponentTemps, tailSamples, fromSec, toSec, stepSeconds, seriesFilter);

        return new MetricsHistoryResponse
        {
            Supported = true,
            RetentionDays = MetricsHistory.RetentionDays,
            StepSeconds = stepSeconds,
            Series = series,
        };
    }

    private static void AddDecimatedScalarSeries(
        List<MetricSeriesWire> output, string id, string kind, string name,
        IEnumerable<(long Slot, double? Avg, double? Max)> dbSlots, Func<MetricSample, double?> tailSelector,
        IReadOnlyList<MetricSample> tailSamples, long fromSec, long toSec, int stepSeconds,
        IReadOnlySet<string>? filter, bool wholeNumbers)
    {
        if (!MatchesFilter(id, kind, filter))
        {
            return;
        }

        var tailPoints = MetricsDecimation.Decimate(
            tailSamples.Select(s => new MetricSamplePoint(s.TsSec, tailSelector(s))), fromSec, toSec, stepSeconds);
        var merged = MergeDecimatedSlots(dbSlots, tailPoints);

        output.Add(new MetricSeriesWire { Id = id, Kind = kind, Name = name, Points = ToWirePoints(merged, wholeNumbers) });
    }

    private static void AddDecimatedGpuSeries(
        List<MetricSeriesWire> output, IReadOnlyList<GpuDecimatedSlot> dbGpu, IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter,
        IReadOnlyDictionary<string, string> adapterLuids)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in dbGpu)
        {
            names[s.GpuId] = s.Name;
        }
        foreach (var s in tailSamples)
        {
            foreach (var g in s.Gpus)
            {
                names[g.GpuId] = g.Name;
            }
        }

        foreach (var (gpuId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var luid = adapterLuids.GetValueOrDefault(gpuId);
            var dbForGpu = dbGpu.Where(s => s.GpuId == gpuId).ToList();

            var loadId = $"gpu:{gpuId}";
            if (MatchesFilter(loadId, "gpu", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.LoadPercent)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForGpu.Select(s => (s.Slot, s.LoadAvg, s.LoadMax)), tailPoints);
                output.Add(new MetricSeriesWire
                {
                    Id = loadId,
                    Kind = "gpu",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(merged, wholeNumbers: false),
                });
            }

            var tempId = $"gpu-temp:{gpuId}";
            if (MatchesFilter(tempId, "gpu-temp", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.TempC)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForGpu.Select(s => (s.Slot, s.TempAvg, s.TempMax)), tailPoints);
                output.Add(new MetricSeriesWire
                {
                    Id = tempId,
                    Kind = "gpu-temp",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(merged, wholeNumbers: false),
                });
            }
        }
    }

    private static void AddDecimatedFanSeries(
        List<MetricSeriesWire> output, IReadOnlyList<FanDecimatedSlot> dbFan, IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in dbFan)
        {
            names[s.FanId] = s.Name;
        }
        foreach (var s in tailSamples)
        {
            foreach (var f in s.Fans)
            {
                names[f.FanId] = f.Name;
            }
        }

        foreach (var (fanId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var dbForFan = dbFan.Where(s => s.FanId == fanId).ToList();

            var rpmId = $"fan:{fanId}";
            if (MatchesFilter(rpmId, "fan", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, (double?)FindFan(s, fanId)?.Rpm)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForFan.Select(s => (s.Slot, s.RpmAvg, s.RpmMax)), tailPoints);
                output.Add(new MetricSeriesWire { Id = rpmId, Kind = "fan", Name = name, Points = ToWirePoints(merged, wholeNumbers: true) });
            }

            var dutyId = $"fan-duty:{fanId}";
            if (MatchesFilter(dutyId, "fan-duty", filter))
            {
                var tailPoints = MetricsDecimation.Decimate(
                    tailSamples.Select(s => new MetricSamplePoint(s.TsSec, (double?)FindFan(s, fanId)?.Duty)),
                    fromSec, toSec, stepSeconds);
                var merged = MergeDecimatedSlots(dbForFan.Select(s => (s.Slot, s.DutyAvg, s.DutyMax)), tailPoints);
                output.Add(new MetricSeriesWire { Id = dutyId, Kind = "fan-duty", Name = name, Points = ToWirePoints(merged, wholeNumbers: false) });
            }
        }
    }

    // mem-temp is one series averaging every kind=="ram" component at each
    // slot (mean of avgs, max of maxes); drive-temp is one series per
    // kind=="storage" component, id-scoped like gpu/fan. pointsForComponent
    // abstracts over the raw-window path (decimating merged samples inline)
    // and the wide-window path (db-slot + tail merge), so the aggregation
    // logic below runs identically for both.
    private static void AddComponentTempSeriesCore(
        List<MetricSeriesWire> output,
        IReadOnlyDictionary<string, (string Kind, string Name)> meta,
        Func<string, IReadOnlyList<MetricPoint>> pointsForComponent,
        IReadOnlySet<string>? filter)
    {
        if (MatchesFilter("mem-temp", "mem-temp", filter))
        {
            var ramPoints = meta.Where(kv => kv.Value.Kind == "ram").Select(kv => pointsForComponent(kv.Key));
            var points = AverageAcrossComponents(ramPoints);
            if (points.Count > 0)
            {
                output.Add(new MetricSeriesWire
                {
                    Id = "mem-temp",
                    Kind = "mem-temp",
                    Name = "Memory Temperature",
                    Points = ToWirePoints(points, wholeNumbers: false),
                });
            }
        }

        var usedDriveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (componentId, info) in meta.Where(kv => kv.Value.Kind == "storage").OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var wireId = $"drive-temp:{ResolveUniqueSanitizedId(componentId, usedDriveIds)}";
            if (!MatchesFilter(wireId, "drive-temp", filter))
            {
                continue;
            }
            output.Add(new MetricSeriesWire
            {
                Id = wireId,
                Kind = "drive-temp",
                Name = info.Name,
                Points = ToWirePoints(pointsForComponent(componentId), wholeNumbers: false),
            });
        }
    }

    // SanitizeId is not injective (e.g. "storage:a/b" and "storage:a-b" both
    // sanitize to "storage:a-b"), so two distinct raw component ids could
    // otherwise collide on the same drive-temp:<id> wire id in one response.
    // On collision, append a hash of the raw id so the disambiguated id is
    // stable for that raw id regardless of iteration order or which sibling
    // claimed the base candidate first.
    private static string ResolveUniqueSanitizedId(string rawComponentId, HashSet<string> usedIds)
    {
        var candidate = MetricsHistory.SanitizeId(rawComponentId);
        if (usedIds.Add(candidate))
        {
            return candidate;
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawComponentId)))[..6].ToLowerInvariant();
        var disambiguated = $"{candidate}-{suffix}";
        while (!usedIds.Add(disambiguated))
        {
            disambiguated += "x";
        }
        return disambiguated;
    }

    // Unions every ram component's slots, averaging whichever components
    // have a reading at each slot (a component missing at a given slot does
    // not drag the average down) and taking the max across components' own
    // per-slot maxes.
    private static List<MetricPoint> AverageAcrossComponents(IEnumerable<IReadOnlyList<MetricPoint>> perComponentPoints)
    {
        var bySlot = new SortedDictionary<long, List<MetricPoint>>();
        foreach (var points in perComponentPoints)
        {
            foreach (var p in points)
            {
                if (!bySlot.TryGetValue(p.T, out var list))
                {
                    list = new List<MetricPoint>();
                    bySlot[p.T] = list;
                }
                list.Add(p);
            }
        }
        return bySlot.Select(kv => new MetricPoint(kv.Key, kv.Value.Average(p => p.Avg), kv.Value.Max(p => p.Max))).ToList();
    }

    private static void AddComponentTempSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter)
    {
        var meta = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);
        foreach (var s in merged)
        {
            foreach (var c in s.ComponentTemps)
            {
                meta[c.ComponentId] = (c.Kind, c.Name);
            }
        }

        AddComponentTempSeriesCore(output, meta, componentId => MetricsDecimation.Decimate(
            merged.Select(s => new MetricSamplePoint(s.TsSec, FindComponent(s, componentId)?.ValueC)),
            fromSec, toSec, stepSeconds), filter);
    }

    private static void AddDecimatedComponentTempSeries(
        List<MetricSeriesWire> output, IReadOnlyList<ComponentTempDecimatedSlot> dbComponentTemps,
        IReadOnlyList<MetricSample> tailSamples, long fromSec, long toSec, int stepSeconds,
        IReadOnlySet<string>? filter)
    {
        var meta = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);
        foreach (var s in dbComponentTemps)
        {
            meta[s.ComponentId] = (s.Kind, s.Name);
        }
        foreach (var s in tailSamples)
        {
            foreach (var c in s.ComponentTemps)
            {
                meta[c.ComponentId] = (c.Kind, c.Name);
            }
        }

        AddComponentTempSeriesCore(output, meta, componentId =>
        {
            var dbForComponent = dbComponentTemps.Where(s => s.ComponentId == componentId).Select(s => (s.Slot, s.Avg, s.Max));
            var tailPoints = MetricsDecimation.Decimate(
                tailSamples.Select(s => new MetricSamplePoint(s.TsSec, FindComponent(s, componentId)?.ValueC)),
                fromSec, toSec, stepSeconds);
            return MergeDecimatedSlots(dbForComponent, tailPoints);
        }, filter);
    }

    // Db slots and the tail can overlap at the flush boundary; the tail wins
    // whole-slot (not per-second like MergeSamples) since it only ever spans
    // the buffered tail - at most the window's right edge or two, which is
    // inherently a live/partial reading either way rather than a fixed
    // historical average.
    private static List<MetricPoint> MergeDecimatedSlots(
        IEnumerable<(long Slot, double? Avg, double? Max)> dbSlots, IReadOnlyList<MetricPoint> tailPoints)
    {
        var map = new SortedDictionary<long, MetricPoint>();
        foreach (var (slot, avg, max) in dbSlots)
        {
            if (avg is { } a && max is { } m)
            {
                map[slot] = new MetricPoint(slot, a, m);
            }
        }
        foreach (var p in tailPoints)
        {
            map[p.T] = p;
        }
        return map.Values.ToList();
    }

    // Pure and directly unit-tested: trims, rejects empty, truncates to
    // MaxCustomEventLabelLength. Returns an error message on failure (label
    // left unset), or null on success (label set via the out parameter).
    internal static string? NormalizeCustomEventLabel(string? rawLabel, out string label)
    {
        var trimmed = (rawLabel ?? "").Trim();
        if (trimmed.Length == 0)
        {
            label = "";
            return "label is required";
        }
        label = trimmed.Length > MaxCustomEventLabelLength ? trimmed[..MaxCustomEventLabelLength] : trimmed;
        return null;
    }

    // Pure and directly unit-tested: a t more than CustomEventFutureClampMs
    // ahead of nowMs clamps to nowMs, per the wire contract.
    internal static long ClampFutureEventTime(long t, long nowMs) =>
        t > nowMs + CustomEventFutureClampMs ? nowMs : t;

    // internal: also called by BroadcastingMonitoringEventStore so the
    // monitoring/events push topic and the HTTP routes share one mapping.
    internal static MonitoringEventDto ToEventDto(MonitoringEvent e) =>
        new(e.Id, e.TUtcMs, e.Kind, e.Label, e.Detail, e.Custom);

    private static HashSet<string>? ParseSeriesFilter(string? series)
    {
        if (string.IsNullOrWhiteSpace(series))
        {
            return null;
        }
        return series
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ResolveGpuAdapterLuids(ISensorProvider sensors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in sensors.GetGpus())
        {
            if (!string.IsNullOrEmpty(g.AdapterLuid))
            {
                result[MetricsHistory.SanitizeId(g.Id)] = g.AdapterLuid;
            }
        }
        return result;
    }

    internal static MetricsHistoryResponse BuildHistoryResponse(
        IReadOnlyList<MetricSample> dbSamples,
        IReadOnlyList<MetricSample> tailSamples,
        long fromSec, long toSec, int maxPoints,
        IReadOnlySet<string>? seriesFilter,
        IReadOnlyDictionary<string, string> gpuAdapterLuids)
    {
        var merged = MergeSamples(dbSamples, tailSamples);
        var stepSeconds = MetricsDecimation.StepSecondsFor(Math.Max(0, toSec - fromSec), maxPoints);

        var series = new List<MetricSeriesWire>();
        AddScalarSeries(series, merged, "cpu", "cpu", "CPU", s => s.CpuPercent, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddScalarSeries(series, merged, "memory", "memory", "Memory", s => s.MemoryPercent, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddScalarSeries(series, merged, "net-in", "net", "Network In", s => s.NetInBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "net-out", "net", "Network Out", s => s.NetOutBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "disk-read", "disk", "Disk Read", s => s.DiskReadBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "disk-write", "disk", "Disk Write", s => s.DiskWriteBytesPerSec, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);
        AddScalarSeries(series, merged, "cpu-temp", "cpu-temp", "CPU Temperature", s => s.CpuTempC, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: false);
        AddScalarSeries(series, merged, "fps", "fps", "FPS", s => (double?)s.Fps, fromSec, toSec, stepSeconds, seriesFilter, wholeNumbers: true);

        AddGpuSeries(series, merged, fromSec, toSec, stepSeconds, seriesFilter, gpuAdapterLuids);
        AddFanSeries(series, merged, fromSec, toSec, stepSeconds, seriesFilter);
        AddComponentTempSeries(series, merged, fromSec, toSec, stepSeconds, seriesFilter);

        return new MetricsHistoryResponse
        {
            Supported = true,
            RetentionDays = MetricsHistory.RetentionDays,
            StepSeconds = stepSeconds,
            Series = series,
        };
    }

    // DB rows and the buffer's tail can overlap at the flush boundary (a
    // sample already committed to disk but not yet trimmed from the tail);
    // the tail wins since it is guaranteed to hold whatever SampleAsync last
    // produced for that second.
    private static List<MetricSample> MergeSamples(IReadOnlyList<MetricSample> db, IReadOnlyList<MetricSample> tail)
    {
        var map = new SortedDictionary<long, MetricSample>();
        foreach (var s in db)
        {
            map[s.TsSec] = s;
        }
        foreach (var s in tail)
        {
            map[s.TsSec] = s;
        }
        return map.Values.ToList();
    }

    private static bool MatchesFilter(string id, string kind, IReadOnlySet<string>? filter) =>
        filter is null || filter.Contains(id) || filter.Contains(kind);

    private static void AddScalarSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        string id, string kind, string name, Func<MetricSample, double?> selector,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter, bool wholeNumbers)
    {
        if (!MatchesFilter(id, kind, filter))
        {
            return;
        }

        var points = MetricsDecimation.Decimate(
            merged.Select(s => new MetricSamplePoint(s.TsSec, selector(s))), fromSec, toSec, stepSeconds);

        output.Add(new MetricSeriesWire
        {
            Id = id,
            Kind = kind,
            Name = name,
            Points = ToWirePoints(points, wholeNumbers),
        });
    }

    private static void AddGpuSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter,
        IReadOnlyDictionary<string, string> adapterLuids)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in merged)
        {
            foreach (var g in s.Gpus)
            {
                names[g.GpuId] = g.Name;
            }
        }

        foreach (var (gpuId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var luid = adapterLuids.GetValueOrDefault(gpuId);

            var loadId = $"gpu:{gpuId}";
            if (MatchesFilter(loadId, "gpu", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.LoadPercent)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire
                {
                    Id = loadId,
                    Kind = "gpu",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(points, wholeNumbers: false),
                });
            }

            var tempId = $"gpu-temp:{gpuId}";
            if (MatchesFilter(tempId, "gpu-temp", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindGpu(s, gpuId)?.TempC)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire
                {
                    Id = tempId,
                    Kind = "gpu-temp",
                    Name = name,
                    AdapterLuid = luid,
                    Points = ToWirePoints(points, wholeNumbers: false),
                });
            }
        }
    }

    private static void AddFanSeries(
        List<MetricSeriesWire> output, IReadOnlyList<MetricSample> merged,
        long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? filter)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in merged)
        {
            foreach (var f in s.Fans)
            {
                names[f.FanId] = f.Name;
            }
        }

        foreach (var (fanId, name) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var rpmId = $"fan:{fanId}";
            if (MatchesFilter(rpmId, "fan", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindFan(s, fanId)?.Rpm)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire { Id = rpmId, Kind = "fan", Name = name, Points = ToWirePoints(points, wholeNumbers: true) });
            }

            var dutyId = $"fan-duty:{fanId}";
            if (MatchesFilter(dutyId, "fan-duty", filter))
            {
                var points = MetricsDecimation.Decimate(
                    merged.Select(s => new MetricSamplePoint(s.TsSec, FindFan(s, fanId)?.Duty)),
                    fromSec, toSec, stepSeconds);
                output.Add(new MetricSeriesWire { Id = dutyId, Kind = "fan-duty", Name = name, Points = ToWirePoints(points, wholeNumbers: false) });
            }
        }
    }

    private static GpuReading? FindGpu(MetricSample sample, string gpuId)
    {
        foreach (var g in sample.Gpus)
        {
            if (g.GpuId == gpuId)
            {
                return g;
            }
        }
        return null;
    }

    private static FanReading? FindFan(MetricSample sample, string fanId)
    {
        foreach (var f in sample.Fans)
        {
            if (f.FanId == fanId)
            {
                return f;
            }
        }
        return null;
    }

    private static ComponentTempReading? FindComponent(MetricSample sample, string componentId)
    {
        foreach (var c in sample.ComponentTemps)
        {
            if (c.ComponentId == componentId)
            {
                return c;
            }
        }
        return null;
    }

    private static List<MetricPoint> ToWirePoints(IReadOnlyList<MetricPoint> points, bool wholeNumbers) =>
        points.Select(p => p with
        {
            T = p.T * 1000,
            Avg = wholeNumbers ? Math.Round(p.Avg) : Math.Round(p.Avg, 1),
            Max = wholeNumbers ? Math.Round(p.Max) : Math.Round(p.Max, 1),
        }).ToList();

    // Sessions are filtered here (not left solely to the store's own WHERE
    // clause) so this stays a pure, directly testable overlap check: an open
    // session (EndUtcSec null) overlaps whenever its start is at or before
    // toSec, since it has no upper bound yet.
    internal static PrivacyAccessResponse BuildPrivacyResponse(
        IReadOnlyList<PrivacySession> sessions, long fromSec, long toSec) =>
        new()
        {
            Supported = true,
            RetentionDays = PrivacyAccess.RetentionDays,
            Sessions = sessions
                .Where(s => s.StartUtcSec <= toSec && (s.EndUtcSec is null || s.EndUtcSec >= fromSec))
                .OrderBy(s => s.StartUtcSec)
                .Select(ToPrivacySessionWire)
                .ToList(),
        };

    // internal: also called by BroadcastingPrivacySessionStore so the
    // monitoring/privacy push topic and GET /monitoring/privacy share one
    // mapping from seconds-based storage to the wire's milliseconds.
    internal static PrivacySessionWire ToPrivacySessionWire(PrivacySession s) => new()
    {
        App = s.AppId,
        Capability = s.Capability,
        Start = s.StartUtcSec * 1000,
        End = s.EndUtcSec is { } end ? end * 1000 : null,
    };
}

// ----- Wire response wrappers -----

public sealed record MetricSeriesWire
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? AdapterLuid { get; init; }
    public IReadOnlyList<MetricPoint> Points { get; init; } = Array.Empty<MetricPoint>();
}

public sealed record MetricsHistoryResponse
{
    public bool Supported { get; init; } = true;
    public int RetentionDays { get; init; } = MetricsHistory.RetentionDays;
    public int StepSeconds { get; init; }
    public IReadOnlyList<MetricSeriesWire> Series { get; init; } = Array.Empty<MetricSeriesWire>();
}

public sealed record PrivacySessionWire
{
    public string App { get; init; } = "";
    public string Capability { get; init; } = "";
    public long Start { get; init; }
    public long? End { get; init; }
}

public sealed record PrivacyAccessResponse
{
    public bool Supported { get; init; } = true;
    public int RetentionDays { get; init; } = PrivacyAccess.RetentionDays;
    public IReadOnlyList<PrivacySessionWire> Sessions { get; init; } = Array.Empty<PrivacySessionWire>();
}

public sealed record AppHistoryPointWire
{
    public long T { get; init; }
    public double Avg { get; init; }
}

public sealed record AppHistoryEntryWire
{
    public string Name { get; init; } = "";
    public long? StartedAtMs { get; init; }
    public double Avg { get; init; }
    public double Max { get; init; }
    /// <summary>Window-average dedicated VRAM in MB; only populated for a
    /// gpu:&lt;gid&gt; series.</summary>
    public double? VramAvgMb { get; init; }
    public IReadOnlyList<AppHistoryPointWire> Points { get; init; } = Array.Empty<AppHistoryPointWire>();
}

public sealed record AppUsageHistoryResponse
{
    public bool Supported { get; init; } = true;
    public IReadOnlyList<AppHistoryEntryWire> Apps { get; init; } = Array.Empty<AppHistoryEntryWire>();
}

// GET/POST/DELETE /monitoring/events. Pinned cross-repo contract with
// nexus-web - field names, kinds, and status codes must not change on one
// side alone.
public sealed record MonitoringEventDto(long Id, long T, string Kind, string Label, string? Detail, bool Custom);

public sealed record MonitoringEventsResponse(IReadOnlyList<MonitoringEventDto> Events);

public sealed record MonitoringEventCreateRequest(long T, string Label);

public sealed record MonitoringEventCreateResponse(MonitoringEventDto Event);
