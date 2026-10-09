using System;
using System.Globalization;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG sensor gauge for the SL-LCD Wireless screen: a
/// ring/arc style (L-Connect's SENSOR_6/7 rings) and a big-number-with-bar
/// style. Pure: takes an already-resolved value/min/max/label/unit
/// (<see cref="Slv3LcdSensorReader"/> supplies the live reading).
/// </summary>
public static class Slv3LcdSensorRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    private static readonly SKColor DefaultAccent = new(0x00, 0xd1, 0xff);
    private static readonly SKColor DefaultText = SKColors.White;
    private static readonly SKColor TrackColor = new(255, 255, 255, 40);

    public static byte[] Render(string? style, float value, float min, float max, string label, string unit, string? accentHex, string? textHex)
    {
        var accent = RenderKit.ParseColor(accentHex, DefaultAccent);
        var text = RenderKit.ParseColor(textHex, DefaultText);
        var fraction = Normalize(value, min, max);
        var valueText = Math.Round(value).ToString("F0", CultureInfo.InvariantCulture);

        return string.Equals(style, "bar", StringComparison.OrdinalIgnoreCase)
            ? RenderBar(fraction, label, valueText, unit, accent, text)
            : RenderRing(fraction, label, valueText, unit, accent, text);
    }

    private static byte[] RenderRing(float fraction, string label, string valueText, string unit, SKColor accent, SKColor text)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var center = new SKPoint(Width / 2f, Height / 2f);
        const float outerRadius = 170f;
        const float thickness = 26f;
        const float innerRadius = outerRadius - thickness;
        const float startDeg = 130f;
        const float sweepDeg = 280f;

        var font = RenderKit.ResolveFont();

        using (var canvas = new SKCanvas(image))
        {
            RenderKit.FillPath(canvas, TrackColor, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, startDeg, startDeg + sweepDeg));
            if (fraction > 0f)
            {
                RenderKit.FillPath(canvas, accent, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, startDeg, startDeg + sweepDeg * fraction));
            }

            using var valueFont = RenderKit.CreateFont(font, 72, bold: true);
            using var unitFont = RenderKit.CreateFont(font, 26);
            using var labelFont = RenderKit.CreateFont(font, 22);
            RenderKit.DrawCentered(canvas, valueText, valueFont, text, new SKPoint(center.X, center.Y - 16));
            RenderKit.DrawCentered(canvas, unit, unitFont, text, new SKPoint(center.X, center.Y + 44));
            RenderKit.DrawCentered(canvas, label.ToUpperInvariant(), labelFont, accent, new SKPoint(center.X, center.Y + 120));
        }

        return RenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderBar(float fraction, string label, string valueText, string unit, SKColor accent, SKColor text)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var font = RenderKit.ResolveFont();

        const float barX = 60f;
        const float barWidth = Width - barX * 2f;
        const float barY = 260f;
        const float barHeight = 40f;
        var filledWidth = barWidth * fraction;

        using (var canvas = new SKCanvas(image))
        {
            using var valueFont = RenderKit.CreateFont(font, 96, bold: true);
            using var unitFont = RenderKit.CreateFont(font, 28);
            using var labelFont = RenderKit.CreateFont(font, 22);
            RenderKit.DrawCentered(canvas, valueText, valueFont, text, new SKPoint(Width / 2f, 150f));
            RenderKit.DrawCentered(canvas, unit, unitFont, text, new SKPoint(Width / 2f, 210f));

            RenderKit.FillRect(canvas, TrackColor, SKRect.Create(barX, barY, barWidth, barHeight));
            if (filledWidth > 0f)
            {
                RenderKit.FillRect(canvas, accent, SKRect.Create(barX, barY, filledWidth, barHeight));
            }

            RenderKit.DrawCentered(canvas, label.ToUpperInvariant(), labelFont, accent, new SKPoint(Width / 2f, barY + barHeight + 34f));
        }

        return RenderKit.EncodeJpeg(image);
    }

    private static float Normalize(float value, float min, float max)
    {
        if (max <= min)
        {
            return 0f;
        }
        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }
}
