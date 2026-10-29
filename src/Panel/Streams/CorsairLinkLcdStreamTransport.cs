using System;
using System.IO;
using System.Diagnostics;
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
    private BgraJpegEncoder?[]? _stepDown;
    private int _level;
    private int _fastRun;
    private readonly byte[] _frame;
    private int _filled;
    private bool _disposed;
    private bool _owned;

    // A stall shows up as the queue trimming rather than an exception, so a dropped frame
    // is only worth logging once per run of drops.
    private bool _dropLogged;

    // A device that cannot keep up with the byte rate blocks one write for a second or more
    // and then resets itself off USB (the GIF drop: sends of 1.2 s, then 1.9 s, then the
    // device left the bus). Send time is the signal: a slow send lowers the JPEG quality for
    // the next frames, and a run of fast sends raises it back.
    internal static readonly int[] StepDownQualities = { 65, 50, 40, 25 };
    // The media worker that played GIFs without drops sent ffmpeg q5 frames (about 24 KB each
    // for the customer's GIF); this encoder at the shared 85 sends about 33 KB. 75 lands near
    // the old size, so the same byte budget buys more frames per second.
    internal const int BaseQuality = 75;
    internal const long SlowSendMs = 250;
    internal const long FastSendMs = 100;
    internal const int StepUpAfterFrames = 60;

    // Measured on the GIF drops: 32 KB frames at 28 fps (about 0.9 MB/s, near the one report per
    // millisecond ceiling of this endpoint) ran with no slow send at all, then one write hung
    // 1.9 s and the device reset itself off USB. Plasma at 16 fps held for 30 minutes. Rate, not
    // send time, is therefore capped: a frame of N bytes holds the next one back N / budget.
    internal const int ByteBudgetPerSec = 300_000;
    internal const int MaxRateWaitMs = 500;
    private long _notBeforeMs;
    private int _seenAttachGeneration = -1;
    private long _statsAtMs;
    private long _statFrames;
    private long _statBytes;
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
        _encoder = new BgraJpegEncoder(CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight, BaseQuality);
        _frame = new byte[_encoder.FrameBytes];
    }

    public bool IsOpen => !_disposed && _lcd.HasDevice;

    public string Serial { get; }

    public void BindOrientation(Func<(bool Flip180, bool Mirror)> source) => _orientation.Bind(source);

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

    internal static int RateWaitMs(long nowMs, long notBeforeMs) =>
        (int)Math.Clamp(notBeforeMs - nowMs, 0, MaxRateWaitMs);

    internal static long NotBeforeAfter(long sentAtMs, int jpegBytes) =>
        sentAtMs + (long)jpegBytes * 1000 / ByteBudgetPerSec;

    private void PushFrame()
    {
        // A re-opened screen comes back with an empty buffer and is the most fragile right
        // then; the drop loop re-triggered at once at full quality, so restart reduced.
        var generation = _lcd.AttachGeneration;
        if (_seenAttachGeneration != generation)
        {
            if (_seenAttachGeneration >= 0)
            {
                _level = Math.Max(_level, 2);
                _fastRun = 0;
                ServiceLog.Warn($"[corsair-lcd] screen re-opened; JPEG quality restarts at {StepDownQualities[_level - 1]}");
            }
            _seenAttachGeneration = generation;
        }
        var rateWait = RateWaitMs(Environment.TickCount64, _notBeforeMs);
        if (rateWait > 0)
        {
            Thread.Sleep(rateWait);
        }
        ReadOnlySpan<byte> jpeg;
        try
        {
            jpeg = EncoderFor(_level).Encode(
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

        var sw = Stopwatch.StartNew();
        var sent = SendCounted(jpeg);
        AdaptQuality(sent, jpeg.Length, sw.ElapsedMilliseconds);
        _notBeforeMs = NotBeforeAfter(Environment.TickCount64, jpeg.Length);
        NoteStats(jpeg.Length);
        if (sent)
        {
            _dropLogged = false;
        }
        else if (!_dropLogged)
        {
            _dropLogged = true;
            ServiceLog.Warn("[corsair-lcd] frame rejected; the next frame retries and a run of failures reopens the screen");
        }
    }

    private BgraJpegEncoder EncoderFor(int level)
    {
        if (level == 0)
        {
            return _encoder;
        }
        _stepDown ??= new BgraJpegEncoder?[StepDownQualities.Length];
        return _stepDown[level - 1] ??= new BgraJpegEncoder(
            CorsairLinkLcd.PanelWidth, CorsairLinkLcd.PanelHeight, StepDownQualities[level - 1]);
    }

    private void AdaptQuality(bool sent, int jpegBytes, long elapsedMs)
    {
        if (sent && elapsedMs >= SlowSendMs)
        {
            _fastRun = 0;
            if (_level < StepDownQualities.Length)
            {
                _level++;
                ServiceLog.Warn($"[corsair-lcd] slow send {elapsedMs} ms for {jpegBytes} B; JPEG quality now {StepDownQualities[_level - 1]}");
            }
        }
        else if (sent && elapsedMs <= FastSendMs && _level > 0 && ++_fastRun >= StepUpAfterFrames)
        {
            _fastRun = 0;
            _level--;
        }
    }

    internal int QualityLevel => _level;

    // One line per 10 s so a bundle shows bytes per frame and per second at the moment of a drop.
    private void NoteStats(int jpegBytes)
    {
        var now = Environment.TickCount64;
        if (_statsAtMs == 0)
        {
            _statsAtMs = now;
        }
        _statFrames++;
        _statBytes += jpegBytes;
        if (now - _statsAtMs < 10_000)
        {
            return;
        }
        var secs = (now - _statsAtMs) / 1000.0;
        ServiceLog.Info($"[corsair-lcd] stream {_statFrames / secs:F1} fps, avg {_statBytes / _statFrames} B/frame, {_statBytes / secs / 1000:F0} KB/s, quality level {_level}");
        _statsAtMs = now;
        _statFrames = 0;
        _statBytes = 0;
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
        if (_stepDown is not null)
        {
            foreach (var e in _stepDown)
            {
                e?.Dispose();
            }
        }
    }
}
