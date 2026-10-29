using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors.Astral;

/// <summary>Per-pin and per-card state, ordered by severity so the worst is the maximum.</summary>
public enum AstralPinState { Ok, Caution, Warning, Dead, Overcurrent, Critical }

/// <summary>What one detector update asks of its owner.</summary>
internal enum AstralAlertKind { None, Warning, Dead, Overcurrent, Critical }

internal readonly record struct AstralStep(
    AstralAlertKind Alert, int Pin, float Amps, float LimitAmps, bool CountdownStarted, bool CountdownCancelled, bool ShutdownLapsed = false);

/// <summary>
/// Power Detector for one ROG Astral card, fed one reading per probe (all six
/// 12V-2x6 pin currents) from the existing GPU read. Limits are the card's rated
/// connector currents and rise with a ROG Equalizer cable. With the matching shutdown on,
/// a pin over the overcurrent limit for the chosen time starts a countdown, and a critical
/// pin for <see cref="CriticalReadings"/> readings in a row starts a short one. A cancel only
/// applies to an overcurrent countdown and holds further ones off; a critical countdown
/// cannot be cancelled. Times are on one monotonic axis chosen by the owner.
/// Not thread-safe; the owner locks. Allocation free.
/// </summary>
internal sealed class AstralPowerDetector
{
    public const float OvercurrentAmps = 12.5f;
    public const float CriticalAmps = 17f;
    public const float EqualizerOvercurrentAmps = 18f;
    public const float EqualizerCriticalAmps = 20f;
    public const float PinWarnAmps = 9.2f;
    public const float PinCautionAmps = 8.5f;
    public const float EqualizerWarnAmps = 17f;
    public const float EqualizerCautionAmps = 16f;
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
    /// <summary>A shutdown still unfinished this long after it was issued was aborted.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(60);

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
    private DateTimeOffset _issuedAt;

    /// <summary>True while a countdown runs; <see cref="Critical"/> says which kind.</summary>
    public bool Running { get; private set; }
    public bool Critical { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
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
        if (amps >= (equalizer ? EqualizerWarnAmps : PinWarnAmps))
        {
            return AstralPinState.Warning;
        }
        return amps >= (equalizer ? EqualizerCautionAmps : PinCautionAmps) ? AstralPinState.Caution : AstralPinState.Ok;
    }

    public static int ClampOvercurrentSeconds(int v) => Math.Clamp(v, MinOvercurrentSeconds, MaxSeconds);
    public static int ClampCountdownSeconds(int v) => Math.Clamp(v, MinCountdownSeconds, MaxSeconds);

    public AstralStep Update(ReadOnlySpan<float> amps, AstralSettings settings, DateTimeOffset now)
    {
        var lapsed = false;
        if (ShutdownIssued && now - _issuedAt >= ShutdownGrace)
        {
            ShutdownFailed();
            lapsed = true;
        }

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
        if (worst > MaxPlausibleAmps)
        {
            _critStreak = 0;
            return new AstralStep(default, 0, 0, 0, false, false, lapsed);
        }
        if (ShutdownIssued)
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
        var warn = settings.Equalizer ? EqualizerWarnAmps : PinWarnAmps;
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

        var cancelled = CancelIfDisabled(settings);

        var started = false;
        if (level == Level.Critical && settings.CriticalShutdown && (!Running || (!Critical && EndsAt > now + CriticalCountdown)))
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

        return new AstralStep(kind, pin, amps[pin], limit, started, cancelled, lapsed);
    }

    /// <summary>Readings stopped coming: streaks and levels forget, a running countdown does not.</summary>
    public void Clear()
    {
        _overSince = null;
        _level = Level.Normal;
        _critStreak = _hotStreak = _deadStreak = 0;
        _hotReported = _deadReported = false;
    }

    /// <summary>Stops the running countdown when its shutdown kind is off; true when it did.</summary>
    public bool CancelIfDisabled(AstralSettings settings)
    {
        if (!Running || ShutdownIssued || (Critical ? settings.CriticalShutdown : settings.OvercurrentShutdown))
        {
            return false;
        }
        Running = false;
        return true;
    }

    /// <summary>After a settings change, stops the running countdown if its shutdown is off or, when the limits moved, the last reading no longer reaches its limit.</summary>
    public bool Reapply(ReadOnlySpan<float> amps, AstralSettings settings, bool limitsChanged)
    {
        if (CancelIfDisabled(settings))
        {
            return true;
        }
        if (!Running || ShutdownIssued || !limitsChanged)
        {
            return false;
        }
        float worst = 0;
        foreach (var a in amps)
        {
            worst = Math.Max(worst, a);
        }
        var (over, crit) = LimitsFor(settings.Equalizer);
        if (worst >= (Critical ? crit : over))
        {
            return false;
        }
        Running = false;
        return true;
    }

    /// <summary>Stops an overcurrent countdown and holds further ones off. A critical countdown cannot be cancelled and keeps its end time.</summary>
    public bool Cancel()
    {
        if (!Running || ShutdownIssued || Critical)
        {
            return false;
        }
        Running = false;
        HeldOff = true;
        return true;
    }

    /// <summary>Only a running countdown can be shortened; this never starts one.</summary>
    public bool ShutdownNow(DateTimeOffset now)
    {
        if (!Running || ShutdownIssued)
        {
            return false;
        }
        ShutdownIssued = true;
        _issuedAt = now;
        return true;
    }

    /// <summary>True once when the running countdown has run out; the owner then shuts down.</summary>
    public bool TakeExpired(DateTimeOffset now)
    {
        if (!Running || ShutdownIssued || now < EndsAt)
        {
            return false;
        }
        ShutdownIssued = true;
        _issuedAt = now;
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
        EndsAt = ends;
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
