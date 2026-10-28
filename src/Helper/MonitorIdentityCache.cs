using System;
using System.Collections.Generic;

namespace Nexus.Service.Helper;

/// <summary>
/// Monitor id per GDI adapter device name, so the ScreenTime poller runs the
/// display-driver identity lookup on a miss instead of every pass. An entry is
/// dropped when it outlives the TTL or when the attached-monitor count it was
/// stored under changes. Not thread-safe; the poller is the only caller and its
/// timer never overlaps passes.
/// </summary>
public sealed class MonitorIdentityCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly long _ttlMs;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, (string Id, long StoredMs, int MonitorCount)> _entries = new(StringComparer.Ordinal);
    private int _hits;
    private int _misses;

    public MonitorIdentityCache(TimeSpan ttl, Func<long>? nowMs = null)
    {
        _ttlMs = (long)ttl.TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public string GetOrResolve(string adapterDevice, int monitorCount, Func<string, string> resolve)
    {
        var now = _nowMs();
        if (_entries.TryGetValue(adapterDevice, out var e)
            && e.MonitorCount == monitorCount
            && now - e.StoredMs < _ttlMs)
        {
            _hits++;
            return e.Id;
        }

        _misses++;
        var id = resolve(adapterDevice);
        _entries[adapterDevice] = (id, now, monitorCount);
        return id;
    }

    /// <summary>Hit and miss counts since the last call.</summary>
    public (int Hits, int Misses) TakeCounts()
    {
        var counts = (_hits, _misses);
        _hits = 0;
        _misses = 0;
        return counts;
    }
}
