using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Validation and copying for the per-skin accent, font and background mode map on a panel record.</summary>
public static class PanelSkinOverrides
{
    public const int MaxEntries = 64;

    private static readonly HashSet<string> BackgroundModes = new() { "solid", "shader", "media", "skin" };

    /// <summary>Returns the route error code for a map with too many entries, a non-slug, padded or empty key, an invalid font or an invalid background, else null.</summary>
    public static string? Validate(Dictionary<string, PanelSkinOverride> input)
    {
        if (input.Count > MaxEntries)
        {
            return "invalid_skin";
        }
        foreach (var (key, value) in input)
        {
            if (!PanelSkinValue.TryNormalize(key, out var slug) || slug != key)
            {
                return "invalid_skin";
            }
            if (value?.Font is not null && !PanelFontValue.TryNormalize(value.Font, out _))
            {
                return "invalid_font";
            }
            if (value is not null && !TryNormalizeBackground(value, out _, out _))
            {
                return "invalid_background";
            }
        }
        return null;
    }

    // Blank fields clear; an unknown mode or a non-slug effect fails. The effect is dropped unless the mode is shader.
    private static bool TryNormalizeBackground(PanelSkinOverride value, out string? mode, out string? effect)
    {
        mode = null;
        effect = null;
        if (!string.IsNullOrWhiteSpace(value.BackgroundMode))
        {
            mode = value.BackgroundMode.Trim();
            if (!BackgroundModes.Contains(mode))
            {
                return false;
            }
        }
        if (value.BackgroundEffect is not null && !PanelSkinValue.TryNormalize(value.BackgroundEffect, out effect))
        {
            return false;
        }
        if (mode != "shader")
        {
            effect = null;
        }
        return true;
    }

    /// <summary>Normalizes a patched map. Returns false when Validate fails; entries with no accent, font or background mode are dropped.</summary>
    public static bool TryNormalize(Dictionary<string, PanelSkinOverride> input, out Dictionary<string, PanelSkinOverride>? normalized)
    {
        normalized = null;
        if (Validate(input) is not null)
        {
            return false;
        }
        var result = new Dictionary<string, PanelSkinOverride>();
        foreach (var (key, value) in input)
        {
            PanelSkinValue.TryNormalize(key, out var skin);
            if (skin is null || value is null)
            {
                continue;
            }
            string? font = null;
            if (value.Font is not null)
            {
                PanelFontValue.TryNormalize(value.Font, out font);
            }
            var accent = string.IsNullOrWhiteSpace(value.Accent) ? null : value.Accent.Trim();
            TryNormalizeBackground(value, out var mode, out var effect);
            if (accent is null && font is null && mode is null)
            {
                continue;
            }
            result[skin] = new PanelSkinOverride { Accent = accent, Font = font, BackgroundMode = mode, BackgroundEffect = effect };
        }
        normalized = result.Count == 0 ? null : result;
        return true;
    }

    public static Dictionary<string, PanelSkinOverride>? Clone(Dictionary<string, PanelSkinOverride>? source)
    {
        if (source is null)
        {
            return null;
        }
        var copy = new Dictionary<string, PanelSkinOverride>(source.Count);
        foreach (var (key, value) in source)
        {
            if (value is null)
            {
                continue;
            }
            copy[key] = new PanelSkinOverride { Accent = value.Accent, Font = value.Font, BackgroundMode = value.BackgroundMode, BackgroundEffect = value.BackgroundEffect };
        }
        return copy;
    }
}
