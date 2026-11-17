using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Monitoring.Events;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Non-persistent fallback used when the binary metrics history store can't
/// be opened, and in unit tests. Self-bounds to a rolling RingWindowSeconds
/// window on every Append rather than relying on the hourly pruneCutoffSec
/// cadence BinaryMetricsHistoryStore needs to avoid a write burst every tick -
/// an in-memory ring always keeps its window regardless of that cadence. Also
/// backs privacy sessions, per-app usage history, and monitoring timeline
/// events for the same fallback reason.
/// </summary>
public sealed class InMemoryMetricsHistoryStore : IMetricsHistoryStore, IPrivacySessionStore, IAppUsageHistoryStore, IMonitoringEventStore
{
    private const long RingWindowSeconds = 3 * 60 * 60;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, MetricSample> _rows = new();
    private readonly Dictionary<(string AppId, string Capability, long StartUtcSec), PrivacySession> _privacySessions = new();
    private readonly SortedDictionary<long, AppUsageTick> _appTicks = new();
    private readonly List<MonitoringEvent> _events = new();
    private long _nextEventId = 1;

    public void Append(IReadOnlyList<MetricSample> samples, long? pruneCutoffSec)
    {
        lock (_lock)
        {
            if (_sensorSeriesDeletedAt.Count > 0 && samples.Count > 0)
            {
                ExpireDeletedSensorSeries(samples.Max(x => x.TsSec));
            }
            foreach (var s in samples)
            {
                _rows[s.TsSec] = DropDeletedSensorReadings(s);
            }

            if (_rows.Count == 0)
            {
                return;
            }

            var newestTs = _rows.Keys.Last();
            var ringFloor = newestTs - RingWindowSeconds;
            var floor = pruneCutoffSec is { } cutoff ? System.Math.Max(cutoff, ringFloor) : ringFloor;

            var stale = _rows.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _rows.Remove(ts);
            }
        }
    }

    public IReadOnlyList<MetricSample> Query(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
        }
    }

    // The ring holds at most RingWindowSeconds of raw MetricSample rows, so
    // decimating in C# here (reusing the same MetricsDecimation
    // ScalarRingStore's raw-path callers use) costs nothing worth avoiding -
    // there is no separate aggregation path to fall back to on this store.
    public IReadOnlyList<ScalarDecimatedSlot> QueryScalarsDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var cpu = Slots(rows, s => s.CpuPercent, fromSec, toSec, stepSeconds);
            var mem = Slots(rows, s => s.MemoryPercent, fromSec, toSec, stepSeconds);
            var netIn = Slots(rows, s => s.NetInBytesPerSec, fromSec, toSec, stepSeconds);
            var netOut = Slots(rows, s => s.NetOutBytesPerSec, fromSec, toSec, stepSeconds);
            var temp = Slots(rows, s => s.CpuTempC, fromSec, toSec, stepSeconds);
            var diskRead = Slots(rows, s => s.DiskReadBytesPerSec, fromSec, toSec, stepSeconds);
            var diskWrite = Slots(rows, s => s.DiskWriteBytesPerSec, fromSec, toSec, stepSeconds);
            var fps = Slots(rows, s => (double?)s.Fps, fromSec, toSec, stepSeconds);

            var allSlots = new SortedSet<long>(cpu.Keys.Concat(mem.Keys).Concat(netIn.Keys).Concat(netOut.Keys)
                .Concat(temp.Keys).Concat(diskRead.Keys).Concat(diskWrite.Keys).Concat(fps.Keys));
            var result = new List<ScalarDecimatedSlot>(allSlots.Count);
            foreach (var slot in allSlots)
            {
                result.Add(new ScalarDecimatedSlot(
                    slot,
                    cpu.GetValueOrDefault(slot)?.Avg, cpu.GetValueOrDefault(slot)?.Max,
                    mem.GetValueOrDefault(slot)?.Avg, mem.GetValueOrDefault(slot)?.Max,
                    netIn.GetValueOrDefault(slot)?.Avg, netIn.GetValueOrDefault(slot)?.Max,
                    netOut.GetValueOrDefault(slot)?.Avg, netOut.GetValueOrDefault(slot)?.Max,
                    temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max,
                    diskRead.GetValueOrDefault(slot)?.Avg, diskRead.GetValueOrDefault(slot)?.Max,
                    diskWrite.GetValueOrDefault(slot)?.Avg, diskWrite.GetValueOrDefault(slot)?.Max,
                    fps.GetValueOrDefault(slot)?.Avg, fps.GetValueOrDefault(slot)?.Max));
            }
            return result;
        }
    }

    public IReadOnlyList<GpuDecimatedSlot> QueryGpuDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in rows)
            {
                foreach (var g in s.Gpus)
                {
                    names[g.GpuId] = g.Name;
                }
            }

            var result = new List<GpuDecimatedSlot>();
            foreach (var (gpuId, name) in names)
            {
                var load = Slots(rows, s => s.Gpus.FirstOrDefault(g => g.GpuId == gpuId)?.LoadPercent, fromSec, toSec, stepSeconds);
                var temp = Slots(rows, s => s.Gpus.FirstOrDefault(g => g.GpuId == gpuId)?.TempC, fromSec, toSec, stepSeconds);
                foreach (var slot in new SortedSet<long>(load.Keys.Concat(temp.Keys)))
                {
                    result.Add(new GpuDecimatedSlot(
                        gpuId, name, slot,
                        load.GetValueOrDefault(slot)?.Avg, load.GetValueOrDefault(slot)?.Max,
                        temp.GetValueOrDefault(slot)?.Avg, temp.GetValueOrDefault(slot)?.Max));
                }
            }
            return result;
        }
    }

    public IReadOnlyList<FanDecimatedSlot> QueryFanDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in rows)
            {
                foreach (var f in s.Fans)
                {
                    names[f.FanId] = f.Name;
                }
            }

            var result = new List<FanDecimatedSlot>();
            foreach (var (fanId, name) in names)
            {
                var rpm = Slots(rows, s => (double?)s.Fans.FirstOrDefault(f => f.FanId == fanId)?.Rpm, fromSec, toSec, stepSeconds);
                var duty = Slots(rows, s => (double?)s.Fans.FirstOrDefault(f => f.FanId == fanId)?.Duty, fromSec, toSec, stepSeconds);
                foreach (var slot in new SortedSet<long>(rpm.Keys.Concat(duty.Keys)))
                {
                    result.Add(new FanDecimatedSlot(
                        fanId, name, slot,
                        rpm.GetValueOrDefault(slot)?.Avg, rpm.GetValueOrDefault(slot)?.Max,
                        duty.GetValueOrDefault(slot)?.Avg, duty.GetValueOrDefault(slot)?.Max));
                }
            }
            return result;
        }
    }

    private readonly Dictionary<string, long> _sensorSeriesDeletedAt = new(StringComparer.Ordinal);

    private MetricSample DropDeletedSensorReadings(MetricSample s)
    {
        if (_sensorSeriesDeletedAt.Count == 0 || s.SensorReadings.Count == 0)
        {
            return s;
        }
        return s with { SensorReadings = s.SensorReadings.Where(r => !_sensorSeriesDeletedAt.TryGetValue(r.Id, out var at) || s.TsSec > at).ToList() };
    }

    private void ExpireDeletedSensorSeries(long newestSec)
    {
        var floor = newestSec - MetricsSampleBuffer.FailureRetentionCapSeconds;
        foreach (var id in _sensorSeriesDeletedAt.Where(kv => kv.Value < floor).Select(kv => kv.Key).ToList())
        {
            _sensorSeriesDeletedAt.Remove(id);
        }
    }

    public void PruneSensorSeries(IReadOnlySet<string> liveIds)
    {
        lock (_lock)
        {
            foreach (var ts in _rows.Keys.ToList())
            {
                var row = _rows[ts];
                if (row.SensorReadings.Any(r => !liveIds.Contains(r.Id)))
                {
                    _rows[ts] = row with { SensorReadings = row.SensorReadings.Where(r => liveIds.Contains(r.Id)).ToList() };
                }
            }
        }
    }

    public IReadOnlyList<SensorSeriesDecimatedSlot> QuerySensorSeriesDecimated(long fromSec, long toSec, int stepSeconds, IReadOnlySet<string>? seriesIds = null)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var ids = rows.SelectMany(s => s.SensorReadings).Select(r => r.Id).Where(id => seriesIds is null || seriesIds.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
            var result = new List<SensorSeriesDecimatedSlot>();
            foreach (var id in ids)
            {
                var value = Slots(rows, s => s.SensorReadings.FirstOrDefault(r => r.Id == id)?.Value, fromSec, toSec, stepSeconds);
                foreach (var slot in value.Keys.OrderBy(k => k))
                {
                    result.Add(new SensorSeriesDecimatedSlot(id, slot, value[slot].Avg, value[slot].Max));
                }
            }
            return result;
        }
    }

    public void DeleteSensorSeries(string seriesId, long nowSec)
    {
        lock (_lock)
        {
            ExpireDeletedSensorSeries(nowSec);
            _sensorSeriesDeletedAt[seriesId] = nowSec;
            foreach (var ts in _rows.Keys.ToList())
            {
                var row = _rows[ts];
                if (row.SensorReadings.Any(r => r.Id == seriesId))
                {
                    _rows[ts] = row with { SensorReadings = row.SensorReadings.Where(r => r.Id != seriesId).ToList() };
                }
            }
        }
    }

    public IReadOnlyList<ComponentTempDecimatedSlot> QueryComponentTempDecimated(long fromSec, long toSec, int stepSeconds)
    {
        lock (_lock)
        {
            var rows = _rows.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
            var meta = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);
            foreach (var s in rows)
            {
                foreach (var c in s.ComponentTemps)
                {
                    meta[c.ComponentId] = (c.Kind, c.Name);
                }
            }

            var result = new List<ComponentTempDecimatedSlot>();
            foreach (var (componentId, info) in meta)
            {
                var value = Slots(rows, s => s.ComponentTemps.FirstOrDefault(c => c.ComponentId == componentId)?.ValueC, fromSec, toSec, stepSeconds);
                foreach (var slot in value.Keys.OrderBy(k => k))
                {
                    var p = value[slot];
                    result.Add(new ComponentTempDecimatedSlot(componentId, info.Kind, info.Name, slot, p.Avg, p.Max));
                }
            }
            return result;
        }
    }

    private const long TempBucketSeconds = MetricsHistory.TempBucketMinutes * 60L;

    // The ring holds at most RingWindowSeconds of raw MetricSample rows, so
    // aggregating cpu/gpu/storage/ram temp readings into buckets here
    // (rather than maintaining a separate rollup table) costs nothing worth
    // avoiding, mirroring QueryScalarsDecimated's fallback approach. Bucketed
    // at TempBucketSeconds width to match TempBucketStore's rollup, so
    // DetectEpisodes' bucket-adjacency check behaves the same regardless of
    // which store backs it.
    public IReadOnlyList<TemperatureBucketRow> QueryTemperatureBuckets(long fromUtcMs, long toUtcMs)
    {
        var fromSec = fromUtcMs / 1000;
        var toSec = toUtcMs / 1000;

        lock (_lock)
        {
            var buckets = new Dictionary<(string ComponentId, long Bucket), (string Kind, string Name, double Sum, double Max, int Count)>();

            void Accumulate(string componentId, string kind, string name, long bucket, double value)
            {
                if (buckets.TryGetValue((componentId, bucket), out var acc))
                {
                    buckets[(componentId, bucket)] = (kind, name, acc.Sum + value, System.Math.Max(acc.Max, value), acc.Count + 1);
                }
                else
                {
                    buckets[(componentId, bucket)] = (kind, name, value, value, 1);
                }
            }

            foreach (var s in _rows.Values)
            {
                if (s.TsSec < fromSec || s.TsSec > toSec)
                {
                    continue;
                }
                var bucket = s.TsSec / TempBucketSeconds * TempBucketSeconds;
                if (s.CpuTempC is { } cpuC)
                {
                    Accumulate("cpu", "cpu", string.IsNullOrEmpty(s.CpuName) ? "CPU" : s.CpuName, bucket, cpuC);
                }
                foreach (var g in s.Gpus)
                {
                    if (g.TempC is { } gpuC)
                    {
                        Accumulate($"gpu:{g.GpuId}", "gpu", g.Name, bucket, gpuC);
                    }
                }
                foreach (var c in s.ComponentTemps)
                {
                    if (c.ValueC is { } compC)
                    {
                        Accumulate(c.ComponentId, c.Kind, c.Name, bucket, compC);
                    }
                }
            }

            return buckets
                .Select(kv => new TemperatureBucketRow(
                    kv.Key.ComponentId, kv.Value.Kind, kv.Value.Name, kv.Key.Bucket * 1000,
                    kv.Value.Sum / kv.Value.Count, kv.Value.Max, kv.Value.Count))
                .OrderBy(r => r.BucketUtcMs)
                .ToList();
        }
    }

    private static Dictionary<long, MetricPoint> Slots(
        IReadOnlyList<MetricSample> rows, Func<MetricSample, double?> selector, long fromSec, long toSec, int stepSeconds) =>
        MetricsDecimation.Decimate(rows.Select(s => new MetricSamplePoint(s.TsSec, selector(s))), fromSec, toSec, stepSeconds)
            .ToDictionary(p => p.T);

    public int ResetAll()
    {
        lock (_lock)
        {
            var count = _rows.Count + _appTicks.Count;
            _rows.Clear();
            _appTicks.Clear();
            return count;
        }
    }

    public int BlankFpsSeries()
    {
        lock (_lock)
        {
            var count = 0;
            var keys = _rows.Keys.ToList();
            foreach (var ts in keys)
            {
                if (_rows[ts].Fps is null)
                {
                    continue;
                }
                _rows[ts] = _rows[ts] with { Fps = null };
                count++;
            }
            return count;
        }
    }

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec)
    {
        lock (_lock)
        {
            _privacySessions[(appId, capability, startUtcSec)] = new PrivacySession(appId, capability, startUtcSec, endUtcSec);
        }
    }

    IReadOnlyList<PrivacySession> IPrivacySessionStore.Query(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _privacySessions.Values
                .Where(s => s.StartUtcSec <= toSec && (s.EndUtcSec is null || s.EndUtcSec >= fromSec))
                .OrderBy(s => s.StartUtcSec)
                .ToList();
        }
    }

    void IPrivacySessionStore.PruneOlderThan(long cutoffSec)
    {
        lock (_lock)
        {
            // An open session (EndUtcSec null) past retention by its start is
            // pruned too - see PrivacyLog's PruneOlderThan.
            var stale = _privacySessions.Where(kv =>
                    kv.Value.EndUtcSec is { } end ? end < cutoffSec : kv.Value.StartUtcSec < cutoffSec)
                .Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                _privacySessions.Remove(key);
            }
        }
    }

    public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec)
    {
        lock (_lock)
        {
            foreach (var t in ticks)
            {
                _appTicks[t.TsSec] = t;
            }

            if (_appTicks.Count == 0)
            {
                return;
            }

            var newestTs = _appTicks.Keys.Last();
            var ringFloor = newestTs - RingWindowSeconds;
            var floor = pruneCutoffSec is { } cutoff ? System.Math.Max(cutoff, ringFloor) : ringFloor;

            var stale = _appTicks.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _appTicks.Remove(ts);
            }
        }
    }

    // "cpu"/"memory" match the single sample with that exact metric id. Bare
    // "gpu"/"vram" (no adapter id) instead matches every "gpu:<id>"/"vram:<id>"
    // sample in the tick, so a process using two adapters is summed across
    // them - the in-memory mirror of AppUsageStore.QueryTopApps' unfiltered
    // gpu/vram resolution.
    private static IEnumerable<AppMetricSample> ResolveMetricSamples(AppUsageTick tick, string metric)
    {
        if (metric == "gpu")
        {
            return tick.Metrics.Where(m => m.Metric.StartsWith("gpu:", StringComparison.Ordinal));
        }
        if (metric == "vram")
        {
            return tick.Metrics.Where(m => m.Metric.StartsWith("vram:", StringComparison.Ordinal));
        }
        var single = tick.Metrics.FirstOrDefault(m => m.Metric == metric);
        return single is null ? Enumerable.Empty<AppMetricSample>() : new[] { single };
    }

    public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps)
    {
        lock (_lock)
        {
            // Divides by the ticks the metric was actually sampled in the
            // window (expectedTicks), not by an app's own row count, so a
            // multi-adapter GPU tick's combined value doesn't inflate the
            // tick count - mirrors AppUsageStore.QueryTopApps' expectedTicks.
            var sums = new Dictionary<string, (double Sum, double Max)>(StringComparer.OrdinalIgnoreCase);
            var expectedTicks = 0;
            foreach (var tick in _appTicks.Values)
            {
                if (tick.TsSec < fromSec || tick.TsSec > toSec)
                {
                    continue;
                }

                // Combine every matching sample's apps for this tick first
                // (a bare "gpu" query spans every adapter; a specific metric
                // has exactly one sample) so a multi-adapter process sums
                // per tick before it contributes to the window sum/max.
                var perTick = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in ResolveMetricSamples(tick, metric))
                {
                    foreach (var a in m.Apps)
                    {
                        perTick[a.Name] = (perTick.TryGetValue(a.Name, out var v) ? v : 0) + a.Value;
                    }
                }
                if (perTick.Count == 0)
                {
                    continue;
                }
                expectedTicks++;
                foreach (var (name, value) in perTick)
                {
                    var acc = sums.TryGetValue(name, out var v) ? v : (0, double.MinValue);
                    sums[name] = (acc.Sum + value, System.Math.Max(acc.Max, value));
                }
            }
            if (expectedTicks == 0)
            {
                return Array.Empty<AppWindowStat>();
            }

            return sums
                .Select(kv => new AppWindowStat(kv.Key, kv.Value.Sum / expectedTicks, kv.Value.Max))
                .OrderByDescending(s => s.Avg)
                .Take(maxApps)
                .ToList();
        }
    }

    public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _appTicks.Values
                .Where(t => t.TsSec >= fromSec && t.TsSec <= toSec
                    && ResolveMetricSamples(t, metric).Any(m => m.Apps.Count > 0))
                .Select(t => t.TsSec)
                .OrderBy(ts => ts)
                .ToList();
        }
    }

    public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec)
    {
        lock (_lock)
        {
            var result = new List<AppRawPoint>();
            foreach (var tick in _appTicks.Values)
            {
                if (tick.TsSec < fromSec || tick.TsSec > toSec)
                {
                    continue;
                }

                double? value = null;
                double? vram = null;
                var found = false;
                foreach (var m in ResolveMetricSamples(tick, metric))
                {
                    foreach (var a in m.Apps)
                    {
                        if (!string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        found = true;
                        value = (value ?? 0) + a.Value;
                        vram = MetricsHistory.SumNullable(vram, a.VramMb);
                    }
                }
                if (found)
                {
                    result.Add(new AppRawPoint(tick.TsSec, value, vram));
                }
            }
            return result;
        }
    }

    public long? QueryFirstSeen(string appName)
    {
        lock (_lock)
        {
            // _appTicks is a SortedDictionary keyed by TsSec, so the first
            // match in iteration order is already the earliest.
            foreach (var tick in _appTicks.Values)
            {
                foreach (var m in tick.Metrics)
                {
                    if (m.Apps.Any(a => string.Equals(a.Name, appName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return tick.TsSec;
                    }
                }
            }
            return null;
        }
    }

    public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom)
    {
        lock (_lock)
        {
            var ev = new MonitoringEvent(_nextEventId, tUtcMs, kind, label, detail, custom);
            _nextEventId++;
            _events.Add(ev);
            return ev;
        }
    }

    IReadOnlyList<MonitoringEvent> IMonitoringEventStore.Query(long fromUtcMs, long toUtcMs, int limit)
    {
        lock (_lock)
        {
            var matches = _events
                .Where(e => e.TUtcMs >= fromUtcMs && e.TUtcMs <= toUtcMs)
                .OrderBy(e => e.TUtcMs)
                .ToList();
            return matches.Count <= limit ? matches : matches.Skip(matches.Count - limit).ToList();
        }
    }

    public bool DeleteCustom(long id)
    {
        lock (_lock)
        {
            var index = _events.FindIndex(e => e.Id == id);
            if (index < 0 || !_events[index].Custom)
            {
                return false;
            }
            _events.RemoveAt(index);
            return true;
        }
    }

    void IMonitoringEventStore.PruneOlderThan(long cutoffUtcMs)
    {
        lock (_lock)
        {
            _events.RemoveAll(e => e.TUtcMs < cutoffUtcMs);
        }
    }

    public void Dispose() { }
}
