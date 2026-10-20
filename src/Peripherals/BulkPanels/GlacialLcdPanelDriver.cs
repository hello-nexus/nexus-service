using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// The ASUS ROG Crosshair X870E Glacial motherboard LCD. A picture upload is far too slow to
/// stream (the app paces its bulk transfers a couple of seconds apart), so this driver
/// pushes a still instead: the newest rendered frame is JPEG-encoded and uploaded on a
/// background thread, at most once per refresh interval, and never while an upload is
/// still running. MB Manager only uploads when the user picks a picture, so the interval
/// is deliberately long; the glass holds the picture on its own.
/// </summary>
public sealed class GlacialLcdPanelDriver : IBulkPanelDriver
{
    /// <summary>Gap between two upload starts. Long on purpose: the vendor app uploads on demand, never on a timer.</summary>
    public const int DefaultRefreshIntervalMs = 15 * 60 * 1000;

    private const int JoinOnDisconnectMs = 5000;

    private readonly int _chunkIntervalMs;
    private readonly int _refreshIntervalMs;
    private readonly object _hidLock = new();
    private readonly ManualResetEventSlim _idle = new(true);

    private BgraJpegEncoder? _encoder;
    private Thread? _worker;
    private volatile bool _cancel;
    private bool _uploading;
    private long _lastStartMs = long.MinValue;
    private int _pendingBrightness = -1;
    private byte[]? _lastUploaded;

    public GlacialLcdPanelDriver(int chunkIntervalMs = GlacialLcdProtocol.ChunkIntervalMs, int refreshIntervalMs = DefaultRefreshIntervalMs)
    {
        _chunkIntervalMs = chunkIntervalMs;
        _refreshIntervalMs = refreshIntervalMs;
    }

    public string HandlerId => "asus-glacial-lcd";
    public string Name => "ASUS ROG Glacial LCD";
    public int VendorId => 0x0B05;
    public IReadOnlyList<int> ProductIds { get; } = new[] { GlacialLcdProtocol.ProductId };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;

    /// <summary>The overlay only needs to hand over a frame every so often; most are dropped.</summary>
    public int Fps => 1;

    public byte WritePipeId => 0x02;
    public byte ReadPipeId => 0x82;
    public bool NeedsHidChannel => true;
    public bool SupportsBrightness => true;

    /// <summary>True while a picture is on its way to the panel.</summary>
    public bool IsUploading
    {
        get { lock (_hidLock) { return _uploading; } }
    }

    /// <summary>Waits for a running upload to finish; true when none is running.</summary>
    public bool WaitIdle(int timeoutMs) => _idle.Wait(timeoutMs);

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (hid is null)
        {
            return null;
        }
        _lastStartMs = long.MinValue;
        // The vendor app opens with this pair; the replies are not used.
        if (!hid.Write(GlacialLcdProtocol.EncodeHello()) || !hid.Write(GlacialLcdProtocol.EncodeQuery()))
        {
            return null;
        }
        return (GlacialLcdProtocol.Width, GlacialLcdProtocol.Height);
    }

    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (hid is null)
        {
            return false;
        }
        long now = Environment.TickCount64;
        // Cheap checks first: the encode below costs a frame's worth of CPU.
        if (IsUploading || (_lastStartMs != long.MinValue && now - _lastStartMs < _refreshIntervalMs))
        {
            return true;
        }
        _encoder ??= new BgraJpegEncoder(GlacialLcdProtocol.Width, GlacialLcdProtocol.Height, GlacialLcdProtocol.JpegQuality);
        var jpeg = _encoder.Encode(bgra).ToArray();
        if (_lastUploaded is not null && _lastUploaded.AsSpan().SequenceEqual(jpeg))
        {
            return true;
        }
        lock (_hidLock)
        {
            if (_uploading)
            {
                return true;
            }
            _uploading = true;
            _idle.Reset();
        }
        _lastStartMs = now;
        _cancel = false;
        _worker = new Thread(() => Upload(pipe, hid, jpeg)) { IsBackground = true, Name = "glacial-lcd-upload" };
        _worker.Start();
        return true;
    }

    private void Upload(IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
    {
        bool sent = false;
        try
        {
            sent = UploadJpeg(pipe, hid, jpeg);
            if (sent)
            {
                _lastUploaded = jpeg;
            }
            else
            {
                // A failed upload retries on the next frame rather than waiting out the interval.
                _lastStartMs = long.MinValue;
            }
        }
        catch (Exception ex)
        {
            _lastStartMs = long.MinValue;
            ServiceLog.Warn($"[{HandlerId}] upload failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            int brightness;
            lock (_hidLock)
            {
                brightness = _pendingBrightness;
                _pendingBrightness = -1;
                _uploading = false;
            }
            if (brightness >= 0 && !_cancel)
            {
                ApplyBrightness(hid, brightness);
            }
            _idle.Set();
        }
        if (sent)
        {
            ServiceLog.Info($"[{HandlerId}] picture uploaded, {jpeg.Length} bytes");
        }
    }

    private bool UploadJpeg(IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
    {
        lock (_hidLock)
        {
            // The app sends nothing else on HID while the bulk transfers run.
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return false;
            }
            foreach (var report in GlacialLcdProtocol.EncodeCustomImagePrelude())
            {
                if (!hid.Write(report))
                {
                    return false;
                }
            }
            if (!hid.Write(GlacialLcdProtocol.EncodeJpegAnnounce(jpeg.Length)))
            {
                return false;
            }
        }
        for (int offset = 0; offset < jpeg.Length; offset += GlacialLcdProtocol.ChunkBytes)
        {
            if (_cancel)
            {
                return false;
            }
            int length = Math.Min(GlacialLcdProtocol.ChunkBytes, jpeg.Length - offset);
            if (!pipe.Write(jpeg.AsSpan(offset, length)))
            {
                return false;
            }
            if (offset + length < jpeg.Length && _chunkIntervalMs > 0)
            {
                Thread.Sleep(_chunkIntervalMs);
            }
        }
        return true;
    }

    /// <summary>The glass holds its picture, so there is nothing to resend.</summary>
    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => true;

    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent)
    {
        if (hid is null)
        {
            return false;
        }
        lock (_hidLock)
        {
            if (_uploading)
            {
                // Applied when the upload ends; a report mid-transfer is not something the vendor app does.
                _pendingBrightness = percent;
                return true;
            }
        }
        return ApplyBrightness(hid, percent);
    }

    /// <summary>
    /// The vendor sequence: query the state, then write it back with one field changed. The
    /// state comes from the panel's reply, so the mode and sleep behavior it holds are kept.
    /// The app also repeats its mode select afterwards; this driver never selects a mode, so
    /// it has none to repeat.
    /// </summary>
    private bool ApplyBrightness(IHidDevice hid, int percent)
    {
        lock (_hidLock)
        {
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return false;
            }
            byte mode = GlacialLcdProtocol.ModeAnimatedAsset;
            bool keepOn = false;
            var reply = new byte[GlacialLcdProtocol.ReportBytes];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int read = hid.Read(reply, 200);
                if (read < 0)
                {
                    return false;
                }
                if (read > 0 && GlacialLcdProtocol.ParseState(reply.AsSpan(0, read)) is { } state)
                {
                    mode = state.Mode;
                    keepOn = state.KeepOnWhileAsleep;
                    break;
                }
            }
            if (!hid.Write(GlacialLcdProtocol.EncodeSettings(percent, mode, keepOn)))
            {
                return false;
            }
            return true;
        }
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _cancel = true;
        _worker?.Join(JoinOnDisconnectMs);
        _worker = null;
        _encoder?.Dispose();
        _encoder = null;
    }
}
