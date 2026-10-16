using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
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

    // One report-sized buffer, reused: a 30 fps stream would otherwise allocate a
    // kilobyte per chunk and roughly 30 chunks per frame.
    private readonly byte[] _report;

    /// <summary>Failures worth a log line are rate limited so a wedged panel cannot spam.</summary>
    private const long FailureLogIntervalMs = 10_000;

    private IHidDevice? _device;
    private long _frameFailures;
    private long _lastFailureLogMs = long.MinValue / 2;
    private volatile bool _attached;
    private bool _disposed;

    public JpegPanelHub(JpegPanelModel model)
    {
        _model = model;
        _report = new byte[model.ReportLength];
    }

    public JpegPanelModel Model => _model;

    public bool IsConnected => _attached;

    /// <summary>Tick clock; replaced in tests.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Frames that did not reach the panel since this hub was created: a rejected report or a refused pre-frame step.</summary>
    public long FrameFailures => Interlocked.Read(ref _frameFailures);

    /// <summary>Delay the worker waits on an open handle before <see cref="Attach"/>; 0 when the model has none.</summary>
    public int SettleMs => _model.Handshake is IJpegPanelSession session ? session.SettleMs : 0;

    /// <summary>Period of <see cref="Tick"/>; 0 when the model has no session traffic.</summary>
    public int TickIntervalMs => _model.Handshake is IJpegPanelSession session ? session.TickIntervalMs : 0;

    /// <summary>Runs the model's periodic session exchange (keepalive, availability check) on the attached handle.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_device != null && _attached && _model.Handshake is IJpegPanelSession session
                && !session.Tick(_device, _model.ReportLength, Clock()))
            {
                // Marked detached so the connection worker closes this handle and runs a full attach.
                _attached = false;
            }
        }
    }

    /// <summary>USB product id of the attached panel, or 0 while nothing is attached; one model row spans several products.</summary>
    public int AttachedProductId
    {
        get { lock (_lock) { return _device != null && _attached ? _device.ProductId : 0; } }
    }

    /// <summary>USB serial of the attached panel, or null when nothing is attached.</summary>
    public string? Serial
    {
        get { lock (_lock) { return _device?.Serial; } }
    }

    /// <summary>
    /// Takes ownership of an open HID handle and runs the model's init sequence. Returns
    /// false when the init writes fail, which is the worker's signal to drop the handle.
    /// </summary>
    public bool Attach(IHidDevice device, CancellationToken cancel = default)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _attached = false;

            if (_model.Handshake is { } handshake)
            {
                bool ok;
                try
                {
                    // A session releases the lock across its pauses, so the handle may be
                    // detached or disposed meanwhile; only the same handle may go live.
                    ok = handshake is IJpegPanelSession session
                        ? session.Attach(device, _model.ReportLength, _lock, cancel)
                        : handshake.OnAttach(device, _model.ReportLength);
                }
                catch
                {
                    DropIfCurrentLocked(device);
                    throw;
                }
                if (!ok || !ReferenceEquals(_device, device))
                {
                    DropIfCurrentLocked(device);
                    return false;
                }
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
            return true;
        }
    }

    private void DropIfCurrentLocked(IHidDevice device)
    {
        if (ReferenceEquals(_device, device))
        {
            _device = null;
        }
        device.Dispose();
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
            if (_device != null)
            {
                Detaching?.Invoke(_device, _report);
            }
            if (_device != null && _model.Handshake is { } handshake)
            {
                handshake.OnDetach(_device, _model.ReportLength);
            }
            _attached = false;
            _device?.Dispose();
            _device = null;
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
                NoteFailureLocked("pre-frame step refused the frame (panel not ready or handle dead)");
                return false;
            }
            int offset = 0;
            int chunkIndex = 0;
            while (offset < jpeg.Length)
            {
                int written = JpegPanelProtocol.FillChunk(
                    _report, _model.HeaderStyle, _model.Selector, jpeg, offset, chunkIndex);
                if (written <= 0)
                {
                    return false;
                }
                if (!_device.Write(_report))
                {
                    NoteFailureLocked($"report write rejected at chunk {chunkIndex} ({offset} of {jpeg.Length} bytes sent)");
                    return false;
                }
                offset += written;
                chunkIndex++;
            }
            return true;
        }
    }

    /// <summary>True at most once per interval; the first call always wins.</summary>
    internal bool TryClaimFailureLog(long nowMs)
    {
        if (nowMs - _lastFailureLogMs < FailureLogIntervalMs)
        {
            return false;
        }
        _lastFailureLogMs = nowMs;
        return true;
    }

    private void NoteFailureLocked(string reason)
    {
        Interlocked.Increment(ref _frameFailures);
        if (!TryClaimFailureLog(Environment.TickCount64))
        {
            return;
        }
        ServiceLog.Warn($"[{_model.HandlerId}] frame failed: {reason}; failures so far {FrameFailures}");
    }

    /// <summary>Best-effort last write on the handle as it detaches, under the hub lock with the report buffer.</summary>
    public Action<IHidDevice, byte[]>? Detaching { get; set; }

    /// <summary>
    /// Runs a control exchange on the attached handle with the report buffer, between frames;
    /// <paramref name="fallback"/> when nothing is attached.
    /// </summary>
    public T Exchange<T>(Func<IHidDevice, byte[], T> op, T fallback)
    {
        lock (_lock)
        {
            return _device != null && _attached ? op(_device, _report) : fallback;
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
        }
    }
}
