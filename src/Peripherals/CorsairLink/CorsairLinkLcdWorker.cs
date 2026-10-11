using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Streams 480x480 JPEG frames to the attached LCD display. Reads settings on
/// each loop pass to pick up media, brightness, and rotation changes immediately.
/// </summary>
public sealed class CorsairLinkLcdWorker : BackgroundService
{

    private readonly CorsairLinkLcd _lcd;
    private readonly CorsairLinkLcdMediaLibrary _library;
    private readonly IConfigStore _store;
    private readonly CorsairLinkHubs _hubs;
    private readonly DeviceControlGate _gate;

    private string? _loadedMediaId;
    private LcdImageData? _loadedImage;
    // Sentinels ensure brightness and rotation are pushed on the first connected loop.
    private byte _appliedBrightness = 255;
    private byte _appliedRotation = 255;
    private int _appliedGeneration = -1;

    public CorsairLinkLcdWorker(
        CorsairLinkLcd lcd,
        CorsairLinkLcdMediaLibrary library,
        IConfigStore store,
        CorsairLinkHubs hubs,
        DeviceControlGate gate)
    {
        _lcd = lcd;
        _library = library;
        _store = store;
        _hubs = hubs;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Same switch the panel stream rides: until the user claims the glass,
                // nothing here writes to it.
                if (!_gate.IsEnabled(CorsairLinkLcd.DeviceId) || _hubs.LcdHub is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (!_lcd.HasDevice)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                // A reconnect re-opens the panel with firmware defaults, so the sentinels
                // go back to "nothing applied yet" and the block below re-pushes.
                var generation = _lcd.AttachGeneration;
                if (generation != _appliedGeneration)
                {
                    _appliedGeneration = generation;
                    _appliedBrightness = 255;
                    _appliedRotation = 255;
                }

                var s = _store.Load().Devices.Corsair;
                _lcd.ShowsStoredImage = s.LcdDeviceMediaId is not null;

                // Brightness and rotation are feature reports on their own channel, so they
                // apply while a panel session streams.
                if (s.LcdBrightness != _appliedBrightness)
                {
                    _lcd.SetBrightness(s.LcdBrightness);
                    _appliedBrightness = s.LcdBrightness;
                }

                if (s.LcdRotation != _appliedRotation)
                {
                    _lcd.SetRotation(s.LcdRotation);
                    _appliedRotation = s.LcdRotation;
                }

                // A panel session owns the frame pipe while it streams widget frames; the
                // still or GIF resumes on the pass after the frames stop.
                if (_lcd.IsStreamLive)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (s.LcdSelectedMediaId is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (s.LcdSelectedMediaId != _loadedMediaId)
                {
                    _loadedImage = _library.LoadImageData(s.LcdSelectedMediaId);
                    _loadedMediaId = s.LcdSelectedMediaId;
                }

                if (_loadedImage is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                var image = _loadedImage;
                var stillResendMs = _lcd.IsAioLcd ? AioStillResendMs : Xd5StillResendMs;
                await StreamImageAsync(image, mem => _lcd.SendFrame(mem.Span), ct, stillResendMs).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair-lcd] worker error: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    // The AIO LCD holds a frame 30-40 s before its own screen takes over, so a still
    // re-sent faster only floods the pipe. The XD5 LCD's hold was never measured and keeps
    // OpenLinkHub's re-send (lsh.go:5071).
    internal const int AioStillResendMs = 1000;
    internal const int Xd5StillResendMs = 10;

    // Frames sent back to back fill the LCD's buffer and stall a chunk for hundreds of ms;
    // this gap between frames measured clean (TITAN RX LCD).
    internal const int MinFrameGapMs = 25;

    /// <summary>
    /// Executes one image display cycle. A still sends one frame and waits
    /// <paramref name="stillResendMs"/>; an animation sends every frame, each holding its
    /// delay minus the time the send took.
    /// </summary>
    internal static async Task StreamImageAsync(
        LcdImageData image,
        Action<ReadOnlyMemory<byte>> sendFrame,
        CancellationToken ct,
        int stillResendMs = AioStillResendMs)
    {
        if (image.Frames.Length == 1)
        {
            sendFrame(image.Frames[0].JpegBytes);
            await Task.Delay(stillResendMs, ct).ConfigureAwait(false);
            return;
        }

        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested) break;
            // The send itself takes tens of ms for a large frame; it counts toward the frame's time.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            sendFrame(frame.JpegBytes);
            var sendMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Task.Delay(Math.Max(MinFrameGapMs, frame.DelayMs - sendMs), ct).ConfigureAwait(false);
        }
    }
}
