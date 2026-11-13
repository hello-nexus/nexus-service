using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests;

/// <summary>Real install state captured at schema 24 to 30 (scrubbed) must load without losing the sections a user configured.</summary>
public class UpgradeRegressionTests : IDisposable
{
    private readonly string _dir;

    public UpgradeRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-upgrade-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly string[] OnboardingFlags =
    {
        "onboardingCompleted", "lightingOnboardingCompleted", "featuresOnboardingCompleted",
    };

    // Sections no migration after schema 24 rewrites, so a load must return them as stored.
    private static readonly string[] StablePaths =
    {
        "theme", "monitoring", "cooling.curves", "cooling.customFanCurveAssignments", "cooling.fanNames",
        "cooling.fanGroups", "cooling.activePreset", "streamDeck.presets", "streamDeck.instances",
        "hotkeys.bindings", "keeb.macros", "audioMixer.presets", "weather.locations", "features", "units",
        "lighting.deviceLayouts", "lighting.deviceNames", "devices.nexusControlEnabled", "devices.nexusControlDisabled",
        "ui.lightingDashboardMode", "ui.coolingDashboardMode",
    };

    private static JsonNode? At(JsonNode root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.'))
        {
            node = (node as JsonObject)?[part];
            if (node is null)
            {
                return null;
            }
        }
        return node;
    }

    private static int AssertStablePathsSurvive(JsonNode raw, JsonNode loaded, string label)
    {
        var compared = 0;
        foreach (var path in StablePaths)
        {
            var before = At(raw, path);
            if (before is null)
            {
                continue;
            }
            Assert.True(JsonNode.DeepEquals(before, At(loaded, path)), $"{label}: {path} changed on load");
            compared++;
        }
        return compared;
    }

    public static IEnumerable<object[]> Captures() => new[]
    {
        new object[] { "v24-3.1.0-era" },
        new object[] { "v25-3.1.5-clean" },
        new object[] { "v26-3.1.5-widgets" },
        new object[] { "v27-3.1.7" },
        new object[] { "v28-dev" },
        new object[] { "v29-dev" },
        new object[] { "v30-3.1.8-beta" },
        new object[] { "v32-main-t1" },
        new object[] { "v32-main-t1-edited" },
    };

    [Theory]
    [MemberData(nameof(Captures))]
    public void Real_install_state_keeps_the_users_sections(string capture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "upgrade", capture);
        foreach (var file in Directory.GetFiles(source, "*.json"))
        {
            File.Copy(file, Path.Combine(_dir, Path.GetFileName(file)));
        }
        var settingsPath = Path.Combine(_dir, "settings.json");
        var raw = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

        var store = new JsonConfigStore(settingsPath);
        var pm = new ProfileManager(store);
        try
        {
            pm.Initialize();
            var loaded = store.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);

            var loadedJson = JsonSerializer.Serialize(loaded, PersistenceJsonContext.Default.NexusSettings);
            var loadedNode = JsonNode.Parse(loadedJson)!.AsObject();
            foreach (var flag in OnboardingFlags)
            {
                if (raw[flag]?.GetValue<bool>() == true)
                {
                    Assert.True(loadedNode[flag]?.GetValue<bool>(), flag);
                }
            }

            Assert.True(AssertStablePathsSurvive(raw, loadedNode, "settings.json") >= 5);
            store.FlushNow();
            // A store that never set the preference writes back without the key.
            var written = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            Assert.Null(At(written, "units.monitoringWholeNumbers"));
            var profiles = Directory.GetFiles(source, "profile-*.json");
            Assert.NotEmpty(profiles);
            foreach (var file in profiles)
            {
                var name = Path.GetFileName(file);
                var after = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, name)))!;
                AssertStablePathsSurvive(JsonNode.Parse(File.ReadAllText(file))!, after, name);
            }
        }
        finally
        {
            pm.Dispose();
            store.Dispose();
        }
    }

    [Fact]
    public void Schema_19_file_loads_and_keeps_the_onboarding_state_it_had()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 19, "onboardingCompleted": true, "lighting": {}, "ui": {} }""");
        var store = new JsonConfigStore(path);
        try
        {
            var s = store.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.OnboardingCompleted);
            Assert.True(s.FeaturesOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Corrupt_file_resets_to_an_upgrade_state_with_onboarding_complete()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        var store = new JsonConfigStore(path);
        try
        {
            var s = store.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.OnboardingCompleted);
            Assert.True(s.LightingOnboardingCompleted);
            Assert.True(s.FeaturesOnboardingCompleted);
            Assert.Equal("advanced", s.Ui.LightingDashboardMode);
            Assert.Equal("advanced", s.Ui.CoolingDashboardMode);
            Assert.False(s.Ui.AutoKillConflictsAtStartup);
        }
        finally
        {
            store.Dispose();
        }
    }
}
