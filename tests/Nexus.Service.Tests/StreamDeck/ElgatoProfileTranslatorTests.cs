using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Translates the synthetic fixture bundle end to end and asserts exact DeckAction/report shapes.</summary>
public sealed class ElgatoProfileTranslatorTests : IDisposable
{
    private readonly string _imagesDir = Path.Combine(Path.GetTempPath(), "nexus-elgato-images-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DeckImageStore _images;
    private readonly ElgatoProfileTranslator _translator;

    private static string ProfilesV3Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ElgatoStore", "ProfilesV3");

    public ElgatoProfileTranslatorTests()
    {
        _images = new DeckImageStore(_imagesDir);
        _translator = new ElgatoProfileTranslator(_images);
    }

    public void Dispose()
    {
        try { Directory.Delete(_imagesDir, recursive: true); } catch { /* best effort */ }
    }

    private ElgatoProfile ReadFixtureProfile() =>
        ElgatoProfileReader.ReadBundle(Path.Combine(ProfilesV3Root, "TESTBUNDLE.sdProfile"))!;

    private static DeckSlot Slot(DeckConfig config, int page, int col, int row) =>
        config.Pages[page].Slots[row * 5 + col];

    [Fact]
    public void Translate_ReportsTotalAndMappedKeysAcrossAllPagesAndFolders()
    {
        var (_, report) = _translator.Translate(ReadFixtureProfile());
        Assert.Equal(29, report.TotalKeys);
        Assert.Equal(22, report.MappedKeys);
        Assert.Equal(10, report.Unmapped.Count);
    }

    [Fact]
    public void Translate_DropsTheEmptyPageAndKeepsTheTwoRealPagesInOrder()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        Assert.Equal(2, config.Pages.Count);
    }

    [Theory]
    [InlineData(0, 0, "shift+meta+5")]      // Cmd+Shift+5
    [InlineData(1, 0, "shift+meta+s")]      // Win+Shift+S
    [InlineData(4, 0, "ctrl+alt+t")]        // Ctrl+Alt+T, extra populated slot ignored
    public void Translate_DecodesHotkeysToParseHotkeyGrammar(int col, int row, string expectedKeys)
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, col, row);
        Assert.NotNull(slot.Action);
        Assert.Equal("hotkey", slot.Action!.Type);
        Assert.Equal(expectedKeys, slot.Action.Keys);
    }

    [Fact]
    public void Translate_PeriodHotkey_NowMapsInsteadOfBeingUnmappablePunctuation()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 2, 0);
        Assert.NotNull(slot.Action);
        Assert.Equal("hotkey", slot.Action!.Type);
        Assert.Equal("meta+.", slot.Action.Keys);
        Assert.Equal("Dot", slot.Label);
        Assert.DoesNotContain(report.Unmapped, e => e.Page == 1 && e.Position == "2,0");
    }

    [Fact]
    public void Translate_Hotkey_PrintScreenQtKeyDecodesToThePrintScreenToken()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-printscreen-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = """
        {
          "Hotkeys": [
            {"KeyModifiers": 8, "QTKeyCode": 16777225},
            {"KeyModifiers": 0, "QTKeyCode": 33554431},
            {"KeyModifiers": 0, "QTKeyCode": 33554431},
            {"KeyModifiers": 0, "QTKeyCode": 33554431}
          ]
        }
        """;
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.system.hotkey", Name = "Full Screen Snip", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, _) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("hotkey", slot.Action!.Type);
        Assert.Equal("meta+printscreen", slot.Action.Keys);
    }

    [Fact]
    public void Translate_UnconfiguredHotkeySentinel_IsPlaceholderWithReason()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 3, 0);
        Assert.Null(slot.Action);
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Position == "3,0" && e.Reason == "hotkey");
    }

    [Fact]
    public void Translate_ExtraPopulatedHotkeySlot_StillMapsAndNotes()
    {
        var (_, report) = _translator.Translate(ReadFixtureProfile());
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Position == "4,0" && e.Reason == "hotkeyExtraSlots");
    }

    [Fact]
    public void Translate_OpenAction_StripsQuotesAndClassifiesAppAsFile()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 0, 1);
        Assert.Equal("openFile", slot.Action!.Type);
        Assert.Equal("/Applications/Safari.app", slot.Action.Path);
    }

    [Fact]
    public void Translate_OpenAction_BarePathIsFile()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 1, 1);
        Assert.Equal("openFile", slot.Action!.Type);
        Assert.Equal("notepad", slot.Action.Path);
    }

    [Fact]
    public void Translate_OpenAction_TrailingSeparatorIsFolder()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 2, 1);
        Assert.Equal("openFolder", slot.Action!.Type);
        Assert.Equal("/Applications/", slot.Action.Path);
    }

    [Fact]
    public void Translate_Website_MapsToOpenUrlAndIngestsIcon()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 3, 1);
        Assert.Equal("openUrl", slot.Action!.Type);
        Assert.Equal("https://twitch.tv", slot.Action.Url);
        Assert.NotNull(slot.Icon);
        Assert.Equal("image", slot.Icon!.Kind);
        Assert.True(DeckImageStore.IsValidId(slot.Icon.Value));
        Assert.NotNull(_images.TryLoad(slot.Icon.Value));
    }

    [Fact]
    public void Translate_Text_KeepsEmojiVerbatim()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 4, 1);
        Assert.Equal("text", slot.Action!.Type);
        Assert.Equal("Hello \U0001F44B World", slot.Action.Text);
    }

    [Fact]
    public void Translate_TextWithSendEnter_MapsButNotesTheIgnoredFlag()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 0, 2);
        Assert.Equal("text", slot.Action!.Type);
        Assert.Equal("Enter test", slot.Action.Text);
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Position == "0,2" && e.Reason == "textEnterIgnored");
    }

    [Fact]
    public void Translate_Multimedia_MapsVerifiedIdxToSystemAction()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 1, 2);
        Assert.Equal("system", slot.Action!.Type);
        Assert.Equal("volumeUp", slot.Action.SystemAction!.Op);
    }

    [Fact]
    public void Translate_Multimedia_UnverifiedIdxIsPlaceholder()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 2, 2);
        Assert.Null(slot.Action);
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Position == "2,2" && e.Reason == "media");
    }

    [Fact]
    public void Translate_TutorialTile_SkipsSilentlyWithNoSlotOrReportEntry()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 3, 2);
        Assert.Null(slot.Action);
        Assert.Null(slot.Label);
        Assert.DoesNotContain(report.Unmapped, e => e.Page == 1 && e.Position == "3,2");
    }

    [Fact]
    public void Translate_Lhm_MapsToMonitoringWithCategoryAndKeepsLabel()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 0, 4, 2);
        Assert.Equal("monitoring", slot.Action!.Type);
        Assert.Equal("cpu", slot.Action.Category);
        Assert.Equal("", slot.Action.Sensor);
        Assert.Equal("line", slot.Action.Style);
        Assert.Null(slot.Icon);
        Assert.Equal("CPU", slot.Label);
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Position == "4,2" && e.Reason == "monitoringSensor");
    }

    private static (DeckConfig Config, ElgatoImportReport Report) TranslateSingleLhmSlot(object settingsPayload)
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-lhm-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = JsonSerializer.Serialize(settingsPayload);
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.moeilijk.lhm.reading", Name = "Reading", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;
        return translator.Translate(profile);
    }

    [Theory]
    [InlineData("/amdcpu/0", "cpu")]
    [InlineData("/intelcpu/0", "cpu")]
    [InlineData("/gpu-nvidia/0", "gpu")]
    [InlineData("/GPU/nvidia/0", "gpu")]
    [InlineData("/ram/0", "memory")]
    [InlineData("/nvme/0", "storage")]
    [InlineData("/hdd/0", "storage")]
    [InlineData("/ssd/0", "storage")]
    [InlineData("/storage/0", "storage")]
    [InlineData("/lpc/nct6798d/0", "motherboard")]
    [InlineData("/mobo/0", "motherboard")]
    [InlineData("/superio/0", "motherboard")]
    [InlineData("/battery/0", "quick")]
    [InlineData("", "quick")]
    public void Translate_Lhm_ResolvesCategoryFromSensorUidPrefix(string sensorUid, string expectedCategory)
    {
        var (config, report) = TranslateSingleLhmSlot(new { sensorUid, min = 10, max = 90 });
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("monitoring", slot.Action!.Type);
        Assert.Equal(expectedCategory, slot.Action.Category);
        Assert.Equal("", slot.Action.Sensor);
        Assert.Equal("line", slot.Action.Style);
        Assert.Contains(report.Unmapped, e => e.Reason == "monitoringSensor");
    }

    /// <summary>
    /// The verbatim settings payload com.moeilijk.lhm writes (captured from a
    /// real ProfilesV3 store): the sensor leaf lives in readingId/readingLabel,
    /// neither of which is a HardwareSensor.Id, and sensorUid is hardware-level.
    /// </summary>
    [Fact]
    public void Translate_Lhm_RealPluginPayload_LeavesSensorEmptyAndDropsPluginRange()
    {
        var (config, report) = TranslateSingleLhmSlot(new
        {
            sensorUid = "/amdcpu/0",
            readingId = "1387652014",
            readingLabel = "Core #1",
            min = 40,
            max = 62,
        });
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("cpu", slot.Action!.Category);
        Assert.Equal("", slot.Action.Sensor);
        Assert.Null(slot.Action.Scale);
        Assert.Null(slot.Action.Min);
        Assert.Null(slot.Action.Max);
        Assert.Contains(report.Unmapped, e => e.Reason == "monitoringSensor");
    }

    [Fact]
    public void Translate_PluginActionWithoutTitleOrImage_FallsBackToActionName()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 0, 2);
        Assert.Null(slot.Action);
        Assert.Null(slot.Icon);
        Assert.Equal("CPU Meter", slot.Label);
        Assert.Contains(report.Unmapped, e => e.Page == 2 && e.Position == "0,2" && e.Reason == "plugin");
    }

    [Fact]
    public void Translate_PluginActionWithImage_KeepsLabelAndIngestsIcon()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 4, 1);
        Assert.Null(slot.Action);
        Assert.Equal("CPU 42%", slot.Label);
        Assert.NotNull(slot.Icon);
        Assert.True(DeckImageStore.IsValidId(slot.Icon!.Value));
        Assert.Contains(report.Unmapped, e => e.Page == 2 && e.Position == "4,1" && e.Reason == "plugin" && e.Detail == "com.elgato.cpu.cpu");
    }

    [Fact]
    public void Translate_OpenChildFolder_BuildsNestedFolderSlots()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 0, 0);
        Assert.Null(slot.Action);
        Assert.NotNull(slot.Folder);
        // 5x3 grid minus the reserved Back key at physical position 0.
        Assert.Equal(14, slot.Folder!.Slots.Count);
    }

    [Fact]
    public void Translate_FolderA_FirstSlotIsItsHotkeyNotItsBackToParent()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var folderA = Slot(config, 1, 0, 0).Folder!;
        // folder-a's own grid: (0,0)=backtoparent (dropped), (1,0)=hotkey,
        // (2,0)=openchild folder-b. After dropping physical key 0 (Back),
        // Slots[0] is the hotkey and Slots[1] is the folder-b button.
        Assert.Equal("hotkey", folderA.Slots[0].Action!.Type);
        Assert.Equal("ctrl+shift+m", folderA.Slots[0].Action!.Keys);
        Assert.NotNull(folderA.Slots[1].Folder);
    }

    [Fact]
    public void Translate_OpenchildCycle_StopsAndReportsInsteadOfRecursingForever()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var folderB = Slot(config, 1, 0, 0).Folder!.Slots[1].Folder!;
        // folder-b's own grid: (0,0)=backtoparent, (1,0)=openchild back to
        // folder-a (a cycle), (2,0)=hotkey. Slots[0] is the cycle placeholder,
        // Slots[1] is the hotkey.
        Assert.Null(folderB.Slots[0].Action);
        Assert.Null(folderB.Slots[0].Folder);
        Assert.Equal("hotkey", folderB.Slots[1].Action!.Type);
        Assert.Equal("alt+f4", folderB.Slots[1].Action!.Keys);
        Assert.Contains(report.Unmapped, e => e.Reason == "unsupported" && e.Detail == "openchild cycle");
    }

    [Fact]
    public void Translate_Routine2_FlattensGroupsIntoSequenceSteps()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 1, 0);
        Assert.Equal("sequence", slot.Action!.Type);
        Assert.Equal(2, slot.Action.Steps!.Count);
        Assert.Equal("hotkey", slot.Action.Steps[0].Action.Type);
        Assert.Equal("ctrl+c", slot.Action.Steps[0].Action.Keys);
        Assert.Equal("openUrl", slot.Action.Steps[1].Action.Type);
        Assert.Equal("https://example.com", slot.Action.Steps[1].Action.Url);
    }

    [Fact]
    public void Translate_Routine2_BothStepsNowTranslatableAfterPlayAudioSupport()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 2, 0);
        Assert.Equal("sequence", slot.Action!.Type);
        Assert.Equal(2, slot.Action.Steps!.Count);
        Assert.Equal("openUrl", slot.Action.Steps[0].Action.Type);
        Assert.Equal("https://example.org", slot.Action.Steps[0].Action.Url);
        Assert.Equal("playAudio", slot.Action.Steps[1].Action.Type);
        Assert.Equal("/Users/test/sounds/airhorn.wav", slot.Action.Steps[1].Action.Path);
        Assert.Equal(80, slot.Action.Steps[1].Action.Volume);
        Assert.DoesNotContain(report.Unmapped, e => e.Page == 2 && e.Position == "2,0");
    }

    [Fact]
    public void Translate_Routine2_UnconfiguredPlayAudioStepStillMaps()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 3, 0);
        Assert.Equal("sequence", slot.Action!.Type);
        Assert.Single(slot.Action.Steps!);
        Assert.Equal("playAudio", slot.Action.Steps![0].Action.Type);
        Assert.Equal("", slot.Action.Steps[0].Action.Path);
        Assert.DoesNotContain(report.Unmapped, e => e.Page == 2 && e.Position == "3,0");
    }

    [Fact]
    public void Translate_PageNavigation_MapsNextAndPrevious()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        Assert.Equal("page", Slot(config, 1, 4, 0).Action!.Type);
        Assert.Equal("next", Slot(config, 1, 4, 0).Action!.Op);
        Assert.Equal("page", Slot(config, 1, 0, 1).Action!.Type);
        Assert.Equal("prev", Slot(config, 1, 0, 1).Action!.Op);
    }

    [Fact]
    public void Translate_OpenApp_WithPath_MapsLikeOpen()
    {
        var (config, _) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 1, 1);
        Assert.Equal("openFile", slot.Action!.Type);
        Assert.Equal("/Applications/Notes.app", slot.Action.Path);
    }

    [Fact]
    public void Translate_OpenApp_WithoutPath_IsPlaceholder()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 2, 1);
        Assert.Null(slot.Action);
        Assert.Contains(report.Unmapped, e => e.Page == 2 && e.Position == "2,1" && e.Reason == "unsupported");
    }

    [Fact]
    public void Translate_HotkeySwitch_NeverGuessesAndIsAlwaysAPlaceholder()
    {
        var (config, report) = _translator.Translate(ReadFixtureProfile());
        var slot = Slot(config, 1, 3, 1);
        Assert.Null(slot.Action);
        Assert.Contains(report.Unmapped, e => e.Page == 2 && e.Position == "3,1" && e.Reason == "unsupported" && e.Detail == "com.elgato.streamdeck.system.hotkeyswitch");
    }

    [Fact]
    public void Translate_OpenAction_AppBundleIsNeverTreatedAsAFolder()
    {
        // Regression: .app is a directory on disk (Directory.Exists is true
        // on a real Mac), but the OS "open" launches it like a file.
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-appcheck-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settings = JsonDocument.Parse("""{"path":"\"/Applications/TextEdit.app\""}""").RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.system.open", Name = "TextEdit", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, _) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("openFile", slot.Action!.Type);
    }

    [Fact]
    public void Translate_OpenAction_ARealDirectoryWithNoTrailingSeparatorIsAFolder()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-dircheck-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var realDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = JsonSerializer.Serialize(new { path = realDir });
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.system.open", Name = "TempDir", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, _) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("openFolder", slot.Action!.Type);
    }

    [Fact]
    public void Translate_Weather_ParsesNestedLocationAndDefaultsUnitsToAuto()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-weather-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = JsonSerializer.Serialize(new
        {
            city = "Fallback City",
            location = new { lat = 37.7749, lon = -122.4194, country = "US", city = "San Francisco" },
        });
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.weather.weather", Name = "Weather", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, report) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("weather", slot.Action!.Type);
        Assert.Equal(37.7749, slot.Action.Lat);
        Assert.Equal(-122.4194, slot.Action.Lon);
        Assert.Equal("San Francisco", slot.Action.City);
        Assert.Equal("US", slot.Action.Cc);
        Assert.Equal("auto", slot.Action.Units);
        Assert.DoesNotContain(report.Unmapped, e => e.Position == "0,0");
    }

    [Fact]
    public void Translate_Weather_FallsBackToTopLevelCityWhenLocationMissing()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-weather-fallback-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = JsonSerializer.Serialize(new { city = "Fallback City" });
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.weather.weather", Name = "Weather", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, _) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("weather", slot.Action!.Type);
        Assert.Null(slot.Action.Lat);
        Assert.Null(slot.Action.Lon);
        Assert.Equal("Fallback City", slot.Action.City);
        Assert.Equal("auto", slot.Action.Units);
    }

    [Fact]
    public void Translate_PlayAudio_WithPath_MapsWithVolumeAndNoNote()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-playaudio-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settingsJson = JsonSerializer.Serialize(new { path = "C:\\sounds\\ding.wav", volume = 65 });
        var settings = JsonDocument.Parse(settingsJson).RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.soundboard.playaudio", Name = "Ding", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, report) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("playAudio", slot.Action!.Type);
        Assert.Equal("C:\\sounds\\ding.wav", slot.Action.Path);
        Assert.Equal(65, slot.Action.Volume);
        Assert.DoesNotContain(report.Unmapped, e => e.Reason == "audioPath");
    }

    [Fact]
    public void Translate_PlayAudio_WithoutPath_MapsUnconfiguredWithNote()
    {
        var images = new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-playaudio-empty-" + Guid.NewGuid().ToString("N")[..8]));
        var translator = new ElgatoProfileTranslator(images);
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.soundboard.playaudio", Name = "Empty Sound", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, report) = translator.Translate(profile);
        var slot = config.Pages[0].Slots[0];
        Assert.Equal("playAudio", slot.Action!.Type);
        Assert.Equal("", slot.Action.Path);
        Assert.Contains(report.Unmapped, e => e.Reason == "audioPath");
    }
}
