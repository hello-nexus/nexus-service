using System;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Holds the open pipes for one bulk-pipe panel and serialises frame pushes onto its
/// driver. One instance per <see cref="IBulkPanelDriver"/>; the worker attaches and
/// detaches it as the panel comes and goes.
/// </summary>
public sealed class BulkPanelHub : IDisposable
{
    private readonly object _lock = new();
    private readonly IBulkPanelDriver _driver;

    private IBulkUsbPipe? _pipe;
    private IHidDevice? _hid;
    private volatile bool _attached;
    private int _width;
    private int _height;
    private bool _disposed;

    public BulkPanelHub(IBulkPanelDriver driver)
    {
        _driver = driver;
    }

    public IBulkPanelDriver Driver => _driver;

    public bool IsConnected => _attached;

    /// <summary>Panel width the driver negotiated, or 0 while nothing is attached.</summary>
    public int Width { get { lock (_lock) { return _width; } } }

    public int Height { get { lock (_lock) { return _height; } } }

    /// <summary>Bytes in one captured BGRA frame at the attached panel's size.</summary>
    public int FrameBytes { get { lock (_lock) { return _width * _height * 4; } } }

    public string? Serial { get { lock (_lock) { return _hid?.Serial; } } }

    /// <summary>
    /// Takes ownership of the pipes and runs the driver's handshake. Returns false when the
    /// panel is not usable, and disposes whatever it was handed so the caller never has to.
    /// </summary>
    public bool Attach(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        lock (_lock)
        {
            DetachLocked();
            _pipe = pipe;
            _hid = hid;

            var geometry = _driver.Connect(pipe, hid);
            if (geometry is null)
            {
                DetachLocked();
                return false;
            }
            (_width, _height) = geometry.Value;
            _attached = _width > 0 && _height > 0;
            if (!_attached)
            {
                DetachLocked();
            }
            return _attached;
        }
    }

    public bool SendFrame(ReadOnlySpan<byte> bgra)
    {
        lock (_lock)
        {
            if (!_attached || _pipe is null)
            {
                return false;
            }
            return _driver.SendFrame(_pipe, _hid, bgra);
        }
    }

    /// <summary>Reads the panel's IN pipe without holding up frame pushes. -1 when nothing is attached.</summary>
    public int ReadInput(Span<byte> buffer, int timeoutMs)
    {
        IBulkUsbPipe? pipe;
        lock (_lock)
        {
            pipe = _attached ? _pipe : null;
        }
        return pipe?.Read(buffer, timeoutMs) ?? -1;
    }

    public bool Resend()
    {
        lock (_lock)
        {
            return _attached && _pipe is not null && _driver.Resend(_pipe, _hid);
        }
    }

    public bool SetBrightness(int percent)
    {
        lock (_lock)
        {
            return _attached && _pipe is not null && _driver.SetBrightness(_pipe, _hid, percent);
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            DetachLocked();
        }
    }

    private void DetachLocked()
    {
        if (_pipe is not null)
        {
            try { _driver.Disconnect(_pipe, _hid); }
            catch { /* the panel is usually already gone */ }
        }
        _attached = false;
        _width = 0;
        _height = 0;
        _pipe?.Dispose();
        _pipe = null;
        _hid?.Dispose();
        _hid = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DetachLocked();
        }
    }
}
