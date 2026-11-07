using System;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// How long the cooler took to answer a frame's start and end commands, and how many waits ran out.
/// Fixed buffers: recording is allocation free and a summary sorts a preallocated copy, so the
/// periodic log costs nothing on the steady path.
/// </summary>
internal sealed class KrakenReplyStats
{
    public KrakenReplySeries Start { get; } = new();

    public KrakenReplySeries End { get; } = new();
}

internal sealed class KrakenReplySeries
{
    private const int Capacity = 1024;

    private readonly object _gate = new();
    private readonly long[] _ticks = new long[Capacity];
    private readonly long[] _scratch = new long[Capacity];
    private int _count;
    private int _head;
    private int _misses;

    public readonly record struct Summary(double P50Ms, double P95Ms, double MaxMs, int Replies, int Misses);

    public void RecordReply(long ticks)
    {
        lock (_gate)
        {
            _ticks[_head] = ticks;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }
    }

    public void RecordMiss()
    {
        lock (_gate)
        {
            _misses++;
        }
    }

    /// <summary>Returns the figures since the last call and starts a new window; null when nothing was waited on.</summary>
    public Summary? Take(long ticksPerSecond)
    {
        lock (_gate)
        {
            var n = _count;
            if (n + _misses == 0)
            {
                return null;
            }
            double toMs = 1000.0 / ticksPerSecond;
            double p50 = 0, p95 = 0, max = 0;
            if (n > 0)
            {
                Array.Copy(_ticks, _scratch, n);
                Array.Sort(_scratch, 0, n);
                p50 = _scratch[Rank(n, 0.5)] * toMs;
                p95 = _scratch[Rank(n, 0.95)] * toMs;
                max = _scratch[n - 1] * toMs;
            }
            var summary = new Summary(p50, p95, max, n, _misses);
            _count = 0;
            _head = 0;
            _misses = 0;
            return summary;
        }
    }

    private static int Rank(int n, double fraction) =>
        Math.Clamp((int)Math.Ceiling(fraction * n) - 1, 0, n - 1);
}
