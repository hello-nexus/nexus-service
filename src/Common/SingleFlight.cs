using System;
using System.Threading.Tasks;

namespace Nexus.Service.Common;

/// <summary>
/// Shares one running operation between callers. A run still going after
/// <c>maxAge</c> is abandoned, so a hung run delays a fresh attempt by at most that long.
/// A finished run is never reused.
/// </summary>
internal sealed class SingleFlight<T>
{
    private readonly Func<Task<T>> _start;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private Task<T>? _current;
    private DateTime _startedAt;

    public SingleFlight(Func<Task<T>> start, TimeSpan maxAge, Func<DateTime>? now = null)
    {
        _start = start;
        _maxAge = maxAge;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public Task<T> Get()
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false } && _now() - _startedAt <= _maxAge) return _current;
            _current = _start();
            _startedAt = _now();
            return _current;
        }
    }
}
