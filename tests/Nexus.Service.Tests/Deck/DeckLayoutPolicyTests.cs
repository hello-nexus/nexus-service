using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Models.Panel;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// A panel session may rearrange, copy, relabel, and delete the deck keys it
/// has, but never author a key that opens a file, sends a chord, types text,
/// or plays an audio file - those are desktop-authored and only panel-triggered.
/// </summary>
public sealed class DeckLayoutPolicyTests
{
    private static PanelLayoutDto Layout(string deckJson, string widgetId = "w1")
    {
        using var doc = JsonDocument.Parse(deckJson);
        return new PanelLayoutDto
        {
            Pages =
            {
                new PanelPageDto
                {
                    Id = "p1",
                    Widgets =
                    {
                        new PanelWidgetDto
                        {
                            Id = widgetId,
                            Type = "deck",
                            Config = new Dictionary<string, JsonElement> { ["deck"] = doc.RootElement.Clone() },
                        },
                    },
                },
            },
        };
    }

    private static string Deck(params string[] slotActions)
    {
        var slots = string.Join(",", slotActions);
        return "{\"pages\":[{\"slots\":[" + slots + "]}]}";
    }

    private const string Hotkey = "{\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+shift+m\"}}";
    private const string OtherHotkey = "{\"action\":{\"type\":\"hotkey\",\"keys\":\"meta+r\"}}";
    private const string OpenUrl = "{\"action\":{\"type\":\"openUrl\",\"url\":\"https://hellonexus.com\"}}";
    private const string Empty = "{}";

    [Fact]
    public void A_new_hotkey_key_needs_the_desktop()
    {
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(Hotkey)), stored: null));
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(Hotkey)), Layout(Deck(OpenUrl))));
    }

    [Fact]
    public void Unprivileged_keys_are_free_to_author()
    {
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(OpenUrl)), stored: null));
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(OpenUrl, Empty)), Layout(Deck(Empty))));
    }

    [Fact]
    public void Moving_copying_relabelling_or_deleting_an_existing_key_is_allowed()
    {
        var stored = Layout(Deck(Hotkey, Empty));
        // Moved to the second slot, and duplicated.
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(Empty, Hotkey)), stored));
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(Hotkey, Hotkey)), stored));
        // Relabelled: label and icon live on the slot, not the action.
        var relabelled = Deck("{\"label\":\"Mute\",\"color\":\"#f00\",\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+shift+m\"}}");
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(relabelled), stored));
        // Deleted.
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(Empty, Empty)), stored));
    }

    [Fact]
    public void Changing_what_an_existing_key_does_needs_the_desktop()
    {
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(Deck(OtherHotkey)), Layout(Deck(Hotkey))));
    }

    [Fact]
    public void Nested_privileged_actions_are_found_in_sequences_toggles_and_folders()
    {
        var sequence = Deck("{\"action\":{\"type\":\"sequence\",\"steps\":[{\"action\":{\"type\":\"openUrl\",\"url\":\"https://a\"}},{\"action\":{\"type\":\"text\",\"text\":\"hi\"}}]}}");
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(sequence), stored: null));

        var toggle = Deck("{\"action\":{\"type\":\"toggle\",\"on\":{\"type\":\"openUrl\",\"url\":\"https://a\"},\"off\":{\"type\":\"openFile\",\"path\":\"C:\\\\x.exe\"}}}");
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(toggle), stored: null));

        var folder = "{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[" + Hotkey + "]}}]}]}";
        Assert.True(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(folder), stored: null));
        // The same folder already stored: allowed.
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(Layout(folder), Layout(folder)));
    }

    [Fact]
    public void Non_deck_widgets_and_malformed_deck_configs_are_ignored()
    {
        var layout = Layout(Deck(Hotkey));
        layout.Pages[0].Widgets[0].Type = "clock";
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(layout, stored: null));

        using var doc = JsonDocument.Parse("\"not-an-object\"");
        var broken = Layout(Deck(Hotkey));
        broken.Pages[0].Widgets[0].Config!["deck"] = doc.RootElement.Clone();
        Assert.False(DeckLayoutPolicy.IntroducesPrivilegedActions(broken, stored: null));
    }
}
