using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// GPU Tweak III's "Power detector +" for ROG Astral cards: watches the current through each
/// 12V pin of the card's 12V-2x6 connector, with GPU Tweak III 2.2's limits. A pin over the
/// overcurrent limit (12.5 A, 18 A with a ROG Equalizer cable) raises an alert; with
/// overcurrent shutdown on and the pin still over after the user's chosen time, a shutdown
/// countdown starts. A pin at the critical limit (17 A, 20 A with the Equalizer) raises an
/// alert and, with critical shutdown on, a 10 second countdown.
///
/// A countdown runs to its end unless the user cancels it or turns its shutdown off: what
/// started it says the connector is in trouble even if the load then drops. A cancel holds
/// further countdowns off until every pin is back under the overcurrent limit. A missing or
/// stale reading changes nothing but the critical streak. Not thread-safe; the owner locks.
///
/// Each pin's own state, as GPU Tweak III's panel colours its dots (CMainDlg::OnTimer): fine
/// from <see cref="PinLowAmps"/> to under <see cref="PinWarnAmps"/> (17 A with the
/// Equalizer), a warning outside that, a pin carrying nothing included. GPU Tweak checks each
/// pin on its own, never one pin against the others. Nexus adds a caution step under the
/// rating (<see cref="PinCautionAmps"/>), and alerts on a pin over its rating or a dead pin
/// once it lasts <see cref="WarningReadings"/> readings.
/// </summary>
internal sealed class AstralPowerDetector
{
    public const float OvercurrentAmps = 12.5f;
    public const float EqualizerOvercurrentAmps = 18f;
    public const float CriticalAmps = 17f;
    public const float EqualizerCriticalAmps = 20f;

    /// <summary>A pin at or over this shows as a warning (GPU Tweak III: red); 17 A with the Equalizer.</summary>
    public const float PinWarnAmps = 9.2f;
    public const float EqualizerPinWarnAmps = 17f;

    /// <summary>A pin under this carries nothing, which GPU Tweak III also shows as a warning.</summary>
    public const float PinLowAmps = 0.01f;

    /// <summary>
    /// A pin at or over this carries clearly more than its share: an RTX 5090 at 575-600 W puts
    /// 8.0-8.4 A on each pin of a sound cable. Shown as a caution, never alerted; 16 A with the
    /// Equalizer. GPU Tweak III has a yellow dot but never shows it.
    /// </summary>
    public const float PinCautionAmps = 8.5f;
    public const float EqualizerPinCautionAmps = 16f;

    /// <summary>A pin carrying nothing counts as dead only while another pin of the card carries at least this.</summary>
    public const float DeadPinLoadAmps = 1f;

    /// <summary>Readings in a row (one a second) a pin over its rating, or a dead pin, lasts before it alerts; a load spike passes sooner.</summary>
    public const int WarningReadings = 3;

    /// <summary>No 12V pin carries this; a reading over it is a bad transfer (GPU Tweak III's ExpanModule drops it too).</summary>
    public const float MaxPlausibleAmps = 50f;
    public static readonly TimeSpan CriticalCountdown = TimeSpan.FromSeconds(10);

    /// <summary>One reading past the critical limit could be a bad transfer; two in a row (a second apart) is the card.</summary>
    public const int CriticalReadings = 2;

    /// <summary>A pin sitting at the limit would flip over and back on noise, so it has to drop this far under it to count as back to normal.</summary>
    public const float HysteresisAmps = 0.5f;

    /// <summary>The same level alerts at most once in this time.</summary>
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Readings missing for this long leave the pins unknown: the level goes back to normal.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    public const int MinOvercurrentSeconds = 5;
    public const int MinCountdownSeconds = 10;
    public const int MaxSeconds = 3600;

    /// <summary>Warning is only ever an alert's level: a pin over its rating, or a dead pin.</summary>
    public enum Level { Normal, Warning, Overcurrent, Critical }

    /// <summary>The pin carrying the most current right now, across every card; Pin is 1-based.</summary>
    public readonly record struct Reading(string GpuId, string GpuName, int Pin, float Amps);

    public readonly record struct Limits(float Overcurrent, float Critical, float PinWarn, float PinCaution);

    public readonly record struct Countdown(Level Reason, DateTimeOffset StartedUtc, DateTimeOffset EndsUtc);

    /// <summary>An alert to show: the level reached, the pin that reached it and the limit it passed; Dead for a pin carrying nothing.</summary>
    public readonly record struct Alert(Level Level, Reading Reading, float LimitAmps, bool Dead = false);

    /// <summary>What one update asks of the owner.</summary>
    public sealed class Step
    {
        public Alert? Alert { get; set; }
        public Countdown? CountdownStarted { get; set; }
        public bool CountdownCancelled { get; set; }
        public bool Shutdown { get; set; }
    }

    private DateTimeOffset? _overSince;
    private DateTimeOffset? _lastReadingUtc;
    private int _criticalStreak;
    private DateTimeOffset? _lastOvercurrentAlert;
    private DateTimeOffset? _lastCriticalAlert;
    private int _hotStreak;
    private int _deadStreak;
    private bool _hotReported;
    private bool _deadReported;
    private DateTimeOffset? _lastHotAlert;
    private DateTimeOffset? _lastDeadAlert;

    public Level Current { get; private set; } = Level.Normal;

    /// <summary>The latest reading, or null before the first or once readings went stale.</summary>
    public Reading? Latest { get; private set; }

    /// <summary>The countdown running, or the one that ran out once the shutdown was asked for.</summary>
    public Countdown? Running { get; private set; }

    /// <summary>The user cancelled a countdown and the pins have not been back to normal since.</summary>
    public bool HeldOff { get; private set; }

    public bool ShutdownIssued { get; private set; }

    public static Limits LimitsFor(AstralPowerDetectorSettings settings) => settings.Equalizer
        ? new Limits(EqualizerOvercurrentAmps, EqualizerCriticalAmps, EqualizerPinWarnAmps, EqualizerPinCautionAmps)
        : new Limits(OvercurrentAmps, CriticalAmps, PinWarnAmps, PinCautionAmps);

    public static TimeSpan OvercurrentTime(AstralPowerDetectorSettings settings) =>
        TimeSpan.FromSeconds(Math.Clamp(settings.OvercurrentSeconds, MinOvercurrentSeconds, MaxSeconds));

    public static TimeSpan CountdownTime(AstralPowerDetectorSettings settings) =>
        TimeSpan.FromSeconds(Math.Clamp(settings.CountdownSeconds, MinCountdownSeconds, MaxSeconds));

    /// <summary>Takes the worst pin of this second (null when no fresh reading came) and the user's settings.</summary>
    public Step Update(Reading? reading, AstralPowerDetectorSettings settings, DateTimeOffset now) =>
        Update(reading, null, settings, now);

    /// <summary>As above, with a pin carrying nothing while another carries current (null when there is none).</summary>
    public Step Update(Reading? reading, Reading? dead, AstralPowerDetectorSettings settings, DateTimeOffset now)
    {
        var step = new Step();
        var limits = LimitsFor(settings);

        if (reading is { } r)
        {
            Read(r, limits, settings, now, step);
            Warn(r, dead, limits, now, step);
        }
        else
        {
            _criticalStreak = 0;
            _hotStreak = 0;
            _deadStreak = 0;
            _hotReported = false;
            _deadReported = false;
            if (_lastReadingUtc is { } last && now - last >= StaleAfter)
            {
                _lastReadingUtc = null;
                _overSince = null;
                Latest = null;
                Current = Level.Normal;
            }
        }

        if (ShutdownIssued)
        {
            return step;
        }

        // A shutdown the user turned off no longer counts down.
        if (Running is { } running
            && ((running.Reason == Level.Critical && !settings.CriticalShutdown)
                || (running.Reason == Level.Overcurrent && !settings.OvercurrentShutdown)))
        {
            Running = null;
            step.CountdownCancelled = true;
        }

        if (!HeldOff)
        {
            if (Current == Level.Critical && settings.CriticalShutdown
                && (Running is not { } shorter || shorter.EndsUtc > now + CriticalCountdown))
            {
                Running = new Countdown(Level.Critical, now, now + CriticalCountdown);
                step.CountdownStarted = Running;
            }
            else if (Running is null && _overSince is { } since && settings.OvercurrentShutdown
                && now - since >= OvercurrentTime(settings))
            {
                Running = new Countdown(Level.Overcurrent, now, now + CountdownTime(settings));
                step.CountdownStarted = Running;
            }
        }

        if (Running is { } countdown && now >= countdown.EndsUtc)
        {
            ShutdownIssued = true;
            step.Shutdown = true;
        }
        return step;
    }

    private void Read(Reading r, Limits limits, AstralPowerDetectorSettings settings, DateTimeOffset now, Step step)
    {
        Latest = r;
        _lastReadingUtc = now;
        _criticalStreak = r.Amps >= limits.Critical ? _criticalStreak + 1 : 0;

        if (r.Amps >= limits.Overcurrent)
        {
            _overSince ??= now;
        }
        else if (r.Amps < limits.Overcurrent - HysteresisAmps)
        {
            _overSince = null;
            HeldOff = false;
        }

        var level = _criticalStreak >= CriticalReadings ? Level.Critical
            : _overSince is not null ? Level.Overcurrent
            : Level.Normal;
        var rose = level > Current;
        Current = level;
        if (!rose)
        {
            return;
        }

        if (level == Level.Critical && Due(ref _lastCriticalAlert, now))
        {
            step.Alert = new Alert(Level.Critical, r, limits.Critical);
        }
        else if (level == Level.Overcurrent && Due(ref _lastOvercurrentAlert, now))
        {
            step.Alert = new Alert(Level.Overcurrent, r, limits.Overcurrent);
        }
    }

    // A pin over its rating (under the overcurrent limit, which alerts on its own) or a dead
    // pin alerts once per spell, when it has lasted WarningReadings readings; an alert already
    // going out this second puts it off to the next.
    private void Warn(Reading worst, Reading? dead, Limits limits, DateTimeOffset now, Step step)
    {
        _hotStreak = worst.Amps >= limits.PinWarn && worst.Amps < limits.Overcurrent ? _hotStreak + 1 : 0;
        _deadStreak = dead is null ? 0 : _deadStreak + 1;
        _hotReported &= _hotStreak > 0;
        _deadReported &= _deadStreak > 0;
        if (step.Alert is not null)
        {
            return;
        }

        if (_deadStreak >= WarningReadings && !_deadReported && dead is { } d)
        {
            _deadReported = true;
            if (Due(ref _lastDeadAlert, now))
            {
                step.Alert = new Alert(Level.Warning, d, PinLowAmps, Dead: true);
            }
        }
        else if (_hotStreak >= WarningReadings && !_hotReported)
        {
            _hotReported = true;
            if (Due(ref _lastHotAlert, now))
            {
                step.Alert = new Alert(Level.Warning, worst, limits.PinWarn);
            }
        }
    }

    private static bool Due(ref DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is { } at && now - at < AlertCooldown)
        {
            return false;
        }
        last = now;
        return true;
    }

    /// <summary>The user stopped the countdown. False when none was running or the shutdown was already asked for.</summary>
    public bool Cancel()
    {
        if (Running is null || ShutdownIssued)
        {
            return false;
        }
        Running = null;
        HeldOff = true;
        return true;
    }

    /// <summary>The user asked not to wait for the countdown. Only a running countdown can be cut short.</summary>
    public bool ShutdownNow()
    {
        if (Running is null || ShutdownIssued)
        {
            return false;
        }
        ShutdownIssued = true;
        return true;
    }

    /// <summary>The shutdown could not be started: the countdown ends and the detector watches again, held off until the pins are normal.</summary>
    public void ShutdownFailed()
    {
        ShutdownIssued = false;
        Running = null;
        HeldOff = true;
    }
}
