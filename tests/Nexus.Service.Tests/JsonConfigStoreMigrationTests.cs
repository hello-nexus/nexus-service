using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Verifies JsonConfigStore is a no-op on records already at the current
/// schema version. The legacy V1-V4 migration tests were removed alongside
/// the migration code itself (nexus-service drop-legacy-schema-migrations
/// refactor).
/// </summary>
public class JsonConfigStoreMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public JsonConfigStoreMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoOpOnAlreadyV5()
    {
        var v5Json = """
        {
          "schemaVersion": 5,
          "theme": { "themeMode": "dark", "accentColor": "#aabbcc", "language": "en" },
          "panel": { "autoLaunch": true, "themeMode": "system" },
          "overlay": { "enabled": true, "scale": 120 },
          "monitoring": { "showAverage": true }
        }
        """;
        File.WriteAllText(_settingsPath, v5Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("dark", s.Theme.ThemeMode);
            Assert.Equal("#aabbcc", s.Theme.AccentColor);
            Assert.True(s.Panel.AutoLaunch);
            Assert.True(s.Overlay.Enabled);
            Assert.Equal(120, s.Overlay.Scale);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Theory]
    [InlineData(true, "notify")]
    [InlineData(false, "always")]
    public void Load_V6_MigratesLegacyAutoUpdateDisabled(bool legacyDisabled, string expectedMode)
    {
        var json = $$"""
        {
          "schemaVersion": 6,
          "update": { "autoUpdateDisabled": {{(legacyDisabled ? "true" : "false")}}, "updateChannel": "production" }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal(expectedMode, s.Update.UpdateMode);
            Assert.Null(s.Update.LegacyAutoUpdateDisabled);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V6_AbsentAutoUpdateDisabled_DefaultsToAlways()
    {
        var json = """
        {
          "schemaVersion": 6,
          "update": { "updateChannel": "beta" }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("always", s.Update.UpdateMode);
            Assert.Equal("beta", s.Update.UpdateChannel);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V7_MigratesOnboardingCompletedForExistingInstall()
    {
        var json = """
        {
          "schemaVersion": 7
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.OnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NullJsonFile_MarksOnboardingAlreadyComplete()
    {
        // The file existed and parsed as valid JSON but held no data ("null"),
        // so this is an upgrade of an existing install, not a fresh one.
        File.WriteAllText(_settingsPath, "null");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.OnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_OnboardingCompletedDefaultsFalse()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.False(s.OnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_TryxOverlay_PredatingItemsFontSizeAlignAndDocked_DefaultsThem()
    {
        // A settings.json from before the sensor-item overlay fields shipped: only
        // the original stats/color/align/opacity keys are present (the old
        // "overlayStats" fixed-name array has no C# property anymore, so it is
        // silently ignored rather than migrated).
        var json = """
        {
          "schemaVersion": 7,
          "tryx": {
            "overlayStats": ["CPU Temperature"],
            "overlayColor": "#ff0000",
            "overlayAlign": "Right",
            "overlayOpacity": 75
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal("#ff0000", s.Tryx.OverlayColor);
            Assert.Empty(s.Tryx.OverlayItems);
            Assert.Equal("roboto-regular", s.Tryx.OverlayFont);
            Assert.Equal(100, s.Tryx.OverlaySize);
            Assert.False(s.Tryx.OverlayDocked);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V11_AddsDeviceToSharedCategories()
    {
        // A pre-feature install already carries sharedCategories WITHOUT
        // device. The present array overrides the field initializer, so only
        // the v12 migration Add can introduce device here - this is the
        // load-bearing upgrade path.
        var json = """
        {
          "schemaVersion": 11,
          "sharedCategories": ["lighting"]
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal(new List<string> { "lighting", "device" }, s.SharedCategories);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V11_WithDeviceAlreadyShared_DoesNotDuplicateIt()
    {
        var json = """
        {
          "schemaVersion": 11,
          "sharedCategories": ["lighting", "device"]
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal(new List<string> { "lighting", "device" }, s.SharedCategories);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_TryxOverlay_ItemsFontSizeAlignAndDocked_RoundTripThroughRealJson()
    {
        var json = """
        {
          "schemaVersion": 7,
          "tryx": {
            "overlayItems": [
              { "sensorId": "s1", "device": "cpu", "label": "CPU Temp", "x": 0.03, "y": 0.1 },
              { "sensorId": "s2", "device": "gpu", "label": "GPU Temp", "x": 0.5, "y": 0.6 }
            ],
            "overlayFont": "roboto-bold",
            "overlaySize": 120,
            "overlayAlign": "center",
            "overlayDocked": true
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(2, s.Tryx.OverlayItems.Count);
            Assert.Equal("s1", s.Tryx.OverlayItems[0].SensorId);
            Assert.Equal("cpu", s.Tryx.OverlayItems[0].Device);
            Assert.Equal("CPU Temp", s.Tryx.OverlayItems[0].Label);
            Assert.Equal(0.03, s.Tryx.OverlayItems[0].X);
            Assert.Equal(0.1, s.Tryx.OverlayItems[0].Y);
            Assert.Equal("roboto-bold", s.Tryx.OverlayFont);
            Assert.Equal(120, s.Tryx.OverlaySize);
            Assert.Equal("center", s.Tryx.OverlayAlign);
            Assert.True(s.Tryx.OverlayDocked);
        }
        finally
        {
            store.Dispose();
        }
    }
}
