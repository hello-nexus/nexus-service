using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public class StreamDeckPersistenceTests
{
    [Fact]
    public void NexusSettings_WithStreamDeckBindings_RoundTripsThroughPersistenceJsonContext()
    {
        var settings = new NexusSettings();
        settings.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "My Deck",
            Brightness = 42,
            Orientation = 180,
            SleepAfterSeconds = 90,
            SleepWhenLocked = false,
            LegacyDeck = new DeckConfig
            {
                Pages = new List<DeckPage>
                {
                    new()
                    {
                        Slots =
                        {
                            new DeckSlot
                            {
                                Label = "Lock",
                                Color = "#ff0000",
                                Action = new DeckAction { Type = "power", PowerAction = "lock" },
                            },
                            new DeckSlot
                            {
                                Folder = new DeckFolder
                                {
                                    Slots =
                                    {
                                        new DeckSlot
                                        {
                                            Action = new DeckAction
                                            {
                                                Type = "toggle",
                                                State = new DeckToggleState { Kind = "mute" },
                                                On = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "muteToggle" } },
                                                Off = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "muteToggle" } },
                                            },
                                        },
                                    },
                                },
                            },
                            new DeckSlot
                            {
                                Action = new DeckAction
                                {
                                    Type = "sequence",
                                    Steps = new List<DeckSequenceStep>
                                    {
                                        new()
                                        {
                                            Action = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Brightness", Value = 80 } },
                                            PressMs = 10,
                                            GapAfterMs = 20,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            LegacyImageRefs = new Dictionary<string, string> { ["0/0"] = "abc123" },
        };
        settings.StreamDeck.Presets.Add(new DeckPreset
        {
            Id = "p-1",
            Name = "Discord",
            Cols = 5,
            Rows = 3,
            Apps = new List<PresetAppBinding> { new() { Id = "proc:discord", Name = "Discord", ProcessName = "discord" } },
            TemplateId = "discord",
            Author = "Nexus",
            Version = "1.0.0",
            Description = "Chat controls",
        });
        settings.StreamDeck.Instances["streamdeck:SERIAL-1"] = new DeckInstance { Mode = "fixed", ActivePresetId = "p-1" };
        settings.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "recentApps" };
        settings.StreamDeck.RecentApps.Add(new RecentApp { ProcessKey = "discord", Name = "Discord", LastFocusedUtcMs = 123 });
        settings.StreamDeck.RecentAppsExcluded.Add("explorer");

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var roundTripped = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(roundTripped);
        var deck = roundTripped!.StreamDeck.Decks["SERIAL-1"];
        Assert.Equal("My Deck", deck.Name);
        Assert.Equal(42, deck.Brightness);
        Assert.Equal(180, deck.Orientation);
        Assert.Equal(90, deck.SleepAfterSeconds);
        Assert.False(deck.SleepWhenLocked);
        Assert.Equal("abc123", deck.LegacyImageRefs!["0/0"]);
        Assert.Single(deck.LegacyDeck!.Pages);
        var slots = deck.LegacyDeck.Pages[0].Slots;
        Assert.Equal(3, slots.Count);
        Assert.Equal("lock", slots[0].Action!.PowerAction);
        Assert.Equal("mute", slots[1].Folder!.Slots[0].Action!.State!.Kind);
        Assert.Equal("muteToggle", slots[1].Folder!.Slots[0].Action!.On!.SystemAction!.Op);
        Assert.Single(slots[2].Action!.Steps!);
        Assert.Equal(80, slots[2].Action!.Steps![0].Action.NexusAction!.Value);
        Assert.Equal(10, slots[2].Action!.Steps![0].PressMs);

        var preset = roundTripped.StreamDeck.Presets.Find(p => p.Id == "p-1")!;
        Assert.Equal("Discord", preset.Name);
        Assert.Equal(5, preset.Cols);
        Assert.Equal(3, preset.Rows);
        Assert.Equal("discord", preset.Apps![0].ProcessName);
        Assert.Equal("discord", preset.TemplateId);
        Assert.Equal("Nexus", preset.Author);
        Assert.Equal("1.0.0", preset.Version);
        Assert.Equal("Chat controls", preset.Description);

        Assert.Equal("p-1", roundTripped.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId);
        Assert.Equal("recentApps", roundTripped.StreamDeck.Instances["widget:w1"].Mode);
        Assert.Equal("discord", roundTripped.StreamDeck.RecentApps[0].ProcessKey);
        Assert.Equal(123, roundTripped.StreamDeck.RecentApps[0].LastFocusedUtcMs);
        Assert.Equal("explorer", roundTripped.StreamDeck.RecentAppsExcluded[0]);
    }

    [Fact]
    public void PhysicalDeckSettings_DefaultBrightness_Is60()
    {
        Assert.Equal(60, new PhysicalDeckSettings().Brightness);
        Assert.Equal(60, PhysicalDeckSettings.DefaultBrightness);
    }

    [Fact]
    public void PhysicalDeckSettings_OrientationAndSleepAfterSeconds_DefaultToZero()
    {
        var deck = new PhysicalDeckSettings();
        Assert.Equal(0, deck.Orientation);
        Assert.Equal(0, deck.SleepAfterSeconds);
    }

    [Fact]
    public void PhysicalDeckSettings_SleepWhenLocked_DefaultsOn()
    {
        Assert.True(new PhysicalDeckSettings().SleepWhenLocked);
    }
}

public class StreamDeckImageCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StreamDeckImageCache _cache;

    public StreamDeckImageCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-cache-" + Guid.NewGuid().ToString("N")[..8]);
        _cache = new StreamDeckImageCache(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("hello deck");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, StreamDeckImageCache.Hash(bytes));
    }

    [Fact]
    public void StoreThenLoad_ReturnsTheSameBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var hash = StreamDeckImageCache.Hash(bytes);

        _cache.Store("SERIAL-1", hash, bytes);
        var loaded = _cache.Load("SERIAL-1", hash);

        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public void Load_UnknownHash_ReturnsNull()
    {
        Assert.Null(_cache.Load("SERIAL-1", "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public void Store_IsANoOpWhenTheHashAlreadyExists()
    {
        var bytes = new byte[] { 9, 9, 9 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);

        // Re-storing different bytes under the SAME hash must not overwrite -
        // content-addressed storage treats an existing hash as already correct.
        _cache.Store("SERIAL-1", hash, new byte[] { 1, 1, 1 });

        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void DifferentSerials_AreIsolated()
    {
        var bytes = new byte[] { 7, 7, 7 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);

        Assert.Null(_cache.Load("SERIAL-2", hash));
    }

    [Theory]
    [InlineData("A00DA431130Y9Y")]
    [InlineData("sd-1a2b3c4d")]
    [InlineData("sim-0001")]
    [InlineData("XL-SERIAL")]
    [InlineData("a")]
    public void IsValidSerial_AcceptsEveryObservedSerialShape(string serial)
    {
        Assert.True(StreamDeckImageCache.IsValidSerial(serial));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidSerial_RejectsTraversalAndEmptyPayloads(string? serial)
    {
        Assert.False(StreamDeckImageCache.IsValidSerial(serial));
    }

    [Fact]
    public void IsValidSerial_RejectsLongerThan64Chars()
    {
        Assert.False(StreamDeckImageCache.IsValidSerial(new string('a', 65)));
        Assert.True(StreamDeckImageCache.IsValidSerial(new string('a', 64)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    public void Store_WithTraversalSerial_WritesNothingOutsideTheCacheRoot(string serial)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var hash = StreamDeckImageCache.Hash(bytes);

        _cache.Store(serial, hash, bytes);

        // The only thing on disk under the parent of _tempDir must still be
        // _tempDir itself (empty, since Store no-ops on an invalid serial) -
        // nothing escaped into a sibling or ancestor directory.
        var parent = Path.GetDirectoryName(_tempDir)!;
        Assert.False(Directory.Exists(Path.Combine(parent, "evil")));
        if (Directory.Exists(_tempDir))
        {
            Assert.Empty(Directory.GetFileSystemEntries(_tempDir));
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    public void Load_WithTraversalSerial_ReturnsNull(string serial)
    {
        Assert.Null(_cache.Load(serial, "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public void Evict_RemovesTheCachedFile()
    {
        var bytes = new byte[] { 4, 5, 6 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);
        Assert.NotNull(_cache.Load("SERIAL-1", hash));

        _cache.Evict("SERIAL-1", hash);

        Assert.Null(_cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void Evict_UnknownHash_IsANoOp()
    {
        _cache.Evict("SERIAL-1", "0000000000000000000000000000000000000000000000000000000000000000");
    }

    [Fact]
    public void Load_SecondCall_ServesFromMemoryWithoutTouchingDiskAgain()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);
        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));

        // Delete the file out from under the cache. If Load still hits disk
        // on every call, this second Load returns null; if it is served from
        // the in-memory copy Store/Load populate, it still returns the bytes.
        File.Delete(Path.Combine(_tempDir, "SERIAL-1", hash + ".bin"));

        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void Load_ColdDiskHit_BackfillsTheMemoryCache()
    {
        var bytes = new byte[] { 4, 4, 4 };
        var hash = StreamDeckImageCache.Hash(bytes);
        // Write directly to disk, bypassing Store, so the first Load is a
        // genuine cold miss that has to backfill the memory cache itself.
        var dir = Path.Combine(_tempDir, "SERIAL-1");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, hash + ".bin"), bytes);

        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));

        File.Delete(Path.Combine(dir, hash + ".bin"));
        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void MemoryCache_IsBoundedPerSerial_OldestHashEvictsFirst()
    {
        // One more than the per-serial capacity: the first hash stored must
        // fall out of the memory cache once the last one is stored.
        var hashes = new List<string>();
        for (var i = 0; i < 129; i++)
        {
            var bytes = BitConverter.GetBytes(i);
            var hash = StreamDeckImageCache.Hash(bytes);
            hashes.Add(hash);
            _cache.Store("SERIAL-1", hash, bytes);
        }

        // Deleting every file on disk means a Load can only succeed if the
        // memory cache still holds that hash.
        foreach (var hash in hashes)
        {
            File.Delete(Path.Combine(_tempDir, "SERIAL-1", hash + ".bin"));
        }

        Assert.Null(_cache.Load("SERIAL-1", hashes[0]));
        Assert.NotNull(_cache.Load("SERIAL-1", hashes[^1]));
    }
}

/// <summary>
/// A malformed DeckAction node anywhere in settings.json must not blast-radius
/// the rest of the document: JsonConfigStore.Load's catch-all resets EVERY
/// setting to defaults on any deserialization exception, so a throwing
/// converter for one field would silently wipe an unrelated user's whole
/// config. Exercises the real JsonConfigStore (see JsonConfigStoreCorruptLoadTests),
/// not a hand-written fake.
/// </summary>
public sealed class DeckActionSettingsLoadSurvivalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DeckActionSettingsLoadSurvivalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-deckaction-survival-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MalformedDeckActionInASlot_DoesNotResetTheRestOfSettings()
    {
        // Also exercises the legacy pre-pagination "deck":{"slots":[...]}
        // shape (no "pages" key) - DeckConfigConverter must normalize it into
        // one page for this document to load at all.
        // schemaVersion is set to CurrentSchemaVersion so the v18 deck-modes
        // migration does not run and hoist legacyDeck away before the
        // assertions below read it.
        var json = "{"
            + $"\"schemaVersion\":{NexusSettings.CurrentSchemaVersion},"
            + "\"lighting\":{\"globalBrightness\":0.42},"
            + "\"streamDeck\":{\"decks\":{\"SERIAL-1\":{\"legacyDeck\":{\"slots\":[{\"action\":\"not an object\"}]}}}}"
            + "}";
        File.WriteAllText(_path, json);

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        // The rest of the document survived (proves Load did not treat this
        // as corrupt and reset to defaults).
        Assert.Equal(0.42f, settings.Lighting.GlobalBrightness);
        Assert.False(File.Exists(_path + ".corrupt"));

        var pages = settings.StreamDeck.Decks["SERIAL-1"].LegacyDeck!.Pages;
        Assert.Single(pages);
        var slot = pages[0].Slots[0];
        Assert.NotNull(slot.Action);
        Assert.Equal("", slot.Action!.Type);
    }
}

/// <summary>
/// DeckConfig's own converter: legacy pre-pagination persisted shape and the
/// documented "a DeckConfig always has at least one page" invariant.
/// </summary>
public sealed class DeckConfigConverterTests
{
    private static DeckConfig Deserialize(string json) =>
        JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.DeckConfig)!;

    [Fact]
    public void LegacySlotsShape_WrapsIntoOnePage()
    {
        var config = Deserialize("{\"slots\":[{\"label\":\"A\"},{\"label\":\"B\"}]}");

        Assert.Single(config.Pages);
        Assert.Equal(2, config.Pages[0].Slots.Count);
        Assert.Equal("A", config.Pages[0].Slots[0].Label);
        Assert.Equal("B", config.Pages[0].Slots[1].Label);
    }

    [Fact]
    public void CurrentPagesShape_RoundTrips()
    {
        var config = Deserialize("{\"pages\":[{\"slots\":[{\"label\":\"P1\"}]},{\"slots\":[{\"label\":\"P2\"}]}]}");

        Assert.Equal(2, config.Pages.Count);
        Assert.Equal("P1", config.Pages[0].Slots[0].Label);
        Assert.Equal("P2", config.Pages[1].Slots[0].Label);

        var json = JsonSerializer.Serialize(config, PersistenceJsonContext.Default.DeckConfig);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("pages", out _));
        Assert.False(doc.RootElement.TryGetProperty("slots", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"oops\"")]
    [InlineData("[1,2,3]")]
    public void NeitherShapePresent_FallsBackToOneEmptyPage(string json)
    {
        var config = Deserialize(json);

        Assert.Single(config.Pages);
        Assert.Empty(config.Pages[0].Slots);
    }

    [Fact]
    public void MalformedPageEntry_IsSkippedRatherThanThrowing()
    {
        var config = Deserialize("{\"pages\":[\"not an object\",{\"slots\":[{\"label\":\"Real\"}]}]}");

        Assert.Equal(2, config.Pages.Count);
        Assert.Empty(config.Pages[0].Slots);
        Assert.Equal("Real", config.Pages[1].Slots[0].Label);
    }

    [Fact]
    public void SlotWithTitleStyle_RoundTripsEveryField()
    {
        var json = "{\"pages\":[{\"slots\":[{\"label\":\"Lock\",\"title\":{"
            + "\"show\":true,\"align\":\"top\",\"font\":\"mono\",\"size\":18,"
            + "\"bold\":true,\"italic\":false,\"underline\":true,\"color\":\"#ff0000\"}}]}]}";
        var config = Deserialize(json);

        var title = config.Pages[0].Slots[0].Title;
        Assert.NotNull(title);
        Assert.True(title!.Show);
        Assert.Equal("top", title.Align);
        Assert.Equal("mono", title.Font);
        Assert.Equal(18, title.Size);
        Assert.True(title.Bold);
        Assert.False(title.Italic);
        Assert.True(title.Underline);
        Assert.Equal("#ff0000", title.Color);

        var reserialized = JsonSerializer.Serialize(config, PersistenceJsonContext.Default.DeckConfig);
        using var doc = JsonDocument.Parse(reserialized);
        var titleEl = doc.RootElement.GetProperty("pages")[0].GetProperty("slots")[0].GetProperty("title");
        Assert.True(titleEl.GetProperty("show").GetBoolean());
        Assert.Equal("top", titleEl.GetProperty("align").GetString());
        Assert.Equal("mono", titleEl.GetProperty("font").GetString());
        Assert.Equal(18, titleEl.GetProperty("size").GetInt32());
        Assert.True(titleEl.GetProperty("bold").GetBoolean());
        Assert.False(titleEl.GetProperty("italic").GetBoolean());
        Assert.True(titleEl.GetProperty("underline").GetBoolean());
        Assert.Equal("#ff0000", titleEl.GetProperty("color").GetString());
    }

    [Fact]
    public void SlotWithNoTitleStyle_OmitsTheTitleKeyOnWrite()
    {
        var config = Deserialize("{\"pages\":[{\"slots\":[{\"label\":\"A\"}]}]}");
        Assert.Null(config.Pages[0].Slots[0].Title);

        var json = JsonSerializer.Serialize(config, PersistenceJsonContext.Default.DeckConfig);
        using var doc = JsonDocument.Parse(json);
        var slot = doc.RootElement.GetProperty("pages")[0].GetProperty("slots")[0];
        Assert.False(slot.TryGetProperty("title", out _));
    }
}
