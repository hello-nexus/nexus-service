using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Power;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// A Windows notification from the Power Detector. With CancelsShutdown its
/// button cancels the countdown; otherwise the notification opens Path (the
/// card's page in the dashboard, when an app shows one).
/// </summary>
public sealed record PowerDetectorNotice(string Title, string Text, string? Path, bool CancelsShutdown);

/// <summary>
/// Runs the ROG Astral Power Detector (<see cref="AstralPowerDetector"/>) once a
/// second, whether or not anything shows it: reads the 12V-2x6 pin currents
/// straight off the card (<see cref="ISensorProvider.ReadGpuPinCurrents"/>),
/// raises <see cref="NoticeRaised"/> for Windows notifications, keeps the
/// latest alert for the in-app pop-up and shuts the PC down when a countdown
/// runs out. Until a card is known it reads the GPU list now and then, since a
/// card is only found as the GPUs are read.
///
/// Debug builds log the shutdown instead of doing it, so testing the detector
/// never powers a developer's PC off.
/// </summary>
public sealed class AstralPowerDetectorService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    // A reading older than this (a failed probe keeps the last good one) does not count.
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(3);
    // While no card is known: the GPU list is read every 30 s for the first 10
    // minutes (a card is found as soon as the GPUs are read), then every 5 minutes,
    // so a PC without one pays almost nothing.
    private static readonly TimeSpan EarlyProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LateProbeInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EarlyProbePeriod = TimeSpan.FromMinutes(10);

    private readonly ISensorProvider _sensors;
    private readonly IConfigStore _store;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedUtc;
    private readonly object _lock = new();
    private readonly AstralPowerDetector _detector = new();
    private IReadOnlyList<GpuPinCurrents> _cards = Array.Empty<GpuPinCurrents>();
    private DateTimeOffset _lastGpuProbe = DateTimeOffset.MinValue;
    private PowerDetectorAlertDto? _lastAlert;
    private long _alertSeq;
    private string _countdownText = "";

    /// <summary>A notification for Windows; the platform bootstrap shows it.</summary>
    public event Action<PowerDetectorNotice>? NoticeRaised;

    /// <summary>Performs the shutdown with its reason; false when it could not be started. Tests substitute it.</summary>
    internal Func<string, bool> ShutdownSink { get; set; }

    public AstralPowerDetectorService(ISensorProvider sensors, IConfigStore store, ISystemPowerProvider power)
        : this(sensors, store, power, TimeProvider.System)
    {
    }

    internal AstralPowerDetectorService(
        ISensorProvider sensors, IConfigStore store, ISystemPowerProvider power, TimeProvider time)
    {
        _sensors = sensors;
        _store = store;
        _time = time;
        _startedUtc = time.GetUtcNow();
#if DEBUG
        ShutdownSink = reason =>
        {
            ServiceLog.Warn($"[power-detector] debug build, not shutting down: {reason}");
            return true;
        };
#else
        ShutdownSink = power.ForceShutdown;
#endif
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[power-detector] check failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One check of the pins.</summary>
    internal void Tick()
    {
        var settings = Settings();
        var cards = _sensors.ReadGpuPinCurrents();
        if (cards.Count == 0 && GpuProbeDue())
        {
            _sensors.GetGpus();
            cards = _sensors.ReadGpuPinCurrents();
        }

        var now = _time.GetUtcNow();
#if DEV_TOOLS
        cards = Simulated(cards, now);
#endif
        var (worst, dead) = Worst(cards, now);
        AstralPowerDetector.Step step;
        AstralPowerDetector.Countdown? running;
        lock (_lock)
        {
            _cards = cards;
            step = _detector.Update(worst, dead, settings, now);
            running = _detector.Running;
            if (step.Alert is { } alert)
            {
                var (title, text) = AlertWording(alert, settings, worst?.Amps);
                ServiceLog.Warn($"[power-detector] {alert.Level}{(alert.Dead ? " (dead pin)" : "")}: {alert.Reading.GpuName} pin {alert.Reading.Pin} at {alert.Reading.Amps:F2} A (limit {alert.LimitAmps:0.##} A)");
                _lastAlert = settings.InAppAlerts
                    ? new PowerDetectorAlertDto
                    {
                        Id = ++_alertSeq,
                        Level = LevelName(alert.Level),
                        Title = title,
                        Text = text,
                        AtMs = now.ToUnixTimeMilliseconds(),
                    }
                    : _lastAlert;
                // A countdown starting with it says the same and more.
                if (settings.TrayAlerts && step.CountdownStarted is null)
                {
                    Raise(new PowerDetectorNotice(title, text, PagePath(), CancelsShutdown: false));
                }
            }
            if (step.CountdownStarted is { } started)
            {
                _countdownText = CountdownText(started, worst, settings);
            }
        }

        if (step.CountdownStarted is { } countdown)
        {
            var seconds = (int)Math.Round((countdown.EndsUtc - countdown.StartedUtc).TotalSeconds);
            ServiceLog.Warn($"[power-detector] {countdown.Reason} shutdown countdown started: {seconds} s");
            // Always shown, alerts on or off: the user must be able to stop it.
            Raise(CountdownNotice(countdown));
        }
        if (step.CountdownCancelled)
        {
            ServiceLog.Info("[power-detector] shutdown countdown stopped: that shutdown was turned off");
        }
        if (step.Shutdown && running is { } ran)
        {
            Shutdown(ran.Reason == AstralPowerDetector.Level.Critical
                ? "Nexus Power Detector: critical overcurrent on the graphics card's 12V-2x6 connector"
                : "Nexus Power Detector: overcurrent on the graphics card's 12V-2x6 connector");
        }
    }

    /// <summary>Stops the running countdown; false when none was running.</summary>
    public bool Cancel()
    {
        bool cancelled;
        lock (_lock)
        {
            cancelled = _detector.Cancel();
        }
        if (cancelled)
        {
            ServiceLog.Info("[power-detector] shutdown countdown cancelled by the user");
        }
        return cancelled;
    }

    /// <summary>The name of the first card whose power monitor answered, or null before one has.</summary>
    public string? FirstCardName
    {
        get
        {
            lock (_lock)
            {
                return _cards.Count > 0 ? _cards[0].Name : null;
            }
        }
    }

    /// <summary>Shuts down now instead of at the end of the running countdown; false when none was running.</summary>
    public bool ShutdownNow()
    {
        bool now;
        lock (_lock)
        {
            now = _detector.ShutdownNow();
        }
        if (now)
        {
            ServiceLog.Warn("[power-detector] shutting down now at the user's request");
            Shutdown("Nexus Power Detector: shut down from the overcurrent countdown");
        }
        return now;
    }

    /// <summary>The notification of the countdown running, for a helper that connects mid-countdown; null when none runs.</summary>
    public PowerDetectorNotice? CurrentCountdownNotice()
    {
        lock (_lock)
        {
            return _detector.Running is { } running && !_detector.ShutdownIssued ? CountdownNotice(running) : null;
        }
    }

    /// <summary>Saves a change to the settings and returns the new state.</summary>
    public PowerDetectorStateDto SetConfig(PowerDetectorConfigBody body)
    {
        _store.Update(s =>
        {
            var c = s.AstralPowerDetector ??= new AstralPowerDetectorSettings();
            if (body.TrayAlerts is { } trayAlerts) c.TrayAlerts = trayAlerts;
            if (body.InAppAlerts is { } inAppAlerts) c.InAppAlerts = inAppAlerts;
            if (body.Equalizer is { } equalizer) c.Equalizer = equalizer;
            if (body.HideEqualizerReminder is { } hide) c.HideEqualizerReminder = hide;
            if (body.OvercurrentShutdown is { } overcurrent) c.OvercurrentShutdown = overcurrent;
            if (body.OvercurrentSeconds is { } overSeconds)
            {
                c.OvercurrentSeconds = Math.Clamp(overSeconds, AstralPowerDetector.MinOvercurrentSeconds, AstralPowerDetector.MaxSeconds);
            }
            if (body.CountdownSeconds is { } countdownSeconds)
            {
                c.CountdownSeconds = Math.Clamp(countdownSeconds, AstralPowerDetector.MinCountdownSeconds, AstralPowerDetector.MaxSeconds);
            }
            if (body.CriticalShutdown is { } critical) c.CriticalShutdown = critical;
        });
        var saved = Settings();
        ServiceLog.Info($"[power-detector] settings: trayAlerts={saved.TrayAlerts} inAppAlerts={saved.InAppAlerts} equalizer={saved.Equalizer} hideReminder={saved.HideEqualizerReminder} overcurrentShutdown={saved.OvercurrentShutdown} after={saved.OvercurrentSeconds}s countdown={saved.CountdownSeconds}s criticalShutdown={saved.CriticalShutdown}");
        return GetState();
    }

    public PowerDetectorStateDto GetState()
    {
        var settings = Settings();
        var limits = AstralPowerDetector.LimitsFor(settings);
        lock (_lock)
        {
            return new PowerDetectorStateDto
            {
                Supported = _cards.Count > 0,
                Gpus = _cards.Select(c => new PowerDetectorGpuDto
                {
                    GpuId = c.GpuId,
                    Name = c.Name,
                    PinAmps = c.PinAmps,
                    ReadAtMs = c.ReadAtMs,
                }).ToList(),
                Level = LevelName(_detector.Current),
                Worst = _detector.Latest is { } worst
                    ? new PowerDetectorPinDto { GpuId = worst.GpuId, Pin = worst.Pin, Amps = worst.Amps }
                    : null,
                OvercurrentAmps = limits.Overcurrent,
                CriticalAmps = limits.Critical,
                PinWarnAmps = limits.PinWarn,
                PinCautionAmps = limits.PinCaution,
                PinLowAmps = AstralPowerDetector.PinLowAmps,
                Countdown = _detector.Running is { } running
                    ? new PowerDetectorCountdownDto
                    {
                        Reason = LevelName(running.Reason),
                        StartedAtMs = running.StartedUtc.ToUnixTimeMilliseconds(),
                        EndsAtMs = running.EndsUtc.ToUnixTimeMilliseconds(),
                        Text = _countdownText,
                    }
                    : null,
                ShuttingDown = _detector.ShutdownIssued,
                HeldOff = _detector.HeldOff,
                Alert = settings.InAppAlerts ? _lastAlert : null,
                Settings = settings,
            };
        }
    }

    private AstralPowerDetectorSettings Settings() => _store.Load().AstralPowerDetector ?? new AstralPowerDetectorSettings();

#if DEV_TOOLS
    // Any shutdown this soon after a simulation is only logged.
    private static readonly TimeSpan SimulationShutdownGuard = TimeSpan.FromMinutes(30);
    private (int Pin, float Amps, DateTimeOffset Until)? _simulation;
    private DateTimeOffset? _simulatedAt;

    /// <summary>
    /// Dev tools: the first card's pin reads <paramref name="amps"/> for
    /// <paramref name="duration"/>, through the real detector, so its alerts,
    /// pop-ups and countdowns can be tried on a card at idle. Nothing it
    /// starts shuts the PC down. A zero duration ends it.
    /// </summary>
    public PowerDetectorStateDto Simulate(int pin, float amps, TimeSpan duration)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _simulation = duration > TimeSpan.Zero ? (Math.Clamp(pin, 1, 6), amps, now + duration) : null;
            _simulatedAt = now;
        }
        ServiceLog.Info($"[power-detector] simulation: pin {pin} at {amps:F1} A for {duration.TotalSeconds:F0} s");
        return GetState();
    }

    private IReadOnlyList<GpuPinCurrents> Simulated(IReadOnlyList<GpuPinCurrents> cards, DateTimeOffset now)
    {
        (int Pin, float Amps, DateTimeOffset Until)? simulation;
        lock (_lock)
        {
            simulation = _simulation is { } s && now < s.Until ? s : null;
        }
        if (simulation is not { } sim || cards.Count == 0)
        {
            return cards;
        }
        var first = cards[0];
        var pins = first.PinAmps.ToArray();
        pins[sim.Pin - 1] = sim.Amps;
        var result = cards.ToList();
        result[0] = first with { PinAmps = pins, ReadAtMs = now.ToUnixTimeMilliseconds() };
        return result;
    }
#endif

    private void Shutdown(string reason)
    {
#if DEV_TOOLS
        // A simulation must never power the PC off, whatever the build. The detector
        // then watches again, as after a shutdown that could not start.
        if (_simulatedAt is { } simulatedAt && _time.GetUtcNow() - simulatedAt < SimulationShutdownGuard)
        {
            ServiceLog.Warn($"[power-detector] simulated readings, not shutting down: {reason}");
            lock (_lock)
            {
                _detector.ShutdownFailed();
            }
            return;
        }
#endif
        bool started;
        try
        {
            started = ShutdownSink(reason);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[power-detector] shutdown threw: {ex.Message}");
            started = false;
        }
        if (started)
        {
            ServiceLog.Warn($"[power-detector] shutdown started: {reason}");
            return;
        }
        ServiceLog.Error($"[power-detector] shutdown could not be started: {reason}");
        lock (_lock)
        {
            _detector.ShutdownFailed();
        }
    }

    private void Raise(PowerDetectorNotice notice)
    {
        try
        {
            NoticeRaised?.Invoke(notice);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[power-detector] notify subscriber failed: {ex.Message}");
        }
    }

    // The pin carrying the most current across every card, and a pin carrying nothing while
    // another pin of its card carries current.
    private static (AstralPowerDetector.Reading? Worst, AstralPowerDetector.Reading? Dead) Worst(
        IReadOnlyList<GpuPinCurrents> cards, DateTimeOffset now)
    {
        AstralPowerDetector.Reading? worst = null;
        AstralPowerDetector.Reading? dead = null;
        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var card in cards)
        {
            // A stale reading, or one no 12V pin could carry, says nothing about the pins now.
            if (nowMs - card.ReadAtMs > FreshFor.TotalMilliseconds
                || card.PinAmps.Any(a => !float.IsFinite(a) || a > AstralPowerDetector.MaxPlausibleAmps))
            {
                continue;
            }
            var loaded = card.PinAmps.Count > 0 && card.PinAmps.Max() >= AstralPowerDetector.DeadPinLoadAmps;
            for (var i = 0; i < card.PinAmps.Count; i++)
            {
                if (worst is null || card.PinAmps[i] > worst.Value.Amps)
                {
                    worst = new AstralPowerDetector.Reading(card.GpuId, card.Name, i + 1, card.PinAmps[i]);
                }
                if (dead is null && loaded && card.PinAmps[i] < AstralPowerDetector.PinLowAmps)
                {
                    dead = new AstralPowerDetector.Reading(card.GpuId, card.Name, i + 1, card.PinAmps[i]);
                }
            }
        }
        return (worst, dead);
    }

    private bool GpuProbeDue()
    {
        var now = _time.GetUtcNow();
        var interval = now - _startedUtc < EarlyProbePeriod ? EarlyProbeInterval : LateProbeInterval;
        if (now - _lastGpuProbe < interval)
        {
            return false;
        }
        _lastGpuProbe = now;
        return true;
    }

    // The card's page in the dashboard, where its Devices entry leads.
    private static string PagePath() => "/system/device/curated-" + Nexus.Service.Devices.Handlers.AstralHandler.HandlerId;

    private PowerDetectorNotice CountdownNotice(AstralPowerDetector.Countdown countdown)
    {
        var left = countdown.EndsUtc - _time.GetUtcNow();
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }
        return new PowerDetectorNotice($"Shutting down in {Duration(left)}", _countdownText, PagePath(), CancelsShutdown: true);
    }

    internal static string LevelName(AstralPowerDetector.Level level) => level switch
    {
        AstralPowerDetector.Level.Critical => "critical",
        AstralPowerDetector.Level.Overcurrent => "overcurrent",
        AstralPowerDetector.Level.Warning => "warning",
        _ => "normal",
    };

    // On a line of its own, so it reads apart from what happened.
    private const string EqualizerReminder = "\nUsing a ROG Equalizer cable? Turn on Equalizer mode in Nexus.";

    internal static (string Title, string Text) AlertWording(
        AstralPowerDetector.Alert alert, AstralPowerDetectorSettings settings, float? highestAmps = null)
    {
        var r = alert.Reading;
        if (alert.Dead)
        {
            var others = highestAmps is { } high ? $" while the others carry up to {high:0.0} A" : "";
            return ("A GPU power pin carries no current",
                $"Pin {r.Pin} of the {ShortName(r.GpuName)} power connector carries no current{others}. " +
                "The cable may not be pushed fully into the card, or a wire or contact is damaged, and the other pins carry its share.");
        }
        if (alert.Level == AstralPowerDetector.Level.Warning)
        {
            var warning = $"Pin {r.Pin} of the {ShortName(r.GpuName)} power connector is carrying {r.Amps:0.0} A, over the {alert.LimitAmps:0.#} A a pin is rated for. Check that the cable is pushed fully into the card.";
            if (!settings.Equalizer && !settings.HideEqualizerReminder)
            {
                warning += EqualizerReminder;
            }
            return ("A GPU power pin is over its rating", warning);
        }
        var critical = alert.Level == AstralPowerDetector.Level.Critical;
        var title = critical ? "Critical current in the GPU power cable" : "High current in the GPU power cable";
        var text = $"Pin {r.Pin} of the {ShortName(r.GpuName)} power connector is carrying {r.Amps:0.0} A, over its {alert.LimitAmps:0.#} A limit.";
        if (critical && settings.CriticalShutdown)
        {
            text += " Nexus will shut the PC down in 10 seconds.";
        }
        else if (!critical && settings.OvercurrentShutdown)
        {
            text += $" Nexus will shut the PC down if it stays over for {Duration(AstralPowerDetector.OvercurrentTime(settings))}.";
        }
        else
        {
            text += " Check that the cable is pushed fully into the card.";
        }
        if (!settings.Equalizer && !settings.HideEqualizerReminder)
        {
            text += EqualizerReminder;
        }
        return (title, text);
    }

    private static string CountdownText(AstralPowerDetector.Countdown countdown, AstralPowerDetector.Reading? worst, AstralPowerDetectorSettings settings)
    {
        var limits = AstralPowerDetector.LimitsFor(settings);
        var pin = worst is { } w ? $"Pin {w.Pin} of the {ShortName(w.GpuName)} power connector" : "A pin of the GPU power connector";
        var why = countdown.Reason == AstralPowerDetector.Level.Critical
            ? $"{pin} reached {(worst?.Amps ?? limits.Critical):0.0} A."
            : $"{pin} stayed over {limits.Overcurrent:0.#} A for {Duration(AstralPowerDetector.OvercurrentTime(settings))}.";
        return $"{why} Save your work, or cancel the shutdown.";
    }

    private static string ShortName(string name) =>
        name.StartsWith("ASUS ", StringComparison.Ordinal) ? name["ASUS ".Length..] : name;

    internal static string Duration(TimeSpan time)
    {
        var seconds = (int)Math.Ceiling(time.TotalSeconds);
        if (seconds < 60 || seconds % 60 != 0)
        {
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }
        var minutes = seconds / 60;
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
