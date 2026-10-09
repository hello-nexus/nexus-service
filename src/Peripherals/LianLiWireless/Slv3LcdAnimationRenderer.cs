using System;
using Nexus.Service.Lighting.Smart;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG procedural animation frame for the SL-LCD Wireless
/// screen. Three tasteful, cheap-to-compute animations; bundled-asset
/// animations (L-Connect's CefSharp index.html library) are out of scope.
/// Pure: takes the elapsed time, so a frame's content is deterministic.
/// </summary>
public static class Slv3LcdAnimationRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    /// <summary>~15 fps, matching the fixed rate video import already resamples GIF/video content to.</summary>
    public const int FrameIntervalMs = 66;

    private static readonly SKColor DefaultColorA = new(0x00, 0xd1, 0xff);
    private static readonly SKColor DefaultColorB = new(0x9b, 0x5d, 0xe5);

    public static byte[] Render(string? animationId, double elapsedSeconds, string? colorAHex, string? colorBHex)
    {
        var colorA = RenderKit.ParseColor(colorAHex, DefaultColorA);
        var colorB = RenderKit.ParseColor(colorBHex, DefaultColorB);

        return animationId switch
        {
            "spectrum" => RenderSpectrum(elapsedSeconds),
            "spin" => RenderSpin(elapsedSeconds, colorA, colorB),
            // "pulse" and any unrecognized/missing id default to the pulse animation.
            _ => RenderPulse(elapsedSeconds, colorA, colorB),
        };
    }

    private static byte[] RenderPulse(double elapsedSeconds, SKColor colorA, SKColor colorB)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var center = new SKPoint(Width / 2f, Height / 2f);
        // Reduce modulo the 2-second period in double before the float cast:
        // an uptime of days/weeks otherwise loses enough float precision in
        // the raw elapsed count that the phase visibly jumps between frames.
        var cycleSeconds = (float)(elapsedSeconds % 2.0);
        var phase = (MathF.Sin(cycleSeconds * MathF.PI) + 1f) / 2f;
        var radius = 60f + phase * 110f;

        using (var canvas = new SKCanvas(image))
        {
            RenderKit.FillCircle(canvas, RenderKit.WithAlpha(colorB, 0.25f), center, radius + 50f);
            RenderKit.FillCircle(canvas, RenderKit.WithAlpha(colorA, 0.55f), center, radius + 22f);
            RenderKit.FillCircle(canvas, colorA, center, radius);
        }

        return RenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderSpectrum(double elapsedSeconds)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var hueOffset = (float)(elapsedSeconds / 4.0 % 1.0);

        using (var canvas = new SKCanvas(image))
        {
            const int bandCount = 40;
            var bandWidth = (float)Width / bandCount;
            for (var i = 0; i < bandCount; i++)
            {
                var hue = ((float)i / bandCount + hueOffset) % 1f;
                var (r, g, b) = ColorMath.HsvToRgb(hue, 1f, 1f);
                RenderKit.FillRect(canvas, new SKColor(r, g, b), SKRect.Create(i * bandWidth, 0f, bandWidth + 1f, Height));
            }
        }

        return RenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderSpin(double elapsedSeconds, SKColor colorA, SKColor colorB)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var center = new SKPoint(Width / 2f, Height / 2f);
        const int spokeCount = 12;
        var rotation = (float)(elapsedSeconds * 120.0 % 360.0);

        using (var canvas = new SKCanvas(image))
        {
            for (var i = 0; i < spokeCount; i++)
            {
                var angle = rotation + i * (360f / spokeCount);
                var fade = 1f - (float)i / spokeCount;
                RenderKit.FillPath(canvas, RenderKit.WithAlpha(colorA, 0.15f + fade * 0.85f), RenderKit.BuildHand(center, 160f, angle, 16f, tailFraction: 0f));
            }
            RenderKit.FillCircle(canvas, colorB, center, 20f);
        }

        return RenderKit.EncodeJpeg(image);
    }
}
