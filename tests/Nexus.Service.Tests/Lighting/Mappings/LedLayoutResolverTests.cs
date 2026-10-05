using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// Layering contract: computed default, then applied mapping artifact, then
/// user deltas (DeviceLedOverrides / LedGroups) - user always wins.
/// </summary>
public class LedLayoutResolverTests
{
    private const string Id = "dev-1";

    private static NexusSettings Settings() => new();

    private static MappingArtifact Artifact(int ledCount = 4)
    {
        var zone = new MappingZone { ZoneIndex = 0, LedCount = ledCount, AspectRatio = 2f };
        for (int i = 0; i < ledCount; i++)
            zone.Leds.Add(new MappingLed { I = i, U = 0.1f * (i + 1), V = 0.9f });
        zone.Disabled.Add(1);
        zone.Groups.Add(new MappingGroup
        { Name = "Fan", Ranges = new() { new MappingLedRange { Start = 0, End = 1 } } });
        return new MappingArtifact
        {
            Name = "artifact",
            Device = new MappingDeviceInfo { Key = "usb:0000:0000" },
            Zones = new() { zone },
        };
    }

    private static void ApplyMapping(NexusSettings settings, MappingArtifact artifact)
        => settings.Devices.AppliedMappings[Id] = new AppliedMappingRef
        {
            Name = artifact.Name,
            Artifact = artifact,
            ContentHash = MappingHash.ContentHash(artifact),
            Source = "community",
        };

    // ── defaults ─────────────────────────────────────────────────────────

    [Fact]
    public void Seedless_resolve_yields_linear_defaults()
    {
        var layout = LedLayoutResolver.ResolveSeeded(Id, 3, null, null, Settings());
        Assert.Equal(new[] { 0f, 0.5f, 1f }, layout.U);
        Assert.All(layout.V, v => Assert.Equal(0.5f, v));
        Assert.Null(layout.Disabled);
        Assert.All(layout.ZoneTypes, t => Assert.Equal("linear", t));
    }

    [Fact]
    public void Seeded_resolve_preserves_provider_uvs()
    {
        var seedU = new[] { 0.25f, 0.75f };
        var seedV = new[] { 0.1f, 0.9f };
        var layout = LedLayoutResolver.ResolveSeeded(Id, 2, seedU, seedV, Settings());
        Assert.Equal(seedU, layout.U);
        Assert.Equal(seedV, layout.V);
        Assert.NotSame(seedU, layout.U);
        Assert.All(layout.ZoneTypes, t => Assert.Equal("matrix", t));
    }

    [Fact]
    public void Seed_length_mismatch_falls_back_to_linear()
    {
        var layout = LedLayoutResolver.ResolveSeeded(Id, 3, new[] { 0.1f }, new[] { 0.2f }, Settings());
        Assert.Equal(new[] { 0f, 0.5f, 1f }, layout.U);
    }

    // ── applied mapping layer ────────────────────────────────────────────

    [Fact]
    public void Applied_mapping_sets_positions_disabled_groups_aspect()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);

        Assert.Equal(0.1f, layout.U[0], 3);
        Assert.Equal(0.4f, layout.U[3], 3);
        Assert.All(layout.V, v => Assert.Equal(0.9f, v, 3));
        Assert.NotNull(layout.Disabled);
        Assert.True(layout.Disabled![1]);
        Assert.False(layout.Disabled[0]);
        Assert.Single(layout.Groups);
        Assert.Equal("Fan", layout.Groups[0].Name);
        Assert.Equal(2f, layout.AspectRatio);
        Assert.NotNull(layout.Applied);
    }

    [Fact]
    public void Mapping_led_indices_beyond_count_are_ignored()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact(ledCount: 8));
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Equal(4, layout.U.Length);
    }

    // ── user delta layer ─────────────────────────────────────────────────

    [Fact]
    public void User_override_beats_mapping_position()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.99f, V = 0.01f },
        };
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Equal(0.99f, layout.U[0]);
        Assert.Equal(0.01f, layout.V[0]);
        Assert.Equal(0.2f, layout.U[1], 3);
        Assert.True(layout.HasUserOverrides);
        Assert.Contains(0, layout.CustomLeds);
    }

    [Fact]
    public void User_override_reenables_mapping_disabled_led()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 1, U = 0.5f, V = 0.5f, Disabled = false },
        };
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Null(layout.Disabled);
    }

    [Fact]
    public void User_groups_replace_mapping_groups()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.LedGroups[Id] = new()
        {
            new MappingGroup { Name = "Mine", Ranges = new() { new MappingLedRange { Start = 2, End = 3 } } },
        };
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Single(layout.Groups);
        Assert.Equal("Mine", layout.Groups[0].Name);
    }

    [Fact]
    public void Empty_user_group_list_clears_mapping_groups()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.LedGroups[Id] = new();
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Empty(layout.Groups);
    }

    [Fact]
    public void User_aspect_ratio_beats_mapping_aspect()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.DeviceAspectRatios[Id] = 3.5f;
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        Assert.Equal(3.5f, layout.AspectRatio);
    }

    // ── OpenRGB paths ────────────────────────────────────────────────────

    private static RgbDevice ZonedDevice() => new()
    {
        Name = "Mobo",
        LedCount = 5,
        Zones = new()
        {
            new RgbZone { Name = "A", ZoneType = 1, LedCount = 2 },
            new RgbZone { Name = "B", ZoneType = 1, LedCount = 3 },
        },
    };

    [Fact]
    public void Zone_resolve_uses_persisted_count_over_reported()
    {
        var settings = Settings();
        settings.Devices.ZoneLedCounts["mobo-1"] = 6;
        var layout = LedLayoutResolver.ResolveOpenRgb(ZonedDevice(), 1, "mobo-1", settings);
        Assert.Equal(6, layout.LedCount);
        Assert.Equal(2, layout.GlobalOffset);
        Assert.Equal(1, layout.ZoneHint);
    }

    [Fact]
    public void Zone_resolve_applies_mapping_for_matching_zone_index()
    {
        var settings = Settings();
        var artifact = Artifact(ledCount: 3);
        artifact.Zones[0].ZoneIndex = 1;
        settings.Devices.AppliedMappings["mobo-1"] = new AppliedMappingRef { Artifact = artifact };
        var layout = LedLayoutResolver.ResolveOpenRgb(ZonedDevice(), 1, "mobo-1", settings);
        Assert.Equal(0.1f, layout.U[0], 3);
    }

    [Fact]
    public void Whole_device_resolve_without_matrix_yields_linear()
    {
        var layout = LedLayoutResolver.ResolveOpenRgb(ZonedDevice(), -1, "mobo", Settings());
        Assert.Equal(5, layout.LedCount);
        Assert.Equal(0f, layout.U[0]);
        Assert.Equal(1f, layout.U[4]);
    }

    // ── frame application ────────────────────────────────────────────────

    [Fact]
    public void ApplyToFrame_skips_on_length_mismatch()
    {
        var frame = new DeviceFrame(0, Id, ledCount: 7);
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, Settings());
        LedLayoutResolver.ApplyToFrame(frame, layout);
        Assert.Null(frame.LedU);
    }

    [Fact]
    public void ApplyToFrame_sets_uv_and_disabled()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        var frame = new DeviceFrame(0, Id, ledCount: 4);
        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);
        LedLayoutResolver.ApplyToFrame(frame, layout);
        Assert.NotNull(frame.LedU);
        Assert.NotNull(frame.LedDisabled);
        Assert.True(frame.LedDisabled![1]);
    }

    // ── artifact round trip ──────────────────────────────────────────────

    [Fact]
    public void Export_then_apply_round_trips_layout()
    {
        var settings = Settings();
        ApplyMapping(settings, Artifact());
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 2, U = 0.42f, V = 0.13f },
        };
        var resolved = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);

        var card = new Nexus.Service.Models.Devices.LightingDevice
        {
            Id = Id,
            Name = "Card",
            DeviceKey = "usb:1234:abcd",
            LedCount = 4,
            ZoneResizable = true,
            ZoneType = "linear",
        };
        var exported = MappingArtifactFactory.FromResolved(card, resolved, "exported", null);

        var freshSettings = Settings();
        freshSettings.Devices.AppliedMappings[Id] = new AppliedMappingRef { Artifact = exported };
        var reResolved = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, freshSettings);

        Assert.Equal(resolved.U, reResolved.U);
        Assert.Equal(resolved.V, reResolved.V);
        Assert.Equal(resolved.Disabled, reResolved.Disabled);
        Assert.Equal(4, exported.Zones[0].LedCount);
        Assert.Equal("usb:1234:abcd", exported.Device.Key);
        Assert.Equal("1234", exported.Device.Match?.Vid);
    }

    [Fact]
    public void Export_omits_count_for_fixed_count_devices()
    {
        var resolved = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, Settings());
        var card = new Nexus.Service.Models.Devices.LightingDevice
        { Id = Id, Name = "Card", DeviceKey = "orgb:abc", LedCount = 4, ZoneResizable = false };
        var exported = MappingArtifactFactory.FromResolved(card, resolved, "exported", null);
        Assert.Null(exported.Zones[0].LedCount);
    }

    // ── built-ins follow the shipped catalog ─────────────────────────────

    private static void ApplyStaleBuiltIn(NexusSettings settings, string key, int ledCount)
    {
        var stale = Artifact(ledCount);
        settings.Devices.AppliedMappings[Id] = new AppliedMappingRef
        {
            MappingId = key,
            Name = stale.Name,
            Artifact = stale,
            ContentHash = MappingHash.ContentHash(stale),
            Source = MappingApplyService.SourceBuiltIn,
        };
    }

    [Fact]
    public void A_built_in_wired_before_a_catalog_fix_draws_the_catalog_geometry()
    {
        var settings = Settings();
        ApplyStaleBuiltIn(settings, LianLiChainArtifacts.SlInfinityKey, 20);

        var layout = LedLayoutResolver.ResolveSeeded(Id, 20, null, null, settings);

        var current = BuiltInMappingsCatalog.Find(LianLiChainArtifacts.SlInfinityKey)!.Zones[0].Leds;
        Assert.Equal(current.Select(l => l.U), layout.U);
        Assert.Equal(current.Select(l => l.V), layout.V);
        Assert.Null(layout.Disabled);
    }

    [Fact]
    public void A_built_in_whose_count_changed_keeps_its_embedded_copy()
    {
        var settings = Settings();
        ApplyStaleBuiltIn(settings, LianLiChainArtifacts.SlInfinityKey, 4);

        var layout = LedLayoutResolver.ResolveSeeded(Id, 4, null, null, settings);

        Assert.Equal(0.1f, layout.U[0], 4);
        Assert.Equal(0.9f, layout.V[0], 4);
    }

    [Fact]
    public void A_community_mapping_keeps_its_embedded_copy_even_under_a_catalog_key()
    {
        var settings = Settings();
        var artifact = Artifact(20);
        ApplyMapping(settings, artifact);
        settings.Devices.AppliedMappings[Id].MappingId = LianLiChainArtifacts.SlInfinityKey;

        var layout = LedLayoutResolver.ResolveSeeded(Id, 20, null, null, settings);

        Assert.Equal(0.1f, layout.U[0], 4);
        Assert.Equal(0.9f, layout.V[0], 4);
    }
}
