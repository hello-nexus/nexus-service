using System;
using System.IO;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.SteelSeries;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink that turns the overlay's raw BGRA frames into Apex Pro OLED pushes. Frames are
/// fixed-size, so the stream is cut into whole frames, thresholded to one bit, packed for the
/// attached model and sent as one feature report.
///
/// The firmware shows its own screen when it stops hearing from the host, so an unchanged
/// frame is skipped but re-sent after <see cref="KeepAliveMs"/>. The screen goes back to the
/// firmware while the session is locked and when this transport is disposed.
/// </summary>
public sealed class ApexOledStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    /// <summary>Interval after which an unchanged frame is sent again.</summary>
    internal const long KeepAliveMs = 5000;

    private readonly IHidEnumerator _hid;
    private readonly ApexOledModel _model;
    private readonly string _serial;
    private readonly LcdLockState? _lock;
    private readonly Func<long> _clock;
    private readonly PanelOrientationFilter _orientation = new();
    private readonly byte[] _frame = new byte[ApexOledMono.FrameLength];
    private readonly bool[] _lit = new bool[ApexOledProtocol.Width * ApexOledProtocol.Height];
    private IHidDevice? _device;
    private byte[]? _lastPacked;
    private long _lastSentMs;
    private int _filled;
    private bool _handedBack;
    private bool _failed;
    private bool _disposed;

    public ApexOledStreamTransport(IHidEnumerator hid, ApexOledModel model, string serial, LcdLockState? lockState = null, Func<long>? clock = null)
    {
        _hid = hid;
        _model = model;
        _serial = serial;
        _lock = lockState;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string Serial => _serial;

    public bool IsOpen => !_disposed && !_failed && _device is not null;

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device?.Dispose();
        _device = null;
        _failed = false;
        _filled = 0;
        _lastPacked = null;
        _handedBack = false;

        string? path = null;
        foreach (var info in _hid.Find(ApexOledProtocol.VendorId, _model.ProductId))
        {
            if (info.UsagePage == ApexOledProtocol.ControlUsagePage)
            {
                path = info.Path;
                break;
            }
        }
        if (path is null)
        {
            throw new IOException($"{_model.Name}: no screen interface (usage page 0x{ApexOledProtocol.ControlUsagePage:X4})");
        }
        _device = _hid.Open(path) ?? throw new IOException($"{_model.Name}: could not open {path}");
    }

    /// <summary>The screen has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
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
            PushFrame();
        }
    }

    private void PushFrame()
    {
        var device = _device ?? throw new IOException($"{_model.Name}: screen not open");
        if (_lock is { IsLocked: true })
        {
            HandBack(device);
            return;
        }
        var oriented = _orientation.Apply(_frame, ApexOledProtocol.Width, ApexOledProtocol.Height);
        if (!ApexOledMono.Convert(oriented, _lit))
        {
            return;
        }
        var packed = ApexOledProtocol.Pack(_lit, _model.Layout);
        var now = _clock();
        if (!_handedBack && _lastPacked is not null && packed.AsSpan().SequenceEqual(_lastPacked) && now - _lastSentMs < KeepAliveMs)
        {
            return;
        }
        if (!device.SetFeature(ApexOledProtocol.FrameReport(packed)))
        {
            _failed = true;
            throw new IOException($"{_model.Name}: the keyboard refused a frame");
        }
        if (_handedBack)
        {
            _handedBack = false;
            ServiceLog.Info($"[steelseries-apex-oled] {_model.Name}: unlocked, showing the panel again");
        }
        _lastPacked = packed;
        _lastSentMs = now;
    }

    private void HandBack(IHidDevice device)
    {
        if (_handedBack)
        {
            return;
        }
        _handedBack = true;
        _lastPacked = null;
        device.Write(ApexOledProtocol.HandBackReport());
        ServiceLog.Info($"[steelseries-apex-oled] {_model.Name}: session locked, the keyboard shows its own screen");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var device = _device;
        _device = null;
        if (device is null)
        {
            return;
        }
        if (!_handedBack && !_failed)
        {
            try { device.Write(ApexOledProtocol.HandBackReport()); }
            catch (Exception ex) { ServiceLog.Warn($"[steelseries-apex-oled] hand back failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        try { device.Dispose(); } catch { }
    }
}
