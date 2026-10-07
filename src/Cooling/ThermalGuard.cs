using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Cooling;

/// <summary>Wire strings for <see cref="ThermalGuard"/> states, plus "off" (toggle off).</summary>
public static class ThermalGuardStates
{
    public const string Off = "off";
    public const string Inactive = "inactive";
    public const string Normal = "normal";
    public const string Floor = "floor";
    public const string Tripped = "tripped";
    public const string Escalated = "escalated";
}

public static class ThermalTripReasons
{
    public const string Limit = "limit";
    public const string CoolingLoss = "cooling-loss";
}

/// <summary>What the engine must do this tick. FloorDuty applies to every guarded channel (max with the computed duty).</summary>
public readonly record struct ThermalGuardOutput(
    string State,
    int FloorDuty,
    bool ForceMax,
    bool StopWriting,
    bool ReleaseAllNow,
    bool TripStarted,
    bool TripEnded,
    string? TripReason);

/// <summary>
/// Every tunable of <see cref="ThermalGuard"/> in one place. Temperatures are offsets below
/// the limit, durations are milliseconds. Pass a different instance to change the design.
/// </summary>
public sealed record ThermalGuardThresholds
{
    public static readonly ThermalGuardThresholds Default = new();

    /// <summary>The floor, the cooling-loss check, escalation and release read the median of this many recent one-second samples, so a single-sample spike never moves them. The limit trip keeps the raw value.</summary>
    public int SmoothingSamples { get; init; } = 5;
    /// <summary>The floor ramp starts this far under the limit.</summary>
    public double FloorStartBelowLimitC { get; init; } = 15;
    /// <summary>Floor duty reached at the limit itself; a curve already at or above it is never touched.</summary>
    public double FloorAtLimitPercent { get; init; } = 60;
    /// <summary>The floor reaches full duty this far above the limit.</summary>
    public double FloorFullAboveLimitC { get; init; } = 3;
    /// <summary>The CPU cannot hold its own limit: trip when this far above it for <see cref="LimitSustainMs"/>.</summary>
    public double LimitTripAboveLimitC { get; init; } = 3;
    public long LimitSustainMs { get; init; } = 5000;
    public long CoolingLossWindowMs { get; init; } = 180_000;
    public double CoolingLossRiseCPerMin { get; init; } = 2.0;
    public double CoolingLossMaxLoadPercent { get; init; } = 20;
    public double CoolingLossMaxDutyPercent { get; init; } = 20;
    /// <summary>Cooling-loss only trips once the temperature is this close to the limit.</summary>
    public double CoolingLossGateBelowLimitC { get; init; } = 15;
    /// <summary>Escalation is considered this long after the trip, and only while the temperature is still at the limit.</summary>
    public long EscalateAfterMs { get; init; } = 20_000;
    /// <summary>A fan that has reported RPM counts as not responding below this fraction of its highest RPM.</summary>
    public double EscalateRpmFraction { get; init; } = 0.25;
    public double ReleaseBelowLimitC { get; init; } = 10;
    public long ReleaseSustainMs { get; init; } = 60_000;
}

/// <summary>
/// Pure, tick-driven CPU thermal guard. No I/O and no clock: callers pass the time
/// in milliseconds. Tunables live in <see cref="ThermalGuardThresholds"/>.
/// </summary>
public sealed class ThermalGuard
{
    private const long SampleSpacingMs = 1000;
    // Readings further apart than this many sample spacings restart the release timer.
    private const long GapResetSpacings = 5;
    // Slack so a window of one-second samples counts as covering the full window.
    private const long WindowCoverageSlackMs = 10_000;

    private readonly ThermalGuardThresholds _t;
    private readonly List<(long Ms, double Temp)> _history = new();
    // One entry per second bucket (nowMs / SampleSpacingMs), so ticks a little under a second apart do not merge in pairs.
    private readonly List<(long Bucket, double Temp)> _recent = new();
    private long? _lastStepMs;
    private string _state = ThermalGuardStates.Inactive;
    private long? _hotSinceMs;
    private long? _belowSinceMs;
    private long _tripAtMs;
    private string? _tripReason;

    public ThermalGuard(ThermalGuardThresholds? thresholds = null)
    {
        _t = thresholds ?? ThermalGuardThresholds.Default;
    }

    public string State => _state;
    public double PeakC { get; private set; }
    public long TripAtMs => _tripAtMs;
    public string? TripReason => _tripReason;
    public bool Escalated => _state == ThermalGuardStates.Escalated;

    private bool IsTripped => _state is ThermalGuardStates.Tripped or ThermalGuardStates.Escalated;

    /// <summary>Forget everything (toggle off, cooling off).</summary>
    public void Reset()
    {
        _history.Clear();
        _recent.Clear();
        _lastStepMs = null;
        _state = ThermalGuardStates.Inactive;
        _hotSinceMs = null;
        _belowSinceMs = null;
        PeakC = 0;
    }

    public static int FloorFor(double tempC, double limitC) => FloorFor(tempC, limitC, ThermalGuardThresholds.Default);

    public static int FloorFor(double tempC, double limitC, ThermalGuardThresholds t)
    {
        var start = limitC - t.FloorStartBelowLimitC;
        var full = limitC + t.FloorFullAboveLimitC;
        double duty;
        if (tempC <= start)
        {
            duty = 0;
        }
        else if (tempC <= limitC)
        {
            duty = (tempC - start) / (limitC - start) * t.FloorAtLimitPercent;
        }
        else if (tempC < full)
        {
            duty = t.FloorAtLimitPercent + (tempC - limitC) / (full - limitC) * (100 - t.FloorAtLimitPercent);
        }
        else
        {
            duty = 100;
        }
        return (int)Math.Round(duty, MidpointRounding.AwayFromZero);
    }

    /// <param name="maxCpuCoolingDutyPercent">Highest duty across CPU-cooling fans; null means there is none to judge, so no cooling-loss trip.</param>
    /// <param name="cpuLoadPercent">Null when unreadable; treated as low so the check stays conservative.</param>
    /// <param name="writesNotLanding">True when every guarded non-pump fan with a known best RPM (established over consecutive ticks, or calibrated) reads far below it, a zero reading counting only once sustained; the only thing that escalates.</param>
    public ThermalGuardOutput Step(
        long nowMs,
        double? tempC,
        double limitC,
        double? cpuLoadPercent,
        double? maxCpuCoolingDutyPercent,
        bool writesNotLanding = false)
    {
        if (tempC is not { } temp || !double.IsFinite(temp) || temp <= 0 || temp > 150)
        {
            // Samples from before the dropout must not bias the medians that follow it.
            _recent.Clear();
            if (!IsTripped)
            {
                _state = ThermalGuardStates.Inactive;
                _history.Clear();
                _hotSinceMs = null;
            }
            return Output(false, false, false, 0);
        }

        // After a long pause in the readings the release timer starts over: it cannot count time nobody observed.
        if (_lastStepMs is { } last && nowMs - last > GapResetSpacings * SampleSpacingMs)
        {
            _belowSinceMs = null;
        }
        _lastStepMs = nowMs;
        var smooth = Smooth(nowMs, temp);
        if (IsTripped)
        {
            return StepTripped(nowMs, temp, smooth, limitC, writesNotLanding);
        }

        Record(nowMs, smooth);

        string? reason = null;
        if (temp >= limitC + _t.LimitTripAboveLimitC)
        {
            _hotSinceMs ??= nowMs;
            if (nowMs - _hotSinceMs.Value >= _t.LimitSustainMs)
            {
                reason = ThermalTripReasons.Limit;
            }
        }
        else
        {
            _hotSinceMs = null;
        }

        reason ??= CoolingLossDetected(nowMs, smooth, limitC, cpuLoadPercent, maxCpuCoolingDutyPercent)
            ? ThermalTripReasons.CoolingLoss
            : null;

        if (reason is not null)
        {
            _state = ThermalGuardStates.Tripped;
            _tripReason = reason;
            _tripAtMs = nowMs;
            PeakC = temp;
            _belowSinceMs = null;
            _hotSinceMs = null;
            _history.Clear();
            return Output(true, false, false, 100);
        }

        var floor = FloorFor(smooth, limitC, _t);
        _state = floor > 0 ? ThermalGuardStates.Floor : ThermalGuardStates.Normal;
        return Output(false, false, false, floor);
    }

    private ThermalGuardOutput StepTripped(long nowMs, double temp, double smooth, double limitC, bool writesNotLanding)
    {
        PeakC = Math.Max(PeakC, temp);

        if (_state == ThermalGuardStates.Tripped
            && nowMs - _tripAtMs >= _t.EscalateAfterMs
            && smooth >= limitC
            && writesNotLanding)
        {
            _state = ThermalGuardStates.Escalated;
            return Output(false, false, true, 100);
        }

        // Smoothing may only keep a trip, never end one early: the raw reading must be below too.
        if (Math.Max(temp, smooth) < limitC - _t.ReleaseBelowLimitC)
        {
            _belowSinceMs ??= nowMs;
            if (nowMs - _belowSinceMs.Value >= _t.ReleaseSustainMs)
            {
                _state = ThermalGuardStates.Normal;
                _belowSinceMs = null;
                _history.Clear();
                return Output(false, true, false, 0);
            }
        }
        else
        {
            _belowSinceMs = null;
        }
        return Output(false, false, false, 100);
    }

    private ThermalGuardOutput Output(bool tripStarted, bool tripEnded, bool releaseAllNow, int floor)
    {
        var tripped = IsTripped;
        return new ThermalGuardOutput(
            _state,
            tripped ? 100 : floor,
            ForceMax: _state == ThermalGuardStates.Tripped,
            StopWriting: _state == ThermalGuardStates.Escalated,
            ReleaseAllNow: releaseAllNow,
            TripStarted: tripStarted,
            TripEnded: tripEnded,
            TripReason: tripStarted ? _tripReason : null);
    }

    // Median of the last few one-second samples; the current reading always counts, and a
    // reading inside the same second replaces that second's sample instead of adding one.
    private double Smooth(long nowMs, double temp)
    {
        var bucket = nowMs / SampleSpacingMs;
        var window = Math.Max(1, _t.SmoothingSamples);
        // Samples older than the window have aged out, however few remain.
        _recent.RemoveAll(r => bucket - r.Bucket >= window);
        if (_recent.Count > 0 && _recent[^1].Bucket == bucket)
        {
            _recent[^1] = (bucket, temp);
        }
        else
        {
            _recent.Add((bucket, temp));
        }
        var excess = _recent.Count - window;
        if (excess > 0)
        {
            _recent.RemoveRange(0, excess);
        }
        var sorted = _recent.Select(r => r.Temp).OrderBy(t => t).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private void Record(long nowMs, double temp)
    {
        if (_history.Count == 0 || nowMs - _history[^1].Ms >= SampleSpacingMs)
        {
            _history.Add((nowMs, temp));
        }
        var cutoff = nowMs - _t.CoolingLossWindowMs - WindowCoverageSlackMs;
        var drop = 0;
        while (drop < _history.Count && _history[drop].Ms < cutoff)
        {
            drop++;
        }
        if (drop > 0)
        {
            _history.RemoveRange(0, drop);
        }
    }

    private bool CoolingLossDetected(long nowMs, double temp, double limitC, double? load, double? maxDuty)
    {
        // No CPU-cooling fan to judge: nothing to say about it.
        if (maxDuty is not { } d || d >= _t.CoolingLossMaxDutyPercent)
        {
            return false;
        }
        if (temp < limitC - _t.CoolingLossGateBelowLimitC)
        {
            return false;
        }
        if (load is { } l && l >= _t.CoolingLossMaxLoadPercent)
        {
            return false;
        }
        if (_history.Count == 0)
        {
            return false;
        }
        var oldest = _history[0];
        var span = nowMs - oldest.Ms;
        if (span < _t.CoolingLossWindowMs - WindowCoverageSlackMs)
        {
            return false;
        }
        var perMin = (temp - oldest.Temp) / (span / 60_000.0);
        return perMin >= _t.CoolingLossRiseCPerMin;
    }
}

public static class GpuGuardStates
{
    public const string Inactive = "inactive";
    public const string Normal = "normal";
    public const string HandedBack = "handedBack";
    public const string Forced = "forced";
}

/// <summary>
/// Pure per-GPU guard against its own slowdown threshold: hand the fan back to the
/// driver near the threshold, drive full duty if it keeps rising, resume after a
/// sustained cool-down.
/// </summary>
public sealed class GpuThermalGuard
{
    public const double HandBackBelowLimitC = 5;
    public const long ForceAfterMs = 30_000;
    public const double ForceRiseC = 1.0;
    public const double ResumeBelowLimitC = 15;
    public const long ResumeSustainMs = 60_000;

    private string _state = GpuGuardStates.Normal;
    private long _handedBackAtMs;
    private double _handedBackTempC;
    private long? _coolSinceMs;

    public string State => _state;

    public (string State, bool HandBackNow, bool Resumed) Step(long nowMs, double tempC, double limitC)
    {
        if (_state == GpuGuardStates.Normal)
        {
            if (tempC >= limitC - HandBackBelowLimitC)
            {
                _state = GpuGuardStates.HandedBack;
                _handedBackAtMs = nowMs;
                _handedBackTempC = tempC;
                _coolSinceMs = null;
                return (_state, true, false);
            }
            return (_state, false, false);
        }

        if (_state == GpuGuardStates.HandedBack
            && nowMs - _handedBackAtMs >= ForceAfterMs
            && tempC >= _handedBackTempC + ForceRiseC)
        {
            _state = GpuGuardStates.Forced;
        }

        if (tempC < limitC - ResumeBelowLimitC)
        {
            _coolSinceMs ??= nowMs;
            if (nowMs - _coolSinceMs.Value >= ResumeSustainMs)
            {
                _state = GpuGuardStates.Normal;
                _coolSinceMs = null;
                return (_state, false, true);
            }
        }
        else
        {
            _coolSinceMs = null;
        }
        return (_state, false, false);
    }
}
