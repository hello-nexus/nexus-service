using System;
using System.IO;
using System.Linq;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Reads and translates a Stream Deck + bundle whose Encoder controllers mirror Elgato's own default profiles.</summary>
public sealed class ElgatoDialImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-elgato-dials-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ElgatoProfileTranslator _translator;

    private const string TopPage = """
    {
      "Controllers": [
        {
          "Type": "Keypad",
          "Actions": {
            "0,0": { "UUID": "com.elgato.streamdeck.profile.openchild", "Name": "Create Folder", "Settings": { "ProfileUUID": "folder-dials" }, "States": [ {} ] },
            "1,0": { "UUID": "com.elgato.streamdeck.profile.openchild", "Name": "Create Folder", "Settings": { "ProfileUUID": "folder-plain" }, "States": [ {} ] }
          }
        },
        {
          "Type": "Encoder",
          "Background": "Images/strip.png",
          "Actions": {
            "0,0": {
              "UUID": "com.elgato.streamdeck.system.hotkey", "Name": "Hotkey",
              "Encoder": { "Icon": "Images/undo.png" },
              "Settings": { "Hotkeys": [
                { "KeyModifiers": 2, "QTKeyCode": 90 },
                { "KeyModifiers": 3, "QTKeyCode": 90 },
                { "KeyModifiers": 0, "QTKeyCode": 33554431 },
                { "KeyModifiers": 0, "QTKeyCode": 33554431 }
              ] },
              "State": 0, "States": [ { "Title": "Undo/Redo" } ]
            },
            "1,0": { "UUID": "com.elgato.streamdeck.system.multimedia", "Name": "Multimedia", "Encoder": { "IconVisible": true }, "Settings": { "actionIdx": 18 }, "States": [ { "Title": " System Volume" } ] },
            "2,0": { "UUID": "com.elgato.streamdeck.system.keybrightness", "Name": "Brightness", "Settings": { "actionIdx": 0 }, "States": [ { "Title": "SD+ Brightness" } ] },
            "3,0": {
              "UUID": "com.elgato.streamdeck.keys.adaptor", "Name": "Action Trigger", "Settings": {}, "States": [ {} ],
              "Actions": [
                { "UUID": "com.elgato.streamdeck.page.previous", "Name": "Previous Page", "Settings": {}, "States": [ {} ] },
                { "UUID": "com.elgato.streamdeck.page.goto", "Name": "Go to Page", "Settings": { "PageIndex": 1 }, "States": [ {} ] },
                { "UUID": "com.elgato.streamdeck.page.next", "Name": "Next Page", "Settings": {}, "States": [ {} ] }
              ]
            }
          }
        }
      ]
    }
    """;

    private const string DialFolder = """
    {
      "Controllers": [
        {
          "Type": "Keypad",
          "Actions": {
            "0,0": { "UUID": "com.elgato.streamdeck.profile.backtoparent", "Settings": {}, "States": [ {} ] },
            "1,0": { "UUID": "com.elgato.streamdeck.system.website", "Name": "Website", "Settings": { "path": "https://example.com" }, "States": [ {} ] }
          }
        },
        {
          "Type": "Encoder",
          "Actions": {
            "0,0": {
              "UUID": "com.elgato.streamdeck.dial.stack", "Name": "Dial Stack", "Settings": { "CurrentIdx": 1 }, "States": [ {} ],
              "Actions": [
                { "UUID": "com.elgato.volume-controller.output-device-control", "Name": "Output", "Settings": { "deviceId": "default", "volumeStep": "3" }, "States": [ { "Title": "Speakers" } ] },
                { "UUID": "com.elgato.volume-controller.input-device-control", "Name": "Input", "Settings": { "deviceId": "default", "volumeStep": "2" }, "States": [ { "Title": "Mic" } ] },
                { "UUID": "com.elgato.wave-link.channellevel", "Name": "Channel Level", "Settings": {}, "States": [ {} ] }
              ]
            },
            "1,0": { "UUID": "com.example.someplugin.dial", "Name": "Plugin Dial", "Settings": {}, "States": [ {} ] }
          }
        }
      ]
    }
    """;

    private const string PlainFolder = """
    {
      "Controllers": [
        { "Type": "Keypad", "Actions": {
          "0,0": { "UUID": "com.elgato.streamdeck.profile.backtoparent", "Settings": {}, "States": [ {} ] },
          "1,0": { "UUID": "com.elgato.streamdeck.system.website", "Name": "Website", "Settings": { "path": "https://example.org" }, "States": [ {} ] }
        } },
        { "Type": "Encoder" }
      ]
    }
    """;

    public ElgatoDialImportTests()
    {
        var bundle = Path.Combine(_root, "PLUS.sdProfile");
        WritePage(bundle, "TOP", TopPage);
        WritePage(bundle, "FOLDER-DIALS", DialFolder);
        WritePage(bundle, "FOLDER-PLAIN", PlainFolder);
        File.WriteAllBytes(Path.Combine(bundle, "Profiles", "TOP", "Images", "undo.png"), TinyPng);
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), """
        { "Name": "Plus Profile", "Device": { "Model": "20GBD9901" }, "Pages": { "Pages": [ "top" ] } }
        """);
        _translator = new ElgatoProfileTranslator(new DeckImageStore(Path.Combine(_root, "images")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static void WritePage(string bundle, string dirName, string manifest)
    {
        var dir = Path.Combine(bundle, "Profiles", dirName);
        Directory.CreateDirectory(Path.Combine(dir, "Images"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
    }

    private ElgatoProfile Read() => ElgatoProfileReader.ReadBundle(Path.Combine(_root, "PLUS.sdProfile"))!;

    [Fact]
    public void Reader_KeysEncoderActionsByDialIndexAndParsesChildren()
    {
        var page = Read().PagesById["top"];

        Assert.Equal(new[] { 0, 1, 2, 3 }, page.Dials.Keys.OrderBy(k => k));
        Assert.EndsWith(Path.Combine("TOP", "Images", "undo.png"), page.Dials[0].EncoderIconPath);
        Assert.Equal(3, page.Dials[3].Children.Count);
        Assert.Equal("com.elgato.streamdeck.page.goto", page.Dials[3].Children[1].Uuid);
    }

    [Fact]
    public void CountKeys_IncludesDials()
    {
        // Keys: 2 + 1 + 1 (backtoparent skipped). Dials: 4 + 2.
        Assert.Equal(10, ElgatoProfileReader.CountKeys(Read()));
    }

    [Fact]
    public void Hotkey_MapsRotateCcwCwAndPressToACustomDial()
    {
        var (config, _) = _translator.Translate(Read());
        var dial = config.Pages[0].Dials![0];

        Assert.Equal("Undo/Redo", dial.Label);
        Assert.Equal("image", dial.Icon?.Kind);
        Assert.Equal(DeckDialTypes.Custom, dial.Action!.Type);
        Assert.Equal("ctrl+z", dial.Action.TurnLeft?.Keys);
        Assert.Equal("ctrl+shift+z", dial.Action.TurnRight?.Keys);
        Assert.Null(dial.Action.Push);
    }

    [Fact]
    public void BuiltinDials_MapToNexusDialTypes()
    {
        var (config, _) = _translator.Translate(Read());
        var dials = config.Pages[0].Dials!;

        Assert.Equal(DeckDialTypes.Volume, dials[1].Action!.Type);
        Assert.Equal("System Volume", dials[1].Label);
        Assert.Equal(DeckDialTypes.DeckBrightness, dials[2].Action!.Type);
    }

    [Fact]
    public void ActionTrigger_MapsCcwPressCwKeyActions()
    {
        var (config, _) = _translator.Translate(Read());
        var action = config.Pages[0].Dials![3].Action!;

        Assert.Equal(DeckDialTypes.Custom, action.Type);
        Assert.Equal("prev", action.TurnLeft?.Op);
        Assert.Equal("goto", action.Push?.Op);
        Assert.Equal(0, action.Push?.Target);
        Assert.Equal("next", action.TurnRight?.Op);
    }

    [Fact]
    public void DialStack_KeepsTranslatableEntriesAndNotesTheDroppedOne()
    {
        var (config, report) = _translator.Translate(Read());
        var folderDials = config.Pages[0].Slots[0].Folder!.Dials!;
        var stack = folderDials[0].Stack!;

        Assert.Equal(2, stack.Count);
        Assert.Equal(DeckDialTypes.Volume, stack[0].Action!.Type);
        Assert.Equal(3, stack[0].Action!.Step);
        Assert.Equal("Speakers", stack[0].Label);
        Assert.Equal(DeckDialTypes.MicVolume, stack[1].Action!.Type);
        Assert.Contains(report.Unmapped, e => e.Dial == 1 && e.Reason == "multiStep" && e.Position == "");
    }

    [Fact]
    public void PluginDial_IsAPlaceholderReportedByDialNumber()
    {
        var (config, report) = _translator.Translate(Read());
        var dial = config.Pages[0].Slots[0].Folder!.Dials![1];

        Assert.Null(dial.Action);
        Assert.Equal("Plugin Dial", dial.Label);
        Assert.Contains(report.Unmapped, e => e.Page == 1 && e.Dial == 2 && e.Reason == "plugin");
    }

    [Fact]
    public void FolderWithoutDialActions_KeepsThePageDials()
    {
        var (config, _) = _translator.Translate(Read());
        Assert.Null(config.Pages[0].Slots[1].Folder!.Dials);
    }

    [Fact]
    public void PageGoto_TargetsTheNexusPageAfterEmptyPagesAreDropped()
    {
        var profile = new ElgatoProfile { Model = "20GBD9901" };
        var goto3 = System.Text.Json.JsonDocument.Parse("""{ "PageIndex": 3 }""").RootElement.Clone();
        var first = new ElgatoPageData();
        first.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.page.goto", Name = "Go to Page", Settings = goto3 };
        var third = new ElgatoPageData();
        third.Actions[(0, 0)] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.page.next", Name = "Next Page" };
        profile.TopPageIds.AddRange(new[] { "p1", "p2-empty", "p3" });
        profile.PagesById["p1"] = first;
        profile.PagesById["p2-empty"] = new ElgatoPageData();
        profile.PagesById["p3"] = third;

        var (config, _) = _translator.Translate(profile);

        Assert.Equal(2, config.Pages.Count);
        Assert.Equal(1, config.Pages[0].Slots[0].Action!.Target);
    }

    [Fact]
    public void Hotkey_UndecodablePopulatedSlot_StillMapsAndNotes()
    {
        var profile = new ElgatoProfile { Model = "20GBD9901" };
        var settings = System.Text.Json.JsonDocument.Parse("""
        { "Hotkeys": [
          { "KeyModifiers": 0, "QTKeyCode": 16777235 },
          { "KeyModifiers": 0, "QTKeyCode": 16777300 },
          { "KeyModifiers": 0, "QTKeyCode": 33554431 }
        ] }
        """).RootElement.Clone();
        var page = new ElgatoPageData();
        page.Dials[0] = new ElgatoActionData { Uuid = "com.elgato.streamdeck.system.hotkey", Name = "Hotkey", Settings = settings };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;

        var (config, report) = _translator.Translate(profile);

        Assert.Equal("up", config.Pages[0].Dials![0].Action!.TurnLeft?.Keys);
        Assert.Null(config.Pages[0].Dials![0].Action!.TurnRight);
        Assert.Equal(1, report.MappedKeys);
        Assert.Contains(report.Unmapped, e => e.Dial == 1 && e.Reason == "hotkey");
    }

    [Fact]
    public void Report_CountsDialsAndNeverEmitsTheOldEncoderNote()
    {
        var (_, report) = _translator.Translate(Read());

        Assert.Equal(10, report.TotalKeys);
        // Every key and dial except the plugin dial.
        Assert.Equal(9, report.MappedKeys);
        Assert.DoesNotContain(report.Unmapped, e => e.Reason == "encoder");
    }
}
