using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Sensors;

/// <summary>
/// Names a hardware item whose single Update ran long, at most once per item per interval, so a persistently slow drive or power monitor cannot flood the log.
/// Called from the walk thread and the off-walk workers, hence the lock.
/// </summary>
internal sealed class LhmSlowItemLog
{
    internal const long SlowItemMs = 1_000;
    internal const long LogIntervalMs = 10 * 60_000;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _lastLoggedMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _suppressed = new(StringComparer.Ordinal);
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
        var skipped = 0;
        lock (_gate)
        {
            if (_lastLoggedMs.TryGetValue(item, out var last) && now - last < LogIntervalMs)
            {
                _suppressed[item] = _suppressed.GetValueOrDefault(item) + 1;
                return null;
            }
            _lastLoggedMs[item] = now;
            skipped = _suppressed.GetValueOrDefault(item);
            _suppressed.Remove(item);
        }
        var line = string.Create(CultureInfo.InvariantCulture, $"[lhm] slow update: {name} ({item}) took {ms / 1000.0:F1}s");
        return skipped > 0 ? string.Create(CultureInfo.InvariantCulture, $"{line} ({skipped} similar suppressed)") : line;
    }
}
