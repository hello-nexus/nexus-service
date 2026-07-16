using System.Collections.Concurrent;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
/// square ImageSharp image at any pixel size. The glyph is the same lucide icon
/// nexus-web's WeatherIcon draws (rasterized white-on-transparent, embedded), and
/// the icon/temperature/location stack mirrors DeckWeatherCell's proportions, so
/// the physical key matches the desktop preview. Pure: no deck, HID, or
/// persistence knowledge, matching MonitoringTileRenderer's shape so both live
/// tiles share the same StreamDeckConnectionWorker render/push path.
/// </summary>
internal static class WeatherTileRenderer
{
    private static readonly Color DefaultBackground = Color.ParseHex("0e1116");
    private static readonly Color DefaultTitleColor = Color.White;

    // Fractions of the square key, matching DeckWeatherCell.module.scss's flex
    // column (icon 42cqmin, temp 28cqmin, city 12-15px) centered as a stack.
    private const float IconSizeFraction = 0.44f;
    private const float IconCenterYFraction = 0.30f;
    private const float TemperatureCenterYFraction = 0.63f;
    private const float TemperatureFontSizeFraction = 0.28f;
    private const float LocationCenterYFraction = 0.87f;
    private const float LocationFontSizeFraction = 0.145f;

    private static readonly ConcurrentDictionary<string, Image<Rgba32>?> IconCache = new();

    public static Image<Rgba32> Render(WeatherTileInput input, int pixelSize)
    {
        var image = new Image<Rgba32>(pixelSize, pixelSize);
        var background = RenderKit.ParseColor(input.BackgroundColorHex, DefaultBackground);
        var titleColor = RenderKit.ParseColor(input.TitleColorHex, DefaultTitleColor);
        var font = RenderKit.ResolveFont();

        image.Mutate(ctx =>
        {
            ctx.Fill(background);
            DrawIcon(ctx, input.WeatherCode, pixelSize);

            if (input.TemperatureText.Length > 0)
            {
                var tempFont = font.CreateFont(pixelSize * TemperatureFontSizeFraction, FontStyle.Bold);
                DrawShadowedText(ctx, input.TemperatureText, tempFont, titleColor,
                    new PointF(pixelSize / 2f, pixelSize * TemperatureCenterYFraction), pixelSize);
            }
            if (input.LocationLabel.Length > 0)
            {
                var locationFont = font.CreateFont(pixelSize * LocationFontSizeFraction, FontStyle.Regular);
                var label = FitLocation(input.LocationLabel, locationFont, pixelSize * 0.96f);
                DrawShadowedText(ctx, label, locationFont, titleColor,
                    new PointF(pixelSize / 2f, pixelSize * LocationCenterYFraction), pixelSize);
            }
        });

        return image;
    }

    private static void DrawIcon(IImageProcessingContext ctx, int weatherCode, int size)
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
        using var scaled = icon.Clone(c => c.Resize(iconPx, iconPx));
        var left = (int)MathF.Round(size / 2f - iconPx / 2f);
        var top = (int)MathF.Round(size * IconCenterYFraction - iconPx / 2f);

        // Drop shadow (mirrors DeckWeatherCell's drop-shadow) so the white glyph
        // reads on a bright custom tile color: a black silhouette offset down.
        var shadowOffset = Math.Max(1, size / 72);
        using (var shadow = scaled.Clone(c => c.Brightness(0)))
        {
            ctx.DrawImage(shadow, new Point(left, top + shadowOffset), 0.55f);
        }
        ctx.DrawImage(scaled, new Point(left, top), 1f);
    }

    private static void DrawShadowedText(IImageProcessingContext ctx, string text, Font font, Color color, PointF center, int size)
    {
        var offset = Math.Max(1, size / 72);
        RenderKit.DrawCentered(ctx, text, font, Color.FromRgba(0, 0, 0, 200), new PointF(center.X, center.Y + offset));
        RenderKit.DrawCentered(ctx, text, font, color, center);
    }

    private static string FitLocation(string label, Font font, float maxWidth)
    {
        if (TextMeasurer.MeasureSize(label, new TextOptions(font)).Width <= maxWidth)
        {
            return label;
        }
        var trimmed = label;
        while (trimmed.Length > 1
            && TextMeasurer.MeasureSize(trimmed + "…", new TextOptions(font)).Width > maxWidth)
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

    private static Image<Rgba32>? LoadIcon(string name) => IconCache.GetOrAdd(name, static key =>
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"weather-icon-{key}.png");
        return stream is null ? null : Image.Load<Rgba32>(stream);
    });
}
