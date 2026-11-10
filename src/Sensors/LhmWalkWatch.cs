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

    /// <summary>Runs <paramref name="action"/> under <paramref name="gate"/> only if no walk releases it within <paramref name="wait"/>; false means it was skipped.</summary>
    internal static bool TryRunWhenIdle(object gate, TimeSpan wait, Action action)
    {
        if (!Monitor.TryEnter(gate, wait))
        {
            return false;
        }
        try
        {
            action();
            return true;
        }
        finally
        {
            Monitor.Exit(gate);
        }
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

    private sealed record ItemAt(string Item, long StartMs);

    private ItemAt? _item;

    /// <summary>Names the hardware item the walk is inside; says which of our threads is in SMBus or driver code.</summary>
    internal void SetItem(string? item) => Volatile.Write(ref _item, item is null ? null : new ItemAt(item, Math.Max(1, _nowMs())));

    /// <summary>The item the running walk is inside and how long it has been there, or null.</summary>
    internal (string Item, TimeSpan Age)? CurrentItem => Volatile.Read(ref _item) is { } at ? (at.Item, TimeSpan.FromMilliseconds(_nowMs() - at.StartMs)) : null;

    /// <summary>True when the walk is stale, or has sat in one memory item or the SPD gate rebuild for <paramref name="limit"/>: a wedge, not a normal read.</summary>
    internal bool WedgedInSmbusItem(TimeSpan limit)
    {
        if (CurrentWalkAge is null)
        {
            return false;
        }
        if (IsStale)
        {
            return true;
        }
        var at = Volatile.Read(ref _item);
        return at is not null && (at.Item.StartsWith("/memory", StringComparison.Ordinal) || at.Item == "spd-gate")
            && TimeSpan.FromMilliseconds(_nowMs() - at.StartMs) >= limit;
    }

    /// <summary>True while our own walk could be holding the SMBus mutex: a walk is running and it is in a memory item, the SPD gate rebuild, or between items.</summary>
    internal bool MaySelfHoldSmbus()
    {
        if (CurrentWalkAge is null)
        {
            return false;
        }
        var at = Volatile.Read(ref _item);
        return at is null || at.Item.StartsWith("/memory", StringComparison.Ordinal) || at.Item == "spd-gate";
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
            Volatile.Write(ref _item, null);
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
