using System;
using System.Collections.Concurrent;

namespace Nexus.Service.Sensors;

/// <summary>
/// Bridges transient null or non-finite LHM readings by republishing the last
/// good value per sensor for a short window. Entries are immutable and swapped
/// whole, so concurrent mappers never see a torn value. Kept free of
/// LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class SensorHoldLast
{
    internal const long HoldWindowMs = 5_000;
    internal const long MissLogIntervalMs = 60_000;

    private sealed record Good(float Value, long AtMs);
    private sealed record MissLog(long LoggedAtMs, int Misses);

    private readonly ConcurrentDictionary<string, Good> _good = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MissLog> _misses = new(StringComparer.Ordinal);

    /// <summary>Returns the value to publish: the fresh reading, the held one inside the window, else 0.</summary>
    internal float Resolve(string id, float? raw, long nowMs, out bool missed)
    {
        if (raw is { } v && float.IsFinite(v))
        {
            _good[id] = new Good(v, nowMs);
            missed = false;
            return v;
        }

        missed = true;
        return _good.TryGetValue(id, out var g) && nowMs - g.AtMs <= HoldWindowMs ? g.Value : 0f;
    }

    /// <summary>Counts a miss; returns the misses accumulated since the last log when one is due, else 0.</summary>
    internal int NoteMiss(string id, long nowMs)
    {
        var due = 0;
        _misses.AddOrUpdate(
            id,
            _ =>
            {
                due = 1;
                return new MissLog(nowMs, 0);
            },
            (_, cur) =>
            {
                var count = cur.Misses + 1;
                if (nowMs - cur.LoggedAtMs < MissLogIntervalMs)
                {
                    due = 0;
                    return new MissLog(cur.LoggedAtMs, count);
                }

                due = count;
                return new MissLog(nowMs, 0);
            });
        return due;
    }
}
