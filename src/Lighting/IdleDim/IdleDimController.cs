using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>What the platform provides: an idle-time watch and a display-state watch. Both report back through the controller.</summary>
public interface IIdleDimWatch
{
    /// <summary>Watch input idle time against <paramref name="thresholdSeconds"/> and report each idle/active transition to <see cref="IdleDimController.OnInputIdle"/>. 0 stops the watch.</summary>
    void SetInputWatch(int thresholdSeconds);

    /// <summary>Watch the display's on/off state and report each change to <see cref="IdleDimController.OnDisplayOff"/>.</summary>
    void SetDisplayWatch(bool armed);
}

/// <summary>
/// Dims lighting to a user-set level while the PC is idle. Owns the idle state
/// machine and nothing platform-specific: the platform's <see cref="IIdleDimWatch"/>
/// is armed only while idle dim is enabled (fixed timeout arms the input watch,
/// "when my screen turns off" arms the display watch), and reports transitions
/// back. The result is a ramp (<see cref="IdleDimRamp"/>) that
/// <see cref="MasterBrightness"/> reads on the frame path.
/// </summary>
public sealed class IdleDimController
{
    /// <summary>Smallest fixed timeout; 0 is "when my screen turns off".</summary>
    public const int MinTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 86400;

    /// <summary>Where there is no display-state source (Linux), a stored 0 means this much input idle; the web shows the same.</summary>
    public const int NoScreenOffFallbackSeconds = 600;

    private readonly IConfigStore _store;
    private readonly IdleDimRamp _ramp;
    private readonly bool _screenOffSupported;
    private readonly object _gate = new();

    private IIdleDimWatch? _watch;
    private bool _started;
    private int _armedThreshold;
    private bool _armedDisplay;
    private bool _inputIdle;
    private bool _displayOff;
    private bool _dimmed;
    private float _target = 1f;

    public IdleDimController(IConfigStore store, IdleDimRamp ramp, bool? screenOffSupported = null)
    {
        _store = store;
        _ramp = ramp;
        _screenOffSupported = screenOffSupported ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    /// <summary>True when "when my screen turns off" has a display-state source (Windows, macOS).</summary>
    public bool ScreenOffSupported => _screenOffSupported;

    /// <summary>Set by the Linux watch once an idle-time source answered. Always true elsewhere.</summary>
    public bool InputSourceAvailable { get; set; } = !OperatingSystem.IsLinux();

    /// <summary>True when at least the fixed-time options can work on this machine.</summary>
    public bool Supported => InputSourceAvailable;

    /// <summary>Set by the Linux watch: asks it to look for an idle-time source again (throttled on its side).</summary>
    public Action? Reprobe { get; set; }

    /// <summary>Called by a reader that found <see cref="Supported"/> false, so a late login or bus start can recover.</summary>
    public void RequestReprobe() => Reprobe?.Invoke();

    /// <summary>Bound by the platform bootstrap; setting it re-applies the stored settings.</summary>
    public IIdleDimWatch? Watch
    {
        get => _watch;
        set
        {
            lock (_gate)
            {
                _watch = value;
                if (value is null)
                {
                    _armedThreshold = 0;
                    _armedDisplay = false;
                    _inputIdle = false;
                    _displayOff = false;
                }
            }
            Reconcile();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
        }
        _store.OnChanged += Reconcile;
        Reconcile();
    }

    public void Stop()
    {
        _store.OnChanged -= Reconcile;
        lock (_gate)
        {
            _started = false;
        }
        // Reconcile with the controller stopped disarms both watches and releases a dim.
        Reconcile();
    }

    /// <summary>Platform report: input idle crossed the armed threshold (true) or input came back (false).</summary>
    public void OnInputIdle(bool idle)
    {
        lock (_gate)
        {
            if (_armedThreshold == 0)
            {
                return;
            }
            _inputIdle = idle;
        }
        Reconcile();
    }

    /// <summary>Platform report: the display turned off (true) or on (false). Dimmed counts as on.</summary>
    public void OnDisplayOff(bool off)
    {
        lock (_gate)
        {
            if (!_armedDisplay)
            {
                return;
            }
            _displayOff = off;
        }
        Reconcile();
    }

    private void Reconcile()
    {
        lock (_gate)
        {
            // Read inside the gate: two concurrent reconciles must apply in the order they read.
            var settings = _store.Load().Lighting.IdleDim ?? new IdleDimSettings();
            var enabled = settings.Enabled && _watch is not null && _started;
            var timeout = settings.TimeoutSeconds == 0 && !_screenOffSupported
                ? NoScreenOffFallbackSeconds
                : settings.TimeoutSeconds;
            var fixedMode = timeout >= MinTimeoutSeconds;
            var screenMode = timeout == 0;
            var wantThreshold = enabled && fixedMode ? Math.Min(timeout, MaxTimeoutSeconds) : 0;
            var wantDisplay = enabled && screenMode;

            if (wantThreshold != _armedThreshold)
            {
                _armedThreshold = wantThreshold;
                _inputIdle = false;
                _watch?.SetInputWatch(wantThreshold);
            }
            if (wantDisplay != _armedDisplay)
            {
                _armedDisplay = wantDisplay;
                _displayOff = false;
                _watch?.SetDisplayWatch(wantDisplay);
            }

            var dim = (_armedThreshold != 0 && _inputIdle) || (_armedDisplay && _displayOff);
            if (!dim)
            {
                Release();
                return;
            }
            var level = Math.Clamp(settings.Level, 0, 100) / 100f;
            if (!_dimmed || level != _target)
            {
                _dimmed = true;
                _target = level;
                _ramp.RampTo(level, SleepBlackoutCoordinator.LockFadeDuration);
            }
        }
    }

    // Caller holds _gate.
    private void Release()
    {
        if (!_dimmed)
        {
            return;
        }
        _dimmed = false;
        _target = 1f;
        _ramp.RampTo(1f, SleepBlackoutCoordinator.UnlockFadeDuration);
    }
}
