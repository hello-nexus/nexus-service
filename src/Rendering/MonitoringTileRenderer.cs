using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Nexus.Service.Rendering;

public enum MonitoringTileStyle { Line, Segments, Backdrop, Number }

/// <summary>
/// Everything <see cref="MonitoringTileRenderer.Render"/> needs to draw one
/// tile, already resolved by the caller (title-style overrides vs deck
/// defaults) except the LabelText-vs-Name and fixed-vs-adaptive domain
/// choices, which Render itself resolves. History is the sample buffer to
/// plot, oldest first, with the current reading as its last entry; an empty
/// buffer renders a blank graph with the current reading treated as 0.
/// </summary>
public sealed class MonitoringTileInput
{
    /// <summary>Default top label (already device-prefixed, e.g. "CPU Total"), shown when LabelText is unset or empty.</summary>
    public string Name { get; init; } = "";
    /// <summary>Custom top label overriding Name. Empty or null falls back to Name.</summary>
    public string? LabelText { get; init; }
    public bool ShowName { get; init; } = true;
    /// <summary>The value already formatted for display (unit-scaled, temp-unit converted, number-format localized by the caller). Never reformatted here.</summary>
    public string ValueText { get; init; } = "";
    /// <summary>HardwareSensor.Type (Load, Temperature, Clock, ...), selects the graph/arc domain when Scale is not fixed.</summary>
    public string SensorType { get; init; } = "";
    public IReadOnlyList<float> History { get; init; } = Array.Empty<float>();
    public MonitoringTileStyle Style { get; init; } = MonitoringTileStyle.Line;
    /// <summary>"fixed" pins the graph/fill domain to [Min,Max]; anything else (including null) is adaptive.</summary>
    public string? Scale { get; init; }
    public float? Min { get; init; }
    public float? Max { get; init; }
    public string? AccentColorHex { get; init; }
    public string? BackgroundColorHex { get; init; }
    /// <summary>"default" | "arial" | "georgia" | "courierNew", matching nexus-web's DECK_TITLE_FONTS ids. Null/unrecognized falls back to the platform default.</summary>
    public string? TitleFont { get; init; }
    /// <summary>Percent of the tile's pixel edge, matching nexus-web's DeckTitleStyle.size convention. Null falls back to MonitoringTileRenderer.DefaultTitleSizePercent.</summary>
    public int? TitleSize { get; init; }
    public bool TitleBold { get; init; }
    public bool TitleItalic { get; init; }
    public string? TitleColorHex { get; init; }
}

/// <summary>
/// Draws a monitoring deck tile (sensor name / value / graph) into a square
/// Skia bitmap at any pixel size. Pure: no deck, HID, or persistence
/// knowledge, so it is independently testable. This is the sole renderer for
/// a physical key's bitmap; the same render also feeds the editor's live
/// preview, broadcast as a streamdeckTiles frame, so the two are
/// pixel-identical by construction rather than needing to be kept in parity.
/// </summary>
internal static class MonitoringTileRenderer
{
    private static readonly SKColor DefaultBackground = new(0x0e, 0x11, 0x16);
    private static readonly SKColor DefaultAccent = new(0x4d, 0xa3, 0xff);
    private static readonly SKColor DefaultTitleColor = SKColors.White;
    private static readonly SKColor TrackColor = new(255, 255, 255, 40);

    private const int DefaultTitleSizePercent = 16;
    private const int MinTitleSizePercent = 8;
    private const int MaxTitleSizePercent = 30;

    private const float NameYFraction = 0.14f;
    private const float ValueYFraction = 0.88f;
    private const float ValueFontSizeFraction = 0.15f;

    private const float LineBandTopWithName = 0.28f;
    private const float LineBandTopNoName = 0.10f;
    private const float LineBandBottom = 0.74f;
    private const float LineBandInsetXFraction = 0.08f;

    private const float LineFillAlpha = 0.4f;
    /// <summary>Stroke thickness scales with the tile's pixel size, so it reads consistently across key sizes.</summary>
    private const float LineStrokeThicknessFraction = 0.02f;
    private const float LineStrokeMinPx = 1f;

    /// <summary>Dimmer than Line/Segments' fill alpha so the value text Render overlays on top stays legible against the full-bleed history fill.</summary>
    private const float BackdropDimAlpha = 0.45f;

    private const int SegmentsCount = 16;
    private const float SegmentsGapFraction = 0.014f;

    private const float NumberBigFontFraction = 0.30f;
    private const float NumberUnitFontFraction = 0.11f;

    /// <summary>
    /// Maps a persisted deck action style string to a render style. Legacy
    /// "radial" (the arc style segments replaced) reads as Segments but is
    /// never written back; unrecognized or absent values fall back to Line.
    /// </summary>
    internal static MonitoringTileStyle ParseStyle(string? style) => style switch
    {
        "segments" => MonitoringTileStyle.Segments,
        "radial" => MonitoringTileStyle.Segments,
        "backdrop" => MonitoringTileStyle.Backdrop,
        "number" => MonitoringTileStyle.Number,
        _ => MonitoringTileStyle.Line,
    };

    public static SKBitmap Render(MonitoringTileInput input, int pixelSize)
    {
        var background = RenderKit.ParseColor(input.BackgroundColorHex, DefaultBackground);
        var image = RenderKit.NewImage(pixelSize, pixelSize, background);
        var accent = RenderKit.ParseColor(input.AccentColorHex, DefaultAccent);
        var titleColor = RenderKit.ParseColor(input.TitleColorHex, DefaultTitleColor);
        var titleFont = ResolveTitleFont(input.TitleFont);
        var titleSizePx = TitlePixelSize(input.TitleSize, pixelSize);
        var displayName = string.IsNullOrEmpty(input.LabelText) ? input.Name : input.LabelText;
        var nameShown = input.ShowName && !string.IsNullOrEmpty(displayName);
        var domain = ResolveDomain(input);

        using var canvas = new SKCanvas(image);

        // Backdrop's history fill spans the whole key face edge to edge,
        // behind the name/value text (mirrors BackdropGauge.tsx's
        // absolute inset:0 chart layer), so it draws before the name.
        if (input.Style == MonitoringTileStyle.Backdrop)
        {
            RenderBackdrop(canvas, input, pixelSize, accent, domain);
        }

        if (nameShown)
        {
            using var nameFont = RenderKit.CreateFont(titleFont, titleSizePx, input.TitleBold, input.TitleItalic);
            RenderKit.DrawCentered(canvas, displayName!, nameFont, titleColor, new SKPoint(pixelSize / 2f, pixelSize * NameYFraction));
        }

        switch (input.Style)
        {
            case MonitoringTileStyle.Number:
                RenderNumber(canvas, input, pixelSize, titleFont, titleColor, nameShown);
                break;
            case MonitoringTileStyle.Segments:
                RenderSegments(canvas, input, pixelSize, accent, domain, nameShown);
                DrawBottomValue(canvas, input, pixelSize, titleFont, titleColor);
                break;
            // Backdrop overlays the value big and centered on the graph,
            // reusing Number's sizing/position, with no bottom value row.
            case MonitoringTileStyle.Backdrop:
                RenderNumber(canvas, input, pixelSize, titleFont, titleColor, nameShown);
                break;
            default:
                RenderLine(canvas, input, pixelSize, accent, domain, nameShown);
                DrawBottomValue(canvas, input, pixelSize, titleFont, titleColor);
                break;
        }

        return image;
    }

    private static void DrawBottomValue(SKCanvas canvas, MonitoringTileInput input, int size, SKTypeface font, SKColor color)
    {
        if (string.IsNullOrEmpty(input.ValueText))
        {
            return;
        }
        using var valueFont = RenderKit.CreateFont(font, size * ValueFontSizeFraction, bold: true);
        RenderKit.DrawCentered(canvas, input.ValueText, valueFont, color, new SKPoint(size / 2f, size * ValueYFraction));
    }

    private static SKRect ComputeGraphBand(int size, bool nameShown)
    {
        var bandTop = size * (nameShown ? LineBandTopWithName : LineBandTopNoName);
        var bandBottom = size * LineBandBottom;
        var inset = size * LineBandInsetXFraction;
        return SKRect.Create(inset, bandTop, size - inset * 2f, bandBottom - bandTop);
    }

    private static List<float> NormalizeSeries(IReadOnlyList<float> history, (float Min, float Max) domain)
    {
        var normalized = new List<float>(history.Count);
        foreach (var sample in history)
        {
            normalized.Add(Normalize(sample, domain.Min, domain.Max));
        }
        return normalized;
    }

    /// <summary>
    /// Draws a translucent accent-fill area topped with a full-opacity accent
    /// stroke along the series' top edge, an open path over the fill.
    /// </summary>
    private static void RenderLine(SKCanvas canvas, MonitoringTileInput input, int size, SKColor accent, (float Min, float Max) domain, bool nameShown)
    {
        var band = ComputeGraphBand(size, nameShown);
        var normalized = NormalizeSeries(input.History, domain);

        RenderKit.FillPath(canvas, RenderKit.WithAlpha(accent, LineFillAlpha), RenderKit.BuildFilledSeries(band, normalized));

        var topEdge = BuildTopEdge(band, normalized);
        if (topEdge.Length >= 2)
        {
            var thickness = Math.Max(LineStrokeMinPx, size * LineStrokeThicknessFraction);
            using var stroke = RenderKit.Stroke(accent, thickness);
            using var path = RenderKit.Polygon(topEdge, close: false);
            canvas.DrawPath(path, stroke);
        }
    }

    /// <summary>
    /// Fills the full history series edge to edge across the whole key face,
    /// in a dimmed accent rather than the bold accent Line/Segments use; the
    /// Backdrop case in Render overlays the value on top afterward.
    /// </summary>
    private static void RenderBackdrop(SKCanvas canvas, MonitoringTileInput input, int size, SKColor accent, (float Min, float Max) domain)
    {
        var band = SKRect.Create(0f, 0f, size, size);
        var normalized = NormalizeSeries(input.History, domain);
        RenderKit.FillPath(canvas, RenderKit.WithAlpha(accent, BackdropDimAlpha), RenderKit.BuildFilledSeries(band, normalized));
    }

    /// <summary>
    /// The top-edge points of RenderKit.BuildFilledSeries' polygon, open (no
    /// baseline corners) so it strokes as a line rather than a closed shape.
    /// Matches that method's x/y math point for point. A single sample draws
    /// no stroke: nexus-web's Sparkline line path for one point is a bare
    /// SVG moveto with no line segment to stroke.
    /// </summary>
    private static SKPoint[] BuildTopEdge(SKRect band, IReadOnlyList<float> normalizedValues)
    {
        var n = normalizedValues.Count;
        if (n <= 1)
        {
            return Array.Empty<SKPoint>();
        }

        var points = new SKPoint[n];
        for (var i = 0; i < n; i++)
        {
            var x = band.Left + band.Width * i / (n - 1);
            var clamped = Math.Clamp(normalizedValues[i], 0f, 1f);
            var y = band.Bottom - clamped * band.Height;
            points[i] = new SKPoint(x, y);
        }
        return points;
    }

    /// <summary>
    /// A row of SegmentsCount pill-shaped bars across the graph band, filled
    /// left to right by the current reading's fill fraction. Reuses the Line
    /// band position so the middle graph area lines up across styles.
    /// </summary>
    private static void RenderSegments(SKCanvas canvas, MonitoringTileInput input, int size, SKColor accent, (float Min, float Max) domain, bool nameShown)
    {
        var band = ComputeGraphBand(size, nameShown);

        var gap = Math.Max(1f, size * SegmentsGapFraction);
        var segmentWidth = (band.Width - gap * (SegmentsCount - 1)) / SegmentsCount;
        if (segmentWidth <= 0f)
        {
            return;
        }

        var current = input.History.Count > 0 ? input.History[^1] : 0f;
        var fraction = FillFraction(current, domain);
        var filledCount = (int)Math.Clamp(MathF.Round(fraction * SegmentsCount, MidpointRounding.AwayFromZero), 0f, (float)SegmentsCount);

        using var track = RenderKit.Fill(TrackColor);
        using var fill = RenderKit.Fill(accent);
        for (var i = 0; i < SegmentsCount; i++)
        {
            var x = band.Left + i * (segmentWidth + gap);
            var rect = SKRect.Create(x, band.Top, segmentWidth, band.Height);
            // A radius of half the bar's width yields a full pill.
            canvas.DrawRoundRect(rect, segmentWidth / 2f, segmentWidth / 2f, i < filledCount ? fill : track);
        }
    }

    private static void RenderNumber(SKCanvas canvas, MonitoringTileInput input, int size, SKTypeface font, SKColor color, bool nameShown)
    {
        var (numberText, unitText) = SplitFormatted(input.ValueText);
        if (numberText.Length == 0)
        {
            return;
        }
        var centerY = size * (nameShown ? 0.58f : 0.52f);
        using var bigFont = RenderKit.CreateFont(font, size * NumberBigFontFraction, bold: true);
        if (unitText.Length == 0)
        {
            RenderKit.DrawCentered(canvas, numberText, bigFont, color, new SKPoint(size / 2f, centerY));
            return;
        }

        // Value + unit sit on one line, the unit small and immediately after
        // the value on its baseline, the pair centered as a group.
        using var unitFont = RenderKit.CreateFont(font, size * NumberUnitFontFraction);
        var numWidth = RenderKit.MeasureWidth(numberText, bigFont);
        var unitWidth = RenderKit.MeasureWidth(unitText, unitFont);
        var gap = size * 0.015f;
        var groupLeft = size / 2f - (numWidth + gap + unitWidth) / 2f;
        RenderKit.DrawCentered(canvas, numberText, bigFont, color, new SKPoint(groupLeft + numWidth / 2f, centerY));
        var unitCenterX = groupLeft + numWidth + gap + unitWidth / 2f;
        var big = bigFont.Metrics;
        var unit = unitFont.Metrics;
        var unitCenterY = centerY - (big.Ascent + big.Descent) / 2f + (unit.Ascent + unit.Descent) / 2f;
        RenderKit.DrawCentered(canvas, unitText, unitFont, color, new SKPoint(unitCenterX, unitCenterY));
    }

    /// <summary>
    /// A valid fixed scale ([min,max] both set, finite, max greater than min)
    /// wins outright; otherwise falls back to the sensorType/history adaptive
    /// rules. Governs the segments/backdrop fill and the line/backdrop series
    /// axis; the number style does not consume a domain.
    /// </summary>
    internal static (float Min, float Max) ResolveDomain(MonitoringTileInput input) =>
        TryFixedDomain(input.Scale, input.Min, input.Max, out var fixedDomain)
            ? fixedDomain
            : ResolveDomain(input.SensorType, input.History);

    private static bool TryFixedDomain(string? scale, float? min, float? max, out (float Min, float Max) domain)
    {
        domain = default;
        if (scale != "fixed" || min is null || max is null)
        {
            return false;
        }
        if (!float.IsFinite(min.Value) || !float.IsFinite(max.Value) || max.Value <= min.Value)
        {
            return false;
        }
        domain = (min.Value, max.Value);
        return true;
    }

    /// <summary>
    /// Load/Temperature/Control/Level are fixed 0-100 so a graph/arc reads
    /// consistently regardless of the sample window; everything else
    /// auto-scales to its own history's min/max.
    /// </summary>
    internal static (float Min, float Max) ResolveDomain(string sensorType, IReadOnlyList<float> history)
    {
        if (string.Equals(sensorType, "Load", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Control", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sensorType, "Level", StringComparison.OrdinalIgnoreCase))
        {
            return (0f, 100f);
        }
        if (history.Count == 0)
        {
            return (0f, 1f);
        }
        var min = history[0];
        var max = history[0];
        for (var i = 1; i < history.Count; i++)
        {
            if (history[i] < min)
            {
                min = history[i];
            }
            if (history[i] > max)
            {
                max = history[i];
            }
        }
        return (min, max);
    }

    /// <summary>A degenerate domain (no variation yet, or a single sample) renders at a neutral mid-fill rather than 0 or 100.</summary>
    private static float Normalize(float value, float min, float max)
    {
        if (max <= min)
        {
            return 0.5f;
        }
        return Math.Clamp((value - min) / (max - min), 0f, 1f);
    }

    /// <summary>
    /// Single-value fill fraction for value-fill styles (Segments' filled
    /// count): value/domainMax, not a min/max normalization like the line
    /// graph's y-axis, so a fixed 0-100 domain reads as a true percent-of-100
    /// fill. A degenerate domain (including a NaN bound) still renders a
    /// neutral mid-fill rather than 0 or 100 - the `!(Max &gt; Min)` guard
    /// shape, not `Max &lt;= Min`, is what catches a NaN bound (every
    /// comparison against NaN is false, so `Max &lt;= Min` would miss it and
    /// fall through to dividing by it). A non-degenerate domain whose Max is
    /// 0 or non-finite renders 0 rather than dividing by it.
    /// </summary>
    internal static float FillFraction(float value, (float Min, float Max) domain)
    {
        if (!(domain.Max > domain.Min))
        {
            return 0.5f;
        }
        if (!float.IsFinite(domain.Max) || domain.Max == 0f)
        {
            return 0f;
        }
        return Math.Clamp(value / domain.Max, 0f, 1f);
    }

    private static float TitlePixelSize(int? sizePercent, int pixelSize)
    {
        var clamped = Math.Clamp(sizePercent ?? DefaultTitleSizePercent, MinTitleSizePercent, MaxTitleSizePercent);
        return pixelSize * clamped / 100f;
    }

    private static SKTypeface ResolveTitleFont(string? fontId)
    {
        var family = fontId switch
        {
            "arial" => RenderKit.TryFamily("Arial"),
            "georgia" => RenderKit.TryFamily("Georgia"),
            "courierNew" => RenderKit.TryFamily("Courier New"),
            _ => null,
        };
        return family ?? RenderKit.ResolveFont();
    }

    /// <summary>Splits a leading numeric run (digits, '.', '-', ',') from its trailing unit suffix, e.g. "4713MHz" -> ("4713", "MHz"). Never reformats the value.</summary>
    private static (string Number, string Unit) SplitFormatted(string formatted)
    {
        var i = 0;
        while (i < formatted.Length && (char.IsDigit(formatted[i]) || formatted[i] == '.' || formatted[i] == '-' || formatted[i] == ','))
        {
            i++;
        }
        if (i == 0)
        {
            return (formatted, "");
        }
        return i == formatted.Length ? (formatted, "") : (formatted[..i], formatted[i..]);
    }
}
