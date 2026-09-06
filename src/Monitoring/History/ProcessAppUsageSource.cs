using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sensors;

namespace Nexus.Service.Monitoring.History;

/// <summary>One app's download/upload rate pair from ComputeNetSplitRatesCore,
/// kept apart from the combined ComputeNetRatesCore result so "net-down" and
/// "net-up" can each rank and cap independently of "net" and of each
/// other.</summary>
internal readonly record struct AppNetSplitPoint(string Name, double DownBytesPerSec, double UpBytesPerSec);

/// <summary>
/// Production IAppUsageSource: reduces the already-live ProcessMonitor /
/// GpuProcessMonitor snapshots to every app above
/// MetricsHistory.AppUsageEpsilon for each metric, capped at
/// MetricsHistory.TopAppsPerSample. Both monitors already sample on their
/// own always-on loop (kept alive via SetDemand); this only reads their
/// latest snapshot and aggregates by process name (ProcessMonitor is
/// per-pid on Windows, not grouped - see ProcessAggregation). GPU
/// per-process entries are attributed to a metric id ("gpu:&lt;gid&gt;")
/// via the same AdapterLuid the scalar gpu series already resolves -
/// which means re-reading ISensorProvider.GetGpus() every tick, the same
/// call SystemMetricsSource already makes every second; LhmComputer's own
/// throttle absorbs the actual hardware re-poll, so this adds no new
/// hardware I/O, only a redundant in-process LUID-matching pass. Each
/// adapter also contributes a "vram:&lt;gid&gt;" sample, ranked by
/// DedicatedMb rather than GpuPercent - a process can hold significant
/// VRAM while nearly idle, so the two rankings can select different apps.
///
/// "net" is different from every other metric here: INetworkProvider is
/// deliberately NOT put on always-on demand the way ProcessMonitor/
/// GpuProcessMonitor are (see the metrics-history plan's warning against
/// churning process handles for network) - its own sampling loop stays
/// gated on real WebSocket subscribers to "network"/"monitoring", so
/// GetSnapshot() can legitimately be empty or stale for long stretches.
/// "net" history only accumulates while that provider happens to already
/// be sampling for the live topic. GetSnapshot(allowOnDemandSample: false)
/// is load-bearing here: LinuxNetworkProvider otherwise runs a synchronous
/// on-demand scan whenever its snapshot is empty, which this class's own
/// 5-second polling cadence would turn into continuous scanning regardless
/// of subscribers - exactly what the always-on-demand avoidance above is
/// for.
/// </summary>
public sealed class ProcessAppUsageSource : IAppUsageSource
{
    private readonly ProcessMonitor _processes;
    private readonly GpuProcessMonitor _gpuProcesses;
    private readonly ISensorProvider _sensors;
    private readonly INetworkProvider _network;

    private const string DemandSource = "app-usage-history";

    // Wider than a few sampling intervals, so a missed tick or a brief
    // system suspend does not get reported as a rate spike/trough once
    // sampling resumes - mirrors NetworkRateReader.ComputeRate's own gap
    // rejection, scaled up for this class's slower per-app cadence.
    private const long MaxNetElapsedMs = MetricsHistory.AppSampleIntervalSeconds * 1000 * 3;

    private long _prevNetTicksMs = -1;
    private Dictionary<string, (long BytesIn, long BytesOut)> _prevNetByName =
        new(StringComparer.OrdinalIgnoreCase);

    // LUID -> sanitized gpu id, rebuilt from ISensorProvider.GetGpus (which
    // maps and formats every gpu sensor per call) when the gpu process
    // snapshot carries a LUID the last build did not see, when the last
    // build mapped nothing (sensors not enumerated yet at boot, a transient
    // DXGI failure), or every LuidMapRefreshMs regardless - the same bound
    // GpuAdapterLuids puts on its own adapter cache.
    private const long LuidMapRefreshMs = 60_000;
    private Dictionary<string, string> _luidToGpuId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unmappedLuids = new(StringComparer.Ordinal);
    private bool _luidMapBuilt;
    private long _luidMapBuiltAtMs;

    public ProcessAppUsageSource(
        ProcessMonitor processes, GpuProcessMonitor gpuProcesses, ISensorProvider sensors, INetworkProvider network)
    {
        _processes = processes;
        _gpuProcesses = gpuProcesses;
        _sensors = sensors;
        _network = network;

        // Per-app usage recording is always-on regardless of WebSocket
        // subscribers - both monitors self-gate their own sampling loop on
        // demand, so this keeps them running for the lifetime of the
        // service without touching their existing subscription-gated path.
        // INetworkProvider intentionally has no equivalent call here - see
        // the class doc.
        _processes.SetDemand(DemandSource, true);
        _gpuProcesses.SetDemand(DemandSource, true);
    }

    public IReadOnlyList<AppMetricSample> Sample()
    {
        var result = new List<AppMetricSample>();

        var procs = _processes.GetProcesses();
        if (procs.Count > 0)
        {
            var grouped = ProcessAggregation.GroupByName(procs).Values;

            var cpuTop = grouped
                .Where(a => a.CpuPercent > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.CpuPercent)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.CpuPercent, null))
                .ToList();
            result.Add(new AppMetricSample("cpu", cpuTop));

            var memTop = grouped
                .Where(a => a.MemoryMb > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.MemoryMb)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.MemoryMb, null))
                .ToList();
            result.Add(new AppMetricSample("memory", memTop));

            var storageTop = grouped
                .Where(a => a.StorageBytesPerSec > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.StorageBytesPerSec)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.StorageBytesPerSec, null))
                .ToList();
            result.Add(new AppMetricSample("storage", storageTop));

            var storageReadTop = grouped
                .Where(a => a.StorageReadBytesPerSec > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.StorageReadBytesPerSec)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.StorageReadBytesPerSec, null))
                .ToList();
            result.Add(new AppMetricSample("storage-read", storageReadTop));

            var storageWriteTop = grouped
                .Where(a => a.StorageWriteBytesPerSec > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.StorageWriteBytesPerSec)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.StorageWriteBytesPerSec, null))
                .ToList();
            result.Add(new AppMetricSample("storage-write", storageWriteTop));
        }

        var gpuEntries = _gpuProcesses.GetSnapshot();
        if (gpuEntries.Count > 0)
        {
            var luidToGpuId = ResolveLuidToGpuId(gpuEntries);
            foreach (var group in gpuEntries.GroupBy(e => e.AdapterLuid))
            {
                if (!luidToGpuId.TryGetValue(group.Key, out var gid))
                {
                    continue;
                }
                var top = group
                    .OrderByDescending(e => e.GpuPercent)
                    .Take(MetricsHistory.TopAppsPerSample)
                    .Select(e => new AppUsagePoint(e.Name, e.GpuPercent, e.DedicatedMb))
                    .ToList();
                result.Add(new AppMetricSample($"gpu:{gid}", top));

                var vramTop = group
                    .Where(e => e.DedicatedMb > MetricsHistory.AppUsageEpsilon)
                    .OrderByDescending(e => e.DedicatedMb)
                    .Take(MetricsHistory.TopAppsPerSample)
                    .Select(e => new AppUsagePoint(e.Name, e.DedicatedMb, null))
                    .ToList();
                result.Add(new AppMetricSample($"vram:{gid}", vramTop));
            }
        }

        var netEntries = _network.GetSnapshot(allowOnDemandSample: false);
        if (netEntries.Count > 0)
        {
            var (netRates, netSplitRates) = ComputeNetRates(netEntries);

            var netTop = netRates
                .Where(a => a.Value > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.Value)
                .Take(MetricsHistory.TopAppsPerSample)
                .ToList();
            result.Add(new AppMetricSample("net", netTop));

            var netDownTop = netSplitRates
                .Where(a => a.DownBytesPerSec > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.DownBytesPerSec)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.DownBytesPerSec, null))
                .ToList();
            result.Add(new AppMetricSample("net-down", netDownTop));

            var netUpTop = netSplitRates
                .Where(a => a.UpBytesPerSec > MetricsHistory.AppUsageEpsilon)
                .OrderByDescending(a => a.UpBytesPerSec)
                .Take(MetricsHistory.TopAppsPerSample)
                .Select(a => new AppUsagePoint(a.Name, a.UpBytesPerSec, null))
                .ToList();
            result.Add(new AppMetricSample("net-up", netUpTop));
        }

        return result;
    }

    // The network provider only exposes cumulative per-app byte counters
    // (see WindowsNetworkProvider's own doc: the frontend computes rates
    // from deltas) - this diffs consecutive Sample() calls the same way,
    // tracking its own previous-snapshot state independently of
    // MonitoringBroadcaster's separate delta tracking for the live
    // "network" topic (two independent consumers of one cumulative
    // snapshot, at their own cadences).
    private (List<AppUsagePoint> Combined, List<AppNetSplitPoint> Split) ComputeNetRates(IReadOnlyList<NetworkProcessInfo> current)
    {
        var nowTicksMs = Environment.TickCount64;
        var currentByName = new Dictionary<string, (long BytesIn, long BytesOut)>(current.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var info in current)
        {
            currentByName[info.Name] = (info.BytesIn, info.BytesOut);
        }

        var combined = ComputeNetRatesCore(_prevNetByName, _prevNetTicksMs, currentByName, nowTicksMs);
        var split = ComputeNetSplitRatesCore(_prevNetByName, _prevNetTicksMs, currentByName, nowTicksMs);

        _prevNetByName = currentByName;
        _prevNetTicksMs = nowTicksMs;
        return (combined, split);
    }

    /// <summary>Pure delta math extracted from ComputeNetRates, mirroring
    /// NetworkRateReader.ComputeRate's shape for a per-app dictionary
    /// instead of one pair of counters: no baseline yet, a non-positive or
    /// over-threshold elapsed gap, or a negative byte delta (counter reset)
    /// all drop that app rather than reporting a spike/trough. Internal and
    /// static so a test can exercise the elapsed-time boundary
    /// deterministically instead of racing the real wall clock.</summary>
    internal static List<AppUsagePoint> ComputeNetRatesCore(
        IReadOnlyDictionary<string, (long BytesIn, long BytesOut)> prevByName, long prevTicksMs,
        IReadOnlyDictionary<string, (long BytesIn, long BytesOut)> currentByName, long nowTicksMs)
    {
        var points = new List<AppUsagePoint>();
        var elapsedMs = nowTicksMs - prevTicksMs;
        if (prevTicksMs < 0 || elapsedMs <= 0 || elapsedMs > MaxNetElapsedMs)
        {
            return points;
        }

        var seconds = elapsedMs / 1000.0;
        foreach (var (name, bytes) in currentByName)
        {
            if (!prevByName.TryGetValue(name, out var prev))
            {
                continue;
            }
            var deltaIn = bytes.BytesIn - prev.BytesIn;
            var deltaOut = bytes.BytesOut - prev.BytesOut;
            if (deltaIn < 0 || deltaOut < 0)
            {
                continue;
            }
            points.Add(new AppUsagePoint(name, (deltaIn + deltaOut) / seconds, null));
        }
        return points;
    }

    /// <summary>Per-app download/upload rates, matching the same drop rules
    /// as ComputeNetRatesCore (no baseline, over-threshold gap, or a
    /// negative delta in either direction all drop the app) but keeping the
    /// two directions apart instead of summing them - powers the
    /// "net-down"/"net-up" metrics alongside ComputeNetRatesCore's combined
    /// "net". Internal and static for the same deterministic-test seam as
    /// ComputeNetRatesCore.</summary>
    internal static List<AppNetSplitPoint> ComputeNetSplitRatesCore(
        IReadOnlyDictionary<string, (long BytesIn, long BytesOut)> prevByName, long prevTicksMs,
        IReadOnlyDictionary<string, (long BytesIn, long BytesOut)> currentByName, long nowTicksMs)
    {
        var points = new List<AppNetSplitPoint>();
        var elapsedMs = nowTicksMs - prevTicksMs;
        if (prevTicksMs < 0 || elapsedMs <= 0 || elapsedMs > MaxNetElapsedMs)
        {
            return points;
        }

        var seconds = elapsedMs / 1000.0;
        foreach (var (name, bytes) in currentByName)
        {
            if (!prevByName.TryGetValue(name, out var prev))
            {
                continue;
            }
            var deltaIn = bytes.BytesIn - prev.BytesIn;
            var deltaOut = bytes.BytesOut - prev.BytesOut;
            if (deltaIn < 0 || deltaOut < 0)
            {
                continue;
            }
            points.Add(new AppNetSplitPoint(name, deltaIn / seconds, deltaOut / seconds));
        }
        return points;
    }

    private Dictionary<string, string> ResolveLuidToGpuId(IReadOnlyList<GpuProcessEntry> gpuEntries)
    {
        var nowMs = Environment.TickCount64;
        var rebuild = !_luidMapBuilt || _luidToGpuId.Count == 0 || nowMs - _luidMapBuiltAtMs >= LuidMapRefreshMs;
        if (!rebuild)
        {
            foreach (var e in gpuEntries)
            {
                if (!_luidToGpuId.ContainsKey(e.AdapterLuid) && !_unmappedLuids.Contains(e.AdapterLuid))
                {
                    rebuild = true;
                    break;
                }
            }
        }
        if (!rebuild)
        {
            return _luidToGpuId;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in _sensors.GetGpus())
        {
            if (!string.IsNullOrEmpty(g.AdapterLuid))
            {
                map[g.AdapterLuid] = MetricsHistory.SanitizeId(g.Id);
            }
        }
        _luidToGpuId = map;
        _luidMapBuilt = true;
        _luidMapBuiltAtMs = nowMs;
        _unmappedLuids.Clear();
        foreach (var e in gpuEntries)
        {
            if (!map.ContainsKey(e.AdapterLuid))
            {
                _unmappedLuids.Add(e.AdapterLuid);
            }
        }
        return map;
    }
}
