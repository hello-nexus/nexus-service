using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// Exclusion reconcile semantics: a device is snapshotted for detector
/// exclusion only when every card it emits is uncontrolled, it carries a
/// stable hardware id, and every live device sharing its OpenRGB name (the
/// denylist is per model) qualifies too. An exclusion lifts when the base id
/// leaves the uncontrolled list - device presence is irrelevant to removal,
/// since an applied exclusion makes the hardware vanish from the live list.
/// </summary>
public class OpenRgbDetectorExclusionsTests
{
    private static RgbDevice Keyboard(string serial = "K70A") => new()
    {
        Index = 0,
        Name = "Corsair K70 RGB",
        Vendor = "Corsair",
        Type = 5,
        LedCount = 104,
        Serial = serial,
        Zones = new() { new RgbZone { Name = "Keyboard", ZoneType = 2, LedCount = 104 } },
    };

    [Fact]
    public void Fully_uncontrolled_stable_device_is_added()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Keyboard() }, settings);

        var add = Assert.Single(delta.Add);
        Assert.Equal("openrgb-s-K70A", add.Key);
        Assert.Equal("Corsair K70 RGB", add.Value.DetectorName);
        Assert.Equal("K70A", add.Value.Serial);
        Assert.Equal(104, add.Value.LedCount);
        Assert.Empty(delta.Remove);
        Assert.Empty(delta.UncontrolledAdds);
    }

    [Fact]
    public void Controlled_device_produces_no_delta()
    {
        var delta = OpenRgbDetectorExclusions.Compute(new[] { Keyboard() }, new NexusSettings());
        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Index_only_id_never_seeds_an_exclusion()
    {
        var device = Keyboard();
        device.Serial = "";
        device.Location = "";
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(device.StableId);

        var delta = OpenRgbDetectorExclusions.Compute(new[] { device }, settings);
        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Zero_led_placeholder_never_seeds_an_exclusion()
    {
        var device = Keyboard();
        device.LedCount = 0;
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { device }, settings);
        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Second_controlled_device_of_same_model_blocks_exclusion()
    {
        // The denylist is per detector name: excluding one K70 would kill both.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Keyboard("K70A"), Keyboard("K70B") }, settings);
        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Both_ignored_devices_of_same_model_are_added_together()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70B");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Keyboard("K70A"), Keyboard("K70B") }, settings);
        Assert.Equal(2, delta.Add.Count);
    }

    [Fact]
    public void Recontrolled_base_id_lifts_the_exclusion_even_while_device_is_absent()
    {
        var settings = new NexusSettings();
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-K70A"] = new OpenRgbDetectorExclusion { DetectorName = "Corsair K70 RGB" };

        var delta = OpenRgbDetectorExclusions.Compute(System.Array.Empty<RgbDevice>(), settings);
        Assert.Equal(new[] { "openrgb-s-K70A" }, delta.Remove);
    }

    [Fact]
    public void Existing_exclusion_with_uncontrolled_base_id_is_stable()
    {
        // Applied exclusion + still-live device (pre-bounce, or an ineffective
        // name mapping) must not re-add or remove on every pass.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-K70A"] = new OpenRgbDetectorExclusion { DetectorName = "Corsair K70 RGB" };

        var live = OpenRgbDetectorExclusions.Compute(new[] { Keyboard() }, settings);
        Assert.True(live.IsEmpty);

        var absent = OpenRgbDetectorExclusions.Compute(System.Array.Empty<RgbDevice>(), settings);
        Assert.True(absent.IsEmpty);
    }

    [Fact]
    public void Split_device_ignored_per_zone_normalizes_base_id_into_uncontrolled()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-1");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Motherboard() }, settings);

        var add = Assert.Single(delta.Add);
        Assert.Equal("openrgb-s-MB01", add.Key);
        Assert.Equal(new[] { "openrgb-s-MB01" }, delta.UncontrolledAdds);
    }

    [Fact]
    public void Unignoring_a_zone_carded_device_purges_zone_ids_and_never_reexcludes()
    {
        // Full round trip: ignore per zone -> exclusion applied -> user toggles
        // the snapshot card back on (base id only). The lift must purge the
        // zone-level ids, or the re-detected device is still fully uncontrolled
        // and the next pass silently re-excludes it, reverting the toggle.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-1");

        OpenRgbDetectorExclusions.Apply(settings, OpenRgbDetectorExclusions.Compute(new[] { Motherboard() }, settings));
        Assert.True(settings.Devices.OpenRgbDetectorExclusions.ContainsKey("openrgb-s-MB01"));
        Assert.Contains("openrgb-s-MB01", settings.Devices.UncontrolledLightingDevices);

        // The controlled route removes exactly the toggled card id.
        settings.Devices.UncontrolledLightingDevices =
            settings.Devices.UncontrolledLightingDevices.Where(id => id != "openrgb-s-MB01").ToList();

        OpenRgbDetectorExclusions.Apply(settings, OpenRgbDetectorExclusions.Compute(System.Array.Empty<RgbDevice>(), settings));
        Assert.Empty(settings.Devices.OpenRgbDetectorExclusions);
        Assert.Empty(settings.Devices.UncontrolledLightingDevices);

        // Device re-detected after the bounce: fully controlled, no re-seed.
        var next = OpenRgbDetectorExclusions.Compute(new[] { Motherboard() }, settings);
        Assert.True(next.IsEmpty);
    }

    [Fact]
    public void Purge_spares_sibling_whose_serial_extends_the_removed_base()
    {
        // Sanitize keeps hyphens, so a sibling device's stable id can extend
        // the removed base ("openrgb-s-MB01-EXT" vs "openrgb-s-MB01"); only
        // numeric zone suffixes belong to the removed device.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-EXT");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01:z2");
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-MB01"] = new OpenRgbDetectorExclusion { DetectorName = "B850I AORUS PRO" };

        OpenRgbDetectorExclusions.Apply(settings, OpenRgbDetectorExclusions.Compute(System.Array.Empty<RgbDevice>(), settings));

        Assert.Empty(settings.Devices.OpenRgbDetectorExclusions);
        Assert.Equal(new[] { "openrgb-s-MB01-EXT" }, settings.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void Apply_replaces_collection_references_instead_of_mutating()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");
        var exclusionsBefore = settings.Devices.OpenRgbDetectorExclusions;
        var uncontrolledBefore = settings.Devices.UncontrolledLightingDevices;

        OpenRgbDetectorExclusions.Apply(settings, OpenRgbDetectorExclusions.Compute(new[] { Keyboard() }, settings));

        // Lock-free readers hold the old references; they must stay untouched.
        Assert.NotSame(exclusionsBefore, settings.Devices.OpenRgbDetectorExclusions);
        Assert.Empty(exclusionsBefore);
        Assert.Single(settings.Devices.OpenRgbDetectorExclusions);
        Assert.Same(uncontrolledBefore, settings.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void Detector_map_supplies_the_denylist_name()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        var map = new Dictionary<string, string> { ["Corsair Vengeance RGB DDR5"] = "Corsair DRAM" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm() }, settings, map);

        var add = Assert.Single(delta.Add);
        Assert.Equal("Corsair DRAM", add.Value.DetectorName);
        Assert.Equal("Corsair Vengeance RGB DDR5", add.Value.DeviceName);
    }

    [Fact]
    public void Unmapped_device_still_falls_back_to_its_own_name()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-K70A");

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Keyboard() }, settings, new Dictionary<string, string>());

        var add = Assert.Single(delta.Add);
        Assert.Equal("Corsair K70 RGB", add.Value.DetectorName);
    }

    [Fact]
    public void Exclusion_naming_the_device_instead_of_the_detector_is_re_snapshotted()
    {
        // The pre-map bug: the denylist held the device name, matched no
        // detector, and the DIMM stayed detected on every launch.
        var settings = new NexusSettings();
        var stored = new OpenRgbDetectorExclusion { DetectorName = "Corsair Vengeance RGB DDR5" };
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        settings.Devices.OpenRgbDetectorExclusions[Dimm().StableId] = stored;
        var map = new Dictionary<string, string> { ["Corsair Vengeance RGB DDR5"] = "Corsair DRAM" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm() }, settings, map);

        var add = Assert.Single(delta.Add);
        Assert.Equal(Dimm().StableId, add.Key);
        Assert.Equal("Corsair DRAM", add.Value.DetectorName);
        Assert.NotSame(stored, add.Value);
        Assert.Equal("Corsair Vengeance RGB DDR5", stored.DetectorName);
        Assert.Empty(delta.UncontrolledAdds);
        Assert.Empty(delta.Remove);
    }

    [Fact]
    public void Exclusion_already_naming_the_detector_produces_no_delta()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        settings.Devices.OpenRgbDetectorExclusions[Dimm().StableId] = new OpenRgbDetectorExclusion { DetectorName = "Corsair DRAM" };
        var map = new Dictionary<string, string> { ["Corsair Vengeance RGB DDR5"] = "Corsair DRAM" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm() }, settings, map);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Missing_map_leaves_a_device_named_exclusion_alone()
    {
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        settings.Devices.OpenRgbDetectorExclusions[Dimm().StableId] = new OpenRgbDetectorExclusion { DetectorName = "Corsair Vengeance RGB DDR5" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm() }, settings);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Map_miss_never_rewrites_a_repaired_exclusion_back_to_the_device_name()
    {
        // A torn or truncated map read parses to empty; re-snapshotting off
        // the fallback would reinstate the exact bug the repair removed.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        settings.Devices.OpenRgbDetectorExclusions[Dimm().StableId] = new OpenRgbDetectorExclusion { DetectorName = "Corsair DRAM" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm() }, settings, new Dictionary<string, string>());

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void A_controlled_sibling_of_the_same_detector_blocks_the_exclusion()
    {
        // Denylisting "Corsair DRAM" would take the Dominator kit off the bus
        // too, and the user never un-controlled it.
        var dominator = Dimm();
        dominator.Name = "Corsair Dominator Platinum RGB";
        dominator.Location = "I2C: i801, address 0x1A";
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        var map = new Dictionary<string, string>
        {
            ["Corsair Vengeance RGB DDR5"] = "Corsair DRAM",
            ["Corsair Dominator Platinum RGB"] = "Corsair DRAM",
        };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm(), dominator }, settings, map);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Both_devices_of_one_detector_uncontrolled_are_excluded_together()
    {
        var dominator = Dimm();
        dominator.Name = "Corsair Dominator Platinum RGB";
        dominator.Location = "I2C: i801, address 0x1A";
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add(Dimm().StableId);
        settings.Devices.UncontrolledLightingDevices.Add(dominator.StableId);
        var map = new Dictionary<string, string>
        {
            ["Corsair Vengeance RGB DDR5"] = "Corsair DRAM",
            ["Corsair Dominator Platinum RGB"] = "Corsair DRAM",
        };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Dimm(), dominator }, settings, map);

        Assert.Equal(2, delta.Add.Count);
        Assert.All(delta.Add, a => Assert.Equal("Corsair DRAM", a.Value.DetectorName));
    }

    [Fact]
    public void A_key_being_lifted_this_pass_is_not_re_seeded()
    {
        // A split motherboard keeps IsFullyUncontrolled true off its zone ids
        // while the base id is being lifted, and its stale exclusion is
        // repairable, so both loops reach it. Apply runs Add before Remove.
        var settings = new NexusSettings();
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-0");
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-MB01-1");
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-MB01"] = new OpenRgbDetectorExclusion { DetectorName = "B850I AORUS PRO" };
        var map = new Dictionary<string, string> { ["B850I AORUS PRO"] = "Gigabyte RGB Fusion 2 SMBus" };

        var delta = OpenRgbDetectorExclusions.Compute(new[] { Motherboard() }, settings, map);

        Assert.Equal(new[] { "openrgb-s-MB01" }, delta.Remove);
        Assert.Empty(delta.Add);
        Assert.Empty(delta.UncontrolledAdds);
    }

    private static RgbDevice Dimm() => new()
    {
        Index = 0,
        Name = "Corsair Vengeance RGB DDR5",
        Vendor = "Corsair",
        Type = 10,
        LedCount = 10,
        Location = "I2C: i801, address 0x18",
        Zones = new() { new RgbZone { Name = "DRAM", ZoneType = 1, LedCount = 10 } },
    };

    private static RgbDevice Motherboard() => new()
    {
        Index = 0,
        Name = "B850I AORUS PRO",
        Type = 0,
        LedCount = 3,
        Serial = "MB01",
        Zones = new()
        {
            new RgbZone { Name = "D_LED1", ZoneType = 1, LedCount = 1 },
            new RgbZone { Name = "D_LED2", ZoneType = 1, LedCount = 2 },
        },
    };
}
