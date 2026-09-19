using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>Schema v18 settings-side migration: per-serial legacy deck/presets hoist into the host-wide preset + instance model.</summary>
public sealed class DeckModesMigrationTests
{
    private static DeckConfig OnePageWithLabel(string label) =>
        new() { Pages = { new DeckPage { Slots = { new DeckSlot { Label = label } } } } };

    [Fact]
    public void LegacyDeckWithContent_HoistsIntoANewPresetAndInstance()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "My Deck",
            ProductId = 0x0063, // Mini
            LegacyDeck = OnePageWithLabel("Hi"),
        };

        DeckModesMigration.Apply(doc);

        var deck = doc.StreamDeck.Decks["SERIAL-1"];
        Assert.Null(deck.LegacyDeck);
        Assert.Null(deck.LegacyImageRefs);
        Assert.Null(deck.LegacyPresets);
        Assert.Null(deck.LegacyActivePresetId);

        var instance = doc.StreamDeck.Instances["streamdeck:SERIAL-1"];
        Assert.Equal("custom", instance.Mode);
        var preset = doc.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
        Assert.NotNull(preset);
        Assert.Equal("My Deck", preset!.Name);
        Assert.Equal("Hi", preset.Deck.Pages[0].Slots[0].Label);
        // Mini's real grid, not the 5x3 fallback.
        Assert.Equal(3, preset.Cols);
        Assert.Equal(2, preset.Rows);
    }

    [Fact]
    public void LegacyDeckWithNoContent_CreatesAFreshEmptyPresetNamedAfterTheDeck()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Name = "Empty Deck", LegacyDeck = new DeckConfig() };

        DeckModesMigration.Apply(doc);

        var instance = doc.StreamDeck.Instances["streamdeck:SERIAL-1"];
        var preset = doc.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
        Assert.NotNull(preset);
        Assert.Equal("Empty Deck", preset!.Name);
        Assert.Empty(preset.Deck.Pages);
    }

    [Fact]
    public void LegacyPresets_HoistIntoTheGlobalListWithIdsKept()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "Deck",
            LegacyPresets = new List<DeckPreset>
            {
                new() { Id = "old-1", Name = "Discord", Deck = OnePageWithLabel("A") },
                new() { Id = "old-2", Name = "Spotify", Deck = OnePageWithLabel("B") },
            },
        };

        DeckModesMigration.Apply(doc);

        Assert.Contains(doc.StreamDeck.Presets, p => p.Id == "old-1" && p.Name == "Discord");
        Assert.Contains(doc.StreamDeck.Presets, p => p.Id == "old-2" && p.Name == "Spotify");
        // No live deck content and no ActivePresetId: falls back to the first hoisted preset.
        Assert.Equal("old-1", doc.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId);
    }

    [Fact]
    public void NameCollision_SuffixesWithTheDeckName()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Presets.Add(new DeckPreset { Id = "existing", Name = "Discord", Cols = 5, Rows = 3 });
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "Office Deck",
            LegacyPresets = new List<DeckPreset> { new() { Id = "old-1", Name = "Discord", Deck = OnePageWithLabel("A") } },
        };

        DeckModesMigration.Apply(doc);

        var hoisted = doc.StreamDeck.Presets.Find(p => p.Id == "old-1")!;
        Assert.Equal("Discord (Office Deck)", hoisted.Name);
    }

    [Fact]
    public void ActivePresetId_ResolvesTheOldSelectionWhenItStillExists()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "Deck",
            LegacyActivePresetId = "old-2",
            LegacyDeck = OnePageWithLabel("Live"),
            LegacyPresets = new List<DeckPreset>
            {
                new() { Id = "old-1", Name = "One", Deck = OnePageWithLabel("A") },
                new() { Id = "old-2", Name = "Two", Deck = OnePageWithLabel("B") },
            },
        };

        DeckModesMigration.Apply(doc);

        Assert.Equal("old-2", doc.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Name = "Deck", LegacyDeck = OnePageWithLabel("Hi") };

        DeckModesMigration.Apply(doc);
        var presetCountAfterFirst = doc.StreamDeck.Presets.Count;
        var activeIdAfterFirst = doc.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId;

        DeckModesMigration.Apply(doc);

        Assert.Equal(presetCountAfterFirst, doc.StreamDeck.Presets.Count);
        Assert.Equal(activeIdAfterFirst, doc.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId);
    }

    [Fact]
    public void DeckWithNoLegacyFieldsAtAll_IsUntouched()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { Name = "Deck" };

        DeckModesMigration.Apply(doc);

        Assert.Empty(doc.StreamDeck.Presets);
        Assert.False(doc.StreamDeck.Instances.ContainsKey("streamdeck:SERIAL-1"));
    }
}

/// <summary>Schema v18 panel-layout-side migration: an inline deck widget config hoists into a host-wide preset + widget instance.</summary>
public sealed class DeckWidgetLayoutMigrationTests
{
    private static PanelWidgetDto DeckWidget(string id, string? configJson, string size = "2x2")
    {
        var widget = new PanelWidgetDto { Id = id, Type = "deck", Size = size };
        if (configJson is not null)
        {
            using var doc = JsonDocument.Parse(configJson);
            widget.Config = new Dictionary<string, JsonElement> { ["deck"] = doc.RootElement.Clone() };
        }
        return widget;
    }

    private const string OneLabelSlot = """{"pages":[{"slots":[{"label":"Hi"}]}]}""";

    [Fact]
    public void DeckWidgetOnAPanelDevice_HoistsIntoAPresetNamedAfterThePanel()
    {
        var doc = new NexusSettings();
        doc.PanelDevices["dev1"] = new PanelDeviceRecord
        {
            Id = "dev1",
            DisplayName = "Living Room",
            Layout = new PanelLayoutDto { Pages = { new PanelPageDto { Id = "p1", Widgets = { DeckWidget("w1", OneLabelSlot) } } } },
        };

        DeckModesMigration.Apply(doc);

        var instance = doc.StreamDeck.Instances["widget:w1"];
        var preset = doc.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
        Assert.NotNull(preset);
        Assert.Equal("Living Room deck", preset!.Name);
        Assert.Equal("Hi", preset.Deck.Pages[0].Slots[0].Label);

        var widget = doc.PanelDevices["dev1"].Layout!.Pages[0].Widgets[0];
        Assert.False(widget.Config?.ContainsKey("deck") ?? false);
    }

    [Fact]
    public void DedupesPresetNameWithANumericSuffix()
    {
        var doc = new NexusSettings();
        doc.StreamDeck.Presets.Add(new DeckPreset { Id = "existing", Name = "Living Room deck", Cols = 2, Rows = 2 });
        doc.PanelDevices["dev1"] = new PanelDeviceRecord
        {
            Id = "dev1",
            DisplayName = "Living Room",
            Layout = new PanelLayoutDto { Pages = { new PanelPageDto { Id = "p1", Widgets = { DeckWidget("w1", OneLabelSlot) } } } },
        };

        DeckModesMigration.Apply(doc);

        var preset = doc.StreamDeck.Presets.Find(p => p.Id == doc.StreamDeck.Instances["widget:w1"].ActivePresetId)!;
        Assert.Equal("Living Room deck 2", preset.Name);
    }

    [Fact]
    public void DeckWidgetWithNoContent_StripsTheConfigKeyWithoutCreatingAPreset()
    {
        var doc = new NexusSettings();
        doc.PanelDevices["dev1"] = new PanelDeviceRecord
        {
            Id = "dev1",
            Layout = new PanelLayoutDto { Pages = { new PanelPageDto { Id = "p1", Widgets = { DeckWidget("w1", """{"pages":[{"slots":[{}]}]}""") } } } },
        };

        DeckModesMigration.Apply(doc);

        Assert.Empty(doc.StreamDeck.Presets);
        Assert.False(doc.StreamDeck.Instances.ContainsKey("widget:w1"));
    }

    [Fact]
    public void NonDeckWidget_IsIgnored()
    {
        var doc = new NexusSettings();
        doc.PanelDevices["dev1"] = new PanelDeviceRecord
        {
            Id = "dev1",
            Layout = new PanelLayoutDto { Pages = { new PanelPageDto { Id = "p1", Widgets = { new PanelWidgetDto { Id = "clock", Type = "clock" } } } } },
        };

        DeckModesMigration.Apply(doc);

        Assert.Empty(doc.StreamDeck.Presets);
    }
}
