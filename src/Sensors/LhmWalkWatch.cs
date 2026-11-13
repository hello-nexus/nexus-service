using System;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors;

/// <summary>
/// Tracks the age of the LHM hardware walk. A walk that outlives <see cref="StallLimit"/> marks readings stale until a walk completes.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmWalkWatch
{
    /// <summary>Well above a forced walk that wakes a spun-down drive; a walk past this is wedged in a driver or bus wait.</summary>
    internal static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(45);

    /// <summary>Added to a deadline so it fires just past the limit it checks, never just short of it.</summary>
    internal static readonly TimeSpan DeadlineSlack = TimeSpan.FromMilliseconds(1);

    /// <summary>The process-wide instance; LhmComputer is a singleton, so the bridge and power listener read the same one.</summary>
    internal static LhmWalkWatch Shared { get; } = new();

    private readonly Func<long> _nowMs;
    private long _walkStartMs;
    private long _lastEndMs;
    private int _stale;
    private readonly object _transition = new();

    private readonly Func<Action, IOneShot> _oneShotFactory;
    private IOneShot? _stallTimer;
    // Set while no memory item or SPD gate rebuild is running; waiters block on it instead of re-reading the item.
    private readonly ManualResetEventSlim _outsideSmbus = new(true);

    internal LhmWalkWatch(Func<long>? nowMs = null, Func<Action, IOneShot>? oneShotFactory = null)
    {
        // Unbiased time: a suspend does not count toward a walk's age.
        _nowMs = nowMs ?? (() => Nexus.Service.Cooling.ThermalGuardController.AwakeMs());
        _oneShotFactory = oneShotFactory ?? (fire => new TimerOneShot(fire));
    }

    /// <summary>Raised once per stall, from the stall deadline's timer thread.</summary>
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
    internal void SetItem(string? item)
    {
        Volatile.Write(ref _item, item is null ? null : new ItemAt(item, Math.Max(1, _nowMs())));
        if (IsSmbusItem(item))
        {
            _outsideSmbus.Reset();
        }
        else
        {
            _outsideSmbus.Set();
        }
    }

    private static bool IsSmbusItem(string? item) => item is not null && (item.StartsWith("/memory", StringComparison.Ordinal) || item == "spd-gate");

    /// <summary>Blocks until the walk is outside every memory item and the SPD gate rebuild, or <paramref name="timeout"/> passes. True means outside.</summary>
    internal bool WaitUntilOutsideSmbusItem(TimeSpan timeout) => _outsideSmbus.Wait(timeout);

    /// <summary>The item the running walk is inside and how long it has been there, or null.</summary>
    internal (string Item, TimeSpan Age)? CurrentItem => Volatile.Read(ref _item) is { } at ? (at.Item, TimeSpan.FromMilliseconds(_nowMs() - at.StartMs)) : null;

    private long _spdQuiescedUntilMs;

    /// <summary>Pauses our own DIMM reads for <paramref name="duration"/>: a diagnostic that shows whether the mutex holder is our walk. Expires on its own.</summary>
    internal void QuiesceSpdFor(TimeSpan duration) => Interlocked.Exchange(ref _spdQuiescedUntilMs, _nowMs() + (long)duration.TotalMilliseconds);

    internal bool SpdQuiesced => _nowMs() < Interlocked.Read(ref _spdQuiescedUntilMs);

    internal void BeginWalk()
    {
        lock (_transition)
        {
            Interlocked.Exchange(ref _walkStartMs, Math.Max(1, _nowMs()));
            // One deadline per walk, created on the first walk; a walk that ends in time disarms it, so nothing wakes while readings are healthy.
            (_stallTimer ??= _oneShotFactory(OnStallDeadline)).Arm(StallLimit + DeadlineSlack);
        }
    }

    /// <summary>Returns true when this completion ends a stale period.</summary>
    internal bool EndWalk()
    {
        lock (_transition)
        {
            Volatile.Write(ref _item, null);
            _outsideSmbus.Set();
            _stallTimer?.Disarm();
            Interlocked.Exchange(ref _lastEndMs, Math.Max(1, _nowMs()));
            Interlocked.Exchange(ref _walkStartMs, 0);
            return Interlocked.Exchange(ref _stale, 0) != 0;
        }
    }

    /// <summary>The walk's stall deadline: flags the stall once, or re-arms for the remainder when it fired before the limit.</summary>
    internal void OnStallDeadline()
    {
        TimeSpan age;
        lock (_transition)
        {
            if (Interlocked.Read(ref _walkStartMs) == 0)
            {
                return;
            }
            if (!CheckStalledLocked(out age))
            {
                if (!IsStale)
                {
                    _stallTimer?.Arm(StallLimit - age + DeadlineSlack);
                }
                return;
            }
        }
        RaiseStalled(age);
    }

    private bool CheckStalledLocked(out TimeSpan age)
    {
        age = CurrentWalkAge ?? TimeSpan.Zero;
        return age > StallLimit && Interlocked.Exchange(ref _stale, 1) == 0;
    }

    /// <summary>Returns true exactly once per stall: the first check that finds the walk past the limit.</summary>
    internal bool CheckStalled(out TimeSpan age)
    {
        lock (_transition)
        {
            return CheckStalledLocked(out age);
        }
    }

    /// <summary>The reading to publish: null while stale. Consumers that cannot carry a null (HardwareSensor.Value) fall to zero once the hold window lapses.</summary>
    internal float? Read(float? raw) => IsStale ? null : raw;
}
