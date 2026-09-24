using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

/// <summary>
/// Shared ImageSharp drawing primitives for every server-rendered device
/// bitmap (SL-LCD Wireless sensor/clock/animation content, deck monitoring
/// tiles): hex color parsing, a manual arc/ring polygon builder (avoids
/// depending on a specific PathBuilder.AddArc overload), a filled-series
/// polygon for sparkline-style graphs, centered text, and JPEG encode. No IO
/// beyond the in-memory encode buffer.
/// </summary>
internal static class RenderKit
{
    private const int JpegQuality = 85;

    private static readonly string[] PreferredFontFamilies =
    {
        "Segoe UI", "Arial", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Liberation Sans", "Verdana", "Tahoma",
    };

    private static FontFamily? _fontFamily;

    /// <summary>Resolves a cross-platform display font once and caches it for the process lifetime.</summary>
    public static FontFamily ResolveFont()
    {
        if (_fontFamily is { } cached)
        {
            return cached;
        }

        foreach (var name in PreferredFontFamilies)
        {
            if (SystemFonts.TryGet(name, out var family))
            {
                _fontFamily = family;
                return family;
            }
        }

        foreach (var family in SystemFonts.Collection.Families)
        {
            _fontFamily = family;
            return family;
        }

        throw new InvalidOperationException("No system font families available for LCD rendering.");
    }

    public static Color ParseColor(string? hex, Color fallback) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParseHex(hex, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Builds a filled ring segment (an annulus wedge) as a polygon: N points
    /// along the outer radius from startDeg to endDeg, then N points back
    /// along the inner radius. innerRadius = 0 degenerates to a filled pie
    /// slice / full disc. Angles are clockwise from the positive x-axis.
    /// </summary>
    public static IPath BuildRingSegment(PointF center, float innerRadius, float outerRadius, float startDeg, float endDeg, int segments = 96)
    {
        var points = new PointF[segments * 2 + 2];
        var idx = 0;
        for (var i = 0; i <= segments; i++)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new PointF(center.X + outerRadius * MathF.Cos(rad), center.Y + outerRadius * MathF.Sin(rad));
        }
        for (var i = segments; i >= 0; i--)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new PointF(center.X + innerRadius * MathF.Cos(rad), center.Y + innerRadius * MathF.Sin(rad));
        }
        return new Polygon(new LinearLineSegment(points));
    }

    public static IPath BuildCircle(PointF center, float radius, int segments = 96) =>
        BuildRingSegment(center, 0f, radius, 0f, 360f, segments);

    /// <summary>
    /// Builds a short radial tick between innerRadius and outerRadius, in the
    /// same clockwise-from-12-o'clock convention as <see cref="BuildHand"/>.
    /// </summary>
    public static IPath BuildClockTick(PointF center, float innerRadius, float outerRadius, float clockDeg, float angularWidthDeg, int segments = 4)
    {
        var start = clockDeg - 90f - angularWidthDeg / 2f;
        var end = clockDeg - 90f + angularWidthDeg / 2f;
        return BuildRingSegment(center, innerRadius, outerRadius, start, end, segments);
    }

    /// <summary>
    /// Builds a clock hand as a thin quadrilateral from a short tail behind
    /// the center out to the tip. clockDeg is clockwise from 12 o'clock (the
    /// convention hour/minute/second angles are computed in), converted here
    /// to the standard screen-space angle BuildRingSegment/trig uses.
    /// </summary>
    public static IPath BuildHand(PointF center, float length, float clockDeg, float width, float tailFraction = 0.12f)
    {
        var rad = (clockDeg - 90f) * MathF.PI / 180f;
        var dir = new PointF(MathF.Cos(rad), MathF.Sin(rad));
        var perp = new PointF(-dir.Y, dir.X);
        var tip = new PointF(center.X + dir.X * length, center.Y + dir.Y * length);
        var tail = length * tailFraction;
        var back = new PointF(center.X - dir.X * tail, center.Y - dir.Y * tail);
        var half = width / 2f;
        return new Polygon(new LinearLineSegment(new[]
        {
            new PointF(back.X + perp.X * half, back.Y + perp.Y * half),
            new PointF(tip.X + perp.X * half, tip.Y + perp.Y * half),
            new PointF(tip.X - perp.X * half, tip.Y - perp.Y * half),
            new PointF(back.X - perp.X * half, back.Y - perp.Y * half),
        }));
    }

    /// <summary>
    /// Builds a filled area-graph polygon across <paramref name="band"/>: a
    /// baseline along the band's bottom edge, rising to each sample's
    /// normalized height (0 = bottom, 1 = top, clamped), evenly spaced left
    /// to right. Zero samples degenerates to a zero-height baseline (no
    /// visible fill); one sample degenerates to a flat-topped rectangle
    /// spanning the full band width at that sample's height, since a single
    /// point has no line to interpolate.
    /// </summary>
    public static IPath BuildFilledSeries(RectangleF band, IReadOnlyList<float> normalizedValues)
    {
        var n = normalizedValues.Count;
        if (n == 0)
        {
            return new Polygon(new LinearLineSegment(new[]
            {
                new PointF(band.Left, band.Bottom),
                new PointF(band.Right, band.Bottom),
            }));
        }
        if (n == 1)
        {
            var y = band.Bottom - Math.Clamp(normalizedValues[0], 0f, 1f) * band.Height;
            return new Polygon(new LinearLineSegment(new[]
            {
                new PointF(band.Left, band.Bottom),
                new PointF(band.Left, y),
                new PointF(band.Right, y),
                new PointF(band.Right, band.Bottom),
            }));
        }

        var points = new PointF[n + 2];
        points[0] = new PointF(band.Left, band.Bottom);
        for (var i = 0; i < n; i++)
        {
            var x = band.Left + band.Width * i / (n - 1);
            var clamped = Math.Clamp(normalizedValues[i], 0f, 1f);
            var y = band.Bottom - clamped * band.Height;
            points[i + 1] = new PointF(x, y);
        }
        points[n + 1] = new PointF(band.Right, band.Bottom);
        return new Polygon(new LinearLineSegment(points));
    }

    public static void DrawCentered(IImageProcessingContext ctx, string text, Font font, Color color, PointF center)
    {
        var size = TextMeasurer.MeasureSize(text, new TextOptions(font));
        var origin = new PointF(center.X - size.Width / 2f, center.Y - size.Height / 2f);
        ctx.DrawText(text, font, color, origin);
    }

    public static byte[] EncodeJpeg(Image<Rgba32> image)
    {
        // libjpeg-turbo where it loaded, ImageSharp otherwise. Rgba32 is R,G,B,A in
        // memory, hence TJPF_RGBX. The availability check gates the pixel copy: without
        // it a build with no library would pay a discarded full-frame copy per encode.
        if (TurboJpegOneShot.IsAvailable)
        {
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            var native = TurboJpegOneShot.TryCompress(
                pixels, image.Width, image.Height, TurboJpeg.PixelFormatRgbx, JpegQuality);
            if (native is not null)
            {
                return native;
            }
        }
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
        return ms.ToArray();
    }

    /// <summary>Extracts a top-down RGB888 buffer from an RGBA image, dropping alpha, for <see cref="BmpEncoder"/>.</summary>
    public static byte[] ToRgb24(Image<Rgba32> image)
    {
        var buffer = new byte[image.Width * image.Height * 3];
        var offset = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    buffer[offset++] = pixel.R;
                    buffer[offset++] = pixel.G;
                    buffer[offset++] = pixel.B;
                }
            }
        });
        return buffer;
    }

    /// <summary>Extracts a top-down RGBA8888 buffer from an image, keeping alpha (unlike <see cref="ToRgb24"/>) for callers that still need to transform pixels before dropping it.</summary>
    public static byte[] ToRgba32Bytes(Image<Rgba32> image)
    {
        var buffer = new byte[image.Width * image.Height * 4];
        var offset = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    buffer[offset++] = pixel.R;
                    buffer[offset++] = pixel.G;
                    buffer[offset++] = pixel.B;
                    buffer[offset++] = pixel.A;
                }
            }
        });
        return buffer;
    }

    /// <summary>Builds an image from a top-down RGBA8888 buffer, the inverse of <see cref="ToRgba32Bytes"/>.</summary>
    public static Image<Rgba32> FromRgba32Bytes(byte[] rgba, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * width * 4;
                for (var x = 0; x < row.Length; x++)
                {
                    var o = rowOffset + x * 4;
                    row[x] = new Rgba32(rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
                }
            }
        });
        return image;
    }
}
