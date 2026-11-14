using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Nexus.Service.Sensors;

/// <summary>
/// Collects per-item update times during one LHM walk and, when the whole walk ran long,
/// produces one log line naming the slowest items. Rate limited so a persistently slow walk logs once a minute.
/// Walks are serialized by the caller, so no locking.
/// </summary>
internal sealed class LhmSlowWalkReport
{
    internal const long SlowWalkMs = 5_000;
    internal const long MinItemMs = 100;
    internal const long LogIntervalMs = 60_000;
    internal const int MaxItems = 3;

    private readonly Func<long> _nowMs;
    private readonly List<(string Item, long Ms)> _items = new();
    private long _lastLoggedMs = long.MinValue;

    internal LhmSlowWalkReport(Func<long> nowMs) => _nowMs = nowMs;

    internal void Begin() => _items.Clear();

    internal void Record(string item, long ms)
    {
        if (ms >= MinItemMs)
        {
            _items.Add((item, ms));
        }
    }

    /// <summary>The line to log, or null when the walk was fast or one was logged within the last interval.</summary>
    internal string? Finish(long totalMs)
    {
        var slowest = _items.OrderByDescending(i => i.Ms).Take(MaxItems).ToList();
        _items.Clear();
        var now = _nowMs();
        if (totalMs < SlowWalkMs || (_lastLoggedMs != long.MinValue && now - _lastLoggedMs < LogIntervalMs))
        {
            return null;
        }

        _lastLoggedMs = now;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[lhm] slow walk {totalMs / 1000.0:F1}s");
        if (slowest.Count == 0)
        {
            sb.Append("; no single item over the item threshold");
            return sb.ToString();
        }

        sb.Append("; slowest items: ");
        sb.Append(string.Join(", ", slowest.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Item} {i.Ms / 1000.0:F1}s"))));
        return sb.ToString();
    }
}
