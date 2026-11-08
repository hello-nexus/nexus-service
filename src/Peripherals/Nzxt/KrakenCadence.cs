using System;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Slot clock for the 2023 Kraken's LCD wire. The vendor app starts a frame every fixed interval and the
/// panel loses frames that arrive sooner or irregularly, so the sender asks this for each start time.
/// Deadlines are absolute, so a late wake does not push the next one; a real overrun re-anchors the grid
/// instead of bursting to catch up; and a start is never earlier than the minimum gap after the last.
/// Time is in clock ticks so tests drive it with a fake clock.
/// </summary>
internal sealed class KrakenCadence : IDisposable
{
    internal const long PeriodUs = 37_800;
    internal const long MinGapUs = 36_900;
    private const long ReanchorSlackUs = 1_000;
    // A sleep can wake late, so the coarse wait ends this far before the deadline and the spin takes the rest.
    private const long CoarseWaitLatenessMs = 1;

    private readonly Func<long> _now;
    private readonly Func<long, bool> _waitUntil;
    private readonly IDisposable? _owned;
    private readonly long _period;
    private readonly long _minGap;
    private readonly long _slack;
    private long _next;
    private long _lastStart;
    private bool _started;

    public KrakenCadence(Func<long> now, long ticksPerSecond, Func<long, bool> waitUntil, IDisposable? owned = null)
    {
        _now = now;
        _waitUntil = waitUntil;
        _owned = owned;
        TicksPerSecond = ticksPerSecond;
        _period = PeriodUs * ticksPerSecond / 1_000_000;
        _minGap = MinGapUs * ticksPerSecond / 1_000_000;
        _slack = ReanchorSlackUs * ticksPerSecond / 1_000_000;
    }

    public long TicksPerSecond { get; }

    public long Now() => _now();

    /// <summary>Blocks until the next slot and returns its actual start; false once stopped.</summary>
    public bool WaitNextSlot(out long start)
    {
        long due = _started ? Math.Max(_next, _lastStart + _minGap) : _now();
        if (!_waitUntil(due))
        {
            start = 0;
            return false;
        }
        start = _now();
        long anchor = _started && start - due <= _slack ? due : start;
        _next = anchor + _period;
        _lastStart = start;
        _started = true;
        return true;
    }

    /// <summary>The production clock: a high-resolution timer plus a spin on Windows, a short sleep plus a spin elsewhere.</summary>
    public static KrakenCadence Create(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var ticker = HighResolutionTicker.TryCreate(ct);
            if (ticker != null)
            {
                return new KrakenCadence(Stopwatch.GetTimestamp, Stopwatch.Frequency, ticker.WaitUntil, ticker);
            }
        }
        return new KrakenCadence(Stopwatch.GetTimestamp, Stopwatch.Frequency, deadline => SleepThenSpin(ct, deadline));
    }

    private static bool SleepThenSpin(CancellationToken ct, long deadline)
    {
        var coarse = Stopwatch.Frequency * CoarseWaitLatenessMs / 1000;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return true;
            }
            if (remaining > coarse)
            {
                ct.WaitHandle.WaitOne((int)((remaining - coarse) * 1000 / Stopwatch.Frequency));
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
    }

    public void Dispose() => _owned?.Dispose();
}
