using System;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>
/// The idle-dim cap, 0..1, as a time-based ramp. Read on every frame by
/// <see cref="MasterBrightness"/>, so a read allocates nothing and takes no
/// lock; a retarget swaps one immutable segment, which is the only allocation.
/// 1 means no cap.
/// </summary>
public sealed class IdleDimRamp
{
    private sealed class Segment
    {
        public readonly float From;
        public readonly float To;
        public readonly long StartMs;
        public readonly long DurationMs;

        public Segment(float from, float to, long startMs, long durationMs)
        {
            From = from;
            To = to;
            StartMs = startMs;
            DurationMs = durationMs;
        }
    }

    private readonly Func<long> _nowMs;
    private volatile Segment _segment = new(1f, 1f, 0, 0);

    public IdleDimRamp(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    /// <summary>The cap right now.</summary>
    public float Cap() => Cap(_nowMs());

    /// <summary>The cap at <paramref name="nowMs"/>.</summary>
    public float Cap(long nowMs)
    {
        var s = _segment;
        if (s.DurationMs <= 0)
        {
            return s.To;
        }
        var t = (nowMs - s.StartMs) / (float)s.DurationMs;
        if (t >= 1f)
        {
            return s.To;
        }
        if (t <= 0f)
        {
            return s.From;
        }
        return s.From + (s.To - s.From) * t;
    }

    /// <summary>Ramp from wherever the cap is now to <paramref name="target"/>.</summary>
    public void RampTo(float target, TimeSpan duration)
    {
        var now = _nowMs();
        var current = Cap(now);
        _segment = new Segment(current, Math.Clamp(target, 0f, 1f), now, (long)Math.Max(0, duration.TotalMilliseconds));
    }
}
