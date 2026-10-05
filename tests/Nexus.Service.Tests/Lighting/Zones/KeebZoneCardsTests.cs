using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Keeb card emission. The default partition is the no-regression bar: card
/// ids, names, counts, and per-card behavior must match the legacy
/// two-hardcoded-cards provider exactly (plus the two new DTO fields).
/// Custom partitions emit ordinal ids and combined names.
/// </summary>
public class KeebZoneCardsTests
{
    private const string HubId = "keeb:SER123";

    [Fact]
    public void Default_partition_matches_legacy_cards()
    {
        var cards = KeebLightingDeviceProvider.BuildCards(HubId, KeebKeyMap.Ansi, new NexusSettings());

        Assert.Equal(2, cards.Count);
        var keys = cards[0];
        Assert.Equal(HubId + ":keys", keys.Id);
        Assert.Equal($"{KeebHub.ProductName} - Keys", keys.Name);
        Assert.Equal("ledstrip", keys.Type);
        Assert.Equal("keyboard", keys.IconType);
        Assert.Equal(KeebKeyMap.Ansi.LedCount, keys.LedCount);
        Assert.Equal(KeebKeyMap.Ansi.LedCount, keys.EnabledLedCount);
        Assert.Equal("usb:3402:0300:keys", keys.DeviceKey);
        Assert.True(keys.LedsOn);
        Assert.Equal(100, keys.Brightness);
        Assert.Equal(HubId, keys.ParentDeviceId);
        Assert.Equal(0, keys.ZoneIndex);
        Assert.Equal("linear", keys.ZoneType);
        Assert.False(keys.ZoneResizable);
        Assert.Equal(HubId, keys.DeviceId);
        Assert.True(keys.ZoneCustomizable);

        var underglow = cards[1];
        Assert.Equal(HubId + ":underglow", underglow.Id);
        Assert.Equal($"{KeebHub.ProductName} - Underglow", underglow.Name);
        Assert.Equal("strip", underglow.IconType);
        Assert.Equal(KeebLayout.SurroundLedCount, underglow.LedCount);
        Assert.Equal(KeebLayout.SurroundLedCount, underglow.EnabledLedCount);
        Assert.Equal(1, underglow.ZoneIndex);
        Assert.Equal(HubId, underglow.DeviceId);
    }

    [Fact]
    public void Default_partition_keeps_per_card_prefs_and_power()
    {
        var settings = new NexusSettings();
        settings.Devices.DisabledLightingDevices.Add(HubId + ":underglow");
        settings.Devices.LightingDevicePrefs[HubId + ":keys"] = new LightingDevicePreference { Brightness = 42 };

        var cards = KeebLightingDeviceProvider.BuildCards(HubId, KeebKeyMap.Ansi, settings);
        Assert.Equal(42, cards[0].Brightness);
        Assert.True(cards[0].LedsOn);
        Assert.False(cards[1].LedsOn);
    }

    [Fact]
    public void Cards_subtract_override_disabled_leds_from_enabled_count()
    {
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[HubId] = new List<SegmentLedOverride>
        {
            new() { Segment = 0, LedIndex = 3, Disabled = true },
            new() { Segment = 1, LedIndex = 0, Disabled = true },
            new() { Segment = 1, LedIndex = 1, Disabled = true },
        };

        var cards = KeebLightingDeviceProvider.BuildCards(HubId, KeebKeyMap.Ansi, settings);
        Assert.Equal(KeebKeyMap.Ansi.LedCount - 1, cards[0].EnabledLedCount);
        Assert.Equal(KeebLayout.SurroundLedCount - 2, cards[1].EnabledLedCount);
        // The plain LED count never changes; only the enabled tally does.
        Assert.Equal(KeebKeyMap.Ansi.LedCount, cards[0].LedCount);
        Assert.Equal(KeebLayout.SurroundLedCount, cards[1].LedCount);
    }

    [Fact]
    public void Custom_partition_emits_ordinal_cards()
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new()
            {
                Name = "Everything",
                Slices =
                {
                    new ZoneSlice { Segment = 0, Start = 0, Count = KeebKeyMap.Ansi.LedCount },
                    new ZoneSlice { Segment = 1, Start = 0, Count = KeebLayout.SurroundLedCount },
                },
            },
        };

        var cards = KeebLightingDeviceProvider.BuildCards(HubId, KeebKeyMap.Ansi, settings);
        Assert.Single(cards);
        var card = cards[0];
        Assert.Equal($"{HubId}:z0", card.Id);
        Assert.Equal($"{KeebHub.ProductName} - Everything", card.Name);
        Assert.Equal(KeebKeyMap.Ansi.LedCount + KeebLayout.SurroundLedCount, card.LedCount);
        Assert.Equal(card.LedCount, card.EnabledLedCount);
        Assert.Equal("", card.DeviceKey);
        Assert.Equal(HubId, card.ParentDeviceId);
        Assert.Equal(HubId, card.DeviceId);
        Assert.Equal(0, card.ZoneIndex);
        Assert.True(card.ZoneCustomizable);
        Assert.False(card.ZoneResizable);
        Assert.Equal("keyboard", card.IconType);
    }

    [Fact]
    public void Custom_partition_split_within_keys_segment()
    {
        var settings = new NexusSettings();
        settings.Devices.ZonePartitions[HubId] = new List<ZoneDef>
        {
            new() { Name = "Left", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 40 } } },
            new()
            {
                Name = "Right",
                Slices = { new ZoneSlice { Segment = 0, Start = 40, Count = KeebKeyMap.Ansi.LedCount - 40 } },
            },
            new()
            {
                Name = "Glow",
                Slices = { new ZoneSlice { Segment = 1, Start = 0, Count = KeebLayout.SurroundLedCount } },
            },
        };

        var cards = KeebLightingDeviceProvider.BuildCards(HubId, KeebKeyMap.Ansi, settings);
        Assert.Equal(3, cards.Count);
        Assert.Equal(new[] { $"{HubId}:z0", $"{HubId}:z1", $"{HubId}:z2" },
            new[] { cards[0].Id, cards[1].Id, cards[2].Id });
        Assert.Equal(40, cards[0].LedCount);
        Assert.Equal(KeebKeyMap.Ansi.LedCount - 40, cards[1].LedCount);
        Assert.Equal(KeebLayout.SurroundLedCount, cards[2].LedCount);
        Assert.Equal("strip", cards[2].IconType);

        // Default canvas slots cycle by ordinal: consecutive zones never
        // share a slot, so 3+ zone partitions don't stack past the first.
        var (x0, y0, _, _) = KeebLightingDeviceProvider.DefaultKeebLayout(0);
        var (x1, y1, _, _) = KeebLightingDeviceProvider.DefaultKeebLayout(1);
        Assert.Equal((x0, y0), (cards[0].CanvasX, cards[0].CanvasY));
        Assert.Equal((x1, y1), (cards[1].CanvasX, cards[1].CanvasY));
        Assert.Equal((x0, y0), (cards[2].CanvasX, cards[2].CanvasY));
        Assert.NotEqual((cards[1].CanvasX, cards[1].CanvasY), (cards[2].CanvasX, cards[2].CanvasY));
    }

    [Fact]
    public void Structure_exposes_two_fixed_segments_in_device_order()
    {
        var structure = KeebZoneSupport.BuildStructure(HubId, KeebKeyMap.Ansi);
        Assert.Equal(HubId, structure.DeviceId);
        Assert.Equal(KeebHub.ProductName, structure.Name);
        Assert.Equal(2, structure.Segments.Count);
        Assert.Equal(KeebKeyMap.Ansi.LedCount, structure.Segments[0].LedCount);
        Assert.Equal(KeebLayout.SurroundLedCount, structure.Segments[1].LedCount);
        Assert.All(structure.Segments, s => Assert.False(s.Resizable));
        Assert.Equal(2, structure.DefaultZones.Count);
        Assert.Equal(HubId + ":keys", structure.DefaultZones[0].Id);
        Assert.Equal(HubId + ":underglow", structure.DefaultZones[1].Id);
        // Raw names are the segment default names (no device prefix); the
        // card names keep the "{DeviceName} - {ZoneName}" form.
        Assert.Equal("Keys", structure.DefaultZones[0].RawName);
        Assert.Equal("Underglow", structure.DefaultZones[1].RawName);
    }
}

public class KeebZoneKeyNamesTests
{
    [Fact]
    public void A_merged_keys_and_underglow_zone_names_its_keys_and_leaves_underglow_blank()
    {
        var keys = KeebKeyMap.Ansi;
        var zone = new ResolvedZone
        {
            Id = "keeb:SER123:z0",
            FrameLedCount = keys.LedCount + KeebLayout.SurroundLedCount,
            Slices = new[]
            {
                new ZoneSlice { Segment = KeebZoneSupport.UnderglowSegment, Start = 0, Count = KeebLayout.SurroundLedCount },
                new ZoneSlice { Segment = KeebZoneSupport.KeysSegment, Start = 0, Count = keys.LedCount },
            },
        };

        Assert.True(KeebLightingDeviceProvider.TouchesKeys(zone));
        var names = KeebLightingDeviceProvider.ZoneKeyNames(zone, keys, null)!;
        Assert.All(names[..KeebLayout.SurroundLedCount], Assert.Null);
        Assert.Equal(KeebKeyNames.For(keys), names[KeebLayout.SurroundLedCount..]);
        // Unchanged names keep the frame's existing array.
        Assert.Same(names, KeebLightingDeviceProvider.ZoneKeyNames(zone, keys, names));
    }

    [Fact]
    public void An_underglow_only_zone_is_not_a_keyboard()
    {
        var zone = new ResolvedZone
        {
            FrameLedCount = KeebLayout.SurroundLedCount,
            Slices = new[] { new ZoneSlice { Segment = KeebZoneSupport.UnderglowSegment, Start = 0, Count = KeebLayout.SurroundLedCount } },
        };
        Assert.False(KeebLightingDeviceProvider.TouchesKeys(zone));
        Assert.Null(KeebLightingDeviceProvider.ZoneKeyNames(zone, KeebKeyMap.Ansi, null));
    }
}
