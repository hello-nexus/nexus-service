using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Deck;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Rendering;

public enum DialSegmentKind { Empty, Value, Page, Monitoring, Custom }

/// <summary>Everything one dial segment shows, already resolved by the worker.</summary>
public sealed class DialSegmentInput
{
    public DialSegmentKind Kind { get; init; } = DialSegmentKind.Empty;
    public string Title { get; init; } = "";
    /// <summary>Explicit icon; null falls back to <see cref="IconName"/>.</summary>
    public DeckIcon? Icon { get; init; }
    /// <summary>Lucide name used when <see cref="Icon"/> is unset.</summary>
    public string IconName { get; init; } = "Sliders";
    public string AccentHex { get; init; } = DeckStripRenderer.DefaultAccentHex;
    /// <summary>Text beside the icon or under the title: "42%", "Muted", "3 / 5", "--".</summary>
    public string ValueText { get; init; } = "";
    /// <summary>Bar fill, 0-1.</summary>
    public double Fraction { get; init; }
    public bool Muted { get; init; }
    public int StackCount { get; init; }
    public int StackIndex { get; init; }
    /// <summary>Monitoring sparkline samples, oldest first.</summary>
    public IReadOnlyList<float> History { get; init; } = Array.Empty<float>();

    /// <summary>Content identity without feedback, for change gating.</summary>
    public string StateKey()
    {
        var history = History.Count == 0 ? "" : string.Join(',', History);
        return string.Join('|', Kind, Title, Icon?.Kind, Icon?.Value, IconName, AccentHex, ValueText,
            Fraction.ToString("F3", CultureInfo.InvariantCulture), Muted, StackCount, StackIndex, history);
    }
}

/// <summary>Neo info screen content. Mode: clock | page | off.</summary>
public sealed class InfoScreenInput
{
    public string Mode { get; init; } = "clock";
    public DateTime LocalTime { get; init; }
    public int Page { get; init; }
    public int PageCount { get; init; } = 1;

    public string StateKey() => Mode switch
    {
        "clock" => "clock|" + LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
        "page" => $"page|{Page}|{PageCount}",
        _ => "off",
    };
}

/// <summary>
/// Draws the dial screens: per-dial segments of a touch strip or Galleon
/// screen at any segment size, the full strip, and the Neo info screen. Pure
/// ImageSharp over <see cref="RenderKit"/>; layouts are designed on the Plus
/// capture's segment geometry (DesignWidth x DesignHeight) and scaled to fit.
/// </summary>
public sealed class DeckStripRenderer
{
    public const string DefaultAccentHex = "#4da3ff";

    private const float DesignWidth = 200f;
    private const float DesignHeight = 100f;

    private static readonly Color Background = Color.ParseHex("0b0e13");
    private static readonly Color DividerColor = Color.ParseHex("1c222b");
    private static readonly Color TitleColor = Color.ParseHex("c7d0dc");
    private static readonly Color TrackColor = Color.FromPixel(new Rgba32(255, 255, 255, 40));
    private static readonly Color DotColor = Color.FromPixel(new Rgba32(255, 255, 255, 70));

    private readonly DeckKeyRenderer _icons;

    public DeckStripRenderer(DeckKeyRenderer icons) => _icons = icons;

    /// <summary>One segment on a dark background. Caller disposes.</summary>
    public Image<Rgba32> RenderSegment(DialSegmentInput input, int width, int height, float feedback = 0f)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx => ctx.Fill(Background));
        if (input.Kind == DialSegmentKind.Empty)
        {
            return image;
        }

        var accent = RenderKit.ParseColor(input.AccentHex, Color.ParseHex(DefaultAccentHex));
        var scale = MathF.Min(width / DesignWidth, height / DesignHeight);
        var origin = new PointF((width - DesignWidth * scale) / 2f, (height - DesignHeight * scale) / 2f);
        PointF P(float x, float y) => new(origin.X + x * scale, origin.Y + y * scale);

        image.Mutate(ctx =>
        {
            DrawTitle(ctx, input.Title, P(100, 16), scale);
            switch (input.Kind)
            {
                case DialSegmentKind.Value:
                    DrawValueBody(ctx, image, input, accent, P, scale);
                    break;
                case DialSegmentKind.Page:
                    DrawIcon(ctx, input, P(16, 40), 48 * scale, dim: false);
                    DrawValueText(ctx, input.ValueText, P(76, 56), 26 * scale, Color.White);
                    break;
                case DialSegmentKind.Monitoring:
                    DrawMonitoringBody(ctx, input, accent, P, scale);
                    break;
                case DialSegmentKind.Custom:
                    DrawIcon(ctx, input, P(72, 36), 56 * scale, dim: false);
                    break;
            }
            DrawStackDots(ctx, input, accent, P, scale);
            if (feedback > 0f)
            {
                var glow = accent.ToPixel<Rgba32>();
                ctx.Fill(Color.FromPixel(new Rgba32(glow.R, glow.G, glow.B, (byte)Math.Clamp(feedback * 80f, 0f, 255f))));
                ctx.Draw(accent, MathF.Max(2f, 3f * scale), new RectangleF(0, 0, width, height));
            }
        });
        return image;
    }

    /// <summary>All segments side by side in one image, thin dividers between them. Caller disposes.</summary>
    public Image<Rgba32> RenderStrip(IReadOnlyList<DialSegmentInput> segments, int width, int height)
    {
        var strip = new Image<Rgba32>(width, height);
        strip.Mutate(ctx => ctx.Fill(Background));
        var count = Math.Max(segments.Count, 1);
        var segmentWidth = width / count;
        for (var i = 0; i < segments.Count; i++)
        {
            using var segment = RenderSegment(segments[i], segmentWidth, height);
            strip.Mutate(ctx => ctx.DrawImage(segment, new Point(i * segmentWidth, 0), 1f));
            if (i > 0)
            {
                strip.Mutate(ctx => ctx.Fill(DividerColor, new RectangleF(i * segmentWidth, 0, 1, height)));
            }
        }
        return strip;
    }

    /// <summary>The Neo info screen. Caller disposes.</summary>
    public Image<Rgba32> RenderInfoScreen(InfoScreenInput input, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Fill(input.Mode == "off" ? Color.Black : Background);
            switch (input.Mode)
            {
                case "clock":
                    DrawValueText(ctx, input.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture), new PointF(width / 2f, height / 2f), height * 0.62f, Color.White, centered: true);
                    break;
                case "page":
                    DrawValueText(ctx, $"{input.Page + 1} / {Math.Max(input.PageCount, 1)}", new PointF(width / 2f, height / 2f), height * 0.62f, Color.White, centered: true);
                    break;
            }
        });
        return image;
    }

    private static void DrawTitle(IImageProcessingContext ctx, string title, PointF center, float scale)
    {
        if (string.IsNullOrEmpty(title))
        {
            return;
        }
        var font = RenderKit.ResolveFont().CreateFont(MathF.Max(8f, 14f * scale), FontStyle.Regular);
        var maxWidth = 184f * scale;
        var text = title;
        while (text.Length > 1 && TextMeasurer.MeasureSize(text, new TextOptions(font)).Width > maxWidth)
        {
            text = text[..^1];
        }
        if (text != title && text.Length > 1)
        {
            text = text[..^1] + "…";
        }
        RenderKit.DrawCentered(ctx, text, font, TitleColor, center);
    }

    private void DrawValueBody(IImageProcessingContext ctx, Image<Rgba32> image, DialSegmentInput input, Color accent, Func<float, float, PointF> p, float scale)
    {
        DrawIcon(ctx, input, p(16, 40), 48 * scale, dim: input.Muted);
        DrawValueText(ctx, input.ValueText, p(76, 56), 26 * scale, input.Muted ? TitleColor : Color.White);
        var barOrigin = p(76, 74);
        var bar = new RectangleF(barOrigin.X, barOrigin.Y, 108 * scale, 12 * scale);
        DrawPill(ctx, bar, TrackColor);
        var fill = input.Muted ? 0 : Math.Clamp(input.Fraction, 0, 1);
        if (fill > 0)
        {
            DrawPill(ctx, new RectangleF(bar.X, bar.Y, MathF.Max(bar.Height, (float)(bar.Width * fill)), bar.Height), accent);
        }
    }

    private static void DrawMonitoringBody(IImageProcessingContext ctx, DialSegmentInput input, Color accent, Func<float, float, PointF> p, float scale)
    {
        DrawValueText(ctx, input.ValueText, p(100, 44), 30 * scale, Color.White, centered: true);
        var topLeft = p(16, 62);
        var band = new RectangleF(topLeft.X, topLeft.Y, 168 * scale, 28 * scale);
        if (input.History.Count == 0)
        {
            return;
        }
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var sample in input.History)
        {
            min = MathF.Min(min, sample);
            max = MathF.Max(max, sample);
        }
        var span = max - min;
        var normalized = new float[input.History.Count];
        for (var i = 0; i < normalized.Length; i++)
        {
            normalized[i] = span < 1e-6f ? 0.5f : (input.History[i] - min) / span;
        }
        var fillColor = accent.ToPixel<Rgba32>();
        ctx.Fill(Color.FromPixel(new Rgba32(fillColor.R, fillColor.G, fillColor.B, 100)), RenderKit.BuildFilledSeries(band, normalized));
    }

    private void DrawIcon(IImageProcessingContext ctx, DialSegmentInput input, PointF topLeft, float size, bool dim)
    {
        using var glyph = _icons.RenderIconGlyph(input.Icon, input.IconName, Math.Max(1, (int)MathF.Round(size)));
        if (glyph is null)
        {
            return;
        }
        ctx.DrawImage(glyph, new Point((int)MathF.Round(topLeft.X), (int)MathF.Round(topLeft.Y)), dim ? 0.4f : 1f);
    }

    private static void DrawValueText(IImageProcessingContext ctx, string text, PointF anchor, float fontPx, Color color, bool centered = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var font = RenderKit.ResolveFont().CreateFont(MathF.Max(8f, fontPx), FontStyle.Bold);
        if (centered)
        {
            RenderKit.DrawCentered(ctx, text, font, color, anchor);
            return;
        }
        var size = TextMeasurer.MeasureSize(text, new TextOptions(font));
        ctx.DrawText(text, font, color, new PointF(anchor.X, anchor.Y - size.Height / 2f));
    }

    private static void DrawPill(IImageProcessingContext ctx, RectangleF rect, Color color)
    {
        var radius = rect.Height / 2f;
        ctx.Fill(color, new RectangleF(rect.X + radius, rect.Y, MathF.Max(0f, rect.Width - rect.Height), rect.Height));
        ctx.Fill(color, RenderKit.BuildCircle(new PointF(rect.X + radius, rect.Y + radius), radius, 24));
        ctx.Fill(color, RenderKit.BuildCircle(new PointF(rect.Right - radius, rect.Y + radius), radius, 24));
    }

    private static void DrawStackDots(IImageProcessingContext ctx, DialSegmentInput input, Color accent, Func<float, float, PointF> p, float scale)
    {
        if (input.StackCount < 2)
        {
            return;
        }
        const float spacing = 10f;
        var first = 100f - (input.StackCount - 1) * spacing / 2f;
        for (var i = 0; i < input.StackCount; i++)
        {
            ctx.Fill(i == input.StackIndex ? accent : DotColor, RenderKit.BuildCircle(p(first + i * spacing, 94), 3f * scale, 16));
        }
    }
}
