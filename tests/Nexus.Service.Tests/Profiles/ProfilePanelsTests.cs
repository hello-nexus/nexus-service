using System.Text.Json;
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

    private PanelDeviceRecord Live(string id) => _store.Load().PanelDevices[id];

    [Fact]
    public void Switch_swaps_looks_and_leaves_identity_and_hardware_alone()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
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
    public void Panel_without_a_match_keeps_its_look()
    {
        _store.Update(s => s.PanelDevices["y70-a"] = Y70());
        _pm.Initialize();
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
        source.PanelDevices["y70-a"] = Y70();

        var file = ProfileSharing.ExtractShareable(source);
        var json = JsonSerializer.Serialize(file, PersistenceJsonContext.Default.NexusSettings);
        var loaded = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;

        var live = new NexusSettings();
        live.PanelDevices["y70-a"] = Y70();
        live.Tryx.Brightness = 90;
        live.Devices.Corsair.LcdBrightness = 80;
        live.Devices.TlLcdScreens["tl-1"] = new LianLiWirelessScreenSettings { Brightness = 55 };
        ProfileSharing.ApplyCategory(live, loaded, ProfileSharing.Panels);

        Assert.Equal("#010203", live.Tryx.OverlayColor);
        Assert.Equal("clip", live.Tryx.CurrentMedia);
        Assert.Equal(90, live.Tryx.Brightness);
        Assert.Equal("media-1", live.Devices.Corsair.LcdSelectedMediaId);
        Assert.Equal(80, live.Devices.Corsair.LcdBrightness);
        Assert.Equal("digital", live.Devices.TlLcdScreens["tl-1"].ClockFace);
        Assert.Equal(55, live.Devices.TlLcdScreens["tl-1"].Brightness);
        Assert.False(live.Devices.TlLcdScreens.ContainsKey("tl-2"));
    }

    [Fact]
    public void Reset_clears_looks_but_keeps_identity()
    {
        var s = new NexusSettings();
        s.PanelDevices["y70-a"] = Y70();
        s.PanelDevices["y70-a"].AccentColor = "#fff";
        s.PanelDevices["y70-a"].Presets = new() { new PanelPreset { Id = "p" } };
        s.Tryx.OverlayColor = "#123";

        ProfileSharing.ResetCategory(s, ProfileSharing.Panels);

        var r = s.PanelDevices["y70-a"];
        Assert.Null(r.AccentColor);
        Assert.Null(r.Presets);
        Assert.Equal("Y70", r.DisplayName);
        Assert.True(r.Flip180);
        Assert.Equal(new TryxSettings().OverlayColor, s.Tryx.OverlayColor);
    }
}
