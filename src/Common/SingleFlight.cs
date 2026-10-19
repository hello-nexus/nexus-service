using System;
using System.Threading.Tasks;

namespace Nexus.Service.Common;

/// <summary>One shared run: its task and a sequence number that grows with every started run. Id is below zero when no run was started.</summary>
internal readonly record struct FlightRun<T>(Task<T> Task, long Id);

/// <summary>
/// Shares one running operation between callers. A run still going after
/// <c>maxAge</c> is abandoned, so a hung run delays a fresh attempt by at most that long.
/// A finished run is never reused. While <c>maxAbandoned</c> abandoned runs are still
/// unfinished, no new run starts and callers get the fallback value.
/// </summary>
internal sealed class SingleFlight<T>
{
    private readonly Func<Task<T>> _start;
    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _now;
    private readonly int _maxAbandoned;
    private readonly Func<T> _fallback;
    private readonly Action? _onStarved;
    private readonly object _gate = new();
    private Task<T>? _current;
    private DateTime _startedAt;
    private long _lastId;
    private int _abandoned;
    private bool _starvedLogged;

    public SingleFlight(
        Func<Task<T>> start,
        TimeSpan maxAge,
        Func<DateTime>? now = null,
        int maxAbandoned = int.MaxValue,
        Func<T>? fallback = null,
        Action? onStarved = null)
    {
        _start = start;
        _maxAge = maxAge;
        _now = now ?? (() => DateTime.UtcNow);
        _maxAbandoned = maxAbandoned;
        _fallback = fallback ?? (() => default!);
        _onStarved = onStarved;
    }

    public Task<T> Get() => GetRun().Task;

    public FlightRun<T> GetRun()
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false } running)
            {
                if (_now() - _startedAt <= _maxAge) return new FlightRun<T>(running, _lastId);
                _abandoned++;
                _ = running.ContinueWith(_ => { lock (_gate) { _abandoned--; } }, TaskScheduler.Default);
                _current = null;
            }

            if (_abandoned >= _maxAbandoned)
            {
                if (!_starvedLogged)
                {
                    _starvedLogged = true;
                    _onStarved?.Invoke();
                }
                return new FlightRun<T>(Task.FromResult(_fallback()), -1);
            }

            _current = _start();
            _startedAt = _now();
            return new FlightRun<T>(_current, ++_lastId);
        }
    }
}

/// <summary>A value that only accepts writes from the newest run seen so far, so a run that finishes late cannot replace newer data.</summary>
internal sealed class LatestRunValue<T>
{
    private long _lastRun = long.MinValue;

    /// <summary>True when the write was accepted. Writes from the same run are accepted so an empty placeholder can be replaced by that run's result.</summary>
    public bool TryWrite(long runId, Action<T> write, T value)
    {
        if (runId < _lastRun) return false;
        _lastRun = runId;
        write(value);
        return true;
    }
}
