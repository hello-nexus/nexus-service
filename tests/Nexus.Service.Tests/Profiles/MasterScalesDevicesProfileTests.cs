using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Profiles;

/// <summary>The master-multiplies-devices choice belongs to the PC: a profile switch swaps the
/// lighting settings whole, and neither that nor a share-apply or category reset may flip it.</summary>
public class MasterScalesDevicesProfileTests : IDisposable
{
    private readonly string _dir;

    public MasterScalesDevicesProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-master-scales-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_profile_switch_keeps_the_choice_whatever_the_profiles_hold()
    {
        File.WriteAllText(Path.Combine(_dir, "profiles.json"), """
        { "activeProfileId": "p1", "profiles": [
          { "id": "p1", "name": "p1", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" },
          { "id": "p2", "name": "p2", "createdAt": "2026-01-01T00:00:00Z", "updatedAt": "2026-01-01T00:00:00Z" } ] }
        """);
        File.WriteAllText(Path.Combine(_dir, "profile-p1.json"), """{ "lighting": { "globalBrightness": 0.5 } }""");
        File.WriteAllText(Path.Combine(_dir, "profile-p2.json"), """{ "lighting": { "globalBrightness": 0.9 } }""");
        var settingsPath = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 24, "deviceCategorySeeded": true, "masterScalesDevices": true }""");

        var store = new JsonConfigStore(settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();
            Assert.True(store.Load().MasterScalesDevices);

            pm.SwitchProfile("p2");
            Assert.Equal(0.9f, store.Load().Lighting.GlobalBrightness);
            Assert.True(store.Load().MasterScalesDevices);

            pm.SwitchProfile("p1");
            Assert.True(store.Load().MasterScalesDevices);
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void Share_apply_and_reset_of_the_lighting_category_leave_the_choice()
    {
        var target = new NexusSettings { MasterScalesDevices = true };
        var source = new NexusSettings { Lighting = new LightingSettings { GlobalBrightness = 0.3f } };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Lighting);
        Assert.Equal(0.3f, target.Lighting.GlobalBrightness);
        Assert.True(target.MasterScalesDevices);

        ProfileSharing.ResetCategory(target, ProfileSharing.Lighting);
        Assert.True(target.MasterScalesDevices);
    }
}
