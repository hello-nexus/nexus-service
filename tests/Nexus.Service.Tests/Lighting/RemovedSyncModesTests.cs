using System.IO;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// The Game Sync mode is gone: every path that can bring its sync value back
/// (settings migration, profile load, preset activation, runtime callers)
/// lands on static, and an upgrade raises the one-release removed notice.
/// </summary>
public sealed class RemovedSyncModesTests : IDisposable
{
    private readonly string _tempDir;
    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    public RemovedSyncModesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-removed-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private const string LegacyDoc = """
    {
      "schemaVersion": 22,
      "lighting": {
        "sync": "gamesync",
        "static": { "effect": "stripes", "color": "#112233" },
        "layoutPresets": [
          { "id": "a", "look": { "sync": "GameSync" } },
          { "id": "b", "look": { "sync": "music" } },
          { "id": "c" }
        ]
      }
    }
    """;

    [Fact]
    public void Migration_turns_game_sync_into_static_everywhere_and_keeps_the_static_look()
    {
        File.WriteAllText(SettingsPath, LegacyDoc);

        using var store = new JsonConfigStore(SettingsPath);
        var lighting = store.Load().Lighting;

        Assert.Equal(NexusSettings.CurrentSchemaVersion, store.Load().SchemaVersion);
        Assert.Equal("static", lighting.Sync);
        Assert.Equal("static", lighting.LayoutPresets[0].Look!.Sync);
        Assert.Equal("music", lighting.LayoutPresets[1].Look!.Sync);
        Assert.Null(lighting.LayoutPresets[2].Look);
        Assert.Equal("stripes", lighting.Static.Effect);
        Assert.Equal("#112233", lighting.Static.Color);
        Assert.True(store.Load().GameSyncRemovedNotice);
    }

    [Fact]
    public void Migration_raises_the_notice_for_an_upgrade_that_never_used_game_sync()
    {
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 22, "lighting": { "sync": "rainbow" } }""");

        using var store = new JsonConfigStore(SettingsPath);

        Assert.Equal("rainbow", store.Load().Lighting.Sync);
        Assert.True(store.Load().GameSyncRemovedNotice);
    }

    [Fact]
    public void A_fresh_install_has_no_notice()
    {
        using var store = new JsonConfigStore(SettingsPath);

        Assert.False(store.Load().GameSyncRemovedNotice);
    }

    [Fact]
    public void A_current_version_file_keeps_its_notice_and_still_drops_a_stray_game_sync_value()
    {
        File.WriteAllText(SettingsPath, $$"""{ "schemaVersion": {{NexusSettings.CurrentSchemaVersion}}, "lighting": { "sync": "gamesync" } }""");

        using var store = new JsonConfigStore(SettingsPath);

        Assert.Equal("static", store.Load().Lighting.Sync);
        Assert.False(store.Load().GameSyncRemovedNotice);
    }

    [Fact]
    public void The_migrated_value_is_persisted()
    {
        File.WriteAllText(SettingsPath, LegacyDoc);
        using (var store = new JsonConfigStore(SettingsPath))
        {
            store.Load();
            store.FlushNow();
        }

        using var reloaded = new JsonConfigStore(SettingsPath);
        Assert.Equal("static", reloaded.Load().Lighting.Sync);
        Assert.True(reloaded.Load().GameSyncRemovedNotice);
    }

    [Fact]
    public void Loading_a_profile_file_normalizes_its_sync_and_presets()
    {
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 22 }""");
        using var store = new JsonConfigStore(SettingsPath);
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var target = profiles.CreateProfile("Target");
        profiles.CreateProfile("Other");
        File.WriteAllText(
            Path.Combine(_tempDir, $"profile-{target.Id}.json"),
            """{ "lighting": { "sync": "gamesync", "layoutPresets": [ { "id": "a", "look": { "sync": "gamesync" } } ] } }""");

        profiles.SwitchProfile(target.Id);

        var lighting = store.Load().Lighting;
        Assert.Equal("static", lighting.Sync);
        Assert.Equal("static", lighting.LayoutPresets[0].Look!.Sync);
    }

    [Fact]
    public void Making_lighting_shared_from_a_primary_that_carries_game_sync_lands_on_static()
    {
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 22 }""");
        using var store = new JsonConfigStore(SettingsPath);
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var primary = profiles.CreateProfile("Primary");
        profiles.CreateProfile("Active");
        Assert.NotEqual(primary.Id, profiles.ActiveProfileId);
        store.Update(s => s.SharedCategories.Remove(ProfileSharing.Lighting));
        profiles.SetPrimary(primary.Id);
        File.WriteAllText(
            Path.Combine(_tempDir, $"profile-{primary.Id}.json"),
            """{ "lighting": { "sync": "gamesync" } }""");

        Assert.True(profiles.SetCategoryShared(ProfileSharing.Lighting, true));

        Assert.Equal("static", store.Load().Lighting.Sync);
    }

    [Theory]
    [InlineData("gamesync", "static")]
    [InlineData("GameSync", "static")]
    [InlineData("static", "static")]
    [InlineData("music", "music")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_maps_only_the_removed_mode(string? input, string expected)
    {
        Assert.Equal(expected, RemovedSyncModes.Normalize(input));
    }

    [Fact]
    public void Activating_a_preset_that_carries_game_sync_lands_on_static()
    {
        var lighting = new LightingSettings { Sync = "none" };

        LightingPresetLooks.Apply(lighting, new LightingPresetLook { Sync = "gamesync" });

        Assert.Equal("static", lighting.Sync);
    }
}
