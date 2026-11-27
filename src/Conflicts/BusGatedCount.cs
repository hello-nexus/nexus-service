using System;
using System.Threading;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Caches a count taken by an expensive walk. The walk repeats only after a bus change
/// (<see cref="Invalidate"/> or a new signature), at most once per <see cref="MinIntervalMs"/>,
/// and a change inside the window is honored when it ends. <see cref="InvalidateNow"/> (a setting
/// toggle) skips the window. A null signature (no bus source wired) counts once. Invalidate never
/// takes a lock, so a PnP callback thread cannot block behind a walk.
/// </summary>
internal sealed class BusGatedCount
{
    internal const long MinIntervalMs = 60_000;

    private readonly Func<long> _clock;
    private readonly object _lock = new();
    private int _busGen;
    private int _forceGen;
    private string? _signature;
    private int _walkedBusGen;
    private int _walkedForceGen;
    private long _lastWalkMs;
    private int _count;
    private bool _has;

    public BusGatedCount(Func<long>? clock = null)
    {
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    public int Get(string? signature, Func<int> walk)
    {
        lock (_lock)
        {
            var bus = Volatile.Read(ref _busGen);
            var force = Volatile.Read(ref _forceGen);
            var now = _clock();
            if (_has && force == _walkedForceGen)
            {
                var busChanged = bus != _walkedBusGen || !string.Equals(signature, _signature, StringComparison.Ordinal);
                if (!busChanged || now - _lastWalkMs < MinIntervalMs)
                {
                    return _count;
                }
            }
            // Generations are snapshotted before the walk, so a change during it stays pending.
            _count = walk();
            _signature = signature;
            _walkedBusGen = bus;
            _walkedForceGen = force;
            _lastWalkMs = now;
            _has = true;
            return _count;
        }
    }

    /// <summary>A bus event: the next Get walks again once the minimum interval has passed.</summary>
    public void Invalidate() => Interlocked.Increment(ref _busGen);

    /// <summary>A setting change: the next Get walks immediately.</summary>
    public void InvalidateNow() => Interlocked.Increment(ref _forceGen);
}
