using System.Collections.Generic;
using Nexus.Service.Deck;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public class DeckConfigNavigationTests
{
    private static DeckConfig TwoPageConfig()
    {
        var config = new DeckConfig();
        config.Pages.Add(new DeckPage
        {
            Slots =
            {
                new DeckSlot { Label = "P0S0" },
                new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot { Label = "P0F0S0" } } } },
            },
        });
        config.Pages.Add(new DeckPage
        {
            Slots = { new DeckSlot { Label = "P1S0" } },
        });
        return config;
    }

    [Fact]
    public void ResolveView_AtRootOfEachPage_ReturnsThatPagesOwnSlots()
    {
        var config = TwoPageConfig();

        var page0Root = DeckConfigNavigation.ResolveView(config, 0, new List<int>());
        var page1Root = DeckConfigNavigation.ResolveView(config, 1, new List<int>());

        Assert.Equal("P0S0", page0Root![0].Label);
        Assert.Equal("P1S0", page1Root![0].Label);
    }

    [Fact]
    public void ResolveView_IntoAFolderOnOnePage_DoesNotSeeTheOtherPagesFolders()
    {
        var config = TwoPageConfig();

        var folderView = DeckConfigNavigation.ResolveView(config, 0, new List<int> { 1 });

        Assert.Equal("P0F0S0", folderView![0].Label);
    }

    [Fact]
    public void ResolveView_PageOutOfRange_ReturnsNull()
    {
        var config = TwoPageConfig();

        Assert.Null(DeckConfigNavigation.ResolveView(config, 2, new List<int>()));
        Assert.Null(DeckConfigNavigation.ResolveView(config, -1, new List<int>()));
    }

    [Fact]
    public void ResolveView_FolderPathIntoALeafSlot_ReturnsNull()
    {
        var config = TwoPageConfig();

        // Slot 0 on page 1 is a leaf (no Folder), so descending into it fails.
        Assert.Null(DeckConfigNavigation.ResolveView(config, 1, new List<int> { 0 }));
    }

    [Fact]
    public void ResolveSlot_OnASpecificPage_ResolvesThatPagesSlot()
    {
        var config = TwoPageConfig();

        var slot = DeckConfigNavigation.ResolveSlot(config, 1, new List<int> { 0 });

        Assert.Equal("P1S0", slot!.Label);
    }

    [Fact]
    public void ResolveSlot_SamePathDifferentPage_ResolvesDifferentSlots()
    {
        var config = TwoPageConfig();

        var onPage0 = DeckConfigNavigation.ResolveSlot(config, 0, new List<int> { 0 });
        var onPage1 = DeckConfigNavigation.ResolveSlot(config, 1, new List<int> { 0 });

        Assert.Equal("P0S0", onPage0!.Label);
        Assert.Equal("P1S0", onPage1!.Label);
    }

    [Fact]
    public void ParseImageRefSlotPath_RootLevelKey_ReturnsThePageAndAnEmptyFolderPath()
    {
        var parsed = DeckConfigNavigation.ParseImageRefSlotPath("0.3");

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed!.Value.Page);
        Assert.Empty(parsed.Value.FolderPath);
        Assert.Equal(3, parsed.Value.SlotIndex);
    }

    [Fact]
    public void ParseImageRefSlotPath_NestedFolderKey_ReturnsThePageAndTheFolderChain()
    {
        var parsed = DeckConfigNavigation.ParseImageRefSlotPath("2.1.5");

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Page);
        Assert.Equal(new List<int> { 1 }, parsed.Value.FolderPath);
        Assert.Equal(5, parsed.Value.SlotIndex);
    }

    [Fact]
    public void ParseImageRefSlotPath_MissingPageSegment_ReturnsNull()
    {
        // A legacy pre-v2 key ("3", no leading page segment) is an orphan
        // under the v2 contract - it must not resolve to any page.
        Assert.Null(DeckConfigNavigation.ParseImageRefSlotPath("3"));
    }

    [Fact]
    public void ParseImageRefSlotPath_MalformedSegment_ReturnsNull()
    {
        Assert.Null(DeckConfigNavigation.ParseImageRefSlotPath("abc.3"));
    }

    [Fact]
    public void BuildImageRefSlotPath_RootLevelSlot_MatchesTheWebsGrammar()
    {
        Assert.Equal("0.3", DeckConfigNavigation.BuildImageRefSlotPath(0, new List<int>(), 3));
    }

    [Fact]
    public void BuildImageRefSlotPath_NestedFolderSlot_MatchesTheWebsGrammar()
    {
        Assert.Equal("2.1.5", DeckConfigNavigation.BuildImageRefSlotPath(2, new List<int> { 1 }, 5));
    }

    [Fact]
    public void ParseImageRefSlotPath_RootLevelKey_FeedsResolveSlotToTheSamePageRootSlot()
    {
        var config = TwoPageConfig();
        var key = DeckConfigNavigation.BuildImageRefSlotPath(1, new List<int>(), 0);

        var parsed = DeckConfigNavigation.ParseImageRefSlotPath(key)!.Value;
        var indices = new List<int>(parsed.FolderPath) { parsed.SlotIndex };
        var slot = DeckConfigNavigation.ResolveSlot(config, parsed.Page, indices);

        Assert.Equal("P1S0", slot!.Label);
    }

    [Fact]
    public void ParseImageRefSlotPath_NestedFolderKey_FeedsResolveSlotToTheFolderChildSlot()
    {
        var config = TwoPageConfig();
        var key = DeckConfigNavigation.BuildImageRefSlotPath(0, new List<int> { 1 }, 0);

        var parsed = DeckConfigNavigation.ParseImageRefSlotPath(key)!.Value;
        var indices = new List<int>(parsed.FolderPath) { parsed.SlotIndex };
        var slot = DeckConfigNavigation.ResolveSlot(config, parsed.Page, indices);

        Assert.Equal("P0F0S0", slot!.Label);
    }
}
