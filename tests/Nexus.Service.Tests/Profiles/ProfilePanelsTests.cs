using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Panel;
using Nexus.Service.Sockets;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Profiles;

public class ProfilePanelsTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _pm;

    public ProfilePanelsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-profile-panels-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _pm = new ProfileManager(_store);
    }

    public void Dispose()
    {
        _pm.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceRecord Y70(string id = "y70-a") => new()
    {
        Id = id,
        DisplayName = "Y70",
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70, Orientation = "landscape" },
        FirstSeenAt = 5,
        Flip180 = true,
        LcdBrightness = 40,
    };

    private static PanelDeviceRecord Monitor(string id, string displayId, string serial) => new()
    {
        Id = id,
        DisplayName = "Mon " + id,
        DisplayId = displayId,
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor },
        DisplayFingerprint = new DisplayFingerprint { Manufacturer = "ACR", Product = "0A1B", Serial = 1, SerialText = serial },
    };

    private void InitializePerProfile()
    {
        _pm.Initialize();
        _pm.SetCategoryShared(ProfileSharing.Panels, false);
    }

    private PanelDeviceRecord Live(string id) => _store.Load().PanelDevices[id];

    [Fact]
    public void Switch_swaps_looks_and_leaves_identity_and_hardware_alone()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        InitializePerProfile();
        _store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#111111");
        var a = _pm.GetActiveEntry()!.Id;
        var b = _pm.CreateProfile("B").Id;
        _store.Update(s =>
        {
            var r = s.PanelDevices["y70-a"];
            r.AccentColor = "#222222";
            r.Presets = new() { new PanelPreset { Id = "p1", Name = "One", ThemeMode = "dark" } };
            r.ActivePresetId = "p1";
            r.Flip180 = false;
            r.DisplayName = "Renamed";
        });

        _pm.SwitchProfile(a);

        var back = Live("y70-a");
        Assert.Equal("#111111", back.AccentColor);
        Assert.Null(back.Presets);
        Assert.Null(back.ActivePresetId);
        Assert.False(back.Flip180);
        Assert.Equal("Renamed", back.DisplayName);
        Assert.Equal(40, back.LcdBrightness);
        Assert.Equal(5, back.FirstSeenAt);

        _pm.SwitchProfile(b);
        Assert.Equal("#222222", Live("y70-a").AccentColor);
        Assert.Equal("p1", Live("y70-a").Presets![0].Id);
    }

    [Fact]
    public void Flex_screen_content_follows_the_profile_across_a_switch()
    {
        InitializePerProfile();
        var a = _pm.GetActiveEntry()!.Id;
        var b = _pm.CreateProfile("B").Id;
        _store.Update(s => s.Devices.FlexLcdScreens["flex-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "digital", Brightness = 30 });
        _pm.SwitchProfile(a);
        _store.Update(s => s.Devices.FlexLcdScreens["flex-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "analogClassic", Brightness = 77 });

        _pm.SwitchProfile(b);
        Assert.Equal("digital", _store.Load().Devices.FlexLcdScreens["flex-1"].ClockFace);
        Assert.Equal(77, _store.Load().Devices.FlexLcdScreens["flex-1"].Brightness);

        _pm.SwitchProfile(a);
        Assert.Equal("analogClassic", _store.Load().Devices.FlexLcdScreens["flex-1"].ClockFace);
    }

    private static NexusSettings ImportedFriend(string accent = "#aaaaaa")
    {
        var src = new NexusSettings();
        src.PanelDevices["y70-x"] = Y70("y70-x");
        src.PanelDevices["y70-x"].AccentColor = accent;
        src.PanelDevices["y70-x"].Presets = new() { new PanelPreset { Id = "t", Name = "Theirs", AccentColor = "#bbbbbb" } };
        return ProfileSharing.ExtractShareable(src);
    }

    [Fact]
    public void Import_with_panels_shared_adds_the_imported_look_and_presets_without_touching_the_live_look()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        _store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#111111");
        var changed = 0;
        _pm.PanelsChanged += () => changed++;

        _pm.ImportProfile("Friend", ImportedFriend());

        var live = Live("y70-a");
        Assert.Equal("#111111", live.AccentColor);
        Assert.Null(live.ActivePresetId);
        Assert.Equal(new[] { "Friend", "Theirs" }, live.Presets!.Select(p => p.Name).ToArray());
        Assert.Equal("#aaaaaa", live.Presets![0].AccentColor);
        Assert.Equal("#bbbbbb", live.Presets![1].AccentColor);
        Assert.Equal(1, changed);

        _pm.ImportProfile("Friend again", ImportedFriend());
        Assert.Equal(2, Live("y70-a").Presets!.Count);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Import_with_panels_per_profile_leaves_presets_alone()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        InitializePerProfile();

        _pm.ImportProfile("Friend", ImportedFriend());

        Assert.Null(Live("y70-a").Presets);
    }

    [Fact]
    public void Import_with_no_matching_panel_adds_nothing()
    {
        _pm.Initialize();
        var changed = 0;
        _pm.PanelsChanged += () => changed++;

        _pm.ImportProfile("Friend", ImportedFriend());

        Assert.Empty(_store.Load().PanelDevices);
        Assert.Equal(0, changed);
    }

    private static NexusSettings ImportedWith(PanelDeviceRecord record) =>
        ProfileSharing.ExtractShareable(new NexusSettings { PanelDevices = { [record.Id] = record } });

    [Fact]
    public void Import_dedupes_looks_that_differ_only_in_seeded_layout_ids()
    {
        _store.Update(s =>
        {
            s.PanelDevices["y70-a"] = Y70();
            s.PanelDevices["y70-a"].Layout = PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70);
        });
        _pm.Initialize();

        var plain = Y70("y70-x");
        plain.Layout = PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70);
        _pm.ImportProfile("Untouched", ImportedWith(plain));
        Assert.Null(Live("y70-a").Presets);

        _store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#123456");
        var same = Y70("y70-x");
        same.Layout = PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70);
        same.AccentColor = "#123456";
        _pm.ImportProfile("Same look", ImportedWith(same));
        Assert.Null(Live("y70-a").Presets);
    }

    [Fact]
    public void Replacing_the_active_profile_by_import_flushes_the_new_presets_to_the_other_files_and_redraws_once()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var first = _pm.GetActiveEntry()!;
        var other = _pm.CreateProfile("Other").Id;
        _pm.SwitchProfile(first.Id);
        var switched = 0;
        var panels = 0;
        _pm.OnProfileSwitched += () => switched++;
        _pm.PanelsChanged += () => panels++;

        _pm.ImportProfile(first.Name, ImportedFriend(), replaceExisting: true);

        var file = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, $"profile-{other}.json")), PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Contains(file.PanelDevices.Values.SelectMany(r => r.Presets ?? new()), p => p.AccentColor == "#aaaaaa");
        Assert.Equal(1, switched);
        Assert.Equal(0, panels);
    }

    [Fact]
    public void Import_with_panels_shared_keeps_looks_for_panels_not_on_this_pc_and_a_later_adopt_applies_them()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var src = new NexusSettings();
        src.PanelDevices["y70-x"] = Y70("y70-x");
        src.PanelDevices["mon-gone"] = Monitor("mon-gone", "d9", "GONE");
        src.PanelDevices["mon-gone"].AccentColor = "#cafe01";
        src.PanelDevices["mon-gone"].LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var imported = _pm.ImportProfile("Friend", ProfileSharing.ExtractShareable(src));

        var file = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, $"profile-{imported.Id}.json")), PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Contains(file.PanelDevices.Values, r => r.AccentColor == "#cafe01");

        _pm.SwitchProfile(imported.Id);
        var registry = new PanelDeviceRegistry(_store);
        var fp = new DisplayFingerprint { Manufacturer = "ACR", Product = "0A1B", Serial = 1, SerialText = "GONE" };
        var record = registry.AllocateForDisplay("d1", "Mon", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, fp).Record;
        Assert.True(_pm.AdoptSavedLook(record.Id));
        Assert.Equal("#cafe01", Live(record.Id).AccentColor);
    }

    [Fact]
    public void ImportProfileWithId_replacing_the_active_profile_merges_flushes_and_redraws_once()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var first = _pm.GetActiveEntry()!;
        var other = _pm.CreateProfile("Other").Id;
        _pm.SwitchProfile(first.Id);
        var switched = 0;
        var panels = 0;
        _pm.OnProfileSwitched += () => switched++;
        _pm.PanelsChanged += () => panels++;

        _pm.ImportProfileWithId(first.Id, first.Name, ImportedFriend(), absorbPanelLooks: true);

        Assert.Equal(new[] { "Default", "Theirs" }, Live("y70-a").Presets!.Select(p => p.Name).ToArray());
        var file = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, $"profile-{other}.json")), PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Contains(file.PanelDevices.Values.SelectMany(r => r.Presets ?? new()), p => p.AccentColor == "#aaaaaa");
        Assert.Equal(1, switched);
        Assert.Equal(0, panels);
    }

    [Fact]
    public void Replace_active_import_does_not_apply_a_second_same_surface_record_over_a_default_live_look()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var active = _pm.GetActiveEntry()!;
        var src = new NexusSettings();
        src.PanelDevices["y70-a"] = Y70("y70-a");
        src.PanelDevices["y70-old"] = Y70("y70-old");
        src.PanelDevices["y70-old"].AccentColor = "#0ldold";
        src.PanelDevices["y70-old"].LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _pm.ImportProfile(active.Name, ProfileSharing.ExtractShareable(src), replaceExisting: true);

        Assert.Null(Live("y70-a").AccentColor);
        Assert.Single(_store.Load().PanelDevices);
    }

    [Fact]
    public void An_imported_loaded_preset_is_named_after_itself_and_its_stale_copy_is_skipped()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var src = Y70("y70-x");
        src.AccentColor = "#aaaaaa";
        src.Presets = new() { new PanelPreset { Id = "t", Name = "Theirs", AccentColor = "#stale1" } };
        src.ActivePresetId = "t";

        _pm.ImportProfile("Friend", ImportedWith(src));

        var presets = Live("y70-a").Presets!;
        Assert.Single(presets);
        Assert.Equal("Theirs", presets[0].Name);
        Assert.Equal("#aaaaaa", presets[0].AccentColor);
    }

    [Fact]
    public void Merged_presets_get_unique_names_and_stop_at_the_cap()
    {
        var live = new NexusSettings();
        live.PanelDevices["y70-a"] = Y70();
        live.PanelDevices["y70-a"].Presets = new() { new PanelPreset { Id = "e", Name = "Friend", AccentColor = "#cccccc" } };
        var imported = ImportedFriend();

        Assert.True(PanelProfileData.MergeImportedLooks(live, imported, "Friend", out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(new[] { "Friend", "Friend 2", "Theirs" }, live.PanelDevices["y70-a"].Presets!.Select(p => p.Name).ToArray());

        var full = new NexusSettings();
        full.PanelDevices["y70-a"] = Y70();
        full.PanelDevices["y70-a"].Presets = Enumerable.Range(0, PanelPresets.Cap - 1)
            .Select(i => new PanelPreset { Id = "p" + i, Name = "P" + i, AccentColor = "#00000" + i }).ToList();
        Assert.True(PanelProfileData.MergeImportedLooks(full, ImportedFriend(), "Friend", out skipped));
        Assert.Equal(1, skipped);
        Assert.Equal(PanelPresets.Cap, full.PanelDevices["y70-a"].Presets!.Count);
    }

    [Fact]
    public void Panel_without_a_match_keeps_its_look()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        InitializePerProfile();
        var a = _pm.GetActiveEntry()!.Id;
        _pm.CreateProfile("B");
        _store.Update(s =>
        {
            s.PanelDevices["y70-a"].AccentColor = "#222222";
            s.PanelDevices["mon-new"] = Monitor("mon-new", "d9", "NEW");
            s.PanelDevices["mon-new"].AccentColor = "#abcdef";
        });

        _pm.SwitchProfile(a);

        Assert.Null(Live("y70-a").AccentColor);
        Assert.Equal("#abcdef", Live("mon-new").AccentColor);
        Assert.Equal(2, _store.Load().PanelDevices.Count);
    }

    [Fact]
    public void Shared_panels_keep_the_same_look_across_a_switch()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
        var a = _pm.GetActiveEntry()!.Id;
        _pm.CreateProfile("B");
        _pm.SetCategoryShared(ProfileSharing.Panels, true);
        _store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#333333");

        _pm.SwitchProfile(a);

        Assert.Equal("#333333", Live("y70-a").AccentColor);
    }

    [Fact]
    public void Export_then_import_matches_by_surface_or_fingerprint_and_skips_phones()
    {
        _store.Update(s =>
        {
            s.PanelDevices["y70-a"] = Y70();
            s.PanelDevices["y70-a"].AccentColor = "#aa0000";
            s.PanelDevices["mon-a"] = Monitor("mon-a", "d1", "SER1");
            s.PanelDevices["mon-a"].AccentColor = "#00aa00";
            s.PanelDevices["mon-b"] = Monitor("mon-b", "d2", "SER2");
            s.PanelDevices["mon-b"].AccentColor = "#0000aa";
            s.PanelDevices["ph"] = new PanelDeviceRecord
            {
                Id = "ph",
                Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Phone },
                AccentColor = "#fefefe",
            };
        });
        _pm.Initialize();
        var json = _pm.ExportProfileJson(_pm.GetActiveEntry()!.Id)!;
        Assert.DoesNotContain("#fefefe", json);
        Assert.DoesNotContain("Mon mon-a", json);
        Assert.DoesNotContain("\"d1\"", json);

        var other = Path.Combine(Path.GetTempPath(), "nexus-profile-panels-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(other);
        var store2 = new JsonConfigStore(Path.Combine(other, "settings.json"));
        var pm2 = new ProfileManager(store2);
        try
        {
            store2.Update(s =>
            {
                s.PanelDevices["zzz"] = Y70("zzz");
                s.PanelDevices["m2"] = Monitor("m2", "x2", "SER2");
                s.PanelDevices["m3"] = Monitor("m3", "x3", "OTHER");
                s.PanelDevices["m3"].AccentColor = "#keep";
                s.PanelDevices["ph2"] = new PanelDeviceRecord
                {
                    Id = "ph2",
                    Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Phone },
                    AccentColor = "#phone",
                };
            });
            pm2.Initialize();
            pm2.SetCategoryShared(ProfileSharing.Panels, false);
            var imported = pm2.ImportProfileJson(json, replaceExisting: true);
            pm2.SwitchProfile(imported.Id);

            var live = store2.Load().PanelDevices;
            Assert.Equal("#aa0000", live["zzz"].AccentColor);
            Assert.Equal("#0000aa", live["m2"].AccentColor);
            Assert.Equal("#keep", live["m3"].AccentColor);
            Assert.Equal("#phone", live["ph2"].AccentColor);
            Assert.Equal(4, live.Count);
        }
        finally
        {
            pm2.Dispose();
            store2.Dispose();
            try { Directory.Delete(other, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Migration_copies_live_looks_into_existing_profiles_once()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            """{ "activeProfileId": "p1", "profiles": [ { "id": "p1", "name": "p1" }, { "id": "p2", "name": "p2" } ] }""");
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), "{}");
        _store.Update(s =>
        {
            s.PanelDevices["y70-a"] = Y70();
            s.PanelDevices["y70-a"].AccentColor = "#123456";
            s.Tryx.OverlayColor = "#654321";
        });

        _pm.Initialize();

        Assert.True(_store.Load().PanelsCategorySeeded);
        foreach (var id in new[] { "p1", "p2" })
        {
            var text = File.ReadAllText(Path.Combine(_dir, $"profile-{id}.json"));
            Assert.Contains("#123456", text);
            Assert.Contains("#654321", text);
        }

        // Idempotent: a second boot must not overwrite a profile's own edits.
        _pm.SwitchProfile("p2");
        _store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#999999");
        _pm.SaveActiveProfile();
        _pm.Dispose();
        var pm2 = new ProfileManager(_store);
        try
        {
            pm2.Initialize();
            Assert.Contains("#999999", File.ReadAllText(Path.Combine(_dir, "profile-p2.json")));
        }
        finally { pm2.Dispose(); }
    }

    [Fact]
    public void Default_looking_records_do_not_break_the_pristine_projection()
    {
        var settings = new NexusSettings();
        settings.PanelDevices["y70-a"] = Y70();
        var extract = ProfileSharing.ExtractShareable(settings);

        Assert.True(PanelProfileData.IsDefaultLook(extract.PanelDevices["y70-a"]));
        settings.PanelDevices["y70-a"].AccentColor = "#fff";
        Assert.False(PanelProfileData.IsDefaultLook(ProfileSharing.ExtractShareable(settings).PanelDevices["y70-a"]));
    }

    [Fact]
    public void Cooler_content_round_trips_and_never_creates_screens()
    {
        var source = new NexusSettings();
        source.Tryx.OverlayColor = "#010203";
        source.Tryx.CurrentMedia = "clip";
        source.Tryx.Brightness = 7;
        source.Devices.Corsair.LcdSelectedMediaId = "media-1";
        source.Devices.Corsair.LcdBrightness = 12;
        source.Devices.TlLcdScreens["tl-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "digital", Brightness = 33 };
        source.Devices.TlLcdScreens["tl-2"] = new LianLiWirelessScreenSettings { ContentType = "image", MediaId = "m" };
        source.Devices.FlexLcdScreens["flex-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "analogClassic", Brightness = 21 };
        source.PanelDevices["y70-a"] = Y70();

        var file = ProfileSharing.ExtractShareable(source);
        var json = JsonSerializer.Serialize(file, PersistenceJsonContext.Default.NexusSettings);
        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;

        var live = new NexusSettings();
        live.PanelDevices["y70-a"] = Y70();
        live.Tryx.Brightness = 90;
        live.Devices.Corsair.LcdBrightness = 80;
        live.Devices.TlLcdScreens["tl-1"] = new LianLiWirelessScreenSettings { Brightness = 55 };
        live.Devices.FlexLcdScreens["flex-1"] = new LianLiWirelessScreenSettings { Brightness = 66 };
        ProfileSharing.ApplyCategory(live, loaded, ProfileSharing.Panels);

        Assert.Equal("#010203", live.Tryx.OverlayColor);
        Assert.Equal("clip", live.Tryx.CurrentMedia);
        Assert.Equal(90, live.Tryx.Brightness);
        Assert.Equal("media-1", live.Devices.Corsair.LcdSelectedMediaId);
        Assert.Equal(80, live.Devices.Corsair.LcdBrightness);
        Assert.Equal("digital", live.Devices.TlLcdScreens["tl-1"].ClockFace);
        Assert.Equal(55, live.Devices.TlLcdScreens["tl-1"].Brightness);
        Assert.False(live.Devices.TlLcdScreens.ContainsKey("tl-2"));
        Assert.Equal("analogClassic", live.Devices.FlexLcdScreens["flex-1"].ClockFace);
        Assert.Equal(66, live.Devices.FlexLcdScreens["flex-1"].Brightness);
    }

    [Fact]
    public void Reset_clears_looks_but_keeps_identity()
    {
        var s = new NexusSettings();
        s.PanelDevices["y70-a"] = Y70();
        s.PanelDevices["y70-a"].AccentColor = "#fff";
        s.PanelDevices["y70-a"].Presets = new() { new PanelPreset { Id = "p" } };
        s.Tryx.OverlayColor = "#123";
        s.Devices.FlexLcdScreens["flex-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "digital", Brightness = 9 };

        ProfileSharing.ResetCategory(s, ProfileSharing.Panels);
        Assert.Equal(new LianLiWirelessScreenSettings().ContentType, s.Devices.FlexLcdScreens["flex-1"].ContentType);
        Assert.Null(s.Devices.FlexLcdScreens["flex-1"].ClockFace);
        Assert.Equal(9, s.Devices.FlexLcdScreens["flex-1"].Brightness);

        var r = s.PanelDevices["y70-a"];
        Assert.Null(r.AccentColor);
        Assert.Null(r.Presets);
        Assert.Equal("Y70", r.DisplayName);
        Assert.True(r.Flip180);
        Assert.Equal(new TryxSettings().OverlayColor, s.Tryx.OverlayColor);
    }

    private static PanelDeviceRecord Lcd(string id, string? accent = null) => new()
    {
        Id = id,
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdRound },
        AccentColor = accent,
    };

    private static NexusSettings FileWith(params PanelDeviceRecord[] saved)
    {
        var file = new NexusSettings { HasPanelsData = true };
        foreach (var r in saved)
        {
            file.PanelDevices[r.Id] = r;
        }
        return file;
    }

    [Fact]
    public void Several_lcd_round_records_match_by_id_only()
    {
        var live = new NexusSettings();
        live.PanelDevices["l1"] = Lcd("l1");
        live.PanelDevices["l2"] = Lcd("l2");
        ProfileSharing.ApplyCategory(live, FileWith(Lcd("l1", "#111111"), Lcd("l2", "#222222")), ProfileSharing.Panels);
        Assert.Equal("#111111", live.PanelDevices["l1"].AccentColor);
        Assert.Equal("#222222", live.PanelDevices["l2"].AccentColor);

        var other = new NexusSettings();
        other.PanelDevices["x1"] = Lcd("x1", "#keep1");
        other.PanelDevices["x2"] = Lcd("x2", "#keep2");
        ProfileSharing.ApplyCategory(other, FileWith(Lcd("l1", "#111111"), Lcd("l2", "#222222")), ProfileSharing.Panels);
        Assert.Equal("#keep1", other.PanelDevices["x1"].AccentColor);
        Assert.Equal("#keep2", other.PanelDevices["x2"].AccentColor);
    }

    [Fact]
    public void Single_surface_fallback_needs_exactly_one_on_each_side()
    {
        var live = new NexusSettings();
        live.PanelDevices["new"] = Lcd("new", "#keep");
        ProfileSharing.ApplyCategory(live, FileWith(Lcd("old1", "#abc"), Lcd("old2", "#abd")), ProfileSharing.Panels);
        Assert.Equal("#keep", live.PanelDevices["new"].AccentColor);

        var y = new NexusSettings();
        y.PanelDevices["y-new"] = Y70("y-new");
        var saved = Y70("y-old");
        saved.AccentColor = "#abc";
        ProfileSharing.ApplyCategory(y, FileWith(saved), ProfileSharing.Panels);
        Assert.Equal("#abc", y.PanelDevices["y-new"].AccentColor);
    }

    [Fact]
    public void Identical_monitors_without_a_serial_are_not_matched_across_ids()
    {
        PanelDeviceRecord Plain(string id, string? accent = null)
        {
            var m = Monitor(id, "d-" + id, "");
            m.DisplayFingerprint!.Serial = 0;
            m.DisplayFingerprint.SerialText = null;
            m.AccentColor = accent;
            return m;
        }

        var saved = ProfileSharing.ExtractShareable(new NexusSettings
        {
            PanelDevices = { ["a"] = Plain("a", "#aaa"), ["b"] = Plain("b", "#bbb") },
        });
        var live = new NexusSettings();
        live.PanelDevices["c"] = Plain("c", "#keep");
        live.PanelDevices["d"] = Plain("d", "#keep");

        ProfileSharing.ApplyCategory(live, saved, ProfileSharing.Panels);

        Assert.Equal("#keep", live.PanelDevices["c"].AccentColor);
        Assert.Equal("#keep", live.PanelDevices["d"].AccentColor);
    }

    [Fact]
    public void Fingerprint_travels_hashed_with_no_raw_serial()
    {
        var source = new NexusSettings();
        source.PanelDevices["m"] = Monitor("m", "d1", "SECRET-SERIAL-77");
        var json = JsonSerializer.Serialize(ProfileSharing.ExtractShareable(source), PersistenceJsonContext.Default.NexusSettings);

        Assert.DoesNotContain("SECRET-SERIAL-77", json);
        Assert.DoesNotContain("ACR", json);
        Assert.Contains(PanelProfileData.FingerprintKey(source.PanelDevices["m"].DisplayFingerprint)!, json);
    }

    [Fact]
    public void WidgetSize_travels_with_layout_and_presets()
    {
        var source = new NexusSettings();
        var r = Y70();
        r.WidgetSize = PanelWidgetSizes.Small;
        r.Layout = new PanelLayoutDto { Surface = "y70" };
        r.Presets = new() { new PanelPreset { Id = "p", WidgetSize = PanelWidgetSizes.Large } };
        source.PanelDevices[r.Id] = r;

        var live = new NexusSettings();
        live.PanelDevices[r.Id] = Y70();
        ProfileSharing.ApplyCategory(live, ProfileSharing.ExtractShareable(source), ProfileSharing.Panels);

        Assert.Equal(PanelWidgetSizes.Small, live.PanelDevices[r.Id].WidgetSize);
        Assert.Equal(PanelWidgetSizes.Large, live.PanelDevices[r.Id].Presets![0].WidgetSize);

        var preset = new PanelPreset();
        PanelPresets.Copy(r, preset);
        Assert.Equal(PanelWidgetSizes.Small, preset.WidgetSize);
    }

    [Fact]
    public void Corsair_only_profile_switches_content_and_null_media_clears_it()
    {
        var file = new NexusSettings { HasPanelsData = true };
        var live = new NexusSettings();
        live.Devices.Corsair.LcdSelectedMediaId = "old";
        live.Devices.Corsair.LcdBrightness = 60;

        ProfileSharing.ApplyCategory(live, file, ProfileSharing.Panels);

        Assert.Null(live.Devices.Corsair.LcdSelectedMediaId);
        Assert.Equal(60, live.Devices.Corsair.LcdBrightness);

        var unmarked = new NexusSettings();
        live.Devices.Corsair.LcdSelectedMediaId = "keep";
        ProfileSharing.ApplyCategory(live, unmarked, ProfileSharing.Panels);
        Assert.Equal("keep", live.Devices.Corsair.LcdSelectedMediaId);
    }

    [Fact]
    public void WriteTo_keeps_saved_records_for_panels_absent_here()
    {
        var elsewhere = Lcd("elsewhere", "#eee");
        elsewhere.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var file = FileWith(elsewhere);
        var source = new NexusSettings();
        source.PanelDevices["y70-a"] = Y70();
        source.PanelDevices["y70-a"].AccentColor = "#here";

        ProfileSharing.WriteCategory(file, source, ProfileSharing.Panels);

        Assert.Equal("#eee", file.PanelDevices["elsewhere"].AccentColor);
        Assert.Equal("#here", file.PanelDevices["y70-a"].AccentColor);

        // A record that pairs with a live one by surface is replaced, not duplicated.
        var again = FileWith(Y70("y70-old"));
        ProfileSharing.WriteCategory(again, source, ProfileSharing.Panels);
        Assert.Single(again.PanelDevices);
        Assert.True(again.PanelDevices.ContainsKey("y70-a"));
    }

    [Fact]
    public void Strip_seeded_and_install_default_layouts_count_as_default_looks()
    {
        var strip = new PanelDeviceRecord
        {
            Id = "w",
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor, CssWidth = 1600, CssHeight = 400 },
        };
        strip.Layout = PanelLayoutDefaults.StripSeedFor(strip);
        Assert.NotNull(strip.Layout);
        Assert.True(PanelProfileData.IsDefaultLook(strip));

        var y = Y70();
        y.Layout = PanelLayoutDefaults.WithStableIds(PanelLayoutDefaults.ForSurface(PanelSurfaces.Y70), "id");
        Assert.True(PanelProfileData.IsDefaultLook(y));

        y.AccentColor = "#fff";
        Assert.False(PanelProfileData.IsDefaultLook(y));
    }

    [Fact]
    public void AioScreens_round_trip_content_but_not_brightness()
    {
        var source = new NexusSettings();
        source.PanelDevices["y70-a"] = Y70();
        source.Devices.LianLiWireless.AioScreens["AA"] = new LianLiAioScreenSettings { Theme = 3, ShowFanSpeed = true, LabelColor = "#123456", Brightness = 10 };

        var live = new NexusSettings();
        live.PanelDevices["y70-a"] = Y70();
        live.Devices.LianLiWireless.AioScreens["AA"] = new LianLiAioScreenSettings { Brightness = 77 };
        ProfileSharing.ApplyCategory(live, ProfileSharing.ExtractShareable(source), ProfileSharing.Panels);

        var aio = live.Devices.LianLiWireless.AioScreens["AA"];
        Assert.Equal(3, aio.Theme);
        Assert.True(aio.ShowFanSpeed);
        Assert.Equal("#123456", aio.LabelColor);
        Assert.Equal(77, aio.Brightness);
    }

    [Fact]
    public void Panels_reapply_broadcasts_every_record_from_any_switch_path()
    {
        var store = new JsonConfigStore(Path.Combine(_dir, "s2.json"));
        try
        {
            store.Update(s =>
            {
                s.PanelDevices["a"] = Y70("a");
                s.PanelDevices["b"] = Lcd("b");
            });
            var hub = new MultiplexHub();
            var seen = new List<string>();
            hub.OnBroadcastForTest += (topic, _) => seen.Add(topic);
            using var sub = hub.AddTestSubscription(PanelTopics.PanelDevice);
            var sp = new ServiceCollection().AddSingleton(hub).BuildServiceProvider();

            AppBootstrap.ReapplyPanelsAfterProfileSwitch(sp, store);

            Assert.Equal(2, seen.Count(t => t == PanelTopics.PanelDevice));
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void Resetting_panels_raises_only_the_panels_event_not_the_switch_event()
    {
        InitializePerProfile();
        var switched = 0;
        var panels = 0;
        _pm.OnProfileSwitched += () => switched++;
        _pm.PanelsChanged += () => panels++;

        _pm.ResetCategory(_pm.GetActiveEntry()!.Id, ProfileSharing.Panels);
        _pm.ResetCategory(_pm.GetActiveEntry()!.Id, ProfileSharing.Theme);
        _pm.ResetProfile(_pm.GetActiveEntry()!.Id);

        Assert.Equal(0, switched);
        Assert.Equal(2, panels);
    }

    [Fact]
    public void Absent_records_age_out_and_are_capped_newest_first()
    {
        var now = DateTimeOffset.UtcNow;
        PanelDeviceRecord Saved(string id, TimeSpan age) => new()
        {
            Id = id,
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdRound },
            LastSeenAt = (now - age).ToUnixTimeMilliseconds(),
        };

        var file = FileWith(Saved("stale", TimeSpan.FromDays(120)));
        for (var i = 0; i < PanelProfileData.MaxAbsentRecords + 4; i++)
        {
            file.PanelDevices["a" + i] = Saved("a" + i, TimeSpan.FromDays(i + 1));
        }

        ProfileSharing.WriteCategory(file, new NexusSettings(), ProfileSharing.Panels);

        Assert.False(file.PanelDevices.ContainsKey("stale"));
        Assert.Equal(PanelProfileData.MaxAbsentRecords, file.PanelDevices.Count);
        Assert.True(file.PanelDevices.ContainsKey("a0"));
        Assert.False(file.PanelDevices.ContainsKey("a" + PanelProfileData.MaxAbsentRecords));
    }

    [Fact]
    public void Preset_widget_size_backfill_covers_live_and_file_and_is_idempotent()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            """{ "activeProfileId": "p1", "profiles": [ { "id": "p1", "name": "p1" } ] }""");
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"),
            """{ "hasPanelsData": true, "panelDevices": { "y70-a": { "id": "y70-a", "widgetSize": "small", "capabilities": { "surface": "y70" }, "presets": [ { "id": "p", "name": "p" } ] } } }""");
        _store.Update(s =>
        {
            var r = Y70();
            r.WidgetSize = PanelWidgetSizes.Large;
            r.Presets = new() { new PanelPreset { Id = "p" }, new PanelPreset { Id = "q", WidgetSize = PanelWidgetSizes.Small } };
            s.PanelDevices["y70-a"] = r;
        });

        _pm.Initialize();

        Assert.Equal(PanelWidgetSizes.Large, Live("y70-a").Presets![0].WidgetSize);
        Assert.Equal(PanelWidgetSizes.Small, Live("y70-a").Presets![1].WidgetSize);
        Assert.Contains("\"widgetSize\": \"large\"", File.ReadAllText(Path.Combine(_dir, "profile-p1.json")));

        PanelProfileData.BackfillPresetWidgetSize(_store.Load());
        Assert.Equal(PanelWidgetSizes.Large, Live("y70-a").Presets![0].WidgetSize);
    }

    [Fact]
    public void Sync_hash_is_stable_across_ticks_while_live_timestamps_move_within_a_day()
    {
        var day = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _store.Update(s =>
        {
            s.PanelDevices["y70-a"] = Y70();
            s.PanelDevices["y70-a"].AccentColor = "#123456";
            s.PanelDevices["y70-a"].LastSeenAt = day;
        });
        _pm.Initialize();
        var id = _pm.GetActiveEntry()!.Id;

        var first = Nexus.Service.Cloud.CloudProfileSyncService.HashPayload(_pm.ExportProfileForSync(id)!);
        _store.Update(s => s.PanelDevices["y70-a"].LastSeenAt = day + 5 * 60_000);
        var second = Nexus.Service.Cloud.CloudProfileSyncService.HashPayload(_pm.ExportProfileForSync(id)!);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_late_fingerprint_reaches_every_profile_file_and_rematches_a_recreated_record()
    {
        _store.Update(s =>
        {
            var m = Monitor("m", "d1", "SER1");
            m.DisplayFingerprint = null;
            m.AccentColor = "#bbbbbb";
            s.PanelDevices["m"] = m;
        });
        _pm.Initialize();
        var a = _pm.GetActiveEntry()!.Id;
        var b = _pm.CreateProfile("B").Id;
        _pm.CreateProfile("C");
        var path = (string id) => Path.Combine(_dir, $"profile-{id}.json");
        Assert.DoesNotContain("fingerprintKey", File.ReadAllText(path(a)));

        var fp = new DisplayFingerprint { Manufacturer = "ACR", Product = "0A1B", Serial = 1, SerialText = "SER1" };
        _store.Update(s => s.PanelDevices["m"].DisplayFingerprint = fp);
        _pm.StampFingerprintKeys(new[] { "m" });

        var key = PanelProfileData.FingerprintKey(fp)!;
        Assert.Contains(key, File.ReadAllText(path(a)));
        Assert.Contains(key, File.ReadAllText(path(b)));

        // Demote and re-promote: a new record id with the same monitor.
        _store.Update(s =>
        {
            s.PanelDevices.Clear();
            var again = Monitor("m2", "d9", "SER1");
            s.PanelDevices["m2"] = again;
        });
        _pm.SwitchProfile(b);

        Assert.Equal("#bbbbbb", Live("m2").AccentColor);
    }

    private static readonly DisplayFingerprint Fp = new() { Manufacturer = "ACR", Product = "0A1B", Serial = 1, SerialText = "SER1" };

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

    private (PanelDeviceRegistry Registry, string A, string B) PromoteAndCustomize()
    {
        var registry = new PanelDeviceRegistry(_store);
        InitializePerProfile();
        registry.DisplayRecordReady += record => _pm.AdoptSavedLook(record.Id);
        var a = _pm.GetActiveEntry()!.Id;
        var caps = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor };
        var first = registry.AllocateForDisplay("d1", "Mon", caps, Fp).Record;
        _store.Update(s => s.PanelDevices[first.Id].AccentColor = "#aaaaaa");
        var b = _pm.CreateProfile("B").Id;
        _store.Update(s => s.PanelDevices[first.Id].AccentColor = "#bbbbbb");
        _pm.SwitchProfile(a);
        return (registry, a, b);
    }

    private string Repromote(PanelDeviceRegistry registry)
    {
        var old = _store.Load().PanelDevices.Values.Single(r => r.DisplayId == "d1").Id;
        registry.DisablePanelForDisplay("d1");
        _store.Update(s => s.PanelDevices.Remove(old));
        return registry.AllocateForDisplay("d1", "Mon", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, Fp).Record.Id;
    }

    [Fact]
    public void Repromoted_monitor_adopts_the_active_look_at_once_and_each_file_keeps_one_entry()
    {
        var (registry, a, b) = PromoteAndCustomize();
        var key = PanelProfileData.FingerprintKey(Fp)!;

        var n = Repromote(registry);

        Assert.Equal("#aaaaaa", Live(n).AccentColor);
        _pm.SwitchProfile(b);
        Assert.Equal("#bbbbbb", Live(n).AccentColor);
        _pm.SwitchProfile(a);
        Assert.Equal(1, Count(File.ReadAllText(Path.Combine(_dir, $"profile-{a}.json")), key));
        Assert.Equal(1, Count(File.ReadAllText(Path.Combine(_dir, $"profile-{b}.json")), key));

        var n2 = Repromote(registry);
        Assert.Equal("#aaaaaa", Live(n2).AccentColor);
        _pm.SwitchProfile(b);
        Assert.Equal("#bbbbbb", Live(n2).AccentColor);
    }

    [Fact]
    public void A_customized_new_record_is_not_overwritten_by_an_older_saved_entry()
    {
        var (registry, a, _) = PromoteAndCustomize();
        var old = _store.Load().PanelDevices.Values.Single().Id;
        _pm.SaveActiveProfile();
        registry.DisablePanelForDisplay("d1");
        _store.Update(s => s.PanelDevices.Remove(old));
        var n = registry.AllocateForDisplay("d1", "Mon", new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, Fp).Record.Id;
        _store.Update(s => s.PanelDevices[n].AccentColor = "#cccccc");

        _pm.SaveActiveProfile();
        _pm.SwitchProfile(_pm.GetManifest().Profiles.First(p => p.Id != a).Id);
        _pm.SwitchProfile(a);

        Assert.Equal("#cccccc", Live(n).AccentColor);
        Assert.Equal(1, Count(File.ReadAllText(Path.Combine(_dir, $"profile-{a}.json")), PanelProfileData.FingerprintKey(Fp)!));
    }
}
