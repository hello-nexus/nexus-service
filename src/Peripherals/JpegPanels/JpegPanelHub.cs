using System;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Owns the HID handle for one JPEG-over-HID cooler LCD and serialises frame uploads.
/// One instance per <see cref="JpegPanelModel"/>; the connection worker attaches and
/// detaches it as the device comes and goes.
///
/// A frame is a whole JPEG chunked across consecutive output reports. There is no
/// acknowledgement to wait for on any of these devices, so a failed write is the only
/// error signal - which is why every write result is checked.
/// </summary>
public sealed class JpegPanelHub : IDisposable
{
    private readonly object _lock = new();
    private readonly JpegPanelModel _model;

    // Reuse one report-sized buffer for control and frame chunks. A 30 fps stream would
    // otherwise allocate a report per chunk.
    private readonly byte[] _report;

    private IHidDevice? _device;
    private volatile bool _attached;
    private bool _disposed;

    public event Action? StateChanged;

    public JpegPanelHub(JpegPanelModel model)
    {
        _model = model;
        _report = new byte[model.ReportLength];
    }

    public JpegPanelModel Model => _model;

    public bool IsConnected => _attached;

    /// <summary>USB serial of the attached panel, or null when nothing is attached.</summary>
    public string? Serial
    {
        get { lock (_lock) { return _device?.Serial; } }
    }

    /// <summary>
    /// Takes ownership of an open HID handle and runs the model's init sequence. Returns
    /// false when the init writes fail, which is the worker's signal to drop the handle.
    /// </summary>
    public bool Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _attached = false;

            if (_model.Handshake is { } handshake && !handshake.OnAttach(device, _model.ReportLength))
            {
                _device?.Dispose();
                _device = null;
                return false;
            }

            if (_model.InitReports is { } inits)
            {
                foreach (var init in inits)
                {
                    if (!WriteRawLocked(init))
                    {
                        ServiceLog.Warn($"[{_model.HandlerId}] init report rejected; dropping the handle");
                        _device?.Dispose();
                        _device = null;
                        return false;
                    }
                }
            }

            _attached = true;
            NotifyStateChanged();
            return true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_device != null && _model.ShutdownReports is { } shutdowns)
            {
                foreach (var report in shutdowns)
                {
                    // Best effort: the device is often already gone by now.
                    WriteRawLocked(report);
                }
            }
            if (_device != null && _model.Handshake is { } handshake)
            {
                handshake.OnDetach(_device, _model.ReportLength);
            }
            _attached = false;
            _device?.Dispose();
            _device = null;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Backlight the panel was last told, or -1 when the model has no backlight command.
    /// </summary>
    public int Brightness
    {
        get { lock (_lock) { return _model.Handshake is IJpegPanelBrightness d ? d.Brightness : -1; } }
    }

    /// <summary>
    /// Sets the backlight, in percent. Recorded whether or not a panel is attached, so the
    /// next attach re-asserts it; the return says only whether it reached hardware now.
    /// </summary>
    public bool SetBrightness(int percent)
    {
        lock (_lock)
        {
            if (_model.Handshake is not IJpegPanelBrightness dimmable)
            {
                return false;
            }
            dimmable.SetBrightness(percent);
            return _device != null && _attached && dimmable.ApplyBrightness(_device, _model.ReportLength);
        }
    }

    /// <summary>
    /// Pushes one encoded JPEG frame. Returns false on the first rejected report, leaving
    /// the frame half-written - the next frame starts from chunk 0, so a partial upload
    /// costs one dropped frame rather than a desynchronised stream.
    /// </summary>
    public bool SendFrame(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.IsEmpty)
        {
            return false;
        }
        lock (_lock)
        {
            if (_device == null || !_attached)
            {
                return false;
            }
            // A panel with a control channel may not be ready for pixels yet (ASRock reports
            // a boot state), and some want a periodic re-assert while streaming.
            if (_model.Handshake is { } handshake
                && !handshake.BeforeFrame(_device, _model.ReportLength, Environment.TickCount64))
            {
                return false;
            }
            int offset = 0;
            int chunkIndex = 0;
            while (offset < jpeg.Length)
            {
                int written = JpegPanelProtocol.FillChunk(
                    _report,
                    _model.HeaderStyle,
                    _model.Selector,
                    jpeg,
                    offset,
                    chunkIndex);
                if (written <= 0)
                {
                    return false;
                }
                if (!_device.Write(_report))
                {
                    return false;
                }
                offset += written;
                chunkIndex++;
            }
            return true;
        }
    }

    /// <summary>
    /// Sends the Galahad II LCD pump's per-LED report through the same handle as the panel
    /// stream. Keeping this on the panel hub prevents a second owner from interleaving
    /// control and frame reports on the shared HID interface.
    /// </summary>
    public bool SendPumpPerLed(ReadOnlySpan<byte> colors)
    {
        if (!IsGalahad2Lcd)
        {
            return false;
        }
        var packet = Galahad2Protocol.EncodePumpPerLed(colors);
        lock (_lock)
        {
            return _device != null && _attached && _device.Write(packet);
        }
    }

    /// <summary>Sends a Galahad II pump zone/effect command padded to the panel's report size.</summary>
    public bool SendPumpZoneLighting(byte ring, byte mode, byte brightness, byte speed, byte direction, ReadOnlySpan<byte> colors)
    {
        if (!IsGalahad2Lcd)
        {
            return false;
        }
        var packet = Galahad2Protocol.EncodeLighting(ring, mode, brightness, speed, direction, colors);
        lock (_lock)
        {
            if (_device == null || !_attached)
            {
                return false;
            }
            _report.AsSpan().Clear();
            packet.AsSpan().CopyTo(_report);
            return _device.Write(_report);
        }
    }

    private bool WriteRawLocked(ReadOnlySpan<byte> payload)
    {
        if (_device == null)
        {
            return false;
        }
        // Vendor init reports are documented short, but Windows wants exactly the declared
        // output report length, so they go out padded.
        _report.AsSpan().Clear();
        payload.CopyTo(_report);
        return _device.Write(_report);
    }

    private bool IsGalahad2Lcd =>
        string.Equals(_model.HandlerId, JpegPanelModel.GalahadIiLcd.HandlerId, StringComparison.Ordinal);

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
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
            _attached = false;
            _device?.Dispose();
            _device = null;
            NotifyStateChanged();
        }
    }
}
