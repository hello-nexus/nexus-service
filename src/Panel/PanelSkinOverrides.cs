using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Validation and copying for the per-skin accent and font map on a panel record.</summary>
public static class PanelSkinOverrides
{
    public const int MaxEntries = 64;

    /// <summary>Returns the route error code for a map with too many entries, a non-slug key or an invalid font, else null.</summary>
    public static string? Validate(Dictionary<string, PanelSkinOverride> input)
    {
        if (input.Count > MaxEntries)
        {
            return "invalid_skin";
        }
        foreach (var (key, value) in input)
        {
            if (!PanelSkinValue.TryNormalize(key, out _))
            {
                return "invalid_skin";
            }
            if (value?.Font is not null && !PanelFontValue.TryNormalize(value.Font, out _))
            {
                return "invalid_font";
            }
        }
        return null;
    }

    /// <summary>Normalizes a patched map. Returns false when it has too many entries, a non-slug key or an invalid font; entries with no accent and no font are dropped.</summary>
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
            if (accent is null && font is null)
            {
                continue;
            }
            result[skin] = new PanelSkinOverride { Accent = accent, Font = font };
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
            copy[key] = new PanelSkinOverride { Accent = value.Accent, Font = value.Font };
        }
        return copy;
    }
}
