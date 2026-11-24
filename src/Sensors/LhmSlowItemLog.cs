using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Sensors;

/// <summary>
/// Names a hardware item whose single Update ran long, at most once per item per interval, so a persistently slow drive cannot flood the log.
/// Called from the walk thread and the storage worker, hence the lock.
/// </summary>
internal sealed class LhmSlowItemLog
{
    internal const long SlowItemMs = 1_000;
    internal const long LogIntervalMs = 10 * 60_000;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _lastLoggedMs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal LhmSlowItemLog(Func<long> nowMs) => _nowMs = nowMs;

    /// <summary>The line to log, or null when the update was fast or this item already logged within the interval.</summary>
    internal string? Record(string item, string name, long ms)
    {
        if (ms < SlowItemMs)
        {
            return null;
        }

        var now = _nowMs();
        lock (_gate)
        {
            if (_lastLoggedMs.TryGetValue(item, out var last) && now - last < LogIntervalMs)
            {
                return null;
            }
            _lastLoggedMs[item] = now;
        }
        return string.Create(CultureInfo.InvariantCulture, $"[lhm] slow update: {name} ({item}) took {ms / 1000.0:F1}s");
    }
}
