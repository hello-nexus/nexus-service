using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Lighting.IdleDim;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// macOS source for idle dim, polled once a second and only while armed. The
/// input watch compares the HID system's seconds-since-last-input (the same
/// counter MacLockInputWatch samples) with the threshold; the display watch
/// samples CGDisplayIsAsleep for the main display. Polling a public CoreGraphics
/// call needs no run loop or notification port, and one second is far finer
/// than the minutes-scale timeouts it serves. Neither call carries key
/// identity: only elapsed idle time and a display power flag.
/// </summary>
internal sealed class MacIdleDimWatch : IIdleDimWatch, IDisposable
{
    private const int PollPeriodMs = 1000;
    private const int HidSystemState = 1;
    private const uint AnyInputEventType = uint.MaxValue;

    private readonly IdleDimController _controller;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _threshold;
    private bool _display;
    private bool? _reportedIdle;
    private bool? _reportedOff;

    public MacIdleDimWatch(IdleDimController controller) => _controller = controller;

    public void SetInputWatch(int thresholdSeconds)
    {
        lock (_gate)
        {
            _threshold = thresholdSeconds;
            _reportedIdle = null;
            UpdateTimerLocked();
        }
        Poll();
    }

    public void SetDisplayWatch(bool armed)
    {
        lock (_gate)
        {
            _display = armed;
            _reportedOff = null;
            UpdateTimerLocked();
        }
        Poll();
    }

    // Caller holds _gate.
    private void UpdateTimerLocked()
    {
        if (_threshold == 0 && !_display)
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }
        _timer ??= new Timer(_ => Poll(), null, PollPeriodMs, PollPeriodMs);
    }

    private void Poll()
    {
        bool? idle = null;
        bool? off = null;
        lock (_gate)
        {
            if (_timer is null)
            {
                return;
            }
            if (_threshold != 0)
            {
                var seconds = IdleSeconds();
                if (seconds is { } s)
                {
                    var now = s >= _threshold;
                    if (_reportedIdle != now)
                    {
                        _reportedIdle = now;
                        idle = now;
                    }
                }
            }
            if (_display)
            {
                var now = DisplayAsleep();
                if (_reportedOff != now)
                {
                    _reportedOff = now;
                    off = now;
                }
            }
        }
        try
        {
            if (idle is { } i)
            {
                _controller.OnInputIdle(i);
            }
            if (off is { } o)
            {
                _controller.OnDisplayOff(o);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-idle-dim] handler failed: {ex.Message}");
        }
    }

    private static double? IdleSeconds()
    {
        try
        {
            var s = CGEventSourceSecondsSinceLastEventType(HidSystemState, AnyInputEventType);
            return double.IsFinite(s) && s >= 0 ? s : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool DisplayAsleep()
    {
        try { return CGDisplayIsAsleep(CGMainDisplayID()) != 0; }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _threshold = 0;
            _display = false;
            UpdateTimerLocked();
        }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGDisplayIsAsleep(uint display);
}
