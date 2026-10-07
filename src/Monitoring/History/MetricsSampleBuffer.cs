using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Locked in-RAM tail of not-yet-flushed MetricSample rows. MetricsSampler
/// appends one sample per tick and flushes the whole tail to
/// IMetricsHistoryStore every MetricsHistory.FlushSeconds ticks;
/// GET /monitoring/history reads SnapshotRange to merge the unflushed tail
/// on top of DB rows, so a query never misses the last few seconds.
/// </summary>
public sealed class MetricsSampleBuffer
{
    // Bounds unflushed growth when the store is failing: a 1Hz sampler with
    // no successful flush would otherwise buffer forever.
    private const int FailureRetentionCapSeconds = 600;

    private readonly object _lock = new();
    private readonly SortedDictionary<long, MetricSample> _samples = new();
    private string? _latestCpuName;

    /// <summary>The CpuName of the most recent sample that carried one. Kept across <see cref="RemoveThrough"/>, so the name does not vanish right after a flush empties the tail.</summary>
    public string? LatestCpuName
    {
        get
        {
            lock (_lock) { return _latestCpuName; }
        }
    }

    /// <summary>Buffers a sample; a second Append at the same TsSec (a tick
    /// re-run) replaces rather than duplicates.</summary>
    public void Append(MetricSample sample)
    {
        lock (_lock)
        {
            _samples[sample.TsSec] = sample;
            if (!string.IsNullOrWhiteSpace(sample.CpuName))
            {
                _latestCpuName = sample.CpuName;
            }
        }
    }

    /// <summary>Every buffered sample, oldest first - the tail a caller is
    /// about to hand to the store.</summary>
    public IReadOnlyList<MetricSample> PendingSnapshot()
    {
        lock (_lock)
        {
            return _samples.Values.ToList();
        }
    }

    /// <summary>Buffered samples with ts in [fromSec, toSec], oldest first.</summary>
    public IReadOnlyList<MetricSample> SnapshotRange(long fromSec, long toSec)
    {
        lock (_lock)
        {
            return _samples.Values.Where(s => s.TsSec >= fromSec && s.TsSec <= toSec).ToList();
        }
    }

    /// <summary>Drops every buffered sample with ts &lt;= throughSec. Called
    /// after a successful store commit so the tail only ever holds what
    /// hasn't reached disk yet.</summary>
    public void RemoveThrough(long throughSec)
    {
        lock (_lock)
        {
            var stale = _samples.Keys.Where(ts => ts <= throughSec).ToList();
            foreach (var ts in stale)
            {
                _samples.Remove(ts);
            }
        }
    }

    /// <summary>Called instead of RemoveThrough when a flush fails: caps
    /// unflushed growth to FailureRetentionCapSeconds by dropping the
    /// oldest entries, so a store outage bounds RAM instead of growing it
    /// without limit.</summary>
    public void TrimToRetentionCap(long nowSec)
    {
        lock (_lock)
        {
            var floor = nowSec - FailureRetentionCapSeconds;
            var stale = _samples.Keys.Where(ts => ts < floor).ToList();
            foreach (var ts in stale)
            {
                _samples.Remove(ts);
            }
        }
    }
}
