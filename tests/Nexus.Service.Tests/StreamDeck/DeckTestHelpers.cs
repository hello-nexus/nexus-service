using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;

namespace Nexus.Service.Tests.StreamDeck;

internal sealed class FakeProcessIconProvider : IProcessIconProvider
{
    public byte[]? GetIcon(string exePath) => System.Array.Empty<byte>();
}

/// <summary>Shared deck-modes test fixtures: a real DeckKeyRenderer over fakes, and the settings-shape bridge from a test's legacy per-serial Deck config to the host-wide preset/instance model StreamDeckConnectionWorker now reads.</summary>
internal static class DeckTestHelpers
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    public static DeckKeyRenderer NewTestKeyRenderer() => new(
        new DeckImageStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-deck-images-test-" + System.Guid.NewGuid().ToString("N"))),
        new FakeShortcutsProvider(),
        new FakeProcessIconProvider());

    /// <summary>Hoists a test-seeded PhysicalDeckSettings.LegacyDeck into a preset + fixed instance, mirroring DeckModesMigration but with an explicit grid (Mini's, by default) instead of ProductId inference.</summary>
    public static void ActivateLegacyDeck(NexusSettings s, string serial) => ActivateLegacyDeck(s, serial, Mini.Columns, Mini.Rows);

    public static void ActivateLegacyDeck(NexusSettings s, string serial, int cols, int rows)
    {
        var deck = s.StreamDeck.Decks[serial];
        var config = deck.LegacyDeck ?? new DeckConfig();
        var presetId = "p-test-" + serial;
        s.StreamDeck.Presets.RemoveAll(p => p.Id == presetId);
        s.StreamDeck.Presets.Add(new DeckPreset { Id = presetId, Name = serial, Cols = cols, Rows = rows, Deck = config });
        s.StreamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId(serial)] = new DeckInstance { Mode = "fixed", ActivePresetId = presetId };
        deck.LegacyDeck = null;
    }

    public static DeckConfig ActiveDeck(NexusSettings s, string serial)
    {
        var instanceId = DeckInstanceResolver.PhysicalInstanceId(serial);
        var presetId = s.StreamDeck.Instances[instanceId].ActivePresetId!;
        return s.StreamDeck.Presets.First(p => p.Id == presetId).Deck;
    }
}
