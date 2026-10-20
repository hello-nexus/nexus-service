using Nexus.Service.Models.Panel;
using System.Text.Json;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>Panels defaults to Shared like Device: fresh installs, the one-time upgrade add, and an opt-out that sticks.</summary>
public class ProfileManagerPanelsDefaultTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;

    public ProfileManagerPanelsDefaultTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-panels-default-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceRecord Y70() => new()
    {
        Id = "y70-a",
        DisplayName = "Y70",
        Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70, Orientation = "landscape" },
    };

    private void WriteTwoProfiles()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"),
            """{ "activeProfileId": "p1", "profiles": [ { "id": "p1", "name": "p1", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" }, { "id": "p2", "name": "p2", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }""");
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), "{}");
    }

    private static readonly PanelDeviceCapabilities Y70Caps = new() { Surface = PanelSurfaces.Y70, Orientation = "landscape" };

    private (JsonConfigStore Store, ProfileManager Pm) Boot()
    {
        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        pm.Initialize();
        return (store, pm);
    }

    [Fact]
    public void Fresh_install_shares_device_and_panels()
    {
        var (store, pm) = Boot();
        try
        {
            var s = store.Load();
            Assert.Contains(ProfileSharing.Device, s.SharedCategories);
            Assert.Contains(ProfileSharing.Panels, s.SharedCategories);
            Assert.True(s.PanelsSharedByDefaultApplied);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upgrade_adds_panels_once_and_stamps_every_profile_file(bool seededByEarlierBuild)
    {
        WriteTwoProfiles();
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "sharedCategories": ["device"],
          "deviceCategorySeeded": true,
          "panelsCategorySeeded": {{(seededByEarlierBuild ? "true" : "false")}},
          "panelDevices": { "y70-a": { "id": "y70-a", "displayName": "Y70", "accentColor": "#123456", "capabilities": { "surface": "y70", "orientation": "landscape" } } }
        }
        """);

        var (store, pm) = Boot();
        try
        {
            var s = store.Load();
            Assert.Contains(ProfileSharing.Panels, s.SharedCategories);
            Assert.Contains(ProfileSharing.Device, s.SharedCategories);
            Assert.True(s.PanelsSharedByDefaultApplied);
            Assert.Contains("#123456", File.ReadAllText(Path.Combine(_dir, "profile-p1.json")));
            Assert.Contains("#123456", File.ReadAllText(Path.Combine(_dir, "profile-p2.json")));
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    private static string FileWithLook(string accent, string overlay)
    {
        var src = new NexusSettings();
        src.PanelDevices["y70-a"] = Y70();
        src.PanelDevices["y70-a"].AccentColor = accent;
        src.Tryx.OverlayColor = overlay;
        src.Devices.Corsair.LcdSelectedMediaId = "media-" + accent;
        src.Devices.TlLcdScreens["tl-1"] = new LianLiWirelessScreenSettings { ContentType = "clock", ClockFace = "face-" + accent };
        src.Devices.LianLiWireless.Screens["ll-1"] = new LianLiWirelessScreenSettings { ContentType = "image", MediaId = "m-" + accent };
        src.Devices.LianLiWireless.AioScreens["aio-1"] = new LianLiAioScreenSettings { LabelColor = accent };
        return JsonSerializer.Serialize(ProfileSharing.ExtractShareable(src), PersistenceJsonContext.Default.NexusSettings);
    }

    private NexusSettings ReadFile(string id) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(_dir, $"profile-{id}.json")), PersistenceJsonContext.Default.NexusSettings)!;

    private static void AssertCoolerContent(NexusSettings s, string accent, string overlay)
    {
        Assert.Equal(overlay, s.Tryx.OverlayColor);
        Assert.Equal("media-" + accent, s.Devices.Corsair.LcdSelectedMediaId);
        Assert.Equal("face-" + accent, s.Devices.TlLcdScreens["tl-1"].ClockFace);
        Assert.Equal("m-" + accent, s.Devices.LianLiWireless.Screens["ll-1"].MediaId);
        Assert.Equal(accent, s.Devices.LianLiWireless.AioScreens["aio-1"].LabelColor);
    }

    [Fact]
    public void Upgrade_with_divergent_looks_makes_the_active_profiles_look_win_everywhere()
    {
        WriteTwoProfiles();
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), FileWithLook("#111111", "#010101"));
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), FileWithLook("#222222", "#020202"));
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "sharedCategories": ["device"],
          "deviceCategorySeeded": true,
          "panelsCategorySeeded": true,
          "panelDevices": { "y70-a": { "id": "y70-a", "displayName": "Y70", "accentColor": "#111111", "capabilities": { "surface": "y70", "orientation": "landscape" } } }
        }
        """);

        var (store, pm) = Boot();
        try
        {
            Assert.Contains("#111111", File.ReadAllText(Path.Combine(_dir, "profile-p1.json")));
            var p2 = File.ReadAllText(Path.Combine(_dir, "profile-p2.json"));
            Assert.Contains("#111111", p2);
            Assert.DoesNotContain("#222222", p2);
            Assert.Equal("#111111", store.Load().PanelDevices["y70-a"].AccentColor);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Lost_settings_json_restores_cooler_content_and_a_flush_keeps_it_in_every_file()
    {
        WriteTwoProfiles();
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), FileWithLook("#111111", "#010101"));
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), FileWithLook("#222222", "#020202"));
        File.WriteAllText(_settingsPath, "{ not json");

        var (store, pm) = Boot();
        try
        {
            AssertCoolerContent(store.Load(), "#111111", "#010101");

            pm.FlushNow();

            AssertCoolerContent(store.Load(), "#111111", "#010101");
            AssertCoolerContent(ReadFile("p1"), "#111111", "#010101");
            AssertCoolerContent(ReadFile("p2"), "#111111", "#010101");
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Y70_created_after_lost_settings_adopts_the_saved_look_even_when_a_flush_runs_first()
    {
        WriteTwoProfiles();
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), FileWithLook("#111111", "#010101"));
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), FileWithLook("#111111", "#010101"));
        File.WriteAllText(_settingsPath, "{ not json");

        var (store, pm) = Boot();
        try
        {
            var registry = new PanelDeviceRegistry(store);
            var record = registry.Allocate("Y70", Y70Caps);
            pm.FlushNow();
            pm.AdoptSavedLook(record.Id);
            pm.FlushNow();

            Assert.Equal("#111111", store.Load().PanelDevices[record.Id].AccentColor);
            Assert.Equal("#111111", ReadFile("p1").PanelDevices[record.Id].AccentColor);
            Assert.Equal("#111111", ReadFile("p2").PanelDevices[record.Id].AccentColor);
            Assert.Single(store.Load().PanelDevices);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Renumbered_monitor_adopts_its_look_while_the_old_record_is_still_live()
    {
        var (store, pm) = Boot();
        try
        {
            var registry = new PanelDeviceRegistry(store);
            var fp = new Nexus.Service.Models.Displays.DisplayFingerprint { Manufacturer = "ACR", Product = "0A1B", Serial = 1, SerialText = "SER1" };
            var caps = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor };
            var old = registry.AllocateForDisplay("d1", "Mon", caps, fp).Record;
            store.Update(s => s.PanelDevices[old.Id].AccentColor = "#abcdef");
            pm.FlushNow();

            var renumbered = registry.AllocateForDisplay("d2", "Mon", caps, fp).Record;
            pm.AdoptSavedLook(renumbered.Id);

            Assert.NotEqual(old.Id, renumbered.Id);
            Assert.Equal("#abcdef", store.Load().PanelDevices[renumbered.Id].AccentColor);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void A_second_panel_on_a_single_instance_surface_does_not_clone_the_first_ones_look()
    {
        var (store, pm) = Boot();
        try
        {
            var registry = new PanelDeviceRegistry(store);
            registry.RecordCreated += r => pm.AdoptSavedLook(r.Id);
            var first = registry.Allocate("Y70", Y70Caps);
            store.Update(s => s.PanelDevices[first.Id].AccentColor = "#abcdef");
            pm.FlushNow();

            var second = registry.Allocate("Y70 b", Y70Caps, _ => false);

            Assert.NotEqual(first.Id, second.Id);
            Assert.True(PanelProfileData.IsDefaultLook(store.Load().PanelDevices[second.Id]));
            Assert.Equal("#abcdef", store.Load().PanelDevices[first.Id].AccentColor);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Brand_new_panel_with_no_saved_look_stays_default()
    {
        var (store, pm) = Boot();
        try
        {
            var registry = new PanelDeviceRegistry(store);
            registry.RecordCreated += r => pm.AdoptSavedLook(r.Id);

            var record = registry.Allocate("Y70", Y70Caps);

            Assert.True(PanelProfileData.IsDefaultLook(store.Load().PanelDevices[record.Id]));
            Assert.Single(store.Load().PanelDevices);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Opt_out_survives_restart_and_is_not_re_added()
    {
        var (store, pm) = Boot();
        pm.SetCategoryShared(ProfileSharing.Panels, false);
        pm.Dispose();
        store.Dispose();

        var (store2, pm2) = Boot();
        try
        {
            Assert.DoesNotContain(ProfileSharing.Panels, store2.Load().SharedCategories);
            Assert.Contains(ProfileSharing.Device, store2.Load().SharedCategories);
        }
        finally { pm2.Dispose(); store2.Dispose(); }
    }

    [Fact]
    public void Edit_with_sharing_on_persists_across_a_switch_and_reaches_every_profile()
    {
        var (store, pm) = Boot();
        try
        {
            store.Update(s => s.PanelDevices["y70-a"] = Y70());
            var a = pm.GetActiveEntry()!.Id;
            var b = pm.CreateProfile("B").Id;
            store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#445566");

            pm.SwitchProfile(a);
            Assert.Equal("#445566", store.Load().PanelDevices["y70-a"].AccentColor);
            pm.SwitchProfile(b);
            Assert.Equal("#445566", store.Load().PanelDevices["y70-a"].AccentColor);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }

    [Fact]
    public void Reset_panels_while_shared_clears_the_look_on_every_profile()
    {
        var (store, pm) = Boot();
        try
        {
            store.Update(s => s.PanelDevices["y70-a"] = Y70());
            var a = pm.GetActiveEntry()!.Id;
            var b = pm.CreateProfile("B").Id;
            store.Update(s => s.PanelDevices["y70-a"].AccentColor = "#445566");

            pm.ResetCategory(b, ProfileSharing.Panels);
            Assert.Null(store.Load().PanelDevices["y70-a"].AccentColor);
            pm.SwitchProfile(a);
            Assert.Null(store.Load().PanelDevices["y70-a"].AccentColor);
        }
        finally { pm.Dispose(); store.Dispose(); }
    }
}
