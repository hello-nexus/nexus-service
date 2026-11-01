using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// One-time v5 to v6 settings migration: per-card LED map overrides and
/// aspect ratios re-key into the device-scoped segment-local dicts via each
/// provider's default-partition card-id mapping; unknown keys become
/// single-segment devices.
/// </summary>
public class LegacyLedOverrideMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public LegacyLedOverrideMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-zonemig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── card-id mapping table ────────────────────────────────────────────

    [Theory]
    [InlineData("keeb:SER1:keys", "keeb:SER1", 0)]
    [InlineData("keeb:SER1:underglow", "keeb:SER1", 1)]
    [InlineData("openrgb-0-1", "openrgb-0", 1)]
    [InlineData("openrgb-s-MB01-2", "openrgb-s-MB01", 2)]
    [InlineData("openrgb-l-COM4-0", "openrgb-l-COM4", 0)]
    public void Known_multi_segment_shapes_map_to_their_segment(string legacy, string deviceId, int segment)
    {
        Assert.Equal((deviceId, segment), LegacyLedOverrideMigration.MapLegacyCardId(legacy));
    }

    [Theory]
    [InlineData("openrgb-5")]               // legacy numeric whole-device id
    [InlineData("openrgb-s-123")]           // whole device with a numeric serial
    [InlineData("openrgb-s-ABC")]           // whole device, serial without dashes
    [InlineData("openrgb-s-X-2024")]        // numeric tail too large for a zone index
    [InlineData("openrgb-s-2")]             // bare stem prefix is not a device id
    [InlineData("openrgb-l-3")]             // same guard for location-keyed ids
    [InlineData("np50:hub1:port2")]         // hub port card
    [InlineData("hue:bridge:light-3000")]   // smart light
    [InlineData("keeb:SER1")]               // bare keeb hub id
    public void Everything_else_maps_as_single_segment_device(string legacy)
    {
        Assert.Equal((legacy, 0), LegacyLedOverrideMigration.MapLegacyCardId(legacy));
    }

    // ── in-place migration ───────────────────────────────────────────────

    [Fact]
    public void Apply_moves_overrides_into_segment_space_and_clears_legacy()
    {
        var settings = new NexusSettings();
        settings.Devices.LedMapOverrides["keeb:SER1:keys"] = new()
        {
            new LedPositionOverride { LedIndex = 3, U = 0.1f, V = 0.2f },
        };
        settings.Devices.LedMapOverrides["keeb:SER1:underglow"] = new()
        {
            new LedPositionOverride { LedIndex = 7, U = 0.3f, V = 0.4f, Disabled = true },
        };
        settings.Devices.LedMapOverrides["openrgb-s-MB01-1"] = new()
        {
            new LedPositionOverride { LedIndex = 12, U = 0.5f, V = 0.6f },
        };
        settings.Devices.LedMapOverrides["np50:hub1:port2"] = new()
        {
            new LedPositionOverride { LedIndex = 0, U = 0.7f, V = 0.8f },
        };
        settings.Devices.LedMapAspectRatios["keeb:SER1:keys"] = 4.2f;
        settings.Devices.LedMapAspectRatios["keeb:SER1:underglow"] = 9.9f;

        LegacyLedOverrideMigration.Apply(settings);

        // Both keeb cards merge into the hub device, segment-local.
        var keeb = settings.Devices.DeviceLedOverrides["keeb:SER1"];
        Assert.Equal(2, keeb.Count);
        Assert.Contains(keeb, o => o is { Segment: 0, LedIndex: 3, U: 0.1f });
        Assert.Contains(keeb, o => o is { Segment: 1, LedIndex: 7, Disabled: true });

        var mobo = settings.Devices.DeviceLedOverrides["openrgb-s-MB01"];
        Assert.Single(mobo);
        Assert.Equal(1, mobo[0].Segment);
        Assert.Equal(12, mobo[0].LedIndex);
        // The dashed-serial reading of the same key is ambiguous, so the
        // whole-device copy exists too (inert unless that device id is real).
        Assert.Single(settings.Devices.DeviceLedOverrides["openrgb-s-MB01-1"]);

        var port = settings.Devices.DeviceLedOverrides["np50:hub1:port2"];
        Assert.Single(port);
        Assert.Equal(0, port[0].Segment);

        // First mapped card wins on the device aspect ratio.
        Assert.Equal(4.2f, settings.Devices.DeviceAspectRatios["keeb:SER1"]);

        Assert.Empty(settings.Devices.LedMapOverrides);
        Assert.Empty(settings.Devices.LedMapAspectRatios);
    }

    [Fact]
    public void Ambiguous_dash_digit_serial_double_writes_both_interpretations()
    {
        // "openrgb-s-K70-2" reads both as zone 2 of "openrgb-s-K70" and as a
        // whole device whose sanitized serial is "K70-2". The migration must
        // not silently pick one and lose (or misfile) the user's overrides.
        var settings = new NexusSettings();
        settings.Devices.LedMapOverrides["openrgb-s-K70-2"] = new()
        {
            new LedPositionOverride { LedIndex = 4, U = 0.6f, V = 0.7f, Disabled = true },
        };
        settings.Devices.LedMapAspectRatios["openrgb-s-K70-2"] = 3.5f;

        LegacyLedOverrideMigration.Apply(settings);

        var split = settings.Devices.DeviceLedOverrides["openrgb-s-K70"];
        Assert.Single(split);
        Assert.Equal((2, 4), (split[0].Segment, split[0].LedIndex));
        Assert.True(split[0].Disabled);

        var whole = settings.Devices.DeviceLedOverrides["openrgb-s-K70-2"];
        Assert.Single(whole);
        Assert.Equal((0, 4), (whole[0].Segment, whole[0].LedIndex));
        Assert.True(whole[0].Disabled);

        Assert.Equal(3.5f, settings.Devices.DeviceAspectRatios["openrgb-s-K70"]);
        Assert.Equal(3.5f, settings.Devices.DeviceAspectRatios["openrgb-s-K70-2"]);
    }

    [Fact]
    public void Bare_stem_dash_digit_key_migrates_as_whole_device_only()
    {
        // "openrgb-s-2" would split into the bare stem "openrgb-s", which is
        // never a device id; the key must migrate as a single-segment device
        // with no sibling entry.
        var settings = new NexusSettings();
        settings.Devices.LedMapOverrides["openrgb-s-2"] = new()
        {
            new LedPositionOverride { LedIndex = 1, U = 0.1f, V = 0.2f },
        };

        LegacyLedOverrideMigration.Apply(settings);

        var whole = settings.Devices.DeviceLedOverrides["openrgb-s-2"];
        Assert.Single(whole);
        Assert.Equal((0, 1), (whole[0].Segment, whole[0].LedIndex));
        Assert.False(settings.Devices.DeviceLedOverrides.ContainsKey("openrgb-s"));
        Assert.Single(settings.Devices.DeviceLedOverrides);
    }

    [Fact]
    public void Unambiguous_zone_split_writes_only_the_split_target()
    {
        // Numeric legacy ids ("openrgb-0-1") cannot be whole-device ids with
        // a dashed serial, so no double-write happens.
        var settings = new NexusSettings();
        settings.Devices.LedMapOverrides["openrgb-0-1"] = new()
        {
            new LedPositionOverride { LedIndex = 9, U = 0.4f, V = 0.5f },
        };

        LegacyLedOverrideMigration.Apply(settings);

        Assert.Single(settings.Devices.DeviceLedOverrides);
        var split = settings.Devices.DeviceLedOverrides["openrgb-0"];
        Assert.Single(split);
        Assert.Equal((1, 9), (split[0].Segment, split[0].LedIndex));
    }

    // ── load-time migration through JsonConfigStore ──────────────────────

    [Fact]
    public void Load_noops_on_current_schema()
    {
        File.WriteAllText(_settingsPath, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "devices": { "deviceAspectRatios": { "dev-1": 1.5 } }
        }
        """);
        var store = new JsonConfigStore(_settingsPath);
        try
        {
            var s = store.Load();
            Assert.Equal(1.5f, s.Devices.DeviceAspectRatios["dev-1"]);
            Assert.Empty(s.Devices.DeviceLedOverrides);
        }
        finally
        {
            store.Dispose();
        }
    }
}
