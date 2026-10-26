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
          "overlay": { "enabled": true, "scale": 120, "layout": [ { "id": "w", "type": "clock" } ] },
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
            // A block that still holds widgets stays whole for the desktop widgets migration.
            Assert.True(s.Overlay.Enabled);
            Assert.Equal(120, s.Overlay.Scale);
            Assert.Single(s.Overlay.Layout!);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_ClearsTheRetiredOverlayBlockWhenItHoldsNoWidgets()
    {
        File.WriteAllText(_settingsPath, """{ "schemaVersion": 26, "overlay": { "enabled": true, "alwaysOnTop": true, "scale": 120, "layout": [] } }""");

        using var store = new JsonConfigStore(_settingsPath);

        var overlay = store.Load().Overlay;
        Assert.Null(overlay.Enabled);
        Assert.Null(overlay.Scale);
        Assert.Null(overlay.Layout);
        Assert.False(overlay.MigratedToPanels);
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
    public void Load_FileWithoutViewPrefs_LeavesThemUnset()
    {
        File.WriteAllText(_settingsPath, "{ \"schemaVersion\": 25, \"ui\": { \"sidebarCollapsed\": true } }");

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.True(s.Ui.SidebarCollapsed);
            Assert.Null(s.Ui.LightingView3d);
            Assert.Null(s.Ui.CoolingView3d);
            Assert.Null(s.Ui.CoolingFlowMode);
            Assert.Null(s.Ui.CoolingCollisionShapes);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Save_ThenFreshStoreLoad_RoundTripsViewPrefs()
    {
        var writer = new JsonConfigStore(_settingsPath);
        try
        {
            writer.Update(s =>
            {
                s.Ui.LightingView3d = true;
                s.Ui.CoolingView3d = true;
                s.Ui.CoolingFlowMode = "airflow";
                s.Ui.CoolingCollisionShapes = true;
            });
            writer.FlushNow();
        }
        finally
        {
            writer.Dispose();
        }

        var reader = new JsonConfigStore(_settingsPath);
        try
        {
            var s = reader.Load();
            Assert.True(s.Ui.LightingView3d);
            Assert.True(s.Ui.CoolingView3d);
            Assert.Equal("airflow", s.Ui.CoolingFlowMode);
            Assert.True(s.Ui.CoolingCollisionShapes);
        }
        finally
        {
            reader.Dispose();
        }
    }

    [Fact]
    public void Load_FileWithViewPrefs_ReadsThem()
    {
        File.WriteAllText(_settingsPath,
            "{ \"schemaVersion\": 25, \"ui\": { \"lightingView3d\": true, \"coolingView3d\": false, \"coolingFlowMode\": \"case\", \"coolingCollisionShapes\": true } }");

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.True(s.Ui.LightingView3d);
            Assert.False(s.Ui.CoolingView3d);
            Assert.Equal("case", s.Ui.CoolingFlowMode);
            Assert.True(s.Ui.CoolingCollisionShapes);
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

    private static readonly string[] Mice =
    {
        "Corsair Katar Pro", "Corsair Katar Pro V2", "Corsair Katar Pro XT", "Corsair M55 RGB PRO",
        "Corsair M65 RGB Ultra Wired", "Corsair Scimitar PRO RGB", "HyperX Pulsefire Haste",
    };

    private static readonly string[] Gamepads = { "GameSir Nova 2 Lite", "Sony DualSense", "Sony DualSense Edge", "Sony DualShock 4" };

    private SortedSet<string> HeldOffAfterLoad(string? json)
    {
        if (json is not null)
        {
            File.WriteAllText(_settingsPath, json);
        }
        using var store = new JsonConfigStore(_settingsPath);
        return Nexus.Service.Lighting.Rgb.OpenRgbGamepadDefaults.HeldOffDetectors(store.Load());
    }

    [Fact]
    public void Load_FreshInstall_HoldsMiceAndGamepadsOff()
    {
        var held = HeldOffAfterLoad(null);

        Assert.All(Mice, m => Assert.Contains(m, held));
        Assert.All(Gamepads, g => Assert.Contains(g, held));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 21, "ui": { "sidebarCollapsed": true } }""")]
    [InlineData("""{ "ui": { "sidebarCollapsed": true } }""")]
    public void Load_ExistingInstall_KeepsMiceOnAndGamepadsHeldOff(string json)
    {
        var held = HeldOffAfterLoad(json);

        Assert.All(Mice, m => Assert.DoesNotContain(m, held));
        Assert.All(Gamepads, g => Assert.Contains(g, held));
    }

    [Fact]
    public void Load_ExistingInstall_KeepsAKatarCardAlreadyHeldOff()
    {
        var held = HeldOffAfterLoad("""
        { "schemaVersion": 21, "devices": { "openRgbDetectorExclusions": {
          "openrgb-gamepad-corsair-katar-pro-xt": { "detectorName": "Corsair Katar Pro XT", "deviceName": "Corsair Katar Pro XT" } } } }
        """);

        Assert.Contains("Corsair Katar Pro XT", held);
        Assert.DoesNotContain("Corsair M55 RGB PRO", held);
    }

    [Fact]
    public void Load_ExistingInstall_DashboardAutoArrangeDefaultsOff()
    {
        File.WriteAllText(_settingsPath, $$"""{ "schemaVersion": {{NexusSettings.CurrentSchemaVersion}}, "ui": { "sidebarCollapsed": true } }""");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.False(s.Ui.DashboardAutoArrange);
            Assert.True(s.Ui.SidebarCollapsed);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NoSettingsFile_DashboardAutoArrangeOff()
    {
        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.False(s.Ui.DashboardAutoArrange);
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
    public void Load_V20WithTheFlagFalse_MarksTheInstallFeaturesOnboarded()
    {
        File.WriteAllText(_settingsPath, """{ "schemaVersion": 20, "featuresOnboardingCompleted": false }""");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.FeaturesOnboardingCompleted);
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_FileWithoutASchemaVersionKey_MarksTheInstallFeaturesOnboarded()
    {
        File.WriteAllText(_settingsPath, """{ "ui": { "sidebarCollapsed": true } }""");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.FeaturesOnboardingCompleted);
            Assert.True(s.Ui.SidebarCollapsed);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_NestedSchemaVersionKeyDoesNotCountAsTheTopLevelOne()
    {
        File.WriteAllText(_settingsPath, """{ "ui": { "schemaVersion": 3 } }""");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.FeaturesOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_DifferentlyCasedSchemaVersionKeyDoesNotCount()
    {
        File.WriteAllText(_settingsPath, """{ "SCHEMAVERSION": 20, "ui": { "sidebarCollapsed": true } }""");

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.True(s.FeaturesOnboardingCompleted);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void FeaturesReplayPending_SurvivesAStoreReload()
    {
        var store = new JsonConfigStore(_settingsPath);
        try
        {
            store.Update(x => x.FeaturesReplayPending = true);
            store.FlushNow();
            store.Reload();
            Assert.True(store.Load().FeaturesReplayPending);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void Load_CurrentSchemaWithTheFlagFalse_StaysUndecided()
    {
        File.WriteAllText(_settingsPath, $$"""{ "schemaVersion": {{NexusSettings.CurrentSchemaVersion}} }""");

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
            Assert.Equal("custom", instance.Mode);
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
            Assert.Equal("custom", s.StreamDeck.Instances["streamdeck:SERIAL-1"].Mode);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>v24: "wgl" was the old default and was always persisted, so it moves to ANGLE; "glfw" and an already-migrated document are left alone.</summary>
    [Theory]
    [InlineData(21, "wgl", "angle")]
    [InlineData(22, "wgl", "angle")]
    [InlineData(22, "glfw", "glfw")]
    [InlineData(23, "wgl", "angle")]
    [InlineData(24, "wgl", "wgl")]
    public void Load_RenderBackend_WglMovesToAngleOnce(int schema, string stored, string expected)
    {
        File.WriteAllText(_settingsPath,
            $$"""{ "schemaVersion": {{schema}}, "lighting": { "renderBackend": "{{stored}}" } }""");

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            Assert.Equal(expected, store.Load().Lighting.RenderBackend);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>v25: 80 was the old keeb firmware brightness default and was always persisted, so it moves to 100; any other stored level is the user's.</summary>
    [Theory]
    [InlineData(24, 80, 100)]
    [InlineData(24, 60, 60)]
    [InlineData(24, 100, 100)]
    [InlineData(25, 80, 80)]
    public void Load_KeebFirmwareBrightness_MovesOldDefaultOnce(int schemaVersion, int stored, int expected)
    {
        File.WriteAllText(_settingsPath,
            $$"""{ "schemaVersion": {{schemaVersion}}, "keeb": { "firmwareLighting": { "brightness": {{stored}} } } }""");

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            Assert.Equal(expected, store.Load().Keeb.FirmwareLighting.Brightness);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>The move runs once: an 80 the user picks afterwards survives the next load.</summary>
    [Fact]
    public void Load_KeebFirmwareBrightness_AnEightyChosenAfterTheMigrationSurvivesARestart()
    {
        File.WriteAllText(_settingsPath,
            """{ "schemaVersion": 24, "keeb": { "firmwareLighting": { "brightness": 80 } } }""");

        var first = new JsonConfigStore(_settingsPath);
        try
        {
            Assert.Equal(100, first.Load().Keeb.FirmwareLighting.Brightness);
            first.Update(s => s.Keeb.FirmwareLighting.Brightness = 80);
        }
        finally
        {
            first.Dispose();
        }

        var second = new JsonConfigStore(_settingsPath);
        try
        {
            var s = second.Load();
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal(80, s.Keeb.FirmwareLighting.Brightness);
        }
        finally
        {
            second.Dispose();
        }
    }

    /// <summary>A v22 file runs both the Game Sync removal (v23) and the render backend move (v24).</summary>
    [Fact]
    public void Load_V22File_RunsBothMigrations()
    {
        File.WriteAllText(_settingsPath,
            """{ "schemaVersion": 22, "lighting": { "renderBackend": "wgl" } }""");

        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.Equal("angle", s.Lighting.RenderBackend);
            Assert.True(s.GameSyncRemovedNotice);
            Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>"fixed" is the pre-rename instance mode name; JsonConfigStore.Load must normalize any already-persisted value to "custom" on every load, independently of SchemaVersion, since the rename is a value change rather than a structural migration.</summary>
    [Fact]
    public void Load_PersistedFixedMode_NormalizesToCustom()
    {
        var json = $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "streamDeck": {
            "presets": [ { "id": "p1", "name": "Existing", "cols": 5, "rows": 3, "deck": { "pages": [] } } ],
            "instances": {
              "streamdeck:SERIAL-1": { "mode": "fixed", "activePresetId": "p1" },
              "widget:w1": { "mode": "recentApps" }
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal("custom", s.StreamDeck.Instances["streamdeck:SERIAL-1"].Mode);
            Assert.Equal("recentApps", s.StreamDeck.Instances["widget:w1"].Mode);
        }
        finally
        {
            store.Dispose();
        }

        // The normalization must have persisted to disk, not just the in-memory
        // cache, and re-loading an already-normalized document is a no-op.
        var reloaded = new JsonConfigStore(_settingsPath);
        try
        {
            Assert.Equal("custom", reloaded.Load().StreamDeck.Instances["streamdeck:SERIAL-1"].Mode);
        }
        finally
        {
            reloaded.Dispose();
        }
    }
}
