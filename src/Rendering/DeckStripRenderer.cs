using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Deck;
using SkiaSharp;

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
/// Skia over <see cref="RenderKit"/>; layouts are designed on the Plus
/// capture's segment geometry (DesignWidth x DesignHeight) and scaled to fit.
/// </summary>
public sealed class DeckStripRenderer
{
    public const string DefaultAccentHex = "#4da3ff";

    private const float DesignWidth = 200f;
    private const float DesignHeight = 100f;

    private static readonly SKColor Background = new(0x0b, 0x0e, 0x13);
    private static readonly SKColor DividerColor = new(0x1c, 0x22, 0x2b);
    private static readonly SKColor TitleColor = new(0xc7, 0xd0, 0xdc);
    private static readonly SKColor TrackColor = new(255, 255, 255, 40);
    private static readonly SKColor DotColor = new(255, 255, 255, 70);
    private static readonly SKColor DefaultAccent = RenderKit.ParseColor(DefaultAccentHex, SKColors.White);

    private readonly DeckKeyRenderer _icons;

    public DeckStripRenderer(DeckKeyRenderer icons) => _icons = icons;

    /// <summary>One segment on a dark background. Caller disposes.</summary>
    public SKBitmap RenderSegment(DialSegmentInput input, int width, int height, float feedback = 0f)
    {
        var image = RenderKit.NewImage(width, height, Background);
        if (input.Kind == DialSegmentKind.Empty)
        {
            return image;
        }

        var accent = RenderKit.ParseColor(input.AccentHex, DefaultAccent);
        var scale = MathF.Min(width / DesignWidth, height / DesignHeight);
        var origin = new SKPoint((width - DesignWidth * scale) / 2f, (height - DesignHeight * scale) / 2f);
        SKPoint P(float x, float y) => new(origin.X + x * scale, origin.Y + y * scale);

        using var canvas = new SKCanvas(image);
        if (feedback > 0f)
        {
            RenderKit.FillRect(canvas, accent.WithAlpha((byte)Math.Clamp(feedback * 80f, 0f, 255f)), SKRect.Create(width, height));
        }
        DrawTitle(canvas, input.Title, P(100, 16), scale);
        switch (input.Kind)
        {
            case DialSegmentKind.Value:
                DrawValueBody(canvas, input, accent, P, scale);
                break;
            case DialSegmentKind.Page:
                DrawIcon(canvas, input, P(16, 40), 48 * scale, dim: false);
                DrawValueText(canvas, input.ValueText, P(76, 56), 26 * scale, SKColors.White);
                break;
            case DialSegmentKind.Monitoring:
                DrawMonitoringBody(canvas, input, accent, P, scale);
                break;
            case DialSegmentKind.Custom:
                DrawIcon(canvas, input, P(72, 36), 56 * scale, dim: false);
                break;
        }
        DrawStackDots(canvas, input, accent, P, scale);
        return image;
    }

    /// <summary>The hold-to-edit progress ring centred on an empty segment's background. Caller disposes.</summary>
    public SKBitmap RenderHoldPrompt(float fraction, int width, int height) =>
        DeckHoldPromptRenderer.Render(fraction, width, height, Background);

    /// <summary>All segments side by side in one image, thin dividers between them. Caller disposes.</summary>
    public SKBitmap RenderStrip(IReadOnlyList<DialSegmentInput> segments, int width, int height)
    {
        var strip = RenderKit.NewImage(width, height, Background);
        using var canvas = new SKCanvas(strip);
        var count = Math.Max(segments.Count, 1);
        var segmentWidth = width / count;
        for (var i = 0; i < segments.Count; i++)
        {
            using var segment = RenderSegment(segments[i], segmentWidth, height);
            RenderKit.DrawImage(canvas, segment, i * segmentWidth, 0);
            if (i > 0)
            {
                RenderKit.FillRect(canvas, DividerColor, SKRect.Create(i * segmentWidth, 0, 1, height));
            }
        }
        return strip;
    }

    /// <summary>The Neo info screen. Caller disposes.</summary>
    public SKBitmap RenderInfoScreen(InfoScreenInput input, int width, int height)
    {
        var image = RenderKit.NewImage(width, height, input.Mode == "off" ? SKColors.Black : Background);
        using var canvas = new SKCanvas(image);
        switch (input.Mode)
        {
            case "clock":
                DrawValueText(canvas, input.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture), new SKPoint(width / 2f, height / 2f), height * 0.62f, SKColors.White, centered: true);
                break;
            case "page":
                DrawValueText(canvas, $"{input.Page + 1} / {Math.Max(input.PageCount, 1)}", new SKPoint(width / 2f, height / 2f), height * 0.62f, SKColors.White, centered: true);
                break;
        }
        return image;
    }

    private static void DrawTitle(SKCanvas canvas, string title, SKPoint center, float scale)
    {
        if (string.IsNullOrEmpty(title))
        {
            return;
        }
        using var font = RenderKit.CreateFont(RenderKit.ResolveFont(), MathF.Max(8f, 14f * scale));
        var maxWidth = 184f * scale;
        var text = title;
        while (text.Length > 1 && RenderKit.MeasureWidth(text, font) > maxWidth)
        {
            text = text[..^1];
        }
        if (text != title && text.Length > 1)
        {
            text = text[..^1] + "…";
        }
        RenderKit.DrawCentered(canvas, text, font, TitleColor, center);
    }

    private void DrawValueBody(SKCanvas canvas, DialSegmentInput input, SKColor accent, Func<float, float, SKPoint> p, float scale)
    {
        DrawIcon(canvas, input, p(16, 40), 48 * scale, dim: input.Muted);
        DrawValueText(canvas, input.ValueText, p(76, 56), 26 * scale, input.Muted ? TitleColor : SKColors.White);
        var barOrigin = p(76, 74);
        var bar = SKRect.Create(barOrigin.X, barOrigin.Y, 108 * scale, 12 * scale);
        DrawPill(canvas, bar, TrackColor);
        var fill = input.Muted ? 0 : Math.Clamp(input.Fraction, 0, 1);
        if (fill > 0)
        {
            DrawPill(canvas, SKRect.Create(bar.Left, bar.Top, MathF.Max(bar.Height, (float)(bar.Width * fill)), bar.Height), accent);
        }
    }

    private static void DrawMonitoringBody(SKCanvas canvas, DialSegmentInput input, SKColor accent, Func<float, float, SKPoint> p, float scale)
    {
        DrawValueText(canvas, input.ValueText, p(100, 44), 30 * scale, SKColors.White, centered: true);
        var topLeft = p(16, 62);
        var band = SKRect.Create(topLeft.X, topLeft.Y, 168 * scale, 28 * scale);
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
        RenderKit.FillPath(canvas, accent.WithAlpha(100), RenderKit.BuildFilledSeries(band, normalized));
    }

    private void DrawIcon(SKCanvas canvas, DialSegmentInput input, SKPoint topLeft, float size, bool dim)
    {
        using var glyph = _icons.RenderIconGlyph(input.Icon, input.IconName, Math.Max(1, (int)MathF.Round(size)));
        if (glyph is null)
        {
            return;
        }
        RenderKit.DrawImage(canvas, glyph, (int)MathF.Round(topLeft.X), (int)MathF.Round(topLeft.Y), dim ? 0.4f : 1f);
    }

    private static void DrawValueText(SKCanvas canvas, string text, SKPoint anchor, float fontPx, SKColor color, bool centered = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        using var font = RenderKit.CreateFont(RenderKit.ResolveFont(), MathF.Max(8f, fontPx), bold: true);
        if (centered)
        {
            RenderKit.DrawCentered(canvas, text, font, color, anchor);
            return;
        }
        RenderKit.DrawLeft(canvas, text, font, color, anchor.X, anchor.Y);
    }

    private static void DrawPill(SKCanvas canvas, SKRect rect, SKColor color)
    {
        var radius = rect.Height / 2f;
        using var paint = RenderKit.Fill(color);
        canvas.DrawRoundRect(rect, radius, radius, paint);
    }

    private static void DrawStackDots(SKCanvas canvas, DialSegmentInput input, SKColor accent, Func<float, float, SKPoint> p, float scale)
    {
        if (input.StackCount < 2)
        {
            return;
        }
        const float spacing = 10f;
        var first = 100f - (input.StackCount - 1) * spacing / 2f;
        for (var i = 0; i < input.StackCount; i++)
        {
            RenderKit.FillCircle(canvas, i == input.StackIndex ? accent : DotColor, p(first + i * spacing, 94), 3f * scale);
        }
    }
}
