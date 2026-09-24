using System;
using System.IO;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA stream into frames on a bulk-pipe cooler LCD.
/// Buffers to exactly one frame before handing it to the driver, which owns whatever
/// encoding its panel wants.
/// </summary>
public sealed class BulkPanelStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport, IBrightnessPanelTransport, ISecondaryMonitorTransport
{
    private const long BrightnessTtlMs = 500;

    private readonly BulkPanelHub _hub;
    private readonly byte[] _frame;
    private int _filled;
    private volatile bool _disposed;
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();
    private Func<(bool Flip180, bool Mirror)> _orientationSource = () => (false, false);
    private readonly object _pushLock = new();

    // A static page produces no captured frames, and some panels fall back to their own
    // screen after a few seconds without one; the timer re-sends the last frame meanwhile.
    private Timer? _keepalive;
    private long _lastSendMs;
    private int _keepaliveBusy;

    private readonly object _brightnessLock = new();
    private Func<int?>? _brightness;
    private int _brightnessApplied = -1;
    private long _brightnessNextReadMs;
    private bool _brightnessFaultLogged;

    private readonly IVirtualMonitorHost? _monitors;
    private readonly object _monitorLock = new();
    // Held across a whole apply, including stopping the old monitor, so an "on" that races an
    // "off" starts only after the previous monitor is gone.
    private readonly object _applyLock = new();
    private Func<bool>? _wantsMonitor;
    private SecondaryMonitorFeed? _monitor;
    private long _monitorStartedMs;
    // A monitor that failed to come up or died is retried at this pace while the setting is on.
    private const long MonitorRetryMs = 30_000;

    public BulkPanelStreamTransport(BulkPanelHub hub, string serial, IVirtualMonitorHost? monitors = null)
    {
        _hub = hub;
        Serial = serial;
        _monitors = hub.Driver.SupportsSecondaryMonitor ? monitors : null;
        // Fixed at construction from the geometry the driver negotiated; discovery only
        // reports a panel once that is known.
        _frame = new byte[Math.Max(0, hub.FrameBytes)];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source)
    {
        _orientationSource = source;
        _orientation.Bind(source);
    }

    public event Action? SecondaryMonitorStateChanged;

    public string? SecondaryMonitorState => Volatile.Read(ref _monitor)?.State;

    /// <summary>Ignored for a panel whose driver cannot show a monitor.</summary>
    public void BindSecondaryMonitor(Func<bool> source)
    {
        if (_monitors is not null)
        {
            _wantsMonitor = source;
        }
    }

    public void ApplySecondaryMonitor()
    {
        if (_monitors is not { } monitors)
        {
            return;
        }
        lock (_applyLock)
        {
            ApplySecondaryMonitorLocked(monitors);
        }
    }

    private void ApplySecondaryMonitorLocked(IVirtualMonitorHost monitors)
    {
        bool want = !_disposed && _hub.IsConnected && SafeWantsMonitor();
        SecondaryMonitorFeed? stopped = null;
        bool changed = false;
        lock (_monitorLock)
        {
            // Dispose may have run since want was read; a feed started now would never be stopped.
            want &= !_disposed;
            if (want && _monitor is { Finished: true } finished
                && Environment.TickCount64 - _monitorStartedMs >= MonitorRetryMs)
            {
                // Removed on this tick and restarted on the next: both would use the one device.
                stopped = finished;
                Volatile.Write(ref _monitor, null);
            }
            else if (want && _monitor is null)
            {
                var feed = new SecondaryMonitorFeed(monitors, _hub, PushFrame, () => _orientationSource(), () => SecondaryMonitorStateChanged?.Invoke());
                Volatile.Write(ref _monitor, feed);
                _monitorStartedMs = Environment.TickCount64;
                feed.Start();
                changed = true;
            }
            else if (!want && _monitor is not null)
            {
                stopped = _monitor;
                Volatile.Write(ref _monitor, null);
                changed = true;
            }
        }
        stopped?.Dispose();
        if (changed)
        {
            ServiceLog.Info($"[{_hub.Driver.HandlerId}] secondary monitor {(want ? "on" : "off")}");
            SecondaryMonitorStateChanged?.Invoke();
        }
    }

    private bool SafeWantsMonitor()
    {
        try { return _wantsMonitor?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>Ignored for a panel with no backlight command.</summary>
    public void BindBrightness(Func<int?> source)
    {
        if (!_hub.Driver.SupportsBrightness)
        {
            return;
        }
        lock (_brightnessLock)
        {
            _brightness = source;
            _brightnessApplied = -1;
            _brightnessNextReadMs = 0;
        }
    }

    public void ApplyBrightness()
    {
        lock (_brightnessLock)
        {
            _brightnessNextReadMs = 0;
            TryApplyBrightnessLocked();
        }
    }

    private void TryApplyBrightnessLocked()
    {
        long nowMs = Environment.TickCount64;
        if (_brightness is null || nowMs < _brightnessNextReadMs)
        {
            return;
        }
        _brightnessNextReadMs = nowMs + BrightnessTtlMs;
        int? wanted;
        try { wanted = _brightness(); }
        catch (Exception ex)
        {
            if (!_brightnessFaultLogged)
            {
                _brightnessFaultLogged = true;
                ServiceLog.Warn($"[{_hub.Driver.HandlerId}] backlight source threw: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
        // No record value means the panel keeps what it powered up with.
        if (wanted is not int percent || percent == _brightnessApplied)
        {
            return;
        }
        if (_hub.SetBrightness(percent))
        {
            _brightnessApplied = percent;
            ServiceLog.Info($"[{_hub.Driver.HandlerId}] backlight {percent}%");
        }
    }

    public string Serial { get; }

    public void Open()
    {
        if (!_hub.IsConnected)
        {
            throw new IOException($"{_hub.Driver.Name} not connected");
        }
        if (_frame.Length <= 0)
        {
            // A zero-length frame would make Write spin forever consuming nothing.
            throw new IOException($"{_hub.Driver.Name} panel size unknown");
        }
        _filled = 0;
        Volatile.Write(ref _lastSendMs, Environment.TickCount64);
        int keepaliveMs = _hub.Driver.KeepaliveMs;
        if (keepaliveMs > 0)
        {
            _keepalive = new Timer(_ => Keepalive(keepaliveMs), null, keepaliveMs / 2, keepaliveMs / 2);
        }
    }

    private void Keepalive(int keepaliveMs)
    {
        // Timer ticks keep coming while a stalled resend holds the hub; run one at a time.
        if (Interlocked.Exchange(ref _keepaliveBusy, 1) == 1)
        {
            return;
        }
        try
        {
            ApplySecondaryMonitor();
            if (_disposed || Environment.TickCount64 - Volatile.Read(ref _lastSendMs) < keepaliveMs)
            {
                return;
            }
            if (_hub.Resend())
            {
                Volatile.Write(ref _lastSendMs, Environment.TickCount64);
            }
        }
        finally
        {
            Volatile.Write(ref _keepaliveBusy, 0);
        }
    }

    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frame.Length <= 0)
        {
            return;
        }
        while (!payload.IsEmpty)
        {
            int take = Math.Min(_frame.Length - _filled, payload.Length);
            payload[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            payload = payload[take..];

            if (_filled < _frame.Length)
            {
                continue;
            }
            _filled = 0;
            // The desktop owns the glass while the monitor is on; a frame the overlay was
            // still rendering when it switched is dropped.
            if (Volatile.Read(ref _monitor) is null)
            {
                PushFrame(_frame);
            }
        }
    }

    private bool PushFrame(byte[] frame)
    {
        lock (_pushLock)
        {
            if (_hub.SendFrame(_orientation.Apply(frame, _hub.Width, _hub.Height)))
            {
                Volatile.Write(ref _lastSendMs, Environment.TickCount64);
                _dropLogged = false;
                lock (_brightnessLock)
                {
                    TryApplyBrightnessLocked();
                }
                return true;
            }
            if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[{_hub.Driver.HandlerId}] frame rejected; retrying on the next frame");
            }
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _keepalive?.Dispose();
        _keepalive = null;
        SecondaryMonitorFeed? monitor;
        lock (_monitorLock)
        {
            monitor = _monitor;
            Volatile.Write(ref _monitor, null);
        }
        monitor?.Dispose();
    }
}
