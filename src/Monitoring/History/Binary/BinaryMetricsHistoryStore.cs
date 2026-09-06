using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Monitoring.Events.Binary;

namespace Nexus.Service.Monitoring.History.Binary;

/// <summary>
/// Binary-file-backed IMetricsHistoryStore, the sole IMetricsHistoryStore
/// implementation (see the metrics-store design notes). The 1Hz scalar
/// series (cpu/mem/net-in/net-out/cpu-temp) runs through ScalarRingStore and
/// SuperBlock; the scalar minute rollup (ScalarMinuteRollupRing) and
/// per-entity gpu/fan/storage-RAM-temp history (GpuRingStore/FanRingStore/
/// TempComponentRingStore, each their own raw-second plus minute-rollup
/// rings) sit alongside it. The unified 90-day cpu/gpu/storage/ram
/// temperature bucket rollup (TempBucketStore) is rebuilt from the other
/// rings' raw data on every Append via RebuildTempBuckets.
///
/// Individual rings are lock-free against a concurrent reader (see
/// RingFile); a store-wide lock instead serializes Append against ResetAll,
/// since resetting several rings is not atomic across them.
///
/// IAppUsageHistoryStore is served via AppUsageStore, the per-app usage
/// tier - see that class's doc for the day-segment format and the
/// AppMetricSample "gpu:&lt;gid&gt;"/"vram:&lt;gid&gt;" dependency on
/// GpuRingStore.TryGetIndex.
///
/// IPrivacySessionStore is served via PrivacyLog, an append-only log with
/// an in-RAM index rather than a fixed-slot ring or day segments - see that
/// class's doc for why privacy sessions get their own format (low-volume,
/// transition-driven, keyed by (appId, capability, startUtcSec) instead of a
/// timestamp).
///
/// IMonitoringEventStore is served via MonitoringEventLog, an append-only
/// log of discrete point events (usb attach/detach, app-open/
/// uac-escalation, custom) - see that class's doc for why it rewrites the
/// file on delete/prune rather than layering a superseding record the way
/// PrivacyLog's session upserts do.
///
/// Wired into NexusServiceCollectionExtensions.AddNexusMonitoringHistory;
/// the parameterless constructor resolves the shared config root every
/// binary store in the db/ tree uses (NexusDataPaths.DatabaseDir()).
/// </summary>
public sealed class BinaryMetricsHistoryStore : IMetricsHistoryStore, IAppUsageHistoryStore, IPrivacySessionStore, IMonitoringEventStore
{
    private const string SuperBlockFileName = "super";
    private const string ScalarsFileName = "scalars.ring";
    private const string ScalarsMinuteFileName = "scalars.min";
    private const string FpsFileName = "fps.ring";
    private const string FpsMinuteFileName = "fps.min";

    // Real hardware never comes close to this many distinct GPUs, fan
    // channels, or storage/ram temperature components; the cap only exists
    // so the store's footprint stays bounded if it somehow did - see
    // EntityRegistry.RegisterOrGet/TempComponentRegistry.RegisterOrGet.
    private const int EntityCapacity = 32;

    // TempBucketStore's registry spans three domains in one namespace: the
    // single fixed "cpu" key, up to EntityCapacity gpus, and up to
    // EntityCapacity storage/ram components.
    private const int TempBucketEntityCapacity = EntityCapacity * 2 + 1;

    private readonly SuperBlock _superBlock;
    private readonly ScalarRingStore _scalars;
    private readonly ScalarMinuteRollupRing _scalarRollup;
    private readonly FpsRingStore _fps;
    private readonly FpsMinuteRollupRing _fpsRollup;
    private readonly GpuRingStore _gpus;
    private readonly FanRingStore _fans;
    private readonly TempComponentRingStore _tempComponents;
    private readonly TempBucketStore _tempBuckets;
    private readonly AppUsageStore _apps;
    private readonly PrivacyLog _privacy;
    private readonly MonitoringEventLog _events;

    // Oldest ts a temp-bucket rebuild may trust the scalar/gpu/temp-component
    // rings to still hold in full. This field recovers from
    // SuperBlock.SourceFloorSec, the same cutoff value every ring's own
    // PruneFloorSec recovers from (both are raised together, from the same
    // Append prune cutoff, every time) - so on reopen it is exactly as
    // current as what the rings themselves already hide, never behind it.
    private long? _sourceFloorSec;

    // Serializes Append against ResetAll (see the class doc); also guards
    // _resetAtUtcSec, which drops samples the buffer replays after a reset.
    private readonly object _resetLock = new();
    private long _resetAtUtcSec = long.MinValue;

    public BinaryMetricsHistoryStore() : this(ResolveDefaultDataDir()) { }

    public BinaryMetricsHistoryStore(string dbDir)
    {
        Directory.CreateDirectory(dbDir);
        var secondCapacity = MetricsHistory.RetentionDays * 86_400L;
        var minuteCapacity = MetricsHistory.RetentionDays * 1440L;
        var tempBucketCapacity = MetricsHistory.TempRetentionDays * 86_400L / TempBucketTier.SecondsPerBucket;

        _superBlock = SuperBlock.CreateOrOpen(Path.Combine(dbDir, SuperBlockFileName), secondCapacity);
        _scalars = new ScalarRingStore(Path.Combine(dbDir, ScalarsFileName), secondCapacity, _superBlock.PruneFloorSec);
        _scalarRollup = new ScalarMinuteRollupRing(Path.Combine(dbDir, ScalarsMinuteFileName), minuteCapacity, _superBlock.PruneFloorSec);
        _fps = new FpsRingStore(Path.Combine(dbDir, FpsFileName), secondCapacity, _superBlock.PruneFloorSec);
        _fpsRollup = new FpsMinuteRollupRing(Path.Combine(dbDir, FpsMinuteFileName), minuteCapacity, _superBlock.PruneFloorSec);
        _gpus = new GpuRingStore(Path.Combine(dbDir, "gpu"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _fans = new FanRingStore(Path.Combine(dbDir, "fan"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _tempComponents = new TempComponentRingStore(Path.Combine(dbDir, "tempcomponent"), secondCapacity, minuteCapacity, EntityCapacity, _superBlock.PruneFloorSec);
        _tempBuckets = new TempBucketStore(
            Path.Combine(dbDir, "tempbucket"), tempBucketCapacity, TempBucketEntityCapacity, ComputeTempBucketFloorSec(_superBlock.PruneFloorSec));
        _apps = new AppUsageStore(Path.Combine(dbDir, "apps"), gpuId => _gpus.TryGetIndex(gpuId));
        _privacy = new PrivacyLog(Path.Combine(dbDir, "privacy.log"));
        _events = new MonitoringEventLog(Path.Combine(dbDir, "events.log"));
        _sourceFloorSec = _superBlock.SourceFloorSec;
    }

    // The temp-bucket tier keeps MetricsHistory.TempRetentionDays instead of
    // the RetentionDays scalarPruneFloorSec encodes, so shift it back by the
    // gap between the two retention windows. The RingFile.UnwrittenStamp
    // sentinel (meaning "never pruned") passes through unchanged rather than
    // underflowing.
    private static long ComputeTempBucketFloorSec(long scalarPruneFloorSec) =>
        scalarPruneFloorSec == RingFile.UnwrittenStamp
            ? RingFile.UnwrittenStamp
            : scalarPruneFloorSec - (MetricsHistory.TempRetentionDays - MetricsHistory.RetentionDays) * 86_400L;

    public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
    {
        if (samples.Count == 0 && pruneCutoffSec is null)
        {
            return;
        }

        lock (_resetLock)
        {
            // Drops samples a buffer upstream (MetricsSampleBuffer) is
            // replaying from before a ResetAll that already ran - without
            // this, a flush landing after a reset re-writes the seconds the
            // reset just cleared.
            if (samples.Count > 0 && _resetAtUtcSec != long.MinValue)
            {
                samples = samples.Where(s => s.TsSec >= _resetAtUtcSec).ToList();
            }

            if (samples.Count > 0)
            {
                _scalars.Append(samples);
                _fps.Append(samples);
                _gpus.Append(samples);
                _fans.Append(samples);
                _tempComponents.Append(samples);

                var touchedMinutes = new HashSet<long>();
                foreach (var s in samples)
                {
                    touchedMinutes.Add(MinuteTier.FloorToMinuteSec(s.TsSec));
                }
                foreach (var minute in touchedMinutes)
                {
                    RebuildScalarMinute(minute);
                    RebuildFpsMinute(minute);
                }
                _scalarRollup.Flush();
                _fpsRollup.Flush();

                RebuildTempBuckets(samples);
                _tempBuckets.Flush();
            }

            if (pruneCutoffSec is { } cutoff)
            {
                var changed = _scalars.RaisePruneFloor(cutoff);
                changed |= _fps.RaisePruneFloor(cutoff);
                changed |= _scalarRollup.RaisePruneFloor(cutoff);
                changed |= _fpsRollup.RaisePruneFloor(cutoff);
                changed |= _gpus.RaisePruneFloor(cutoff);
                changed |= _fans.RaisePruneFloor(cutoff);
                changed |= _tempComponents.RaisePruneFloor(cutoff);

                var newSourceFloor = Math.Max(_sourceFloorSec ?? long.MinValue, cutoff);
                var sourceFloorChanged = newSourceFloor != _sourceFloorSec;
                _sourceFloorSec = newSourceFloor;

                changed |= _tempBuckets.RaisePruneFloor(ComputeTempBucketFloorSec(cutoff));

                if (changed || sourceFloorChanged)
                {
                    _superBlock.Persist(cutoff, _sourceFloorSec);
                }
            }
        }
    }

    // Rebuilds every (key, bucket) pair this batch touched in the unified
    // cpu/gpu/storage/ram temperature bucket rollup. A bucket whose start
    // has aged past _sourceFloorSec is excluded from the touched sets
    // entirely (never rebuilt, never clobbered by a partial or empty
    // rebuild) via the WithinSourceRetention guard below.
    private void RebuildTempBuckets(IReadOnlyList<MetricSample> samples)
    {
        var width = TempBucketTier.SecondsPerBucket;
        bool WithinSourceRetention(long bucketStart) => _sourceFloorSec is not { } floor || bucketStart >= floor;

        var cpuName = "CPU";
        foreach (var s in samples)
        {
            if (!string.IsNullOrEmpty(s.CpuName))
            {
                cpuName = s.CpuName;
                break;
            }
        }

        var cpuBuckets = new HashSet<long>();
        var gpuPairs = new HashSet<(long Bucket, string GpuId)>();
        var gpuNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var componentPairs = new HashSet<(long Bucket, string ComponentId)>();
        var componentInfo = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);

        foreach (var s in samples)
        {
            var bucket = s.TsSec / width * width;
            if (!WithinSourceRetention(bucket))
            {
                continue;
            }

            cpuBuckets.Add(bucket);
            foreach (var g in s.Gpus)
            {
                gpuPairs.Add((bucket, g.GpuId));
                gpuNames[g.GpuId] = g.Name;
            }
            foreach (var c in s.ComponentTemps)
            {
                componentPairs.Add((bucket, c.ComponentId));
                componentInfo[c.ComponentId] = (c.Kind, c.Name);
            }
        }

        foreach (var bucket in cpuBuckets)
        {
            var rows = _scalars.Query(bucket, bucket + width - 1);
            var agg = FieldAgg.FromReadings(rows.Select(r => r.CpuTempC));
            _tempBuckets.RebuildBucket("cpu", "cpu", cpuName, bucket, agg);
        }

        foreach (var bucketGroup in gpuPairs.GroupBy(p => p.Bucket))
        {
            var bucket = bucketGroup.Key;
            var byTs = _gpus.Query(bucket, bucket + width - 1);
            var byGpu = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
            foreach (var readings in byTs.Values)
            {
                foreach (var g in readings)
                {
                    if (!byGpu.TryGetValue(g.GpuId, out var values))
                    {
                        values = new List<double?>();
                        byGpu[g.GpuId] = values;
                    }
                    values.Add(g.TempC);
                }
            }

            foreach (var (_, gpuId) in bucketGroup)
            {
                var agg = byGpu.TryGetValue(gpuId, out var values) ? FieldAgg.FromReadings(values) : FieldAgg.Empty;
                _tempBuckets.RebuildBucket("gpu:" + gpuId, "gpu", gpuNames[gpuId], bucket, agg);
            }
        }

        foreach (var bucketGroup in componentPairs.GroupBy(p => p.Bucket))
        {
            var bucket = bucketGroup.Key;
            var byTs = _tempComponents.Query(bucket, bucket + width - 1);
            var byComponent = new Dictionary<string, List<double?>>(StringComparer.Ordinal);
            foreach (var readings in byTs.Values)
            {
                foreach (var c in readings)
                {
                    if (!byComponent.TryGetValue(c.ComponentId, out var values))
                    {
                        values = new List<double?>();
                        byComponent[c.ComponentId] = values;
                    }
                    values.Add(c.ValueC);
                }
            }

            foreach (var (_, componentId) in bucketGroup)
            {
                var agg = byComponent.TryGetValue(componentId, out var values) ? FieldAgg.FromReadings(values) : FieldAgg.Empty;
                var (kind, name) = componentInfo[componentId];
                _tempBuckets.RebuildBucket(componentId, kind, name, bucket, agg);
            }
        }
    }

    // Rebuilds (never accumulates) a touched minute from ScalarRingStore's
    // raw per-second data, which by this point in Append already reflects
    // everything this batch just wrote - see ScalarMinuteRollupRing's class
    // doc for the replay-dedup and floor-filtering implications.
    private void RebuildScalarMinute(long minuteFloorSec)
    {
        var raw = _scalars.Query(minuteFloorSec, minuteFloorSec + 59);
        var agg = new ScalarMinuteAgg(
            FieldAgg.FromReadings(raw.Select(r => r.CpuPercent)),
            FieldAgg.FromReadings(raw.Select(r => r.MemoryPercent)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.NetInBytesPerSec)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.NetOutBytesPerSec)),
            FieldAgg.FromReadings(raw.Select(r => r.CpuTempC)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.DiskReadBytesPerSec)),
            FieldAgg.FromReadings(raw.Select(r => (double?)r.DiskWriteBytesPerSec)));
        _scalarRollup.RebuildMinute(minuteFloorSec, agg);
    }

    private void RebuildFpsMinute(long minuteFloorSec)
    {
        var raw = _fps.Query(minuteFloorSec, minuteFloorSec + 59);
        var agg = FieldAgg.FromReadings(raw.Select(r => (double?)r.Fps));
        _fpsRollup.RebuildMinute(minuteFloorSec, agg);
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        var scalars = _scalars.Query(fromSec, toSec);
        var fpsByTs = _fps.Query(fromSec, toSec).ToDictionary(r => r.TsSec, r => r.Fps);
        var gpusByTs = _gpus.Query(fromSec, toSec);
        var fansByTs = _fans.Query(fromSec, toSec);
        var componentsByTs = _tempComponents.Query(fromSec, toSec);

        var result = new List<MetricSample>(scalars.Count);
        foreach (var s in scalars)
        {
            result.Add(new MetricSample(
                s.TsSec, s.CpuPercent, s.MemoryPercent, s.NetInBytesPerSec, s.NetOutBytesPerSec, s.CpuTempC,
                gpusByTs.TryGetValue(s.TsSec, out var gpus) ? gpus : Array.Empty<GpuReading>(),
                fansByTs.TryGetValue(s.TsSec, out var fans) ? fans : Array.Empty<FanReading>())
            {
                ComponentTemps = componentsByTs.TryGetValue(s.TsSec, out var comps) ? comps : Array.Empty<ComponentTempReading>(),
                DiskReadBytesPerSec = s.DiskReadBytesPerSec,
                DiskWriteBytesPerSec = s.DiskWriteBytesPerSec,
                Fps = fpsByTs.TryGetValue(s.TsSec, out var fps) ? fps : null,
            });
        }
        return result;
    }

    // The real ladder never routes a sub-minute step here, but
    // ScalarDecimatedRawSpec calls this directly with arbitrary steps, so
    // the same threshold applies.
    private static bool IsRollupEligible(int stepSeconds) => stepSeconds >= 60 && stepSeconds % 60 == 0;

    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds)
    {
        var scalarSlots = IsRollupEligible(stepSeconds)
            ? _scalarRollup.QueryDecimated(fromSec, toSec, stepSeconds)
            : _scalars.QueryScalarsDecimatedRaw(fromSec, toSec, stepSeconds);
        var fpsSlots = IsRollupEligible(stepSeconds)
            ? _fpsRollup.QueryDecimated(fromSec, toSec, stepSeconds)
            : _fps.QueryDecimatedRaw(fromSec, toSec, stepSeconds);

        var fpsBySlot = fpsSlots.ToDictionary(f => f.Slot);
        var seenSlots = new HashSet<long>();
        var result = new List<ScalarDecimatedSlot>(scalarSlots.Count);
        foreach (var s in scalarSlots)
        {
            seenSlots.Add(s.Slot);
            var fps = fpsBySlot.TryGetValue(s.Slot, out var f) ? f : default;
            result.Add(s with { FpsAvg = fps.Avg, FpsMax = fps.Max });
        }
        foreach (var f in fpsSlots)
        {
            if (seenSlots.Contains(f.Slot))
            {
                continue;
            }
            result.Add(default(ScalarDecimatedSlot) with { Slot = f.Slot, FpsAvg = f.Avg, FpsMax = f.Max });
        }
        return result.OrderBy(s => s.Slot).ToList();
    }

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _gpus.QueryRollupDecimated(fromSec, toSec, stepSeconds)
            : _gpus.QueryRawDecimated(fromSec, toSec, stepSeconds);

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _fans.QueryRollupDecimated(fromSec, toSec, stepSeconds)
            : _fans.QueryRawDecimated(fromSec, toSec, stepSeconds);

    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds) =>
        IsRollupEligible(stepSeconds)
            ? _tempComponents.QueryRollupDecimated(fromSec, toSec, stepSeconds)
            : _tempComponents.QueryRawDecimated(fromSec, toSec, stepSeconds);

    // fromUtcMs/toUtcMs are milliseconds; TempBucketStore's own keys are
    // seconds, so both bounds are floor-divided rather than rounded - a
    // window boundary landing mid-second still includes that second's
    // bucket.
    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs) =>
        _tempBuckets.Query(fromUtcMs / 1000, toUtcMs / 1000);

    // Resets every ring/segment store this class owns; screen time and fps
    // session history are separate stores under separate directories and are
    // never touched here.
    public int ResetAll()
    {
        lock (_resetLock)
        {
            _resetAtUtcSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var count = 0;
            _scalars.Clear();
            count++;
            _fps.Clear();
            count++;
            _scalarRollup.Clear();
            count++;
            _fpsRollup.Clear();
            count++;
            count += _gpus.Clear();
            count += _fans.Clear();
            count += _tempComponents.Clear();
            count += _tempBuckets.Clear();
            count += _apps.ClearAll();
            return count;
        }
    }

    public int BlankFpsSeries()
    {
        lock (_resetLock)
        {
            _fps.Clear();
            _fpsRollup.Clear();
            return 2;
        }
    }

    // ----- IAppUsageHistoryStore -----

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        lock (_resetLock)
        {
            if (ticks.Count > 0 && _resetAtUtcSec != long.MinValue)
            {
                ticks = ticks.Where(t => t.TsSec >= _resetAtUtcSec).ToList();
            }
            _apps.Append(ticks, pruneCutoffSec);
        }
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps) =>
        _apps.QueryTopApps(metric, fromSec, toSec, maxApps);

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec) =>
        _apps.QueryAppSeries(metric, appName, fromSec, toSec);

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec) =>
        _apps.QuerySampledTicks(metric, fromSec, toSec);

    public AppUsageWindow QueryWindow(string metric, long fromSec, long toSec, int maxApps) =>
        _apps.QueryWindow(metric, fromSec, toSec, maxApps);

    public IReadOnlyDictionary<string, IReadOnlyList<AppRawPoint>> QueryAppSeriesBatch(
        string metric, IReadOnlyCollection<string> appNames, long fromSec, long toSec) =>
        _apps.QueryAppSeriesBatch(metric, appNames, fromSec, toSec);

    public long? QueryFirstSeen(string appName) => _apps.QueryFirstSeen(appName);

    // ----- IPrivacySessionStore -----

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec) =>
        _privacy.Upsert(capability, appId, startUtcSec, endUtcSec);

    IReadOnlyList<PrivacySession> IPrivacySessionStore.Query(long fromSec, long toSec) => _privacy.Query(fromSec, toSec);

    void IPrivacySessionStore.PruneOlderThan(long cutoffSec) => _privacy.PruneOlderThan(cutoffSec);

    // ----- IMonitoringEventStore -----

    public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom) =>
        _events.Append(tUtcMs, kind, label, detail, custom);

    IReadOnlyList<MonitoringEvent> IMonitoringEventStore.Query(long fromUtcMs, long toUtcMs, int limit) =>
        _events.Query(fromUtcMs, toUtcMs, limit);

    public bool DeleteCustom(long id) => _events.DeleteCustom(id);

    void IMonitoringEventStore.PruneOlderThan(long cutoffUtcMs) => _events.PruneOlderThan(cutoffUtcMs);

    public void Dispose()
    {
        _scalars.Dispose();
        _fps.Dispose();
        _scalarRollup.Dispose();
        _fpsRollup.Dispose();
        _gpus.Dispose();
        _fans.Dispose();
        _tempComponents.Dispose();
        _tempBuckets.Dispose();
        _apps.Dispose();
        _superBlock.Dispose();
    }

    // Under the shared config root's database directory
    // (NexusDataPaths.DatabaseDir()), in its own metrics/ subdirectory.
    private static string ResolveDefaultDataDir() =>
        Path.Combine(Nexus.Service.Persistence.NexusDataPaths.DatabaseDir(), "metrics");
}
