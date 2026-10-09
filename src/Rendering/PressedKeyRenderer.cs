using System;
using SkiaSharp;

namespace Nexus.Service.Rendering;

/// <summary>
/// Push-in feedback: scales a decoded key image down and composites it
/// centered onto a black canvas the same size as the source, so a physical
/// key-down shows an inset variant of whatever the key currently displays.
/// The canvas is always black (the bezel color), never the slot color - a
/// slot-colored canvas hides the inset on keys whose tile background is that
/// same color, so the shrink read differently per key type. Pure Skia
/// compositing - no deck, HID, or wire-format knowledge, so it works on any
/// already-decoded square image.
/// </summary>
internal static class PressedKeyRenderer
{
    /// <summary>User-tuned push-in inset amount.</summary>
    internal const float PressScale = 0.80f;

    public static SKBitmap Render(SKBitmap source)
    {
        var scaledWidth = Math.Max(1, (int)MathF.Round(source.Width * PressScale));
        var scaledHeight = Math.Max(1, (int)MathF.Round(source.Height * PressScale));
        var offsetX = (source.Width - scaledWidth) / 2;
        var offsetY = (source.Height - scaledHeight) / 2;

        using var scaled = RenderKit.Resize(source, scaledWidth, scaledHeight);
        var image = RenderKit.NewImage(source.Width, source.Height, SKColors.Black);
        using var canvas = new SKCanvas(image);
        RenderKit.DrawImage(canvas, scaled, offsetX, offsetY);
        return image;
    }
}
