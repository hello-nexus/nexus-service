using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Zones;

/// <summary>
/// Partition changes drop the device's per-zone state (prefs, canvas
/// layouts, applied mappings, groups, power, controlled, auto-apply veto) for the
/// outgoing card ids while keeping device-scoped state (segment-local
/// overrides, aspect ratio, segment LED counts) and other devices untouched.
/// </summary>
public class ZoneStateDropTests
{
    private const string ZoneA = "keeb:SER1:keys";
    private const string ZoneB = "keeb:SER1:underglow";
    private const string Other = "openrgb-s-X-0";

    private static NexusSettings Seeded()
    {
        var settings = new NexusSettings();
        foreach (var id in new[] { ZoneA, ZoneB, Other })
        {
            settings.Devices.LightingDevicePrefs[id] = new LightingDevicePreference { Brightness = 33 };
            settings.Lighting.DeviceLayouts[id] = new DeviceLayout { X = 1, Y = 2 };
            settings.Devices.AppliedMappings[id] = new AppliedMappingRef { Name = "m" };
            settings.Devices.LedGroups[id] = new List<MappingGroup> { new() { Name = "g" } };
            settings.Devices.MappingAutoApplyDeclined.Add(id);
            settings.Devices.DisabledLightingDevices.Add(id);
            settings.Devices.UncontrolledLightingDevices.Add(id);
        }
        settings.Devices.DeviceLedOverrides["keeb:SER1"] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 1, U = 0.5f, V = 0.5f },
        };
        settings.Devices.DeviceAspectRatios["keeb:SER1"] = 2f;
        settings.Devices.ZoneLedCounts["openrgb-s-X-0"] = 60;
        return settings;
    }

    [Fact]
    public void Drop_removes_per_zone_state_for_given_ids_only()
    {
        var settings = Seeded();
        ZoneStateDrop.Drop(settings, new[] { ZoneA, ZoneB });

        foreach (var id in new[] { ZoneA, ZoneB })
        {
            Assert.False(settings.Devices.LightingDevicePrefs.ContainsKey(id));
            Assert.False(settings.Lighting.DeviceLayouts.ContainsKey(id));
            Assert.False(settings.Devices.AppliedMappings.ContainsKey(id));
            Assert.False(settings.Devices.LedGroups.ContainsKey(id));
            Assert.DoesNotContain(id, settings.Devices.MappingAutoApplyDeclined);
            Assert.DoesNotContain(id, settings.Devices.DisabledLightingDevices);
            Assert.DoesNotContain(id, settings.Devices.UncontrolledLightingDevices);
        }

        // The unrelated device's state survives.
        Assert.True(settings.Devices.LightingDevicePrefs.ContainsKey(Other));
        Assert.True(settings.Lighting.DeviceLayouts.ContainsKey(Other));
        Assert.True(settings.Devices.AppliedMappings.ContainsKey(Other));
        Assert.Contains(Other, settings.Devices.DisabledLightingDevices);
        Assert.Contains(Other, settings.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void Drop_keeps_device_scoped_state()
    {
        var settings = Seeded();
        ZoneStateDrop.Drop(settings, new[] { ZoneA, ZoneB });

        Assert.True(settings.Devices.DeviceLedOverrides.ContainsKey("keeb:SER1"));
        Assert.Equal(2f, settings.Devices.DeviceAspectRatios["keeb:SER1"]);
        Assert.Equal(60, settings.Devices.ZoneLedCounts["openrgb-s-X-0"]);
    }
}
