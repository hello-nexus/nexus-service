using System;
using System.IO;
using System.Linq;
using Nexus.Service.Deck;
using Nexus.Service.Hotkeys;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>A synthetic profile shaped like a Galleon 100 SD export from Stream Deck 7.6: Default page outside the page list, stacked dials, keypad hotkeys.</summary>
public sealed class ElgatoGalleonImportTests : IDisposable
{
    private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "nexus-elgato-galleon-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ElgatoProfileTranslator _translator;

    private static string BundleDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ElgatoGalleon", "ProfilesV3", "GALLEON.sdProfile");

    public ElgatoGalleonImportTests()
    {
        _translator = new ElgatoProfileTranslator(new DeckImageStore(_imagesDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_imagesDir, recursive: true); } catch { /* best effort */ }
    }

    private (DeckConfig Config, ElgatoImportReport Report) Import()
    {
        var profile = ElgatoProfileReader.ReadBundle(BundleDir)!;
        return _translator.Translate(profile);
    }

    private static DeckSlot Slot(DeckConfig config, int page, int col, int row) => config.Pages[page].Slots[row * 3 + col];

    [Fact]
    public void Model_GretschIsTheGalleonThreeByFourGrid()
    {
        var (cols, rows, label) = ElgatoModelCatalog.Resolve("GRETSCH", 0, 0);
        Assert.Equal((3, 4, "Galleon K100 SD"), (cols, rows, label));
        var (config, _) = Import();
        Assert.Equal(12, config.Pages[0].Slots.Count);
    }

    [Fact]
    public void Counts_AreStableForTheGalleonShapedFixture()
    {
        var (_, report) = Import();
        Assert.Equal(26, report.TotalKeys);
        Assert.Equal(24, report.MappedKeys);
    }

    [Theory]
    [InlineData(0, 0, "numpad7")]
    [InlineData(1, 0, "numpadadd")]
    [InlineData(2, 0, "numpaddivide")]
    [InlineData(0, 1, ",")]
    [InlineData(1, 1, "=")]
    [InlineData(2, 1, "return")]
    public void Hotkeys_KeypadFlagAndPunctuationDecode(int col, int row, string expected)
    {
        var (config, _) = Import();
        var action = Slot(config, 0, col, row).Action;
        Assert.Equal("hotkey", action!.Type);
        Assert.Equal(expected, action.Keys);
    }

    [Fact]
    public void KeyBrightnessSleepAndEmptyMultimedia_Map()
    {
        var (config, _) = Import();
        Assert.Equal(("deckBrightness", "up"), (Slot(config, 0, 0, 2).Action!.Type, Slot(config, 0, 0, 2).Action!.Op));
        Assert.Equal(("deckBrightness", "down"), (Slot(config, 0, 1, 2).Action!.Type, Slot(config, 0, 1, 2).Action!.Op));
        Assert.Equal("deckSleep", Slot(config, 0, 2, 2).Action!.Type);
        Assert.Equal("mediaPlayPause", Slot(config, 0, 0, 3).Action!.SystemAction!.Op);
        Assert.Equal("mediaPrev", Slot(config, 0, 1, 3).Action!.SystemAction!.Op);
    }

    [Fact]
    public void OpenApp_UsesExecBundlePathAndTheShortcutWhenArgsAreNeeded()
    {
        var (config, report) = Import();
        Assert.Equal("C:\\Games\\Game.exe", Slot(config, 1, 0, 0).Action!.Path);
        Assert.Equal("C:\\Menu\\Chat.lnk", Slot(config, 1, 1, 0).Action!.Path);
        Assert.Equal("C:\\Program Files\\WindowsApps\\Vendor.App_1.0\\App.exe", Slot(config, 1, 2, 0).Action!.Path);
        Assert.DoesNotContain(report.Unmapped, e => e.Uuid == "com.elgato.streamdeck.system.openapp");
    }

    [Fact]
    public void DefaultPageDials_FillPagesThatDefineNone_WithTheRouterRowFirst()
    {
        var (config, _) = Import();
        var dials = config.Pages[0].Dials!;
        Assert.Equal(DeckDialTypes.Custom, dials[0].Action!.Type);
        var stack = dials[1].Stack!;
        Assert.Equal(2, stack.Count);
        Assert.Equal(DeckDialTypes.Custom, stack[0].Action!.Type);
        Assert.Equal(DeckDialTypes.Volume, stack[1].Action!.Type);
    }

    [Fact]
    public void StackedDial_ImportsEveryEntryAndCarriesTheOutputDevice()
    {
        var (config, _) = Import();
        var stack = config.Pages[1].Dials![1].Stack!;
        Assert.Equal(2, stack.Count);
        Assert.Equal(DeckDialTypes.Volume, stack[0].Action!.Type);
        Assert.Equal("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}", stack[0].Action!.DeviceId);
        var spotify = stack[1].Action!;
        Assert.Equal(DeckDialTypes.Custom, spotify.Type);
        Assert.Equal("mediaPlayPause", spotify.Push!.SystemAction!.Op);
        Assert.Equal("volumeUp", spotify.TurnRight!.SystemAction!.Op);
    }

    [Fact]
    public void DefaultOutputDevice_StaysUnset()
    {
        var (config, _) = Import();
        Assert.Null(config.Pages[0].Dials![1].Stack![1].Action!.DeviceId);
    }

    [Fact]
    public void Report_ListsEveryUnimportedActionWithItsUuid_AndDefaultDialsOnPageZero()
    {
        var (_, report) = Import();
        Assert.Contains(report.Unmapped, e => e.Uuid == "com.example.vitals.plugin" && e.Reason == "plugin");
        Assert.Contains(report.Unmapped, e => e.Uuid == "com.elgato.weather.weather" && e.Page == 0 && e.Dial == 1);
        Assert.Single(report.Unmapped, e => e.Uuid == "com.elgato.weather.weather");
        Assert.Contains(report.Unmapped, e => e.Uuid == "com.elgato.spotify.multimedia" && e.Reason == "dialPartial");
        Assert.All(report.Unmapped, e => Assert.NotEqual("", e.Uuid));
    }

    [Fact]
    public void DefaultInPageList_IsAnOrdinaryPage()
    {
        var profile = ElgatoProfileReader.ReadBundle(BundleDir)!;
        profile.TopPageIds.Add(profile.DefaultPageId);
        var (config, _) = _translator.Translate(profile);
        Assert.Null(config.Pages[0].Dials);
    }

    [Theory]
    [InlineData("numpad0")]
    [InlineData("numpad9")]
    [InlineData("numpadadd")]
    [InlineData("numpadsubtract")]
    [InlineData("numpadmultiply")]
    [InlineData("numpaddivide")]
    [InlineData("numpaddecimal")]
    [InlineData("numpadenter")]
    [InlineData("numpadequal")]
    [InlineData("numlock")]
    public void KeypadTokens_ParseAsDistinctKeys(string token)
    {
        var parsed = DeckActionExecutor.ParseHotkey("ctrl+" + token);
        Assert.NotNull(parsed);
        Assert.NotEqual(DeckActionExecutor.ParseHotkey("ctrl+0")!.Value.Key, parsed!.Value.Key);
        Assert.NotNull(HotkeyCombo.TryParse("ctrl+" + token));
    }

    [Theory]
    [InlineData("ctrl+numpad5", "Numpad5")]
    [InlineData("ctrl+numpadadd", "NumpadAdd")]
    [InlineData("ctrl+numlock", "NumLock")]
    [InlineData("ctrl+numpadenter", "NumpadEnter")]
    public void ParseHotkey_UsesKeyboardEventCodes(string keys, string code) =>
        Assert.Equal(code, DeckActionExecutor.ParseHotkey(keys)!.Value.Key);
}
