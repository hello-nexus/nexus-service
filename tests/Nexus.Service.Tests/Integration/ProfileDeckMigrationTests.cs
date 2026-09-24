using System.IO;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Profile files never pass through JsonConfigStore.Migrate (LoadProfileIntoSettings
/// applies LayoutRotationMigration/AppPrefixMigration itself for the same reason),
/// so a pre-v18 profile's Device (StreamDeck) and Dashboard categories need
/// DeckModesMigration run explicitly too, or switching to that profile silently
/// strands its Stream Deck layout with no preset/instance to resolve.
/// </summary>
public sealed class ProfileDeckMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileDeckMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-profile-deck-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SwitchingToAPreV18Profile_HoistsItsLegacyDeckIntoAPresetAndInstance()
    {
        var target = _profiles.CreateProfile("Target");
        _profiles.CreateProfile("Other"); // becomes active; Target's file stays untouched below

        // Device (Stream Deck) sharing defaults ON, which would otherwise
        // have FlushActiveProfile propagate the (empty) shared StreamDeck
        // block onto every other profile's file - including Target's -
        // before SwitchProfile ever reads it. Turning it off makes the
        // profile's own StreamDeck block the one that actually loads, the
        // scenario a pre-v18 per-profile Stream Deck config predates.
        _store.Update(s => s.SharedCategories.Clear());

        var legacyProfileJson = """
        {
          "streamDeck": {
            "decks": {
              "SERIAL-1": {
                "name": "My Deck",
                "productId": 99,
                "deck": { "pages": [ { "slots": [ { "label": "Hi" } ] } ] }
              }
            }
          }
        }
        """;
        var targetPath = Path.Combine(_tempDir, $"profile-{target.Id}.json");
        File.WriteAllText(targetPath, legacyProfileJson);

        _profiles.SwitchProfile(target.Id);

        var settings = _store.Load();
        var instance = Assert.Contains("streamdeck:SERIAL-1", settings.StreamDeck.Instances);
        Assert.Equal("custom", instance.Mode);
        var preset = settings.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
        Assert.NotNull(preset);
        Assert.Equal("My Deck", preset!.Name);
        Assert.Equal("Hi", preset.Deck.Pages[0].Slots[0].Label);
        Assert.Null(settings.StreamDeck.Decks["SERIAL-1"].LegacyDeck);
    }
}
