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
public sealed class BulkPanelStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport, IBrightnessPanelTransport
{
    private const long BrightnessTtlMs = 500;

    private readonly BulkPanelHub _hub;
    private readonly byte[] _frame;
    private int _filled;
    private volatile bool _disposed;
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

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

    public BulkPanelStreamTransport(BulkPanelHub hub, string serial)
    {
        _hub = hub;
        Serial = serial;
        // Fixed at construction from the geometry the driver negotiated; discovery only
        // reports a panel once that is known.
        _frame = new byte[Math.Max(0, hub.FrameBytes)];
    }

    public bool IsOpen => !_disposed && _hub.IsConnected;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

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
            if (_hub.SendFrame(_orientation.Apply(_frame, _hub.Width, _hub.Height)))
            {
                Volatile.Write(ref _lastSendMs, Environment.TickCount64);
                _dropLogged = false;
                lock (_brightnessLock)
                {
                    TryApplyBrightnessLocked();
                }
            }
            else if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[{_hub.Driver.HandlerId}] frame rejected; retrying on the next frame");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _keepalive?.Dispose();
        _keepalive = null;
    }
}
