using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Deck;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>
/// DeckPresetCatalog against the REAL embedded data/deck-presets bundle (the
/// csproj glob, not a fixture), plus the CONTRACT ADDENDUM's package-content
/// rules every bundled template source must satisfy: format 1, only lucide
/// icons the embedded deck-icons resources actually carry, every hotkey
/// parseable, no machine-specific action type, and every page fitting the
/// preset's own grid.
/// </summary>
public sealed class DeckPresetCatalogTests
{
    private static readonly string[] ExpectedIds =
    {
        "discord", "microsoft-teams", "zoom", "spotify", "google-chrome",
        "vscode", "photoshop", "premiere-pro", "slack", "powerpoint",
    };

    /// <summary>Machine-specific per the CONTRACT ADDENDUM's package section - unsafe to ship in a bundled/downloadable template regardless of DeckLayoutPolicy's broader (panel-authoring) privileged set, which also flags hotkey/text.</summary>
    private static readonly HashSet<string> UnsafeForBundling = new(System.StringComparer.Ordinal)
    {
        "launchApp", "openFile", "openFolder", "playAudio",
    };

    private readonly DeckPresetCatalog _catalog = new();

    [Fact]
    public void Templates_ContainsExactlyTheTenBundledIds()
    {
        var ids = _catalog.Templates.Select(t => t.Id).OrderBy(x => x).ToList();
        Assert.Equal(ExpectedIds.OrderBy(x => x), ids);
    }

    [Fact]
    public void Open_UnknownId_ReturnsNull()
    {
        Assert.Null(_catalog.Open("not-a-real-template"));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Open_EveryBundledTemplate_Validates(string id)
    {
        var result = _catalog.Open(id);
        Assert.NotNull(result);
        Assert.True(result!.Ok, $"{id}: {result.Error}");
        Assert.Equal(1, result.Manifest!.Format);
        Assert.Equal(id, result.Manifest.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Manifest.Name));
        Assert.InRange(result.Manifest.Cols, 1, 8);
        Assert.InRange(result.Manifest.Rows, 1, 8);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Open_EveryPage_FitsTheGrid(string id)
    {
        var manifest = _catalog.Open(id)!.Manifest!;
        var capacity = manifest.Cols * manifest.Rows;
        foreach (var page in manifest.Deck.Pages)
        {
            Assert.True(page.Slots.Count <= capacity, $"{id}: page has {page.Slots.Count} slots, grid holds {capacity}");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Open_EveryIcon_IsAKnownLucideResource(string id)
    {
        var manifest = _catalog.Open(id)!.Manifest!;
        var asm = typeof(DeckPresetCatalog).Assembly;
        foreach (var slot in AllSlots(manifest.Deck))
        {
            if (slot.Icon is not { Kind: "lucide" } icon)
            {
                continue;
            }
            using var stream = asm.GetManifestResourceStream($"deck-icon-{icon.Value}.png");
            Assert.True(stream is not null, $"{id}: icon '{icon.Value}' has no data/deck-icons/{icon.Value}.png resource");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Open_EveryHotkey_IsParseable(string id)
    {
        var manifest = _catalog.Open(id)!.Manifest!;
        foreach (var slot in AllSlots(manifest.Deck))
        {
            var action = slot.Action;
            if (action is null)
            {
                continue;
            }
            if (action.Type == "hotkey")
            {
                Assert.True(DeckActionExecutor.ParseHotkey(action.Keys ?? "") is not null, $"{id}: unparseable hotkey '{action.Keys}'");
            }
            if (action.Type == "hotkeySwitch")
            {
                Assert.True(DeckActionExecutor.ParseHotkey(action.KeysA ?? "") is not null, $"{id}: unparseable keysA '{action.KeysA}'");
                Assert.True(DeckActionExecutor.ParseHotkey(action.KeysB ?? "") is not null, $"{id}: unparseable keysB '{action.KeysB}'");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Open_NoActionType_IsUnsafeForBundling(string id)
    {
        var manifest = _catalog.Open(id)!.Manifest!;
        foreach (var slot in AllSlots(manifest.Deck))
        {
            if (slot.Action is { } action)
            {
                Assert.DoesNotContain(action.Type, UnsafeForBundling);
            }
        }
    }

    public static IEnumerable<object[]> Ids() => ExpectedIds.Select(id => new object[] { id });

    private static IEnumerable<DeckSlot> AllSlots(DeckConfig deck)
    {
        foreach (var page in deck.Pages)
        {
            foreach (var slot in AllSlots(page.Slots))
            {
                yield return slot;
            }
        }
    }

    private static IEnumerable<DeckSlot> AllSlots(List<DeckSlot> slots)
    {
        foreach (var slot in slots)
        {
            yield return slot;
            if (slot.Folder is not null)
            {
                foreach (var nested in AllSlots(slot.Folder.Slots))
                {
                    yield return nested;
                }
            }
        }
    }
}
