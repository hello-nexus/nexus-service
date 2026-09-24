using System.Collections.Generic;
using Nexus.Service.Deck;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public class DeckPlatformKeysTests
{
    private static DeckConfig Config() => new()
    {
        Pages = new List<DeckPage>
        {
            new()
            {
                Slots = new List<DeckSlot>
                {
                    new() { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+shift+m", KeysMac = "cmd+shift+m" } },
                    new() { Action = new DeckAction { Type = "hotkey", Keys = "alt+up" } },
                    new()
                    {
                        Folder = new DeckFolder
                        {
                            Slots = new List<DeckSlot>
                            {
                                new()
                                {
                                    Action = new DeckAction
                                    {
                                        Type = "toggle",
                                        On = new DeckAction { Type = "hotkey", Keys = "ctrl+1", KeysMac = "cmd+1" },
                                        Off = new DeckAction { Type = "hotkey", Keys = "ctrl+2", KeysMac = "cmd+2" },
                                    },
                                },
                                new()
                                {
                                    Action = new DeckAction
                                    {
                                        Type = "sequence",
                                        Steps = new List<DeckSequenceStep> { new() { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+s", KeysMac = "cmd+s" } } },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public void ApplyHost_Mac_TakesKeysMacEverywhereAndClearsIt()
    {
        var deck = Config();

        DeckPlatformKeys.ApplyHost(deck, mac: true);

        var slots = deck.Pages[0].Slots;
        Assert.Equal("cmd+shift+m", slots[0].Action!.Keys);
        Assert.Equal("alt+up", slots[1].Action!.Keys);
        var folder = slots[2].Folder!.Slots;
        Assert.Equal("cmd+1", folder[0].Action!.On!.Keys);
        Assert.Equal("cmd+2", folder[0].Action!.Off!.Keys);
        Assert.Equal("cmd+s", folder[1].Action!.Steps![0].Action.Keys);
        Assert.Null(slots[0].Action!.KeysMac);
        Assert.Null(folder[0].Action!.On!.KeysMac);
        Assert.Null(folder[1].Action!.Steps![0].Action.KeysMac);
    }

    [Fact]
    public void ApplyHost_Windows_KeepsKeysAndClearsKeysMac()
    {
        var deck = Config();

        DeckPlatformKeys.ApplyHost(deck, mac: false);

        var slots = deck.Pages[0].Slots;
        Assert.Equal("ctrl+shift+m", slots[0].Action!.Keys);
        Assert.Null(slots[0].Action!.KeysMac);
        Assert.Equal("ctrl+1", slots[2].Folder!.Slots[0].Action!.On!.Keys);
        Assert.Null(slots[2].Folder!.Slots[0].Action!.On!.KeysMac);
    }
}
