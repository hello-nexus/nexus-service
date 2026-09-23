using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Matches a media widget's source key (e.g. "Spotify", "Edge", "Music 2",
/// from <see cref="MediaSourceNames.Friendly"/> deduped by MediaPusher.UniqueKey)
/// to the mixer strip that plays it. Pure and platform-independent so it can be
/// unit-tested without a real audio session enumerator.
/// </summary>
public static class VolumeTargetMatcher
{
    /// <summary>Source key (normalized) to candidate strip ids (raw, normalized
    /// at match time). UWP process names carry dots, which Normalize strips,
    /// so the comparison still lands on the same string as a bare "msedge".</summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["edge"] = new[] { "msedge" },
        ["music"] = new[] { "microsoft.media.player", "music.ui" },
        ["mediaplayer"] = new[] { "microsoft.media.player", "music.ui" },
        ["moviestv"] = new[] { "video.ui" },
    };

    public static AudioSessionDto? Match(string source, IReadOnlyList<AudioSessionDto> strips)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var n = Normalize(StripDedupeSuffix(source.Trim()));
        if (n.Length == 0) return null;

        if (Aliases.TryGetValue(n, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var hit = FindById(strips, Normalize(candidate));
                if (hit is not null) return hit;
            }
        }

        var byId = FindById(strips, n);
        if (byId is not null) return byId;

        var byName = FindByName(strips, n);
        if (byName is not null) return byName;

        foreach (var strip in strips)
        {
            if (IsSystem(strip)) continue;
            var normalizedId = Normalize(strip.Id);
            if (normalizedId.Length >= 4 && n.StartsWith(normalizedId, StringComparison.Ordinal))
                return strip;
        }

        return null;
    }

    private static AudioSessionDto? FindById(IReadOnlyList<AudioSessionDto> strips, string normalized)
    {
        foreach (var strip in strips)
        {
            if (IsSystem(strip)) continue;
            if (Normalize(strip.Id) == normalized) return strip;
        }
        return null;
    }

    private static AudioSessionDto? FindByName(IReadOnlyList<AudioSessionDto> strips, string normalized)
    {
        foreach (var strip in strips)
        {
            if (IsSystem(strip)) continue;
            if (Normalize(strip.Name) == normalized) return strip;
        }
        return null;
    }

    private static bool IsSystem(AudioSessionDto strip) =>
        string.Equals(strip.Id, AudioMixerIds.SystemSounds, StringComparison.Ordinal);

    /// <summary>Undoes MediaPusher.UniqueKey's " 2", " 3", ... dedupe suffix.</summary>
    private static string StripDedupeSuffix(string source)
    {
        var i = source.Length;
        while (i > 0 && char.IsDigit(source[i - 1])) i--;
        if (i == source.Length || i == 0 || source[i - 1] != ' ') return source;
        return source[..(i - 1)];
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var count = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) buffer[count++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..count]);
    }
}
