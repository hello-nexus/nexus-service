using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// Schema v18 one-shot migration: hoists every physical deck's per-serial
/// presets/live config into the host-wide <see cref="StreamDeckSettings.Presets"/>
/// list plus a <see cref="StreamDeckSettings.Instances"/> row, then lifts every
/// deck widget's inline <c>config.deck</c> the same way. Runs inside
/// JsonConfigStore.Migrate, gated by SchemaVersion so it applies exactly once
/// per settings.json.
/// </summary>
public static class DeckModesMigration
{
    /// <summary>The Stream Deck Original's grid - the fallback when a legacy deck's ProductId does not resolve to a known model.</summary>
    private const int FallbackCols = 5;
    private const int FallbackRows = 3;

    public static void Apply(NexusSettings doc)
    {
        var streamDeck = doc.StreamDeck;
        var imageCache = new StreamDeckImageCache();
        foreach (var (serial, deck) in streamDeck.Decks)
        {
            MigrateDeck(streamDeck, serial, deck, imageCache);
        }
        DeckWidgetLayoutMigration.Apply(doc);
    }

    private static void MigrateDeck(StreamDeckSettings streamDeck, string serial, PhysicalDeckSettings deck, StreamDeckImageCache imageCache)
    {
        if (deck.LegacyDeck is null && deck.LegacyImageRefs is null && deck.LegacyPresets is null && deck.LegacyActivePresetId is null)
        {
            return;
        }

        var model = StreamDeckModels.ByProductId(deck.ProductId);
        var cols = model?.Columns ?? FallbackCols;
        var rows = model?.Rows ?? FallbackRows;
        var deckName = string.IsNullOrEmpty(deck.Name) ? (model?.Name ?? serial) : deck.Name;

        string? firstHoistedId = null;
        if (deck.LegacyPresets is { Count: > 0 } legacyPresets)
        {
            foreach (var legacy in legacyPresets)
            {
                var preset = new DeckPreset
                {
                    Id = legacy.Id,
                    Name = UniqueName(streamDeck.Presets, legacy.Name, deckName),
                    Cols = cols,
                    Rows = rows,
                    Deck = legacy.Deck,
                };
                streamDeck.Presets.Add(preset);
                firstHoistedId ??= preset.Id;
            }
        }

        string? deckPresetId = null;
        if (deck.LegacyDeck is { } legacyDeck && DeckConfigNavigation.HasContent(legacyDeck))
        {
            var preset = new DeckPreset
            {
                Id = NewPresetId(),
                Name = UniqueName(streamDeck.Presets, deckName, deckName),
                Cols = cols,
                Rows = rows,
                Deck = legacyDeck,
            };
            streamDeck.Presets.Add(preset);
            deckPresetId = preset.Id;
        }

        var activeId = deck.LegacyActivePresetId is { } legacyActive && streamDeck.Presets.Any(p => p.Id == legacyActive)
            ? legacyActive
            : deckPresetId ?? firstHoistedId;

        if (activeId is null)
        {
            var empty = new DeckPreset
            {
                Id = NewPresetId(),
                Name = UniqueName(streamDeck.Presets, deckName, deckName),
                Cols = cols,
                Rows = rows,
            };
            streamDeck.Presets.Add(empty);
            activeId = empty.Id;
        }

        streamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId(serial)] = new DeckInstance { Mode = "custom", ActivePresetId = activeId };

        deck.LegacyDeck = null;
        deck.LegacyImageRefs = null;
        deck.LegacyPresets = null;
        deck.LegacyActivePresetId = null;
        imageCache.DeleteAll(serial);
    }

    /// <summary>baseName if free (case-insensitive against every preset name so far), else "baseName (suffix)".</summary>
    internal static string UniqueName(List<DeckPreset> presets, string baseName, string suffix)
    {
        if (!presets.Any(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }
        var candidate = $"{baseName} ({suffix})";
        return presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase))
            ? EnsureUnique(presets, candidate)
            : candidate;
    }

    internal static string EnsureUnique(List<DeckPreset> presets, string baseName)
    {
        var n = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} {n}";
            n++;
        }
        while (presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }

    internal static string NewPresetId() => "p-" + Guid.NewGuid().ToString("N")[..12];
}
