using System;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Start-to-start gaps of the LCD frames on the cadenced wire, plus how many sends carried a new
/// frame and how many repeated the last one. Fixed buffers: recording is allocation free and a
/// summary sorts a preallocated copy, so the periodic log costs nothing on the steady path.
/// </summary>
internal sealed class KrakenCadenceStats
{
    private const int Capacity = 1024;

    private readonly object _gate = new();
    private readonly long[] _gaps = new long[Capacity];
    private readonly long[] _scratch = new long[Capacity];
    private int _count;
    private int _head;
    private long _previousStart;
    private int _fresh;
    private int _repeated;

    public readonly record struct Summary(double MinMs, double P50Ms, double P95Ms, double MaxMs, int Samples, int Fresh, int Repeated);

    /// <summary>Notes one frame's bulk start. The first start after <see cref="BreakChain"/> has no gap to record.</summary>
    public void RecordStart(long timestamp)
    {
        lock (_gate)
        {
            if (_previousStart != 0)
            {
                _gaps[_head] = timestamp - _previousStart;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity)
                {
                    _count++;
                }
            }
            _previousStart = timestamp;
        }
    }

    public void CountSend(bool fresh)
    {
        lock (_gate)
        {
            if (fresh)
            {
                _fresh++;
            }
            else
            {
                _repeated++;
            }
        }
    }

    /// <summary>A stream boundary: the gap across it is not a cadence measurement.</summary>
    public void BreakChain()
    {
        lock (_gate)
        {
            _previousStart = 0;
        }
    }

    /// <summary>Returns the figures since the last call and starts a new window; null when nothing was sent.</summary>
    public Summary? Take(long ticksPerSecond)
    {
        lock (_gate)
        {
            if (_fresh + _repeated == 0)
            {
                return null;
            }
            var n = _count;
            double toMs = 1000.0 / ticksPerSecond;
            double min = 0, p50 = 0, p95 = 0, max = 0;
            if (n > 0)
            {
                Array.Copy(_gaps, _scratch, n);
                Array.Sort(_scratch, 0, n);
                min = _scratch[0] * toMs;
                p50 = _scratch[Rank(n, 0.5)] * toMs;
                p95 = _scratch[Rank(n, 0.95)] * toMs;
                max = _scratch[n - 1] * toMs;
            }
            var summary = new Summary(min, p50, p95, max, n, _fresh, _repeated);
            _count = 0;
            _head = 0;
            _fresh = 0;
            _repeated = 0;
            return summary;
        }
    }

    private static int Rank(int n, double fraction) =>
        Math.Clamp((int)Math.Ceiling(fraction * n) - 1, 0, n - 1);
}
