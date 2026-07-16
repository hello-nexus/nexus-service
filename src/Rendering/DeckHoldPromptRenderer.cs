using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Renders the "hold to edit" progress ring a blank Stream Deck key shows
/// while held: a faint track ring with an accent arc that fills clockwise from
/// the top as the hold fraction (0..1) advances. At fraction 1 the ring is
/// full and the connection worker fires the open-editor intent. Pure and
/// square (KeyPixelSize per side); the worker runs the result through the same
/// orient/transform/encode pipeline as a monitoring tile. Uses only primitive
/// fills, so it is AOT-clean like the rest of RenderKit.
/// </summary>
internal static class DeckHoldPromptRenderer
{
    private static readonly Color Background = Color.Black;
    private static readonly Color Track = Color.FromPixel(new Rgba32(255, 255, 255, 45));
    private static readonly Color Accent = Color.ParseHex("#4DA3FF");

    public static Image<Rgba32> Render(float fraction, int pixelSize)
    {
        var clamped = Math.Clamp(fraction, 0f, 1f);
        var image = new Image<Rgba32>(pixelSize, pixelSize);
        var center = new PointF(pixelSize / 2f, pixelSize / 2f);
        var outerRadius = pixelSize * 0.40f;
        var thickness = pixelSize * 0.13f;
        var innerRadius = outerRadius - thickness;

        image.Mutate(ctx =>
        {
            ctx.Fill(Background);
            ctx.Fill(Track, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, 0f, 360f));
            if (clamped > 0f)
            {
                // Clockwise from 12 o'clock: BuildRingSegment measures degrees
                // clockwise from the positive x-axis (screen space, y down),
                // so 12 o'clock is -90.
                ctx.Fill(Accent, RenderKit.BuildRingSegment(center, innerRadius, outerRadius, -90f, -90f + 360f * clamped));
            }
        });

        return image;
    }
}
