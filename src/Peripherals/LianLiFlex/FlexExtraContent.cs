using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>The Flex-only content types: the live "screen" and the "animation2d" sprite motion.</summary>
public sealed class FlexExtraContent : IFanLcdExtraContent, IDisposable
{
    public const string ScreenMode = "screen";
    public const string Animation2dMode = "animation2d";

    private sealed class Running : IDisposable
    {
        public required FlexOverlayRenderer Renderer { get; init; }
        public required string Signature { get; init; }
        public required DateTime StartedUtc { get; init; }

        public void Dispose() => Renderer.Dispose();
    }

    private readonly Slv3LcdMediaLibrary _media;
    private readonly FlexSpriteLibrary _sprites;
    private readonly FlexScreenCapture _capture;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);

    public FlexExtraContent(Slv3LcdMediaLibrary media, FlexSpriteLibrary sprites, FlexScreenCapture capture, Func<DateTime>? utcNow = null)
    {
        _media = media;
        _sprites = sprites;
        _capture = capture;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool Handles(string mode) => mode is ScreenMode or Animation2dMode;

    public (byte[]? Jpeg, int DelayMs) Render(string serial, LianLiWirelessScreenSettings settings)
    {
        if (settings.ContentType == ScreenMode)
        {
            DropRenderer(serial);
            return (_capture.RenderJpeg(serial, settings.ScreenSource), FlexScreenCapture.FrameIntervalMs);
        }
        _capture.Release(serial);
        if (settings.Overlay is not { } overlay)
        {
            return (null, 1000);
        }
        var running = EnsureRenderer(serial, settings, overlay);
        if (running is null)
        {
            return (null, 1000);
        }
        var elapsed = (_utcNow() - running.StartedUtc).TotalSeconds;
        return (running.Renderer.RenderJpeg(elapsed), running.Renderer.FrameIntervalMs);
    }

    private Running? EnsureRenderer(string serial, LianLiWirelessScreenSettings settings, LianLiFlexOverlaySettings overlay)
    {
        var signature = SignatureOf(settings.MediaId, overlay);
        lock (_gate)
        {
            if (_running.TryGetValue(serial, out var existing) && existing.Signature == signature)
            {
                return existing;
            }
        }
        var sprite = string.IsNullOrEmpty(overlay.SpriteMediaId) ? null : _sprites.Load(overlay.SpriteMediaId);
        if (sprite is null)
        {
            return null;
        }
        SKBitmap? background = null;
        if (!string.IsNullOrEmpty(settings.MediaId) && _media.LoadFrames(settings.MediaId) is { Frames.Length: > 0 } image)
        {
            background = RenderKit.Decode(image.Frames[0].JpegBytes);
        }
        var created = new Running
        {
            Renderer = new FlexOverlayRenderer(overlay, background, new[] { sprite }, new[] { 0 }),
            Signature = signature,
            StartedUtc = _utcNow(),
        };
        Running? old;
        lock (_gate)
        {
            _running.Remove(serial, out old);
            _running[serial] = created;
        }
        old?.Dispose();
        return created;
    }

    private static string SignatureOf(string? mediaId, LianLiFlexOverlaySettings o)
    {
        var p = o.Params;
        return string.Join('|', new[]
        {
            mediaId ?? "", o.Type, o.SpriteMediaId, o.Speed.ToString(CultureInfo.InvariantCulture), o.Loop.ToString(), o.Zoom.ToString(CultureInfo.InvariantCulture),
            o.Direction.ToString(CultureInfo.InvariantCulture), o.CenterX.ToString(CultureInfo.InvariantCulture), o.CenterY.ToString(CultureInfo.InvariantCulture),
            p.Radius.ToString(CultureInfo.InvariantCulture), p.RotateAngle.ToString(CultureInfo.InvariantCulture), p.AutoReverse.ToString(),
            p.UpHeight.ToString(CultureInfo.InvariantCulture), p.Bounciness.ToString(CultureInfo.InvariantCulture),
            p.DwellSeconds.ToString(CultureInfo.InvariantCulture), p.RotationIntensity.ToString(CultureInfo.InvariantCulture), p.SelfRotate.ToString(),
        });
    }

    private void DropRenderer(string serial)
    {
        Running? old;
        lock (_gate)
        {
            _running.Remove(serial, out old);
        }
        old?.Dispose();
    }

    public void ReleaseExtra(string serial)
    {
        DropRenderer(serial);
        _capture.Release(serial);
    }

    public void Dispose()
    {
        List<Running> all;
        lock (_gate)
        {
            all = new List<Running>(_running.Values);
            _running.Clear();
        }
        all.ForEach(r => r.Dispose());
        _capture.Dispose();
    }
}
