using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Profile files were seeded with the old keeb brightness default, and a profile switch copies the
/// Device category (Keeb included) from the primary file over the workstation settings. The settings
/// migration alone would therefore be undone by the first switch; ProfileManager.Initialize moves the
/// old default in every local profile file once, gated by a marker so a level chosen later is kept.
/// </summary>
public class ProfileManagerKeebBrightnessTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;

    public ProfileManagerKeebBrightnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-keeb-bright-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteProfiles(int brightnessP1, int brightnessP2)
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), """
        { "activeProfileId": "p1", "profiles": [
          { "id": "p1", "name": "p1", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
          { "id": "p2", "name": "p2", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
        """);
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), $$"""{ "keeb": { "firmwareLighting": { "brightness": {{brightnessP1}} } } }""");
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), $$"""{ "keeb": { "firmwareLighting": { "brightness": {{brightnessP2}} } } }""");
    }

    private static int BrightnessIn(string file)
    {
        var json = File.ReadAllText(file);
        var doc = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        return doc.Keeb.FirmwareLighting.Brightness;
    }

    [Fact]
    public void An_upgrade_moves_the_old_default_in_every_profile_file_and_a_switch_keeps_it()
    {
        WriteProfiles(80, 80);
        File.WriteAllText(_settingsPath,
            """{ "schemaVersion": 24, "deviceCategorySeeded": true, "keeb": { "firmwareLighting": { "brightness": 80 } } }""");

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();

            Assert.Equal(100, BrightnessIn(Path.Combine(_dir, "profile-p1.json")));
            Assert.Equal(100, BrightnessIn(Path.Combine(_dir, "profile-p2.json")));
            Assert.True(store.Load().KeebBrightnessProfilesMigrated);

            pm.SwitchProfile("p2");
            Assert.Equal(100, store.Load().Keeb.FirmwareLighting.Brightness);
            pm.SwitchProfile("p1");
            Assert.Equal(100, store.Load().Keeb.FirmwareLighting.Brightness);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void A_level_other_than_the_old_default_is_left_alone()
    {
        WriteProfiles(60, 100);
        File.WriteAllText(_settingsPath, """{ "schemaVersion": 24, "deviceCategorySeeded": true }""");

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();
            Assert.Equal(60, BrightnessIn(Path.Combine(_dir, "profile-p1.json")));
            Assert.Equal(100, BrightnessIn(Path.Combine(_dir, "profile-p2.json")));
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void Once_migrated_a_level_of_80_chosen_later_survives_a_restart_and_a_switch()
    {
        WriteProfiles(100, 100);
        File.WriteAllText(_settingsPath,
            """{ "schemaVersion": 25, "deviceCategorySeeded": true, "keebBrightnessProfilesMigrated": true, "keeb": { "firmwareLighting": { "brightness": 80 } } }""");
        // The user picked 80 afterwards; the profile file holds it.
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), """{ "keeb": { "firmwareLighting": { "brightness": 80 } } }""");

        var store = new JsonConfigStore(_settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();
            Assert.Equal(80, BrightnessIn(Path.Combine(_dir, "profile-p1.json")));

            pm.SwitchProfile("p2");
            pm.SwitchProfile("p1");
            Assert.Equal(80, store.Load().Keeb.FirmwareLighting.Brightness);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }
}
