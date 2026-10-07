using System;

namespace Nexus.Service.Lighting;

/// <summary>Who can want the lock input watch. Add a member here and nothing else needs a count.</summary>
internal enum LockInputWatchConsumer
{
    Blackout,
    StreamDeck,
    Sentry,
}

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

    public LockInputWatchDemand(Action<bool> apply)
    {
        _wanted = new bool[Enum.GetValues<LockInputWatchConsumer>().Length];
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

    /// <summary>The arm/disarm callback for <paramref name="consumer"/>.</summary>
    public Action<bool> Consumer(LockInputWatchConsumer consumer) => enabled =>
    {
        lock (_gate)
        {
            _wanted[(int)consumer] = enabled;
            _apply(Array.IndexOf(_wanted, true) >= 0);
        }
    };
}
