using Nexus.Service.Models.Panel;
using System.Text.Json;
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
        return JsonSerializer.Serialize(ProfileSharing.ExtractShareable(src), PersistenceJsonContext.Default.NexusSettings);
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
    public void Lost_settings_json_keeps_profile_files_and_restores_the_active_profiles_cooler_content()
    {
        WriteTwoProfiles();
        var p1 = FileWithLook("#111111", "#010101");
        var p2 = FileWithLook("#222222", "#020202");
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), p1);
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), p2);
        File.WriteAllText(_settingsPath, "{ not json");

        var (store, pm) = Boot();
        try
        {
            var after1 = File.ReadAllText(Path.Combine(_dir, "profile-p1.json"));
            var after2 = File.ReadAllText(Path.Combine(_dir, "profile-p2.json"));
            Assert.Contains("#111111", after1);
            Assert.Contains("#010101", after1);
            Assert.Contains("#222222", after2);
            Assert.Contains("#020202", after2);
            Assert.Equal("#010101", store.Load().Tryx.OverlayColor);
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
