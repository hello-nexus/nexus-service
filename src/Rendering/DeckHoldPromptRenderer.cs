using System;
using SkiaSharp;

namespace Nexus.Service.Rendering;

/// <summary>
/// Renders the "hold to edit" progress ring a blank Stream Deck key shows
/// while held: a faint track ring with an accent arc that fills clockwise from
/// the top as the hold fraction (0..1) advances. At fraction 1 the ring is
/// full and the connection worker fires the open-editor intent. Pure and
/// square (KeyPixelSize per side); the worker runs the result through the same
/// orient/transform/encode pipeline as a monitoring tile.
/// </summary>
internal static class DeckHoldPromptRenderer
{
    private static readonly SKColor DefaultBackground = SKColors.Black;
    private static readonly SKColor Track = new(255, 255, 255, 45);
    private static readonly SKColor Accent = new(0x4d, 0xa3, 0xff);

    public static SKBitmap Render(float fraction, int pixelSize) => Render(fraction, pixelSize, pixelSize, DefaultBackground);

    /// <summary>The same ring centred on a width x height canvas (a dial segment), sized by the shorter edge.</summary>
    public static SKBitmap Render(float fraction, int width, int height, SKColor background)
    {
        var clamped = Math.Clamp(fraction, 0f, 1f);
        var pixelSize = Math.Min(width, height);
        var image = RenderKit.NewImage(width, height, background);
        var center = new SKPoint(width / 2f, height / 2f);
        var outerRadius = pixelSize * 0.40f;
        var thickness = pixelSize * 0.13f;
        var innerRadius = outerRadius - thickness;

        using var canvas = new SKCanvas(image);
        RenderKit.FillPath(canvas, Track, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, 0f, 360f));
        if (clamped > 0f)
        {
            // Clockwise from 12 o'clock: BuildRingSegment measures degrees
            // clockwise from the positive x-axis (screen space, y down),
            // so 12 o'clock is -90.
            RenderKit.FillPath(canvas, Accent, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, -90f, -90f + 360f * clamped));
        }

        return image;
    }
}
