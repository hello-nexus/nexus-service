using System;
using System.IO;
using System.Threading;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.SteelSeries;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink that turns the overlay's raw BGRA frames into Apex Pro OLED pushes. Frames are
/// fixed-size, so the stream is cut into whole frames, thresholded to one bit, packed for the
/// attached model and sent as one feature report.
///
/// The renderers deliver nothing for a static page, so a timer re-sends the last frame: the
/// firmware shows its own screen when it stops hearing from the host. The same timer hands the
/// screen to the firmware while the session is locked and repaints it on unlock. Every access
/// to the handle is under one lock, and disposing never closes it under a write in flight.
/// </summary>
public sealed class ApexOledStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    /// <summary>Interval after which an unchanged frame is sent again.</summary>
    internal const long KeepAliveMs = 5000;

    /// <summary>How often the timer looks at the lock and the keepalive.</summary>
    internal const int TickMs = 500;

    private readonly object _sync = new();
    private readonly IHidEnumerator _hid;
    private readonly string _serial;
    private readonly LcdLockState? _lock;
    private readonly AttachBackoff? _backoff;
    private readonly Func<long> _clock;
    private readonly int _tickMs;
    private readonly int _disposeWaitMs;
    private readonly PanelOrientationFilter _orientation = new();
    private readonly byte[] _frame = new byte[ApexOledMono.FrameLength];
    private readonly bool[] _lit = new bool[ApexOledProtocol.Width * ApexOledProtocol.Height];
    private readonly ApexOledModel _model;
    private IHidDevice? _device;
    private Timer? _timer;
    private byte[]? _latest;
    private byte[]? _shown;
    private long _lastSentMs;
    private long _openedMs;
    private int _filled;
    private bool _handedBack;
    private bool _handBackWarned;
    private volatile bool _failed;
    private volatile bool _disposed;

    public ApexOledStreamTransport(
        IHidEnumerator hid,
        ApexOledModel model,
        string serial,
        LcdLockState? lockState = null,
        Func<long>? clock = null,
        AttachBackoff? backoff = null,
        int tickMs = TickMs,
        int disposeWaitMs = 1000)
    {
        _hid = hid;
        _model = model;
        _serial = serial;
        _lock = lockState;
        _backoff = backoff;
        _clock = clock ?? (() => Environment.TickCount64);
        _tickMs = tickMs;
        _disposeWaitMs = disposeWaitMs;
    }

    public string Serial => _serial;

    public bool IsOpen => !_disposed && !_failed && _device is not null;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
            _device?.Dispose();
            _device = null;
            _failed = false;
            _filled = 0;
            _latest = null;
            _shown = null;
            _handedBack = false;
            _handBackWarned = false;

            var screen = ApexOledScreens.Find(_hid).Find(s => ApexOledPanelDiscovery.PanelSerial(s) == _serial)
                ?? throw new IOException($"{_model.Name}: screen interface not found (usage page 0x{ApexOledProtocol.ControlUsagePage:X4}, feature report of at least {ApexOledScreens.MinFeatureLength} bytes)");
            _device = _hid.Open(screen.Info.Path) ?? throw new IOException($"{_model.Name}: could not open {screen.Info.Path}");
            _openedMs = _clock();
        }
    }

    /// <summary>Starts the timer that keeps the screen painted while no frames arrive.</summary>
    public void StartPlayer()
    {
        lock (_sync)
        {
            if (_disposed || _device is null)
            {
                return;
            }
            _timer ??= new Timer(_ => Tick(), null, _tickMs, _tickMs);
        }
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!payload.IsEmpty)
        {
            var take = Math.Min(_frame.Length - _filled, payload.Length);
            payload[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            payload = payload[take..];
            if (_filled < _frame.Length)
            {
                continue;
            }
            _filled = 0;
            lock (_sync)
            {
                PushFrameLocked();
            }
        }
    }

    private void PushFrameLocked()
    {
        if (_disposed)
        {
            return;
        }
        var device = _device ?? throw new IOException($"{_model.Name}: screen not open");
        if (_lock is { IsLocked: true })
        {
            HandBackLocked(device);
            return;
        }
        var oriented = _orientation.Apply(_frame, ApexOledProtocol.Width, ApexOledProtocol.Height);
        if (!ApexOledMono.Convert(oriented, _lit))
        {
            return;
        }
        var packed = ApexOledProtocol.Pack(_lit, _model.Layout);
        _latest = packed;
        if (!_handedBack && _shown is not null && packed.AsSpan().SequenceEqual(_shown) && _clock() - _lastSentMs < KeepAliveMs)
        {
            return;
        }
        if (!SendLocked(device, packed))
        {
            throw new IOException($"{_model.Name}: the keyboard refused a frame");
        }
    }

    // False when the keyboard refused the report; the transport is then faulted.
    private bool SendLocked(IHidDevice device, byte[] packed)
    {
        var now = _clock();
        var accepted = device.SetFeature(ApexOledProtocol.FrameReport(packed));
        if (accepted)
        {
            if (_handedBack)
            {
                _handedBack = false;
                _handBackWarned = false;
                ServiceLog.Info($"[steelseries-apex-oled] {_model.Name}: unlocked, showing the panel again");
            }
            _shown = packed;
            _lastSentMs = now;
            if (_backoff is { Failures: > 0 } && now - _openedMs >= AttachBackoff.HealthyAfterMs)
            {
                _backoff.NoteHealthy();
            }
        }
        else
        {
            _failed = true;
            if ((_backoff?.NoteFailure() ?? 0) > 0)
            {
                ServiceLog.Warn($"[steelseries-apex-oled] {_model.Name}: the keyboard refused a frame; retrying with a growing delay");
            }
        }
        if (_disposed)
        {
            // Dispose could not wait for this write; the handle is released here instead.
            TeardownLocked();
        }
        return accepted;
    }

    private void HandBackLocked(IHidDevice device)
    {
        if (_handedBack)
        {
            return;
        }
        _shown = null;
        if (device.Write(ApexOledProtocol.HandBackReport()))
        {
            _handedBack = true;
            ServiceLog.Info($"[steelseries-apex-oled] {_model.Name}: session locked, the keyboard shows its own screen");
        }
        else if (!_handBackWarned)
        {
            _handBackWarned = true;
            ServiceLog.Warn($"[steelseries-apex-oled] {_model.Name}: the keyboard did not accept the hand back; retrying while locked");
        }
    }

    // Timer: carries the lock, the unlock repaint and the keepalive when no frame is arriving.
    private void Tick()
    {
        if (_disposed || !Monitor.TryEnter(_sync))
        {
            return;
        }
        try
        {
            if (_disposed || _failed || _device is not { } device)
            {
                return;
            }
            if (_lock is { IsLocked: true })
            {
                HandBackLocked(device);
                return;
            }
            if (_latest is { } latest && (_handedBack || _shown is null || _clock() - _lastSentMs >= KeepAliveMs))
            {
                SendLocked(device, latest);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            ServiceLog.Warn($"[steelseries-apex-oled] repaint failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    // Caller holds _sync. Gives the screen back unless it already is, then releases the handle.
    private void TeardownLocked()
    {
        var device = _device;
        _device = null;
        if (device is null)
        {
            return;
        }
        if (!_handedBack && !_failed)
        {
            try
            {
                if (!device.Write(ApexOledProtocol.HandBackReport()))
                {
                    ServiceLog.Warn($"[steelseries-apex-oled] {_model.Name}: the keyboard did not accept the hand back; its screen returns when it times out");
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[steelseries-apex-oled] hand back failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        try { device.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        if (!Monitor.TryEnter(_sync, _disposeWaitMs))
        {
            // A write is stuck in the keyboard. It sees the disposed flag when it returns and
            // releases the handle itself, so the handle is never closed under it.
            return;
        }
        try
        {
            TeardownLocked();
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }
}
