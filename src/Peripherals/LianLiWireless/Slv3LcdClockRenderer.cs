using System;
using System.Globalization;
using Nexus.Service.Rendering;
using SkiaSharp;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Renders a 400x400 JPEG clock face for the SL-LCD Wireless screen: two
/// digital faces and two analog faces, a subset of L-Connect's seven
/// Clock1..7 themes. Pure: takes the instant to render, so tests don't need
/// to mock the system clock.
/// </summary>
public static class Slv3LcdClockRenderer
{
    public const int Width = Slv3LcdProtocol.PanelWidth;
    public const int Height = Slv3LcdProtocol.PanelHeight;

    private static readonly SKColor DefaultAccent = new(0x00, 0xd1, 0xff);
    private static readonly SKColor DefaultText = SKColors.White;

    public static byte[] Render(string? face, DateTime now, string? accentHex, string? textHex)
    {
        var accent = RenderKit.ParseColor(accentHex, DefaultAccent);
        var text = RenderKit.ParseColor(textHex, DefaultText);

        return face switch
        {
            "digitalMinimal" => RenderDigital(now, accent, text, showDate: false),
            "analogClassic" => RenderAnalog(now, accent, text, showNumbers: true),
            "analogMinimal" => RenderAnalog(now, accent, text, showNumbers: false),
            // "digital" and any unrecognized/missing face default to the full digital face.
            _ => RenderDigital(now, accent, text, showDate: true),
        };
    }

    private static byte[] RenderDigital(DateTime now, SKColor accent, SKColor text, bool showDate)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var font = RenderKit.ResolveFont();
        var center = new SKPoint(Width / 2f, Height / 2f);

        using (var canvas = new SKCanvas(image))
        {
            var timeY = showDate ? center.Y - 20f : center.Y;
            using var timeFont = RenderKit.CreateFont(font, 64, bold: true);
            RenderKit.DrawCentered(canvas, now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), timeFont, accent, new SKPoint(center.X, timeY));
            if (showDate)
            {
                using var dateFont = RenderKit.CreateFont(font, 26);
                RenderKit.DrawCentered(canvas, now.ToString("ddd, MMM d", CultureInfo.InvariantCulture), dateFont, text, new SKPoint(center.X, timeY + 60f));
            }
        }

        return RenderKit.EncodeJpeg(image);
    }

    private static byte[] RenderAnalog(DateTime now, SKColor accent, SKColor text, bool showNumbers)
    {
        using var image = RenderKit.NewImage(Width, Height, SKColors.Black);
        var font = RenderKit.ResolveFont();
        var center = new SKPoint(Width / 2f, Height / 2f);
        const float dialRadius = 175f;

        var hourDeg = (now.Hour % 12 + now.Minute / 60f) * 30f;
        var minuteDeg = (now.Minute + now.Second / 60f) * 6f;
        var secondDeg = now.Second * 6f;

        using (var canvas = new SKCanvas(image))
        {
            using (var dial = RenderKit.Stroke(text, 3f))
            {
                canvas.DrawCircle(center, dialRadius, dial);
            }

            using var numberFont = RenderKit.CreateFont(font, 24);
            for (var tick = 0; tick < 12; tick++)
            {
                var tickDeg = tick * 30f;
                RenderKit.FillPath(canvas, text, RenderKit.BuildClockTick(center, dialRadius - 24f, dialRadius - 6f, tickDeg, 6f));
                if (showNumbers && tick % 3 == 0)
                {
                    var rad = (tickDeg - 90f) * MathF.PI / 180f;
                    var numberRadius = dialRadius - 34f;
                    var point = new SKPoint(center.X + numberRadius * MathF.Cos(rad), center.Y + numberRadius * MathF.Sin(rad));
                    var hourNumber = tick == 0 ? 12 : tick;
                    RenderKit.DrawCentered(canvas, hourNumber.ToString(CultureInfo.InvariantCulture), numberFont, text, point);
                }
            }

            RenderKit.FillPath(canvas, text, RenderKit.BuildHand(center, dialRadius * 0.5f, hourDeg, 10f));
            RenderKit.FillPath(canvas, text, RenderKit.BuildHand(center, dialRadius * 0.75f, minuteDeg, 7f));
            RenderKit.FillPath(canvas, accent, RenderKit.BuildHand(center, dialRadius * 0.82f, secondDeg, 3f, tailFraction: 0.18f));
            RenderKit.FillCircle(canvas, accent, center, 8f);
        }

        return RenderKit.EncodeJpeg(image);
    }
}
