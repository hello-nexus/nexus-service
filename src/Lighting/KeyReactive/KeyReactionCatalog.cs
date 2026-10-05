using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>Effect, colour-mode and background ids a <see cref="KeyReaction"/> may carry, and the bounds its numbers clamp to.</summary>
public static class KeyReactionCatalog
{
    // The first four are the keeb's onboard reactive set (SingleKey,
    // HorizontalLine, VerticalLine, Ripple), generalised to any board's geometry.
    public const string Fade = "fade";
    public const string RowSweep = "rowSweep";
    public const string ColumnSweep = "columnSweep";
    public const string Ripple = "ripple";
    public const string Crosshair = "crosshair";
    public const string Starburst = "starburst";
    public const string Heatmap = "heatmap";
    public const string Sparks = "sparks";
    public const string Lightning = "lightning";
    public const string Trace = "trace";

    public static readonly string[] Effects =
    {
        Fade, RowSweep, ColumnSweep, Ripple, Crosshair, Starburst, Heatmap, Sparks, Lightning, Trace,
    };

    public const string ColorCustom = "custom";
    public const string ColorRainbow = "rainbow";
    public const string ColorRandom = "random";
    public static readonly string[] ColorModes = { ColorCustom, ColorRainbow, ColorRandom };

    public const string BackgroundEffect = "effect";
    public const string BackgroundDim = "dim";
    public const string BackgroundDark = "dark";
    public const string BackgroundReveal = "reveal";
    public static readonly string[] Backgrounds = { BackgroundEffect, BackgroundDim, BackgroundDark, BackgroundReveal };

    public const string DefaultColor = "#ff2d55";

    public const float MinSpeed = 0.25f;
    public const float MaxSpeed = 3f;
    public const float MinSize = 0.5f;
    public const float MaxSize = 3f;

    /// <summary>Below this a "keyboard" lights zones, not keys (the KM7), so reactions are not offered.</summary>
    public const int MinKeyLeds = 40;

    /// <summary>A copy with unknown ids replaced by defaults and numbers clamped; never returns the argument.</summary>
    public static KeyReaction Sanitize(KeyReaction? input)
    {
        var src = input ?? new KeyReaction();
        return new KeyReaction
        {
            Enabled = src.Enabled,
            Effect = Pick(src.Effect, Effects, Ripple),
            ColorMode = Pick(src.ColorMode, ColorModes, ColorCustom),
            Color = StaticColorHex.TryParse(src.Color, out var r, out var g, out var b)
                ? $"#{r:x2}{g:x2}{b:x2}"
                : DefaultColor,
            Speed = Clamp(src.Speed, MinSpeed, MaxSpeed, 1f),
            Size = Clamp(src.Size, MinSize, MaxSize, 1f),
            Background = Pick(src.Background, Backgrounds, BackgroundEffect),
        };
    }

    private static string Pick(string? value, string[] allowed, string fallback) =>
        value is not null && Array.IndexOf(allowed, value) >= 0 ? value : fallback;

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
