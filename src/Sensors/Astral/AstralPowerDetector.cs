using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors.Astral;

/// <summary>Per-pin and per-card state, ordered by severity so the worst is the maximum.</summary>
public enum AstralPinState { Ok, Caution, Warning, Dead, Overcurrent, Critical }

/// <summary>What one detector update asks of its owner.</summary>
internal enum AstralAlertKind { None, Warning, Dead, Overcurrent, Critical }

internal readonly record struct AstralStep(
    AstralAlertKind Alert, int Pin, float Amps, float LimitAmps, bool CountdownStarted, bool CountdownCancelled);

/// <summary>
/// Power Detector for one ROG Astral card, fed one reading per second (all six
/// 12V-2x6 pin currents) from the existing GPU read. Limits follow GPU Tweak III:
/// overcurrent at 12.5 A and critical at 17 A (18 and 20 A with a ROG Equalizer
/// cable). With the matching shutdown on, a pin over the overcurrent limit for the
/// chosen time starts a countdown, and a critical pin (two readings in a row) starts
/// a short one. A cancel only holds off further overcurrent countdowns; a critical
/// pin always arms again. Not thread-safe; the owner locks. Allocation free.
/// </summary>
internal sealed class AstralPowerDetector
{
    public const float OvercurrentAmps = 12.5f;
    public const float CriticalAmps = 17f;
    public const float EqualizerOvercurrentAmps = 18f;
    public const float EqualizerCriticalAmps = 20f;
    public const float PinWarnAmps = 9.2f;
    public const float PinCautionAmps = 8.5f;
    public const float PinLowAmps = 0.01f;
    public const float DeadPinLoadAmps = 1f;
    public const float MaxPlausibleAmps = 50f;
    public const float HysteresisAmps = 0.5f;
    public const int WarningReadings = 3;
    public const int CriticalReadings = 2;
    public const int MinOvercurrentSeconds = 10;
    public const int MinCountdownSeconds = 10;
    public const int MaxSeconds = 300;
    public static readonly TimeSpan CriticalCountdown = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);

    private enum Level { Normal, Overcurrent, Critical }

    private DateTimeOffset? _overSince;
    private Level _level;
    private int _critStreak;
    private int _hotStreak;
    private int _deadStreak;
    private bool _hotReported;
    private bool _deadReported;
    private DateTimeOffset _lastCritical = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOvercurrent = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHot = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDead = DateTimeOffset.MinValue;

    /// <summary>True while a countdown runs; <see cref="Critical"/> says which kind.</summary>
    public bool Running { get; private set; }
    public bool Critical { get; private set; }
    public DateTimeOffset EndsUtc { get; private set; }
    /// <summary>The user cancelled an overcurrent countdown and no pin has been back under the limit since.</summary>
    public bool HeldOff { get; private set; }
    public bool ShutdownIssued { get; private set; }

    public static (float Overcurrent, float Critical) LimitsFor(bool equalizer) => equalizer
        ? (EqualizerOvercurrentAmps, EqualizerCriticalAmps)
        : (OvercurrentAmps, CriticalAmps);

    public static AstralPinState Classify(float amps, float maxAmps, bool equalizer)
    {
        var (over, crit) = LimitsFor(equalizer);
        if (amps >= crit)
        {
            return AstralPinState.Critical;
        }
        if (amps >= over)
        {
            return AstralPinState.Overcurrent;
        }
        if (amps < PinLowAmps && maxAmps >= DeadPinLoadAmps)
        {
            return AstralPinState.Dead;
        }
        if (amps >= (equalizer ? 17f : PinWarnAmps))
        {
            return AstralPinState.Warning;
        }
        return amps >= (equalizer ? 16f : PinCautionAmps) ? AstralPinState.Caution : AstralPinState.Ok;
    }

    public static int ClampOvercurrentSeconds(int v) => Math.Clamp(v, MinOvercurrentSeconds, MaxSeconds);
    public static int ClampCountdownSeconds(int v) => Math.Clamp(v, MinCountdownSeconds, MaxSeconds);

    public AstralStep Update(ReadOnlySpan<float> amps, AstralSettings settings, DateTimeOffset now)
    {
        float worst = 0;
        var worstPin = 0;
        for (var i = 0; i < amps.Length; i++)
        {
            if (amps[i] > worst)
            {
                worst = amps[i];
                worstPin = i;
            }
        }
        if (worst > MaxPlausibleAmps || ShutdownIssued)
        {
            return default;
        }

        var dead = -1;
        for (var i = 0; worst >= DeadPinLoadAmps && i < amps.Length; i++)
        {
            if (amps[i] < PinLowAmps)
            {
                dead = i;
                break;
            }
        }

        var (over, crit) = LimitsFor(settings.Equalizer);
        _critStreak = worst >= crit ? _critStreak + 1 : 0;
        if (worst >= over)
        {
            _overSince ??= now;
        }
        else if (worst < over - HysteresisAmps)
        {
            _overSince = null;
            HeldOff = false;
        }

        var level = _critStreak >= CriticalReadings ? Level.Critical : _overSince is not null ? Level.Overcurrent : Level.Normal;
        var rose = level > _level;
        _level = level;

        var kind = AstralAlertKind.None;
        var limit = 0f;
        var pin = worstPin;
        if (rose && level == Level.Critical && Due(ref _lastCritical, now))
        {
            (kind, limit) = (AstralAlertKind.Critical, crit);
        }
        else if (rose && level == Level.Overcurrent && Due(ref _lastOvercurrent, now))
        {
            (kind, limit) = (AstralAlertKind.Overcurrent, over);
        }

        // A pin over its rating, or a dead pin, alerts once per spell after it lasts a few readings.
        var warn = settings.Equalizer ? 17f : PinWarnAmps;
        _hotStreak = worst >= warn && worst < over ? _hotStreak + 1 : 0;
        _deadStreak = dead >= 0 ? _deadStreak + 1 : 0;
        _hotReported &= _hotStreak > 0;
        _deadReported &= _deadStreak > 0;
        if (kind == AstralAlertKind.None && _deadStreak >= WarningReadings && !_deadReported)
        {
            _deadReported = true;
            if (Due(ref _lastDead, now))
            {
                (kind, limit, pin) = (AstralAlertKind.Dead, PinLowAmps, dead);
            }
        }
        else if (kind == AstralAlertKind.None && _hotStreak >= WarningReadings && !_hotReported)
        {
            _hotReported = true;
            if (Due(ref _lastHot, now))
            {
                (kind, limit) = (AstralAlertKind.Warning, warn);
            }
        }

        var cancelled = false;
        if (Running && (Critical ? !settings.CriticalShutdown : !settings.OvercurrentShutdown))
        {
            Running = false;
            cancelled = true;
        }

        var started = false;
        if (level == Level.Critical && settings.CriticalShutdown && (!Running || (!Critical && EndsUtc > now + CriticalCountdown)))
        {
            Start(true, now + CriticalCountdown);
            started = true;
        }
        else if (!Running && !HeldOff && _overSince is { } since && settings.OvercurrentShutdown
            && now - since >= TimeSpan.FromSeconds(ClampOvercurrentSeconds(settings.OvercurrentSeconds)))
        {
            Start(false, now + TimeSpan.FromSeconds(ClampCountdownSeconds(settings.CountdownSeconds)));
            started = true;
        }

        return new AstralStep(kind, pin, amps[pin], limit, started, cancelled);
    }

    /// <summary>Readings stopped coming: streaks and levels forget, a running countdown does not.</summary>
    public void Clear()
    {
        _overSince = null;
        _level = Level.Normal;
        _critStreak = _hotStreak = _deadStreak = 0;
        _hotReported = _deadReported = false;
    }

    public bool Cancel()
    {
        if (!Running || ShutdownIssued)
        {
            return false;
        }
        Running = false;
        HeldOff |= !Critical;
        return true;
    }

    /// <summary>Only a running countdown can be shortened; this never starts one.</summary>
    public bool ShutdownNow()
    {
        if (!Running || ShutdownIssued)
        {
            return false;
        }
        ShutdownIssued = true;
        return true;
    }

    /// <summary>True once when the running countdown has run out; the owner then shuts down.</summary>
    public bool TakeExpired(DateTimeOffset now)
    {
        if (!Running || ShutdownIssued || now < EndsUtc)
        {
            return false;
        }
        ShutdownIssued = true;
        return true;
    }

    /// <summary>The shutdown could not start: the countdown ends and overcurrent stays held off until the pins recover.</summary>
    public void ShutdownFailed()
    {
        ShutdownIssued = false;
        Running = false;
        HeldOff = true;
    }

    private void Start(bool critical, DateTimeOffset ends)
    {
        Running = true;
        Critical = critical;
        EndsUtc = ends;
    }

    private static bool Due(ref DateTimeOffset last, DateTimeOffset now)
    {
        if (now - last < AlertCooldown)
        {
            return false;
        }
        last = now;
        return true;
    }
}
