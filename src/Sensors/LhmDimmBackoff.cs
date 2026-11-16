using System;

namespace Nexus.Service.Sensors;

/// <summary>
/// Pauses DIMM SPD reads when they keep taking seconds, which means another process holds the global SMBus mutex
/// and every stick burns the mutex wait. Walks are serialized by the caller, so no locking.
/// Free of LibreHardwareMonitor types so it tests on every platform.
/// </summary>
internal sealed class LhmDimmBackoff
{
    internal const long SlowStickMs = 2_000;
    internal const int SlowWalksToPause = 3;
    internal const long PauseMs = 10 * 60_000;

    internal enum Outcome
    {
        None,
        Paused,
        Recovered,
    }

    private readonly Func<long> _nowMs;
    private long _pausedUntilMs;
    private bool _probing;
    private int _slowWalks;
    private long _walkSlowestMs;
    private bool _walkRead;
    private bool _probeStickSlow;

    internal LhmDimmBackoff(Func<long> nowMs) => _nowMs = nowMs;

    /// <summary>True while paused. Once the pause has run out this is false, so the next walk reads the sticks as a probe.</summary>
    internal bool ShouldSkip => (_pausedUntilMs != 0 && _nowMs() < _pausedUntilMs) || _probeStickSlow;

    /// <summary>Longest stick read in the walk that just ended; for the log line.</summary>
    internal long LastSlowestMs { get; private set; }

    internal void BeginWalk()
    {
        _walkSlowestMs = 0;
        _walkRead = false;
        _probeStickSlow = false;
    }

    internal void RecordStick(long ms)
    {
        _walkRead = true;
        _walkSlowestMs = Math.Max(_walkSlowestMs, ms);
        // A blocked probe costs one mutex wait, not one per stick.
        _probeStickSlow = _probing && ms >= SlowStickMs;
    }

    internal Outcome EndWalk()
    {
        if (!_walkRead)
        {
            return Outcome.None;
        }

        LastSlowestMs = _walkSlowestMs;
        _probeStickSlow = false;
        var slow = _walkSlowestMs >= SlowStickMs;
        if (_probing)
        {
            if (slow)
            {
                _pausedUntilMs = _nowMs() + PauseMs;
                return Outcome.Paused;
            }

            _probing = false;
            _pausedUntilMs = 0;
            _slowWalks = 0;
            return Outcome.Recovered;
        }

        _slowWalks = slow ? _slowWalks + 1 : 0;
        if (_slowWalks < SlowWalksToPause)
        {
            return Outcome.None;
        }

        _slowWalks = 0;
        _probing = true;
        _pausedUntilMs = _nowMs() + PauseMs;
        return Outcome.Paused;
    }
}
