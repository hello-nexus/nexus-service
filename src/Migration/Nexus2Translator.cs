using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Migration;

/// <summary>Root-level (not per-profile) translations: the active profile
/// lookup, q60-rotation, and settings.general.language.</summary>
internal static class Nexus2Translator
{
    /// <summary>nexus-web/src/locales/*.json basenames Nexus 2's Language codes
    /// are checked against; an N2 language not in this set is skipped rather
    /// than imported as an unsupported locale.</summary>
    public static readonly IReadOnlyList<string> SupportedLocales = new[]
    {
        "cs", "de", "en", "es", "fr", "fur", "it", "ja", "ko", "nl", "pl", "pt", "pt-BR", "ru", "tr", "zh-CN", "zh-TW",
    };

    public static JsonElement? FindActiveProfile(JsonElement root)
    {
        var profiles = Nexus2Json.GetArray(root, "profiles");
        if (profiles is not { } arr)
        {
            return null;
        }
        foreach (var p in arr.EnumerateArray())
        {
            if (Nexus2Json.GetBool(p, "active", false))
            {
                return p;
            }
        }
        return null;
    }

    public static string? TranslateRotation(JsonElement root) => Nexus2Json.GetString(root, "q60-rotation") switch
    {
        "portrait" => DisplayOrientations.Portrait,
        "flipped" => DisplayOrientations.PortraitFlipped,
        _ => null,
    };

    public static string? TranslateLanguage(JsonElement root)
    {
        var settings = Nexus2Json.GetObject(root, "settings");
        var general = settings is { } s ? Nexus2Json.GetObject(s, "general") : null;
        var lang = general is { } g ? Nexus2Json.GetString(g, "language") : null;
        if (string.IsNullOrEmpty(lang))
        {
            return null;
        }
        foreach (var candidate in SupportedLocales)
        {
            if (string.Equals(candidate, lang, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return null;
    }
}
