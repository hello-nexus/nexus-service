using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Strimer speed and colors used to be one shared setting; each effect now keeps its own. The effect the
/// user was on takes the old values, so it plays as before, and every other effect starts from its default.
/// Runs on the stored file's content, not its schema version: a block lacking the per-effect keys is migrated,
/// and once the next save writes them it never runs again.
/// </summary>
public static class StrimerEffectsMigration
{
    /// <summary>True when the document changed and should be saved.</summary>
    public static bool Apply(NexusSettings doc, string json)
    {
        if (doc.Devices?.StrimerLighting is not { } ls || !Inspect(json, out var hasSpeeds, out var hasColors))
        {
            return false;
        }
        if (hasSpeeds && hasColors)
        {
            return false;
        }
        var key = ls.Mode != "custom" ? ls.Mode : ls.EffectMode ?? "rainbow";
        // A stored key this build does not know plays as the effect the page falls back to, so the values follow it.
        var mode = StrimerLightingModes.Find(key) ?? StrimerEffectLooks.Shared(new StrimerLightingSettings { Mode = key }).Mode;
        key = mode.Key;
        if (!hasSpeeds)
        {
            // The two fastest levels swapped bytes, so the level that sent the same byte as before is the other one.
            var level = ls.Speed switch { 3 => 4, 4 => 3, _ => ls.Speed };
            ls.Speed = level;
            if (mode.HasSpeed)
            {
                ls.ModeSpeeds[key] = level;
            }
        }
        if (!hasColors)
        {
            // The old list stays so a build without per-effect colors still finds its palette.
            if (mode.DefaultColors.Length > 0 && ls.Colors.Count > 0)
            {
                ls.ModeColors[key] = new List<string>(ls.Colors);
            }
        }
        return true;
    }

    /// <summary>False when the file has no Strimer block; otherwise whether it already carries the per-effect keys.</summary>
    private static bool Inspect(string json, out bool hasSpeeds, out bool hasColors)
    {
        hasSpeeds = false;
        hasColors = false;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var devices in parsed.RootElement.EnumerateObject())
            {
                if (!string.Equals(devices.Name, "devices", StringComparison.OrdinalIgnoreCase) || devices.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var block in devices.Value.EnumerateObject())
                {
                    if (!string.Equals(block.Name, "strimerLighting", StringComparison.OrdinalIgnoreCase) || block.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var field in block.Value.EnumerateObject())
                    {
                        hasSpeeds |= string.Equals(field.Name, "modeSpeeds", StringComparison.OrdinalIgnoreCase);
                        hasColors |= string.Equals(field.Name, "modeColors", StringComparison.OrdinalIgnoreCase);
                    }
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }
}
