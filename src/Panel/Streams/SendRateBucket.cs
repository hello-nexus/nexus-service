using System;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Token bucket for a writer that sends on arrival: it lets a short burst through, so a frame
/// that lands early after a capture gap is not held back, while the long-run rate never
/// exceeds the stream's. Not thread-safe; one writer thread owns it.
/// </summary>
public sealed class SendRateBucket
{
    private readonly double _ticksPerToken;
    private readonly double _capacity;
    private double _tokens;
    private long _last;

    public SendRateBucket(double framesPerSecond, double capacity, long ticksPerSecond)
    {
        _ticksPerToken = ticksPerSecond / Math.Max(0.001, framesPerSecond);
        _capacity = Math.Max(1, capacity);
        _tokens = _capacity;
    }

    /// <summary>Ticks to wait from <paramref name="now"/> until a send is allowed; zero when one is.</summary>
    public long WaitTicks(long now)
    {
        var tokens = Math.Min(_capacity, _tokens + (now - _last) / _ticksPerToken);
        return tokens >= 1 ? 0 : (long)Math.Ceiling((1 - tokens) * _ticksPerToken);
    }

    /// <summary>Records a send at <paramref name="now"/>.</summary>
    public void Consume(long now)
    {
        _tokens = Math.Min(_capacity, _tokens + (now - _last) / _ticksPerToken);
        _last = now;
        _tokens = Math.Max(0, _tokens - 1);
    }
}
