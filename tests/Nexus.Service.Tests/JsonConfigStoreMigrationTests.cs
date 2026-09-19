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
          "monitoring": { "showMacStatusBarIcon": true }
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
            Assert.False(s.LightingOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V12_MigratesLightingOnboardingCompletedForExistingInstall()
    {
        var json = """
        {
          "schemaVersion": 12
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.LightingOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V14_MigratesDashboardModesForExistingInstall()
    {
        var json = """
        {
          "schemaVersion": 14
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("advanced", s.Ui.LightingDashboardMode);
            Assert.Equal("advanced", s.Ui.CoolingDashboardMode);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_DashboardModesDefaultSimple()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal("simple", s.Ui.LightingDashboardMode);
            Assert.Equal("simple", s.Ui.CoolingDashboardMode);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_TelemetryDefaultsOn()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.Telemetry.CollectAnonymousData);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_ExistingFileWithoutTelemetryField_StaysOff()
    {
        // A settings.json from before this field existed: the file is present
        // (not a fresh install), so the fresh-install default-on branch in
        // JsonConfigStore.Load never runs and the field keeps its own false.
        var json = """
        {
          "schemaVersion": 11
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.False(s.Telemetry.CollectAnonymousData);
            Assert.Equal("", s.Telemetry.InstallId);
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

    [Fact]
    public void Load_V15_MigratesFeaturesOnboardingCompletedForExistingInstall()
    {
        var json = """
        {
          "schemaVersion": 15
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.True(s.FeaturesOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V15_KeepsTheStartupShutdownOffForExistingInstall()
    {
        File.WriteAllText(_settingsPath, """
        {
          "schemaVersion": 15
        }
        """);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.False(s.Ui.AutoKillConflictsAtStartup);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_V16_KeepsAnExplicitStartupShutdownChoice()
    {
        File.WriteAllText(_settingsPath, """
        {
          "schemaVersion": 16,
          "ui": { "autoKillConflictsAtStartup": true }
        }
        """);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.Ui.AutoKillConflictsAtStartup);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_StartupShutdownDefaultsOn()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.Ui.AutoKillConflictsAtStartup);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_FeaturesOnboardingCompletedDefaultsFalse()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.False(s.FeaturesOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_PreFeaturesJson_LoadsAllFeatureFlagsTrue()
    {
        // Features is additive with no migration arm: a document from before
        // the field existed simply never sets it, and the initializers on
        // FeaturesSettings resolve every flag to true.
        var json = """
        {
          "schemaVersion": 11
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.Features.Lighting);
            Assert.True(s.Features.Cooling);
            Assert.True(s.Features.Monitoring);
            Assert.True(s.Features.Diagnostics);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// The frost slider replaced the "backgroundFrost" step with the numeric
    /// BackgroundFrostLevel and drops the old value instead of migrating it.
    /// That is only safe while unmapped members are skipped: switching
    /// PersistenceJsonContext to UnmappedMemberHandling.Disallow would throw
    /// here, and JsonConfigStore.Load treats a throw as a corrupt file - it
    /// snapshots and resets the WHOLE settings file, not just the panel record.
    /// </summary>
    [Fact]
    public void Load_PanelDeviceWithDroppedFrostStep_LoadsAndIgnoresIt()
    {
        var json = """
        {
          "schemaVersion": 11,
          "panelDevices": {
            "abc123": {
              "id": "abc123",
              "displayName": "My Panel",
              "backgroundFrost": "heavy"
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            var record = Assert.Contains("abc123", s.PanelDevices);
            Assert.Equal("My Panel", record.DisplayName);
            Assert.Null(record.BackgroundFrostLevel);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// Regression test for a real-world data-loss bug: PhysicalDeckSettings'
    /// pre-v18 fields (Deck/ImageRefs/Presets/ActivePresetId) were renamed to
    /// LegacyDeck/etc. with no JsonPropertyName, so under CamelCase naming
    /// they deserialized from "legacyDeck" instead of the actual on-disk
    /// "deck" - every field bound to null, DeckModesMigration saw nothing to
    /// hoist, and the schema version still advanced to 18, permanently
    /// discarding the user's layout. This deserializes a literal pre-v18
    /// settings.json through the real PersistenceJsonContext (not a C#
    /// object graph built by hand) so a naming-attribute regression fails it
    /// again.
    /// </summary>
    [Fact]
    public void Load_V17_DeckModesMigration_HoistsLegacyDeckFromRawJson()
    {
        var json = """
        {
          "schemaVersion": 17,
          "streamDeck": {
            "decks": {
              "SERIAL-1": {
                "name": "My Deck",
                "productId": 99,
                "deck": { "pages": [ { "slots": [ { "label": "Hi" } ] } ] }
              }
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            var instance = Assert.Contains("streamdeck:SERIAL-1", s.StreamDeck.Instances);
            Assert.Equal("fixed", instance.Mode);
            var preset = s.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
            Assert.NotNull(preset);
            Assert.Equal("My Deck", preset!.Name);
            Assert.Equal("Hi", preset.Deck.Pages[0].Slots[0].Label);
            Assert.Null(s.StreamDeck.Decks["SERIAL-1"].LegacyDeck);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// A downgrade to a pre-deck-modes build (then a re-upgrade) leaves
    /// SchemaVersion already at 18 - the downgraded build wrote fresh
    /// Legacy* content with its own older model, which knows nothing of
    /// Presets/Instances - so the schema-gated migration never runs again on
    /// re-upgrade. JsonConfigStore.Load must recover independently of
    /// SchemaVersion whenever it sees empty presets/instances alongside a
    /// deck that still carries Legacy* content.
    /// </summary>
    [Fact]
    public void Load_SchemaAlreadyAtCurrent_StillRecoversAStrandedLegacyDeck()
    {
        var json = $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "streamDeck": {
            "decks": {
              "SERIAL-1": {
                "name": "My Deck",
                "productId": 99,
                "deck": { "pages": [ { "slots": [ { "label": "Hi" } ] } ] }
              }
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            var instance = Assert.Contains("streamdeck:SERIAL-1", s.StreamDeck.Instances);
            var preset = s.StreamDeck.Presets.Find(p => p.Id == instance.ActivePresetId);
            Assert.NotNull(preset);
            Assert.Equal("Hi", preset!.Deck.Pages[0].Slots[0].Label);
            Assert.Null(s.StreamDeck.Decks["SERIAL-1"].LegacyDeck);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>The recovery path above must not re-run once a preset/instance already exists - it only fires on the specific "empty presets and instances, but a deck still has Legacy* content" shape.</summary>
    [Fact]
    public void Load_SchemaAlreadyAtCurrent_WithAnExistingPreset_DoesNotReRunRecovery()
    {
        var json = $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "streamDeck": {
            "presets": [ { "id": "p1", "name": "Existing", "cols": 5, "rows": 3, "deck": { "pages": [] } } ],
            "instances": { "streamdeck:SERIAL-1": { "mode": "fixed", "activePresetId": "p1" } },
            "decks": { "SERIAL-1": { "name": "My Deck", "productId": 99 } }
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Single(s.StreamDeck.Presets);
            Assert.Equal("p1", s.StreamDeck.Instances["streamdeck:SERIAL-1"].ActivePresetId);
        }
        finally
        {
            store.Dispose();
        }
    }
}
