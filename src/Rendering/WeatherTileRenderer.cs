using System.Collections.Concurrent;
using System.Reflection;
using SkiaSharp;

namespace Nexus.Service.Rendering;

/// <summary>
/// Everything <see cref="WeatherTileRenderer.Render"/> needs to draw one
/// weather tile. TemperatureText and LocationLabel already carry the caller's
/// unit conversion and localization (never reformatted here), mirroring
/// MonitoringTileInput's ValueText convention.
/// </summary>
public sealed class WeatherTileInput
{
    public string TemperatureText { get; init; } = "";
    public string LocationLabel { get; init; } = "";
    /// <summary>Open-Meteo WMO weather code, selects the condition glyph. -1 when unknown.</summary>
    public int WeatherCode { get; init; } = -1;
    public string? BackgroundColorHex { get; init; }
    public string? TitleColorHex { get; init; }
}

/// <summary>
/// Draws a weather deck tile (condition glyph / temperature / location) into a
/// square Skia bitmap at any pixel size. The glyph is the same lucide icon
/// nexus-web's WeatherIcon draws (rasterized white-on-transparent, embedded), and
/// the icon/temperature/location stack mirrors DeckWeatherCell's proportions, so
/// the physical key matches the desktop preview. Pure: no deck, HID, or
/// persistence knowledge, matching MonitoringTileRenderer's shape so both live
/// tiles share the same StreamDeckConnectionWorker render/push path.
/// </summary>
internal static class WeatherTileRenderer
{
    private static readonly SKColor DefaultBackground = new(0x0e, 0x11, 0x16);
    private static readonly SKColor DefaultTitleColor = SKColors.White;
    private static readonly SKColor TextShadow = new(0, 0, 0, 200);

    // Fractions of the square key, matching DeckWeatherCell.module.scss's flex
    // column (icon 42cqmin, temp 28cqmin, city 12-15px) centered as a stack.
    private const float IconSizeFraction = 0.44f;
    private const float IconCenterYFraction = 0.30f;
    private const float TemperatureCenterYFraction = 0.63f;
    private const float TemperatureFontSizeFraction = 0.28f;
    private const float LocationCenterYFraction = 0.87f;
    private const float LocationFontSizeFraction = 0.145f;

    private static readonly ConcurrentDictionary<string, SKBitmap?> IconCache = new();

    public static SKBitmap Render(WeatherTileInput input, int pixelSize)
    {
        var background = RenderKit.ParseColor(input.BackgroundColorHex, DefaultBackground);
        var image = RenderKit.NewImage(pixelSize, pixelSize, background);
        var titleColor = RenderKit.ParseColor(input.TitleColorHex, DefaultTitleColor);
        var font = RenderKit.ResolveFont();

        using var canvas = new SKCanvas(image);
        DrawIcon(canvas, input.WeatherCode, pixelSize);

        if (input.TemperatureText.Length > 0)
        {
            using var tempFont = RenderKit.CreateFont(font, pixelSize * TemperatureFontSizeFraction, bold: true);
            DrawShadowedText(canvas, input.TemperatureText, tempFont, titleColor,
                new SKPoint(pixelSize / 2f, pixelSize * TemperatureCenterYFraction), pixelSize);
        }
        if (input.LocationLabel.Length > 0)
        {
            using var locationFont = RenderKit.CreateFont(font, pixelSize * LocationFontSizeFraction);
            var label = FitLocation(input.LocationLabel, locationFont, pixelSize * 0.96f);
            DrawShadowedText(canvas, label, locationFont, titleColor,
                new SKPoint(pixelSize / 2f, pixelSize * LocationCenterYFraction), pixelSize);
        }

        return image;
    }

    private static void DrawIcon(SKCanvas canvas, int weatherCode, int size)
    {
        var icon = LoadIcon(IconResourceName(weatherCode));
        if (icon is null)
        {
            return;
        }
        var iconPx = (int)MathF.Round(size * IconSizeFraction);
        if (iconPx < 1)
        {
            return;
        }
        using var scaled = RenderKit.Resize(icon, iconPx, iconPx);
        var left = (int)MathF.Round(size / 2f - iconPx / 2f);
        var top = (int)MathF.Round(size * IconCenterYFraction - iconPx / 2f);

        // Drop shadow (mirrors DeckWeatherCell's drop-shadow) so the white glyph
        // reads on a bright custom tile color: a black silhouette offset down.
        var shadowOffset = Math.Max(1, size / 72);
        using (var silhouette = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn))
        using (var shadow = new SKPaint { ColorFilter = silhouette, Color = SKColors.White.WithAlpha(140) })
        {
            canvas.DrawBitmap(scaled, left, top + shadowOffset, SKSamplingOptions.Default, shadow);
        }
        RenderKit.DrawImage(canvas, scaled, left, top);
    }

    private static void DrawShadowedText(SKCanvas canvas, string text, SKFont font, SKColor color, SKPoint center, int size)
    {
        var offset = Math.Max(1, size / 72);
        RenderKit.DrawCentered(canvas, text, font, TextShadow, new SKPoint(center.X, center.Y + offset));
        RenderKit.DrawCentered(canvas, text, font, color, center);
    }

    private static string FitLocation(string label, SKFont font, float maxWidth)
    {
        if (RenderKit.MeasureWidth(label, font) <= maxWidth)
        {
            return label;
        }
        var trimmed = label;
        while (trimmed.Length > 1
            && RenderKit.MeasureWidth(trimmed + "…", font) > maxWidth)
        {
            trimmed = trimmed[..^1];
        }
        return trimmed + "…";
    }

    /// <summary>Lucide icon name for an Open-Meteo WMO code - the exact mapping nexus-web's WeatherIcon uses.</summary>
    private static string IconResourceName(int code) => code switch
    {
        0 or 1 => "sun",
        2 => "cloud-sun",
        3 => "cloud",
        45 or 48 => "cloud-fog",
        >= 51 and <= 57 => "cloud-drizzle",
        >= 61 and <= 67 => "cloud-rain",
        >= 71 and <= 77 or 85 or 86 => "cloud-snow",
        >= 80 and <= 82 => "cloud-rain-wind",
        >= 95 and <= 99 => "cloud-lightning",
        _ => "circle-help",
    };

    private static SKBitmap? LoadIcon(string name) => IconCache.GetOrAdd(name, static key =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"weather-icon-{key}.png");
        return stream is null ? null : RenderKit.DecodeShared(stream);
    });
}
