using System;
using System.IO;
using System.Threading;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Byte sink turning the overlay's raw BGRA frame stream into JPEG pushes on the iCUE LINK
/// cooler LCD. Same shape as <see cref="JpegPanelStreamTransport"/> - that family's panels
/// are claimed by their own hub, while this one shares <see cref="CorsairLinkLcd"/> with the
/// still/GIF media worker, so opening here takes ownership and disposing hands it back.
///
/// The interface is a byte stream, not a frame queue: the paced writer may split or
/// concatenate writes, so this buffers to exactly one frame before encoding. Frames are
/// fixed-size, which makes the boundary unambiguous.
/// </summary>
public sealed class CorsairLinkLcdStreamTransport : IStreamedPanelTransport, IOrientablePanelTransport
{
    private readonly CorsairLinkLcd _lcd;
    private readonly BgraJpegEncoder _encoder;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;
    private bool _owned;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;
    private readonly PanelOrientationFilter _orientation = new();

    // The firmware falls back to its own screen when frames stop (30-40 s on a TITAN RX LCD),
    // so the last frame is re-pushed while the renderer is quiet. Only into an idle pipe:
    // frames sent back to back fill the LCD's buffer and one chunk then blocks 250-700 ms,
    // while a 25 ms gap between frames measured zero stalls. It expires shortly after the
    // renderer goes quiet, so a stalled overlay hands the glass back to the media worker.
    private const int KeepaliveIdleMs = 100;
    private const long KeepaliveGraceMs = 1000;
    private readonly object _lastFrameLock = new();
    private byte[] _lastFrame = Array.Empty<byte>();
    private int _lastFrameLength;
    private long _lastRealFrameMs;
    private long _lastSentMs;
    private int _sendsInFlight;
    private Timer? _keepalive;

    public CorsairLinkLcdStreamTransport(CorsairLinkLcd lcd, string serial)
    {
        _lcd = lcd;
        Serial = serial;
        _encoder = new BgraJpegEncoder(CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight);
        _frame = new byte[_encoder.FrameBytes];
    }

    public bool IsOpen => !_disposed && _lcd.HasDevice;

    public string Serial { get; }

    public void BindOrientation(Func<(int Rotation, bool Mirror)> source) => _orientation.Bind(source);

    public void Open()
    {
        if (!_lcd.HasDevice)
        {
            throw new IOException("corsair iCUE LINK LCD not attached");
        }
        _filled = 0;
        _owned = true;
        _lcd.ClaimStream(this);
        _keepalive = new Timer(_ => Keepalive(), null, KeepaliveIdleMs, KeepaliveIdleMs);
    }

    /// <summary>The panel has no player to start; the first pushed frame is the start.</summary>
    public void StartPlayer()
    {
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            PushFrame();
        }
    }

    private void PushFrame()
    {
        ReadOnlySpan<byte> jpeg;
        try
        {
            jpeg = _encoder.Encode(
                _orientation.Apply(_frame, CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight));
        }
        catch (Exception ex)
        {
            // An encode failure is a bug in this pipeline, not a device fault; log it once
            // and keep the stream alive so the next frame still gets a chance.
            if (!_dropLogged)
            {
                _dropLogged = true;
                ServiceLog.Warn($"[corsair-lcd] frame encode failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        lock (_lastFrameLock)
        {
            if (_lastFrame.Length < jpeg.Length)
            {
                _lastFrame = new byte[jpeg.Length];
            }
            jpeg.CopyTo(_lastFrame);
            _lastFrameLength = jpeg.Length;
            _lastRealFrameMs = Environment.TickCount64;
            Interlocked.Increment(ref _sendsInFlight);
        }
        _lcd.NoteStreamFrame(this);

        if (SendCounted(jpeg))
        {
            _dropLogged = false;
        }
        else if (!_dropLogged)
        {
            _dropLogged = true;
            ServiceLog.Warn("[corsair-lcd] frame rejected; the next frame retries and a run of failures reopens the screen");
        }
    }

    /// <summary>
    /// Re-pushes the last rendered frame so the panel keeps latching between the render
    /// side's own frames. Silent once the renderer has been quiet past the grace window.
    /// </summary>
    private void Keepalive()
    {
        if (_disposed)
        {
            return;
        }
        // Timer callbacks overlap when a send outlasts the period; a busy lock means one is in flight.
        if (!Monitor.TryEnter(_lastFrameLock))
        {
            return;
        }
        try
        {
            if (Volatile.Read(ref _sendsInFlight) > 0
                || Environment.TickCount64 - Volatile.Read(ref _lastSentMs) < KeepaliveIdleMs
                || _lastFrameLength == 0)
            {
                return;
            }
            // A screen select shows the stored screen until a frame lands; a static
            // widget may not render again for seconds, so the last frame goes now.
            var repaint = _lcd.TakeRepaintRequest(this);
            if (Environment.TickCount64 - _lastRealFrameMs > KeepaliveGraceMs && !repaint)
            {
                return;
            }
            Interlocked.Increment(ref _sendsInFlight);
            SendCounted(_lastFrame.AsSpan(0, _lastFrameLength));
        }
        finally
        {
            Monitor.Exit(_lastFrameLock);
        }
    }

    // Callers count the send under _lastFrameLock first, so a keepalive that takes the lock
    // next sees a real frame in flight (stalls included); idle time runs from when it finished.
    private bool SendCounted(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            return _lcd.SendFrame(jpeg);
        }
        finally
        {
            Volatile.Write(ref _lastSentMs, Environment.TickCount64);
            Interlocked.Decrement(ref _sendsInFlight);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _keepalive?.Dispose();
        _keepalive = null;
        if (_owned)
        {
            // Hand the glass back to the media worker, which repaints the selected still
            // or GIF on its next pass. By identity: a transport that faulted after its
            // replacement opened must not release the live one.
            _lcd.ReleaseStream(this);
        }
        _encoder.Dispose();
    }
}
