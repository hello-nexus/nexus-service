using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Lock-screen wake-on-input for macOS, the in-process counterpart of the
/// Windows helper's LockInputPoller: while armed, samples the HID system's
/// seconds-since-last-input every PollPeriodMs and reports "input happened"
/// whenever the counter restarts. Carries no key identity, only that input
/// occurred. Arming re-baselines first and ignores ArmGraceMs, so the gesture
/// that locked the machine never wakes what it just darkened.
/// </summary>
internal sealed class MacLockInputWatch : IDisposable
{
    private const int PollPeriodMs = 250;
    private const int EmitThrottleMs = 1000;
    private const int ArmGraceMs = 500;
    /// <summary>Two samples of the same event differ by clock rounding only; a real new event moves the time by far more.</summary>
    private const int InputSlackMs = 50;
    private const int HidSystemState = 1;
    private const uint AnyInputEventType = uint.MaxValue;

    private readonly Action _onInput;
    private readonly object _lock = new();
    private Timer? _timer;
    private long _lastInputAtMs;
    private long _armedAtMs;
    private long _lastEmitMs;

    public MacLockInputWatch(Action onInput)
    {
        _onInput = onInput;
    }

    public void Set(bool enabled)
    {
        lock (_lock)
        {
            if (!enabled)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
            if (_timer is not null)
            {
                return;
            }
            _lastInputAtMs = LastInputAtMs();
            _armedAtMs = Environment.TickCount64;
            _lastEmitMs = 0;
            _timer = new Timer(_ => Poll(), null, PollPeriodMs, PollPeriodMs);
        }
    }

    private void Poll()
    {
        bool emit;
        lock (_lock)
        {
            if (_timer is null)
            {
                return;
            }
            // Absolute last-input time, as GetLastInputInfo on Windows: immune to timer jitter.
            var inputAtMs = LastInputAtMs();
            var sawInput = inputAtMs > _lastInputAtMs + InputSlackMs;
            if (sawInput)
            {
                _lastInputAtMs = inputAtMs;
            }
            var now = Environment.TickCount64;
            emit = sawInput && now - _armedAtMs >= ArmGraceMs && now - _lastEmitMs >= EmitThrottleMs;
            if (emit)
            {
                _lastEmitMs = now;
            }
        }
        if (emit)
        {
            try { _onInput(); }
            catch (Exception ex) { Console.Error.WriteLine($"[mac-lock-input] input handler failed: {ex.Message}"); }
        }
    }

    private static long LastInputAtMs()
    {
        try { return Environment.TickCount64 - (long)(CGEventSourceSecondsSinceLastEventType(HidSystemState, AnyInputEventType) * 1000.0); }
        catch { return long.MinValue / 2; }
    }

    public void Dispose() => Set(false);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);
}
