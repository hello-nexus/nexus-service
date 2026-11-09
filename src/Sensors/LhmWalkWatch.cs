using System;
using System.Threading;

namespace Nexus.Service.Sensors;

/// <summary>
/// Tracks the age of the LHM hardware walk. A walk that outlives <see cref="StallLimit"/> marks readings stale until a walk completes.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmWalkWatch
{
    /// <summary>Well above a forced walk that wakes a spun-down drive; a walk past this is wedged in a driver or bus wait.</summary>
    internal static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(45);

    /// <summary>The process-wide instance; LhmComputer is a singleton, so the bridge and power listener read the same one.</summary>
    internal static LhmWalkWatch Shared { get; } = new();

    private readonly Func<long> _nowMs;
    private long _walkStartMs;
    private long _lastEndMs;
    private int _stale;
    private readonly object _transition = new();

    internal LhmWalkWatch(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Raised once per stall, on the watchdog timer thread.</summary>
    internal event Action<TimeSpan>? Stalled;

    internal void RaiseStalled(TimeSpan age) => Stalled?.Invoke(age);

    /// <summary>Longest a walk may run before cached readings stop counting as fresh for the thermal watchdog; a healthy walk is far shorter.</summary>
    internal static readonly TimeSpan FreshLimit = TimeSpan.FromSeconds(2);

    /// <summary>False once the running walk is past <see cref="FreshLimit"/> or has wedged.</summary>
    internal bool IsFresh => !IsStale && (CurrentWalkAge ?? TimeSpan.Zero) < FreshLimit;

    /// <summary>Takes <paramref name="gate"/>. A reader that must never wait gets false at once when a walk holds it.</summary>
    internal static bool Acquire(object gate, bool neverWait)
    {
        if (neverWait)
        {
            return Monitor.TryEnter(gate);
        }
        Monitor.Enter(gate);
        return true;
    }

    internal bool IsStale => Volatile.Read(ref _stale) != 0;

    /// <summary>Age of the walk in progress, or null when none is running.</summary>
    internal TimeSpan? CurrentWalkAge
    {
        get
        {
            var start = Interlocked.Read(ref _walkStartMs);
            return start == 0 ? null : TimeSpan.FromMilliseconds(_nowMs() - start);
        }
    }

    /// <summary>Time since the last completed walk, or null before the first.</summary>
    internal TimeSpan? SinceLastWalk
    {
        get
        {
            var end = Interlocked.Read(ref _lastEndMs);
            return end == 0 ? null : TimeSpan.FromMilliseconds(_nowMs() - end);
        }
    }

    internal void BeginWalk()
    {
        lock (_transition)
        {
            Interlocked.Exchange(ref _walkStartMs, Math.Max(1, _nowMs()));
        }
    }

    /// <summary>Returns true when this completion ends a stale period.</summary>
    internal bool EndWalk()
    {
        lock (_transition)
        {
            Interlocked.Exchange(ref _lastEndMs, Math.Max(1, _nowMs()));
            Interlocked.Exchange(ref _walkStartMs, 0);
            return Interlocked.Exchange(ref _stale, 0) != 0;
        }
    }

    /// <summary>Returns true exactly once per stall: the first check that finds the walk past the limit.</summary>
    internal bool CheckStalled(out TimeSpan age)
    {
        lock (_transition)
        {
            age = CurrentWalkAge ?? TimeSpan.Zero;
            return age > StallLimit && Interlocked.Exchange(ref _stale, 1) == 0;
        }
    }

    /// <summary>The reading to publish: null while stale. Consumers that cannot carry a null (HardwareSensor.Value) fall to zero once the hold window lapses.</summary>
    internal float? Read(float? raw) => IsStale ? null : raw;
}
