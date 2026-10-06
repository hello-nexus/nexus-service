using System;
using System.Collections.Generic;

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
/// Pure, tick-driven CPU thermal guard. No I/O and no clock: callers pass the time
/// in milliseconds. See the thresholds below.
/// </summary>
public sealed class ThermalGuard
{
    /// <summary>Floor ramp starts this far under the limit, at 0 percent.</summary>
    public const double FloorStartBelowLimitC = 15;
    /// <summary>Floor reaches 100 percent this far under the limit.</summary>
    public const double FloorFullBelowLimitC = 5;
    public const double LimitTripBelowLimitC = 5;
    public const long LimitSustainMs = 3000;
    public const long CoolingLossWindowMs = 180_000;
    public const double CoolingLossRiseCPerMin = 2.0;
    public const double CoolingLossMaxLoadPercent = 20;
    public const double CoolingLossMaxDutyPercent = 20;
    public const long EscalateAfterMs = 30_000;
    public const double EscalateRiseC = 1.0;
    public const double ReleaseBelowLimitC = 20;
    public const long ReleaseSustainMs = 60_000;

    private const long SampleSpacingMs = 1000;
    // Slack so a window of 1 s samples counts as covering the full window.
    private const long WindowCoverageSlackMs = 10_000;

    private readonly List<(long Ms, double Temp)> _history = new();
    private string _state = ThermalGuardStates.Inactive;
    private long? _hotSinceMs;
    private long? _belowSinceMs;
    private long _tripAtMs;
    private double _tripTempC;
    private string? _tripReason;

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
        _state = ThermalGuardStates.Inactive;
        _hotSinceMs = null;
        _belowSinceMs = null;
        PeakC = 0;
    }

    public static int FloorFor(double tempC, double limitC)
    {
        var start = limitC - FloorStartBelowLimitC;
        var full = limitC - FloorFullBelowLimitC;
        if (tempC <= start)
        {
            return 0;
        }
        if (tempC >= full)
        {
            return 100;
        }
        return (int)Math.Ceiling((tempC - start) / (full - start) * 100.0);
    }

    /// <param name="maxCpuCoolingDutyPercent">Highest duty across CPU-cooling channels; null when unknown.</param>
    /// <param name="cpuLoadPercent">Null when unreadable; treated as low so the check stays conservative.</param>
    public ThermalGuardOutput Step(long nowMs, double? tempC, double limitC, double? cpuLoadPercent, double? maxCpuCoolingDutyPercent)
    {
        if (tempC is not { } temp || !double.IsFinite(temp) || temp <= 0 || temp > 150)
        {
            if (!IsTripped)
            {
                _state = ThermalGuardStates.Inactive;
                _history.Clear();
                _hotSinceMs = null;
            }
            return Output(false, false, false, 0);
        }

        if (IsTripped)
        {
            return StepTripped(nowMs, temp, limitC);
        }

        Record(nowMs, temp);

        string? reason = null;
        if (temp >= limitC)
        {
            reason = ThermalTripReasons.Limit;
        }
        else if (temp >= limitC - LimitTripBelowLimitC)
        {
            _hotSinceMs ??= nowMs;
            if (nowMs - _hotSinceMs.Value >= LimitSustainMs)
            {
                reason = ThermalTripReasons.Limit;
            }
        }
        else
        {
            _hotSinceMs = null;
        }

        reason ??= CoolingLossDetected(nowMs, temp, cpuLoadPercent, maxCpuCoolingDutyPercent)
            ? ThermalTripReasons.CoolingLoss
            : null;

        if (reason is not null)
        {
            _state = ThermalGuardStates.Tripped;
            _tripReason = reason;
            _tripAtMs = nowMs;
            _tripTempC = temp;
            PeakC = temp;
            _belowSinceMs = null;
            _hotSinceMs = null;
            _history.Clear();
            return Output(true, false, false, 100);
        }

        var floor = FloorFor(temp, limitC);
        _state = floor > 0 ? ThermalGuardStates.Floor : ThermalGuardStates.Normal;
        return Output(false, false, false, floor);
    }

    private ThermalGuardOutput StepTripped(long nowMs, double temp, double limitC)
    {
        PeakC = Math.Max(PeakC, temp);

        if (_state == ThermalGuardStates.Tripped
            && nowMs - _tripAtMs >= EscalateAfterMs
            && temp >= _tripTempC + EscalateRiseC)
        {
            _state = ThermalGuardStates.Escalated;
            return Output(false, false, true, 100);
        }

        if (temp < limitC - ReleaseBelowLimitC)
        {
            _belowSinceMs ??= nowMs;
            if (nowMs - _belowSinceMs.Value >= ReleaseSustainMs)
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

    private void Record(long nowMs, double temp)
    {
        if (_history.Count == 0 || nowMs - _history[^1].Ms >= SampleSpacingMs)
        {
            _history.Add((nowMs, temp));
        }
        var cutoff = nowMs - CoolingLossWindowMs - WindowCoverageSlackMs;
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

    private bool CoolingLossDetected(long nowMs, double temp, double? load, double? maxDuty)
    {
        if (load is { } l && l >= CoolingLossMaxLoadPercent)
        {
            return false;
        }
        if (maxDuty is { } d && d >= CoolingLossMaxDutyPercent)
        {
            return false;
        }
        if (_history.Count == 0)
        {
            return false;
        }
        var oldest = _history[0];
        var span = nowMs - oldest.Ms;
        if (span < CoolingLossWindowMs - WindowCoverageSlackMs)
        {
            return false;
        }
        var perMin = (temp - oldest.Temp) / (span / 60_000.0);
        return perMin >= CoolingLossRiseCPerMin;
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
/// driver near the threshold, drive 100 percent if it keeps rising, resume after a
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
