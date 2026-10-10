using System;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>Lets an action run at most once per interval; the first call always runs.</summary>
public sealed class ProbeThrottle
{
    private readonly Func<long> _nowMs;
    private readonly long _intervalMs;
    private readonly object _gate = new();
    private bool _ran;
    private long _lastMs;

    public ProbeThrottle(TimeSpan interval, Func<long>? nowMs = null)
    {
        _intervalMs = (long)interval.TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>True, and stamps now, when the interval has elapsed since the last true (or none has happened).</summary>
    public bool TryBegin()
    {
        lock (_gate)
        {
            var now = _nowMs();
            if (_ran && now - _lastMs < _intervalMs)
            {
                return false;
            }
            _ran = true;
            _lastMs = now;
            return true;
        }
    }
}
