using Nexus.Service.Deck;
using Nexus.Service.Hotkeys;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class HotkeySlotResolverTests
{
    private static DeckAction Keys(string combo) => new() { Type = "hotkey", Keys = combo };

    private static StreamDeckSettings Settings()
    {
        var folderSlots = new List<DeckSlot>
        {
            new() { Action = Keys("ctrl+1") },
            new() { Action = Keys("ctrl+2") },
        };
        var page0 = new DeckPage
        {
            Slots =
            {
                new DeckSlot { Action = Keys("ctrl+a") },
                new DeckSlot(),
                new DeckSlot { Folder = new DeckFolder { Slots = folderSlots } },
            },
            Dials = new List<DeckDial>
            {
                new()
                {
                    Action = new DeckDialAction
                    {
                        Type = "custom",
                        Push = Keys("ctrl+p"),
                        TurnLeft = Keys("ctrl+l"),
                        TurnRight = Keys("ctrl+r"),
                    },
                },
                new() { Action = new DeckDialAction { Type = "volume" } },
                new()
                {
                    Stack = new List<DeckDial>
                    {
                        new() { Action = new DeckDialAction { Type = "custom", Push = Keys("ctrl+s0") } },
                        new() { Action = new DeckDialAction { Type = "custom", Push = Keys("ctrl+s1") } },
                    },
                },
            },
        };
        // Page 0's folder at index 2 carries its own dials.
        page0.Slots[2].Folder!.Dials = new List<DeckDial>
        {
            new() { Action = new DeckDialAction { Type = "custom", Push = Keys("ctrl+f"), Touch = Keys("ctrl+t") } },
        };
        var page1 = new DeckPage { Slots = { new DeckSlot { Action = Keys("ctrl+b") } } };
        var settings = new StreamDeckSettings();
        settings.Presets.Add(new DeckPreset
        {
            Id = "p1",
            Name = "One",
            Cols = 3,
            Rows = 2,
            Deck = new DeckConfig { Pages = { page0, page1 } },
        });
        return settings;
    }

    private static HotkeyTarget Key(int page, params int[] path) => new()
    {
        Kind = HotkeyTarget.KindDeck,
        PresetId = "p1",
        Slot = new HotkeySlotAddress { Type = HotkeySlotAddress.TypeKey, Page = page, Path = path.ToList() },
    };

    private static HotkeyTarget Dial(int page, int dial, string ev, int[]? folder = null, int? stack = null) => new()
    {
        Kind = HotkeyTarget.KindDeck,
        PresetId = "p1",
        Slot = new HotkeySlotAddress
        {
            Type = HotkeySlotAddress.TypeDial, Page = page, Dial = dial, Event = ev, Stack = stack, FolderPath = folder?.ToList(),
        },
    };

    [Theory]
    [InlineData(0, new[] { 0 }, "ctrl+a")]
    [InlineData(0, new[] { 2, 0 }, "ctrl+1")]
    [InlineData(0, new[] { 2, 1 }, "ctrl+2")]
    [InlineData(1, new[] { 0 }, "ctrl+b")]
    public void Key_slots_resolve_through_folders_and_pages(int page, int[] path, string keys)
    {
        Assert.Equal(keys, HotkeySlotResolver.Resolve(Key(page, path), Settings())?.Keys);
    }

    [Theory]
    [InlineData(0, new[] { 1 })]        // empty slot
    [InlineData(0, new[] { 2 })]        // a folder, not an action
    [InlineData(0, new[] { 9 })]        // index past the list
    [InlineData(0, new[] { 0, 0 })]     // a leaf action used as a folder
    [InlineData(0, new[] { 2, 5 })]     // index past the folder
    [InlineData(5, new[] { 0 })]        // page gone
    public void Key_slots_that_no_longer_resolve_are_orphaned(int page, int[] path)
    {
        Assert.Null(HotkeySlotResolver.Resolve(Key(page, path), Settings()));
    }

    [Fact]
    public void Missing_preset_or_empty_path_is_orphaned()
    {
        var settings = Settings();
        var gone = Key(0, 0);
        gone.PresetId = "nope";
        Assert.Null(HotkeySlotResolver.Resolve(gone, settings));
        Assert.Null(HotkeySlotResolver.Resolve(Key(0), settings));
    }

    [Fact]
    public void Slot_follows_the_current_action_after_an_edit()
    {
        var settings = Settings();
        settings.Presets[0].Deck.Pages[0].Slots[0].Action = Keys("ctrl+z");
        Assert.Equal("ctrl+z", HotkeySlotResolver.Resolve(Key(0, 0), settings)?.Keys);
    }

    [Theory]
    [InlineData("push", "ctrl+p")]
    [InlineData("turnLeft", "ctrl+l")]
    [InlineData("turnRight", "ctrl+r")]
    [InlineData("touch", "ctrl+p")] // touch falls back to push
    public void Dial_events_resolve_to_the_custom_action(string ev, string keys)
    {
        Assert.Equal(keys, HotkeySlotResolver.Resolve(Dial(0, 0, ev), Settings())?.Keys);
    }

    [Fact]
    public void Dial_touch_prefers_its_own_action()
    {
        Assert.Equal("ctrl+t", HotkeySlotResolver.Resolve(Dial(0, 0, "touch", folder: new[] { 2 }), Settings())?.Keys);
    }

    [Fact]
    public void Folder_dials_replace_the_page_dials()
    {
        Assert.Equal("ctrl+f", HotkeySlotResolver.Resolve(Dial(0, 0, "push", folder: new[] { 2 }), Settings())?.Keys);
        // The folder lists one dial, so index 1 is gone there even though the page has it.
        Assert.Null(HotkeySlotResolver.Resolve(Dial(0, 1, "push", folder: new[] { 2 }), Settings()));
    }

    [Fact]
    public void Stacked_dials_need_an_entry_index()
    {
        var settings = Settings();
        Assert.Equal("ctrl+s1", HotkeySlotResolver.Resolve(Dial(0, 2, "push", stack: 1), settings)?.Keys);
        Assert.Null(HotkeySlotResolver.Resolve(Dial(0, 2, "push"), settings));
        Assert.Null(HotkeySlotResolver.Resolve(Dial(0, 2, "push", stack: 4), settings));
    }

    [Theory]
    [InlineData(1, "push")]      // a non-custom dial has no per-event action
    [InlineData(7, "push")]      // dial gone
    [InlineData(0, "bogus")]     // unknown event
    public void Dials_without_an_action_for_the_event_are_orphaned(int dial, string ev)
    {
        Assert.Null(HotkeySlotResolver.Resolve(Dial(0, dial, ev), Settings()));
    }

    [Fact]
    public void Action_targets_and_unknown_slot_types_do_not_resolve_as_deck_slots()
    {
        var settings = Settings();
        Assert.Null(HotkeySlotResolver.Resolve(new HotkeyTarget { Kind = HotkeyTarget.KindAction, Action = Keys("ctrl+a") }, settings));
        var target = Key(0, 0);
        target.Slot!.Type = "weird";
        Assert.Null(HotkeySlotResolver.Resolve(target, settings));
    }
}
