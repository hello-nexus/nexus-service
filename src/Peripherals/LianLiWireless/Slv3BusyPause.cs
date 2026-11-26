using System.Threading;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Experiment state machine for an RX busy spell: once the spell is old enough the RGB TX is held silent for a fixed time,
/// then released for a fixed time of TX, then silent again, and so on until the spell ends. The log then shows whether
/// silence ends spells. Driven by the busy polls under the hub lock; <see cref="IsPaused"/> is safe from any thread.
/// </summary>
internal sealed class Slv3BusyPause
{
    internal const long PauseAfterSpellMs = 3_000;
    internal const long PauseMs = 20_000;
    internal const long TxOnMs = 20_000;

    internal enum Change { None, Started, Ended }

    private enum Phase { Off, Paused, TxOn }

    private Phase _phase;
    private long _phaseStartMs;
    private long _lastPauseStartMs;
    private bool _everPaused;
    private int _pausedFlag;

    /// <summary>When the current phase began, in the hub clock.</summary>
    public long PhaseStartMs => Volatile.Read(ref _phaseStartMs);

    /// <summary>How long the phase that just ended ran, valid right after <see cref="Change.Ended"/> or <see cref="Change.Started"/>.</summary>
    public long LastPhaseMs { get; private set; }

    /// <summary>Held silent now. Expires on the clock even when no poll arrives to end it.</summary>
    public bool IsPaused(long nowMs) => Volatile.Read(ref _pausedFlag) == 1 && nowMs - Volatile.Read(ref _phaseStartMs) < PauseMs;

    /// <summary>One busy poll of a spell that is <paramref name="spellAgeMs"/> old.</summary>
    public Change Update(long nowMs, long spellAgeMs)
    {
        if (_phase == Phase.Off)
        {
            if (spellAgeMs < PauseAfterSpellMs)
            {
                return Change.None;
            }
            Enter(Phase.Paused, nowMs, 0);
            return Change.Started;
        }
        if (SettleExpiredPause(nowMs))
        {
            return Change.Ended;
        }
        if (_phase == Phase.TxOn && nowMs - _phaseStartMs >= TxOnMs)
        {
            Enter(Phase.Paused, nowMs, nowMs - _phaseStartMs);
            return Change.Started;
        }
        return Change.None;
    }

    // A pause that ran out on the clock is over from its full length on, whenever the next poll notices.
    private bool SettleExpiredPause(long nowMs)
    {
        if (_phase != Phase.Paused || nowMs - _phaseStartMs < PauseMs)
        {
            return false;
        }
        Enter(Phase.TxOn, _phaseStartMs + PauseMs, PauseMs);
        return true;
    }

    private void Enter(Phase phase, long nowMs, long previousPhaseMs)
    {
        LastPhaseMs = previousPhaseMs;
        _phase = phase;
        Volatile.Write(ref _phaseStartMs, nowMs);
        Volatile.Write(ref _pausedFlag, phase == Phase.Paused ? 1 : 0);
        if (phase == Phase.Paused)
        {
            _lastPauseStartMs = nowMs;
            _everPaused = true;
        }
    }

    /// <summary>The spell ended: where it ended and how long after the last pause began, then back to idle.</summary>
    public (bool EverPaused, string EndedIn, long SincePauseStartMs) EndSpell(long nowMs)
    {
        SettleExpiredPause(nowMs);
        var endedIn = _phase switch
        {
            Phase.Paused => "pause",
            Phase.TxOn => "TX",
            _ => "TX (never paused)",
        };
        var result = (_everPaused, endedIn, _everPaused ? nowMs - _lastPauseStartMs : 0L);
        _phase = Phase.Off;
        _everPaused = false;
        Volatile.Write(ref _pausedFlag, 0);
        return result;
    }
}
