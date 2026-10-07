using System;

namespace Nexus.Service.Lighting;

/// <summary>
/// One lock input watch, several consumers (lock blackout, Stream Deck lock
/// sleep, Sentry). Each consumer reports only whether it wants the watch; the
/// watch runs while any of them does, so one consumer disarming never switches
/// off another's.
/// </summary>
internal sealed class LockInputWatchDemand
{
    private readonly object _gate = new();
    private readonly bool[] _wanted;
    private readonly Action<bool> _apply;

    public LockInputWatchDemand(int consumers, Action<bool> apply)
    {
        _wanted = new bool[consumers];
        _apply = apply;
    }

    /// <summary>True while any consumer wants the watch.</summary>
    public bool Armed
    {
        get
        {
            lock (_gate)
            {
                return Array.IndexOf(_wanted, true) >= 0;
            }
        }
    }

    /// <summary>The arm/disarm callback for consumer <paramref name="index"/>.</summary>
    public Action<bool> Consumer(int index) => enabled =>
    {
        lock (_gate)
        {
            _wanted[index] = enabled;
            _apply(Array.IndexOf(_wanted, true) >= 0);
        }
    };
}
