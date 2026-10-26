using Nexus.Service.Deck;
using Nexus.Service.Hotkeys;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class HotkeySlotResolverTests
{
    private static DeckAction Keys(string combo) => new() { Type = "hotkey", Keys = combo };

    private static DeckDial Custom(DeckDialHotkeyTags? tags, DeckAction? push = null, DeckAction? turnLeft = null, DeckAction? touch = null) => new()
    {
        Action = new DeckDialAction { Type = "custom", Push = push, TurnLeft = turnLeft, Touch = touch, HotkeyTags = tags },
    };

    private static StreamDeckSettings Settings()
    {
        var folder = new DeckFolder
        {
            Slots =
            {
                new DeckSlot { Action = Keys("ctrl+1"), HotkeyTag = "in-folder" },
                new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Action = Keys("ctrl+deep"), HotkeyTag = "deep" } } } },
            },
            Dials = new List<DeckDial> { Custom(new DeckDialHotkeyTags { Push = "folder-dial" }, push: Keys("ctrl+fd")) },
        };
        var page0 = new DeckPage
        {
            Slots =
            {
                new DeckSlot { Action = Keys("ctrl+a"), HotkeyTag = "key-a" },
                new DeckSlot { HotkeyTag = "empty-slot" },
                new DeckSlot { Folder = folder, HotkeyTag = "folder-itself" },
            },
            Dials = new List<DeckDial>
            {
                Custom(new DeckDialHotkeyTags { Push = "d-push", TurnLeft = "d-left", Touch = "d-touch" }, push: Keys("ctrl+p"), turnLeft: Keys("ctrl+l")),
                new DeckDial
                {
                    Stack = new List<DeckDial>
                    {
                        Custom(new DeckDialHotkeyTags { Push = "stack-0" }, push: Keys("ctrl+s0")),
                        Custom(new DeckDialHotkeyTags { TurnRight = "stack-1" }, push: Keys("ctrl+s1")),
                    },
                },
            },
        };
        var page1 = new DeckPage { Slots = { new DeckSlot { Action = Keys("ctrl+b"), HotkeyTag = "key-b" } } };
        var settings = new StreamDeckSettings();
        settings.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Cols = 3, Rows = 2, Deck = new DeckConfig { Pages = { page0, page1 } } });
        return settings;
    }

    private static HotkeyTarget Tag(string tag, string preset = "p1") => new() { Kind = HotkeyTarget.KindDeck, PresetId = preset, Tag = tag };

    [Theory]
    [InlineData("key-a", "ctrl+a")]
    [InlineData("in-folder", "ctrl+1")]
    [InlineData("deep", "ctrl+deep")]
    [InlineData("key-b", "ctrl+b")]
    public void Key_tags_resolve_through_folders_and_pages(string tag, string keys)
    {
        Assert.Equal(keys, HotkeySlotResolver.Resolve(Tag(tag), Settings())?.Keys);
    }

    [Theory]
    [InlineData("d-push", "ctrl+p")]
    [InlineData("d-touch", "ctrl+p")] // touch falls back to push
    [InlineData("stack-0", "ctrl+s0")]
    [InlineData("folder-dial", "ctrl+fd")]
    public void Dial_event_tags_resolve_to_the_event_action(string tag, string keys)
    {
        Assert.Equal(keys, HotkeySlotResolver.Resolve(Tag(tag), Settings())?.Keys);
    }

    [Fact]
    public void A_stacked_dials_own_action_tags_do_not_match_only_its_entries_do()
    {
        var settings = Settings();
        var stacked = settings.Presets[0].Deck.Pages[0].Dials![1];
        stacked.Action = new DeckDialAction
        {
            Type = "custom",
            Push = Keys("ctrl+own"),
            HotkeyTags = new DeckDialHotkeyTags { Push = "own-tag" },
        };
        Assert.Null(HotkeySlotResolver.Resolve(Tag("own-tag"), settings));
        Assert.Equal("ctrl+s0", HotkeySlotResolver.Resolve(Tag("stack-0"), settings)?.Keys);

        // With a single entry the stack is not active and the dial's own action is what runs.
        stacked.Stack!.RemoveAt(1);
        Assert.Equal("ctrl+own", HotkeySlotResolver.Resolve(Tag("own-tag"), settings)?.Keys);
        Assert.Null(HotkeySlotResolver.Resolve(Tag("stack-0"), settings));
    }

    [Fact]
    public void A_tag_on_a_dial_event_without_an_action_is_orphaned()
    {
        Assert.Null(HotkeySlotResolver.Resolve(Tag("stack-1"), Settings()));
    }

    [Theory]
    [InlineData("empty-slot")]      // tagged slot holds no action
    [InlineData("folder-itself")]   // tagged slot is a folder
    [InlineData("nope")]            // tag not in the preset
    [InlineData("")]
    public void Tags_that_do_not_land_on_an_action_are_orphaned(string tag)
    {
        Assert.Null(HotkeySlotResolver.Resolve(Tag(tag), Settings()));
    }

    [Fact]
    public void Missing_preset_is_orphaned()
    {
        Assert.Null(HotkeySlotResolver.Resolve(Tag("key-a", "gone"), Settings()));
    }

    [Fact]
    public void The_tag_follows_its_slot_when_keys_are_swapped_and_pages_removed()
    {
        var settings = Settings();
        var page0 = settings.Presets[0].Deck.Pages[0];
        (page0.Slots[0], page0.Slots[1]) = (page0.Slots[1], page0.Slots[0]);
        Assert.Equal("ctrl+a", HotkeySlotResolver.Resolve(Tag("key-a"), settings)?.Keys);

        settings.Presets[0].Deck.Pages.RemoveAt(0);
        Assert.Null(HotkeySlotResolver.Resolve(Tag("key-a"), settings));
        Assert.Equal("ctrl+b", HotkeySlotResolver.Resolve(Tag("key-b"), settings)?.Keys);
    }

    [Fact]
    public void Slot_follows_the_current_action_after_an_edit()
    {
        var settings = Settings();
        settings.Presets[0].Deck.Pages[0].Slots[0].Action = Keys("ctrl+z");
        Assert.Equal("ctrl+z", HotkeySlotResolver.Resolve(Tag("key-a"), settings)?.Keys);
    }

    [Fact]
    public void A_duplicate_tag_resolves_to_the_first_match_in_page_slot_then_dial_order()
    {
        var settings = Settings();
        var page0 = settings.Presets[0].Deck.Pages[0];
        // Same tag on a later key, in a folder before the page dials, and on a dial: the earliest slot wins.
        page0.Slots[2].Folder!.Slots[0].HotkeyTag = "dup";
        page0.Slots[0].HotkeyTag = "dup";
        Assert.Equal("ctrl+a", HotkeySlotResolver.Resolve(Tag("dup"), settings)?.Keys);

        page0.Slots[0].HotkeyTag = "key-a";
        Assert.Equal("ctrl+1", HotkeySlotResolver.Resolve(Tag("dup"), settings)?.Keys);

        page0.Slots[2].Folder!.Slots[0].HotkeyTag = "in-folder";
        page0.Dials![0].Action!.HotkeyTags!.Touch = "dup";
        page0.Dials[1].Stack![0].Action!.HotkeyTags!.Push = "dup";
        Assert.Equal("ctrl+p", HotkeySlotResolver.Resolve(Tag("dup"), settings)?.Keys);
    }

    [Fact]
    public void Action_targets_do_not_resolve_as_deck_slots()
    {
        Assert.Null(HotkeySlotResolver.Resolve(new HotkeyTarget { Kind = HotkeyTarget.KindAction, Action = Keys("ctrl+a") }, Settings()));
    }

    [Fact]
    public void Tags_survive_a_json_round_trip_of_the_deck_config()
    {
        var deck = Settings().Presets[0].Deck;
        var json = System.Text.Json.JsonSerializer.Serialize(deck, Nexus.Service.Serialization.PersistenceJsonContext.Default.DeckConfig);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.DeckConfig)!;
        var settings = new StreamDeckSettings();
        settings.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Deck = back });
        Assert.Equal("ctrl+deep", HotkeySlotResolver.Resolve(Tag("deep"), settings)?.Keys);
        Assert.Equal("ctrl+s0", HotkeySlotResolver.Resolve(Tag("stack-0"), settings)?.Keys);
        Assert.Contains("\"hotkeyTag\"", json);
        Assert.Contains("\"hotkeyTags\"", json);
    }
}
