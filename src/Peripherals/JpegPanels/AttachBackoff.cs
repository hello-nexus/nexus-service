using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Spaces out attach cycles for a panel that keeps failing, and decides which failures are
/// worth a log line, so a device that never recovers neither hammers the bus nor floods the log.
/// </summary>
public sealed class AttachBackoff
{
    public const long BaseDelayMs = 5_000;
    public const long MaxDelayMs = 300_000;

    /// <summary>A connection that stays up this long counts as healthy and clears the streak.</summary>
    public const long HealthyAfterMs = 30_000;

    /// <summary>Gap between repeat summaries once the first failure has been logged.</summary>
    public const long SummaryIntervalMs = 600_000;

    private readonly Func<long> _clock;
    private long _nextAttemptMs;
    private long _lastLogMs;
    private int _unloggedFailures;

    private readonly bool _enabled;

    /// <param name="enabled">False leaves every call a no-op, so a model that keeps its fixed retry cadence is untouched.</param>
    public AttachBackoff(Func<long> clock, bool enabled = true)
    {
        _clock = clock;
        _enabled = enabled;
    }

    public int Failures { get; private set; }

    /// <summary>Time to wait before the next attempt; zero when no failure is pending.</summary>
    public long DelayMs => Failures == 0 ? 0 : Math.Max(0, _nextAttemptMs - _clock());

    /// <summary>
    /// Records a failed cycle and grows the delay. Returns the number of failures to report
    /// when this one should be logged (the first, then one summary per interval), else 0.
    /// </summary>
    public int NoteFailure()
    {
        if (!_enabled)
        {
            return 0;
        }
        long now = _clock();
        Failures++;
        _unloggedFailures++;
        int shift = Math.Min(Failures - 1, 16);
        _nextAttemptMs = now + Math.Min(BaseDelayMs << shift, MaxDelayMs);
        if (Failures > 1 && now - _lastLogMs < SummaryIntervalMs)
        {
            return 0;
        }
        _lastLogMs = now;
        int report = _unloggedFailures;
        _unloggedFailures = 0;
        return report;
    }

    /// <summary>How long one slice of <see cref="WaitAsync"/> sleeps before it looks at the device again.</summary>
    public const int WaitSliceMs = 500;

    /// <summary>
    /// Waits out the pending delay in short slices. When <paramref name="deviceGone"/> turns true
    /// (unplugged or control switched off) the streak is cleared and the wait ends, so a return
    /// is not held back by a stale delay. Cancellation ends it at the next slice.
    /// </summary>
    public async Task WaitAsync(Func<bool> deviceGone, Func<int, CancellationToken, Task> delay, CancellationToken cancel)
    {
        while (DelayMs > 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (deviceGone())
            {
                Clear();
                return;
            }
            await delay((int)Math.Min(WaitSliceMs, DelayMs), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Clears the streak after a healthy connection; returns how many failures it ended.</summary>
    public int NoteHealthy()
    {
        int ended = Failures;
        Clear();
        return ended;
    }

    /// <summary>Forgets the streak without a verdict, for when the device left or was switched off and a return deserves a fresh start.</summary>
    public void Clear()
    {
        Failures = 0;
        _unloggedFailures = 0;
        _nextAttemptMs = 0;
    }
}
