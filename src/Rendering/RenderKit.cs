using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Nexus.Service.Rendering;

/// <summary>
/// Shared Skia drawing primitives for every server-rendered device bitmap
/// (SL-LCD Wireless sensor/clock/animation content, deck keys, tiles and
/// screens): RGBA bitmaps, hex color parsing, ring/hand/series polygons,
/// centered text, decode, resize and JPEG/PNG encode.
/// </summary>
internal static class RenderKit
{
    private const int JpegQuality = 85;

    /// <summary>
    /// Decode ceiling. Stored deck images cap their encoded size, not their pixel count,
    /// and a small PNG can describe a multi-gigabyte bitmap; no device surface comes close.
    /// </summary>
    private const long MaxDecodePixels = 4096L * 4096L;

    private static readonly string[] PreferredFontFamilies =
    {
        "Segoe UI", "Arial", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Liberation Sans", "Verdana", "Tahoma",
    };

    private static SKTypeface? _fontFamily;
    private static readonly ConcurrentDictionary<string, SKTypeface?> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), SKTypeface> Faces = new();

    /// <summary>One per typeface: each holds the font's tables in native memory. Shape calls lock it.</summary>
    private static readonly ConcurrentDictionary<SKTypeface, SKShaper> Shapers = new();

    /// <summary>Every bitmap here is RGBA8888 premultiplied, so pixel bytes are R,G,B,A in memory.</summary>
    public static SKImageInfo Info(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

    /// <summary>A transparent bitmap. Caller disposes.</summary>
    public static SKBitmap NewImage(int width, int height)
    {
        var image = new SKBitmap(Info(width, height));
        image.Erase(SKColors.Transparent);
        return image;
    }

    /// <summary>A bitmap filled with one color. Caller disposes.</summary>
    public static SKBitmap NewImage(int width, int height, SKColor fill)
    {
        var image = new SKBitmap(Info(width, height));
        image.Erase(fill);
        return image;
    }

    /// <summary>Resolves a cross-platform display font once and caches it for the process lifetime.</summary>
    public static SKTypeface ResolveFont()
    {
        if (_fontFamily is { } cached)
        {
            return cached;
        }

        foreach (var name in PreferredFontFamilies)
        {
            if (TryFamily(name) is { } family)
            {
                _fontFamily = family;
                return family;
            }
        }

        _fontFamily = SKTypeface.Default;
        return _fontFamily;
    }

    /// <summary>The platform's family of that name, or null; Skia answers an unknown name with its default face.</summary>
    public static SKTypeface? TryFamily(string name) => Families.GetOrAdd(name, static key =>
        SKTypeface.FromFamilyName(key) is { } face && string.Equals(face.FamilyName, key, StringComparison.OrdinalIgnoreCase) ? face : null);

    /// <summary>A font of the family at a pixel size; a missing bold or italic face is synthesized. Caller disposes.</summary>
    public static SKFont CreateFont(SKTypeface family, float size, bool bold = false, bool italic = false)
    {
        var face = Faces.GetOrAdd((family.FamilyName, bold, italic), static key =>
            SKTypeface.FromFamilyName(key.Family,
                key.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default);
        return new SKFont(face, size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
            Embolden = bold && face.FontWeight < (int)SKFontStyleWeight.SemiBold,
            SkewX = italic && face.FontSlant == SKFontStyleSlant.Upright ? -0.2f : 0f,
        };
    }

    public static SKColor ParseColor(string? hex, SKColor fallback) =>
        !string.IsNullOrWhiteSpace(hex) && TryParseHex(hex, out var parsed) ? parsed : fallback;

    /// <summary>CSS order, as nexus-web writes it: rgb, rgba, rrggbb or rrggbbaa, with or without a leading '#'.</summary>
    public static bool TryParseHex(string hex, out SKColor color)
    {
        color = default;
        var s = hex.Trim().TrimStart('#');
        if (s.Length is 3 or 4)
        {
            var expanded = new char[s.Length * 2];
            for (var i = 0; i < s.Length; i++)
            {
                expanded[i * 2] = s[i];
                expanded[i * 2 + 1] = s[i];
            }
            s = new string(expanded);
        }
        if (s.Length is not (6 or 8) || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        color = s.Length == 6
            ? new SKColor((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new SKColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static SKColor WithAlpha(SKColor color, float alpha) =>
        color.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f));

    public static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static SKPaint Stroke(SKColor color, float width) =>
        new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };

    /// <summary>Fills and then disposes the path, so a builder call can be passed inline.</summary>
    public static void FillPath(SKCanvas canvas, SKColor color, SKPath path)
    {
        using (path)
        using (var paint = Fill(color))
        {
            canvas.DrawPath(path, paint);
        }
    }

    public static void FillRect(SKCanvas canvas, SKColor color, SKRect rect)
    {
        using var paint = Fill(color);
        canvas.DrawRect(rect, paint);
    }

    public static void FillCircle(SKCanvas canvas, SKColor color, SKPoint center, float radius)
    {
        using var paint = Fill(color);
        canvas.DrawCircle(center, radius, paint);
    }

    /// <summary>
    /// Builds a filled ring segment (an annulus wedge) as a polygon: N points
    /// along the outer radius from startDeg to endDeg, then N points back
    /// along the inner radius. innerRadius = 0 degenerates to a filled pie
    /// slice / full disc. Angles are clockwise from the positive x-axis.
    /// </summary>
    public static SKPath BuildRingSegment(SKPoint center, float innerRadius, float outerRadius, float startDeg, float endDeg, int segments = 96)
    {
        var points = new SKPoint[segments * 2 + 2];
        var idx = 0;
        for (var i = 0; i <= segments; i++)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new SKPoint(center.X + outerRadius * MathF.Cos(rad), center.Y + outerRadius * MathF.Sin(rad));
        }
        for (var i = segments; i >= 0; i--)
        {
            var deg = startDeg + (endDeg - startDeg) * i / segments;
            var rad = deg * MathF.PI / 180f;
            points[idx++] = new SKPoint(center.X + innerRadius * MathF.Cos(rad), center.Y + innerRadius * MathF.Sin(rad));
        }
        return Polygon(points, close: true);
    }

    /// <summary>
    /// Builds a short radial tick between innerRadius and outerRadius, in the
    /// same clockwise-from-12-o'clock convention as <see cref="BuildHand"/>.
    /// </summary>
    public static SKPath BuildClockTick(SKPoint center, float innerRadius, float outerRadius, float clockDeg, float angularWidthDeg, int segments = 4)
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
    public static SKPath BuildHand(SKPoint center, float length, float clockDeg, float width, float tailFraction = 0.12f)
    {
        var rad = (clockDeg - 90f) * MathF.PI / 180f;
        var dir = new SKPoint(MathF.Cos(rad), MathF.Sin(rad));
        var perp = new SKPoint(-dir.Y, dir.X);
        var tip = new SKPoint(center.X + dir.X * length, center.Y + dir.Y * length);
        var tail = length * tailFraction;
        var back = new SKPoint(center.X - dir.X * tail, center.Y - dir.Y * tail);
        var half = width / 2f;
        return Polygon(new[]
        {
            new SKPoint(back.X + perp.X * half, back.Y + perp.Y * half),
            new SKPoint(tip.X + perp.X * half, tip.Y + perp.Y * half),
            new SKPoint(tip.X - perp.X * half, tip.Y - perp.Y * half),
            new SKPoint(back.X - perp.X * half, back.Y - perp.Y * half),
        }, close: true);
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
    public static SKPath BuildFilledSeries(SKRect band, IReadOnlyList<float> normalizedValues)
    {
        var n = normalizedValues.Count;
        if (n == 0)
        {
            return Polygon(new[] { new SKPoint(band.Left, band.Bottom), new SKPoint(band.Right, band.Bottom) }, close: true);
        }
        if (n == 1)
        {
            var y = band.Bottom - Math.Clamp(normalizedValues[0], 0f, 1f) * band.Height;
            return Polygon(new[]
            {
                new SKPoint(band.Left, band.Bottom),
                new SKPoint(band.Left, y),
                new SKPoint(band.Right, y),
                new SKPoint(band.Right, band.Bottom),
            }, close: true);
        }

        var points = new SKPoint[n + 2];
        points[0] = new SKPoint(band.Left, band.Bottom);
        for (var i = 0; i < n; i++)
        {
            var x = band.Left + band.Width * i / (n - 1);
            var clamped = Math.Clamp(normalizedValues[i], 0f, 1f);
            var y = band.Bottom - clamped * band.Height;
            points[i + 1] = new SKPoint(x, y);
        }
        points[n + 1] = new SKPoint(band.Right, band.Bottom);
        return Polygon(points, close: true);
    }

    public static SKPath Polygon(SKPoint[] points, bool close)
    {
        using var builder = new SKPathBuilder();
        builder.AddPoly(points, close);
        return builder.Detach();
    }

    /// <summary>Advance width of a single line at this font.</summary>
    public static float MeasureWidth(string text, SKFont font)
    {
        if (!NeedsShaping(text))
        {
            return font.MeasureText(text);
        }
        var shaper = Shaper(font.Typeface);
        lock (shaper)
        {
            return shaper.Shape(text, font).Width;
        }
    }

    public static void DrawCentered(SKCanvas canvas, string text, SKFont font, SKColor color, SKPoint center) =>
        DrawLine(canvas, text, font, color, center.X, center.Y, SKTextAlign.Center);

    /// <summary>Draws text with its left edge at x and its line centered on centerY.</summary>
    public static void DrawLeft(SKCanvas canvas, string text, SKFont font, SKColor color, float x, float centerY) =>
        DrawLine(canvas, text, font, color, x, centerY, SKTextAlign.Left);

    private static void DrawLine(SKCanvas canvas, string text, SKFont font, SKColor color, float x, float centerY, SKTextAlign align)
    {
        var metrics = font.Metrics;
        var baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;
        using var paint = Fill(color);
        if (!NeedsShaping(text))
        {
            canvas.DrawText(text, x, baseline, align, font, paint);
            return;
        }
        var shaper = Shaper(font.Typeface);
        lock (shaper)
        {
            canvas.DrawShapedText(shaper, text, x, baseline, align, font, paint);
        }
    }

    /// <summary>
    /// Latin draws glyph by glyph; anything past Latin Extended (emoji sequences and
    /// variation selectors, complex scripts) needs HarfBuzz to form its glyphs.
    /// </summary>
    private static bool NeedsShaping(string text)
    {
        foreach (var c in text)
        {
            if (c > '\u024f')
            {
                return true;
            }
        }
        return false;
    }

    private static SKShaper Shaper(SKTypeface typeface) => Shapers.GetOrAdd(typeface, static face => new SKShaper(face));

    /// <summary>Composites src at (x, y) at its own size.</summary>
    public static void DrawImage(SKCanvas canvas, SKBitmap src, int x, int y, float opacity = 1f)
    {
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255f)) };
        canvas.DrawBitmap(src, x, y, SKSamplingOptions.Default, paint);
    }

    /// <summary>
    /// Resampled copy. A shrink halves with bilinear filtering until within 2x
    /// (a mip chain; one cubic pass aliases thin icon strokes past 2x), then a
    /// Mitchell cubic pass lands the exact size. Caller disposes.
    /// </summary>
    public static SKBitmap Resize(SKBitmap src, int width, int height)
    {
        var current = src;
        try
        {
            while (current.Width >= width * 2 && current.Height >= height * 2)
            {
                var half = current.Resize(Info((current.Width + 1) / 2, (current.Height + 1) / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (!ReferenceEquals(current, src))
                {
                    current.Dispose();
                }
                current = half;
            }
            return current.Resize(Info(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        finally
        {
            if (!ReferenceEquals(current, src))
            {
                current.Dispose();
            }
        }
    }

    /// <summary>The rect of src as its own bitmap, transparent where it falls outside src. Caller disposes.</summary>
    public static SKBitmap Crop(SKBitmap src, SKRectI rect)
    {
        var image = NewImage(rect.Width, rect.Height);
        using var pixmap = src.PeekPixels();
        pixmap.ReadPixels(image.Info, image.GetPixels(), image.RowBytes, rect.Left, rect.Top);
        return image;
    }

    /// <summary>
    /// Decodes PNG, JPEG, WebP, GIF (first frame), BMP or ICO bytes, or null when they
    /// are none of these, corrupt, or larger than <see cref="MaxDecodePixels"/>. Caller disposes.
    /// </summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        return Decode(data);
    }

    public static SKBitmap? Decode(Stream stream)
    {
        using var data = SKData.Create(stream);
        return data is null ? null : Decode(data);
    }

    /// <summary>A decode for a process-wide cache: immutable, so concurrent renders only ever read it.</summary>
    public static SKBitmap? DecodeShared(Stream stream)
    {
        var image = Decode(stream);
        image?.SetImmutable();
        return image;
    }

    private static SKBitmap? Decode(SKData data)
    {
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaxDecodePixels)
        {
            return null;
        }
        var image = new SKBitmap(Info(codec.Info.Width, codec.Info.Height));
        if (image.GetPixels() == IntPtr.Zero)
        {
            image.Dispose();
            return null;
        }
        var result = codec.GetPixels(image.Info, image.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return image;
        }
        image.Dispose();
        return null;
    }

    public static byte[] EncodeJpeg(SKBitmap image)
    {
        // libjpeg-turbo where it loaded; Skia's encoder is several times slower on x64
        // (its bundled libjpeg-turbo carries no x86 SIMD), so it is the fallback only.
        // The pixels are premultiplied RGBA, hence TJPF_RGBX straight off the bitmap:
        // a translucent pixel encodes as composited over black, the key's bezel.
        if (image.RowBytes == image.Width * 4
            && TurboJpegOneShot.TryCompress(image.GetPixelSpan(), image.Width, image.Height, TurboJpeg.PixelFormatRgbx, JpegQuality) is { } native)
        {
            return native;
        }
        using var pixmap = image.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        return data?.ToArray() ?? Array.Empty<byte>();
    }

    public static byte[] EncodePng(SKBitmap image)
    {
        using var pixmap = image.PeekPixels();
        using var data = pixmap.Encode(SKPngEncoderOptions.Default);
        return data?.ToArray() ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Extracts a top-down RGB888 buffer for <see cref="BmpEncoder"/>, composited over
    /// black like <see cref="EncodeJpeg"/>, so BMP and JPEG decks show the same key.
    /// </summary>
    public static byte[] ToRgb24(SKBitmap image)
    {
        var pixels = image.GetPixelSpan();
        var buffer = new byte[image.Width * image.Height * 3];
        var dst = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var row = pixels.Slice(y * image.RowBytes, image.Width * 4);
            for (var src = 0; src < row.Length; src += 4)
            {
                buffer[dst++] = row[src];
                buffer[dst++] = row[src + 1];
                buffer[dst++] = row[src + 2];
            }
        }
        return buffer;
    }

    /// <summary>Extracts a top-down unpremultiplied RGBA8888 buffer from an image, keeping alpha (unlike <see cref="ToRgb24"/>).</summary>
    public static unsafe byte[] ToRgba32Bytes(SKBitmap image)
    {
        var buffer = new byte[image.Width * image.Height * 4];
        using var pixmap = image.PeekPixels();
        fixed (byte* dst = buffer)
        {
            pixmap.ReadPixels(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul), (IntPtr)dst, image.Width * 4);
        }
        return buffer;
    }

    /// <summary>Builds an image from a top-down unpremultiplied RGBA8888 buffer, the inverse of <see cref="ToRgba32Bytes"/>.</summary>
    public static unsafe SKBitmap FromRgba32Bytes(byte[] rgba, int width, int height)
    {
        var image = new SKBitmap(Info(width, height));
        fixed (byte* src = rgba)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), (IntPtr)src, width * 4);
            pixmap.ReadPixels(image.Info, image.GetPixels(), image.RowBytes);
        }
        return image;
    }
}
