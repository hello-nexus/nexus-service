using System.Text.Json.Nodes;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// OpenRGB.json detector-override writing: the hardcoded first-party denylist
/// always lands as detectors:false; user exclusions land as detectors:false
/// plus membership in the service-owned placeholder_only array; names dropped
/// from that array get their detector re-enabled on the next write.
/// </summary>
public class OpenRgbDetectorOverridesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openrgb-cfg-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonObject ReadConfig()
    {
        var text = File.ReadAllText(Path.Combine(_dir, "OpenRGB.json"));
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static bool DetectorEnabled(JsonObject root, string name)
    {
        var map = (JsonObject)root["Detectors"]!["detectors"]!;
        return map[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static List<string> PlaceholderOnly(JsonObject root)
    {
        var result = new List<string>();
        if (root["Detectors"]!["placeholder_only"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                result.Add(node!.GetValue<string>());
            }
        }
        return result;
    }

    [Fact]
    public void First_party_denylist_written_without_placeholders()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "HYTE Keeb TKL"));
        Assert.False(DetectorEnabled(root, "Corsair iCUE Link System Hub"));
        Assert.Empty(PlaceholderOnly(root));
    }

    [Fact]
    public void Hyte_nexus_detector_disabled_only_where_the_devices_are_driven_natively()
    {
        Assert.Contains("HYTE Nexus", OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.DoesNotContain("HYTE Nexus", OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
        Assert.Contains("HYTE Keeb TKL", OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    [Fact]
    public void Stream_deck_detector_stays_disabled_on_every_os()
    {
        Assert.Contains("Elgato Stream Deck MK.2", OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.Contains("Elgato Stream Deck MK.2", OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    private void SeedHold(params string[] names)
    {
        var map = new JsonObject();
        foreach (var n in names)
        {
            map[n] = false;
        }
        File.WriteAllText(Path.Combine(_dir, "OpenRGB.json"), new JsonObject { ["Detectors"] = new JsonObject { ["detectors"] = map } }.ToJsonString());
    }

    private const string Amd = "Sapphire Radeon RX 9070 XT Nitro+";
    private const string Nvidia = "MSI GeForce RTX 3070 Gaming Trio";

    [Fact]
    public void Lifting_the_amd_hold_releases_amd_and_stale_nvidia_entries_on_linux()
    {
        SeedHold(Amd, Nvidia, "HYTE Keeb TKL");
        var lifted = OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: false);

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), Array.Empty<string>(), lifted, linux: true);

        var root = ReadConfig();
        var map = (JsonObject)root["Detectors"]!["detectors"]!;
        Assert.False(map.ContainsKey(Amd));
        Assert.False(map.ContainsKey(Nvidia));
        Assert.False(DetectorEnabled(root, "HYTE Keeb TKL"));
    }

    [Fact]
    public void Amd_hold_is_written_and_stale_nvidia_released_when_the_setting_is_off()
    {
        SeedHold(Nvidia);
        var held = OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: true);

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), Array.Empty<string>(), held, linux: true);

        var map = (JsonObject)ReadConfig()["Detectors"]!["detectors"]!;
        Assert.False(map[Amd]!.GetValue<bool>());
        Assert.False(map.ContainsKey(Nvidia));
    }

    [Fact]
    public void User_excluded_gpu_detector_stays_disabled_when_the_hold_lifts()
    {
        SeedHold(Nvidia);
        var lifted = OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: false);

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { Nvidia }, Array.Empty<string>(), lifted, linux: true);

        Assert.False(DetectorEnabled(ReadConfig(), Nvidia));
    }

    [Fact]
    public void Off_linux_the_gpu_entries_are_left_alone()
    {
        SeedHold(Amd, Nvidia);
        var native = OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: false);

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), Array.Empty<string>(), native, linux: false);

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, Amd));
        Assert.False(DetectorEnabled(root, Nvidia));
    }

    [Theory]
    [InlineData("ASUS ROG STRIX Radeon RX 6600 XT Gaming OC")]
    [InlineData("ASUS TUF Radeon RX 9070 XT Gaming OC")]
    [InlineData("Sapphire Radeon RX 9070 XT Nitro+")]
    [InlineData("PowerColor Red Devil RX9070XT")]
    public void Amd_gpu_i2c_detectors_disabled_on_linux_only(string detector)
    {
        Assert.Contains(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: true));
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    [Theory]
    [InlineData("ASUS ROG STRIX GeForce RTX 4090 Gaming OC")]
    [InlineData("MSI GeForce RTX 3070 Gaming Trio")]
    public void Nvidia_gpu_detectors_are_never_force_disabled_on_linux(string detector)
    {
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: true));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Amd_hold_follows_the_setting_on_linux_and_ignores_a_stale_value_elsewhere(bool linux, bool setting, bool held)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.AmdGpuLighting = setting);

        Assert.Equal(held, OpenRgbProcessManager.HoldAmdGpuDetectorsOff(linux, store));
    }

    [Fact]
    public void Amd_hold_is_on_without_a_store()
    {
        Assert.True(OpenRgbProcessManager.HoldAmdGpuDetectorsOff(linux: true, store: null));
    }

    [Fact]
    public void Amd_gpu_i2c_detector_names_are_unique_and_keep_first_party_entries()
    {
        Assert.Equal(OpenRgbAmdGpuI2cDetectors.Names.Length, OpenRgbAmdGpuI2cDetectors.Names.Distinct().Count());
        Assert.DoesNotContain(OpenRgbAmdGpuI2cDetectors.Names, n => n.Contains("GeForce", StringComparison.Ordinal));
        Assert.Contains("HYTE Keeb TKL", OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: true));
    }

    [Theory]
    [InlineData("ASUS ROG STRIX GeForce RTX 4090 Gaming OC")]
    [InlineData("Gigabyte GeForce RTX 3080 Gaming OC")]
    [InlineData("MSI GeForce RTX 3070 Gaming Trio")]
    public void Nvidia_gpu_i2c_detectors_are_not_disabled_on_any_os(string detector)
    {
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false, linux: true));
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.DoesNotContain(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    [Theory]
    [InlineData("Lian Li Strimer L Connect")]
    [InlineData("Lian Li Uni Hub - SL")]
    [InlineData("Lian Li Uni Hub - AL")]
    [InlineData("Lian Li Uni Hub - SL V2")]
    [InlineData("Lian Li Uni Hub - AL V2")]
    [InlineData("Lian Li Uni Hub - SL V2 v0.5")]
    [InlineData("Lian Li Uni Hub - SL Infinity")]
    [InlineData("Lian Li GA II Trinity")]
    [InlineData("Lian Li GA II Trinity Performance")]
    public void Natively_driven_lian_li_detectors_stay_disabled_on_every_os(string detector)
    {
        Assert.Contains(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.Contains(detector, OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    [Fact]
    public void User_exclusion_lands_as_disabled_plus_placeholder_only()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Equal(new[] { "Corsair K70 RGB" }, PlaceholderOnly(root));
    }

    [Fact]
    public void Dropped_exclusion_reenables_the_detector()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.True(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Empty(PlaceholderOnly(root));
    }

    [Fact]
    public void Dropped_exclusion_never_reenables_a_first_party_detector()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair iCUE Link System Hub" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair iCUE Link System Hub"));
    }

    private static List<string> BusDisabled(JsonObject root)
    {
        var result = new List<string>();
        if (root["Detectors"]!["bus_disabled"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                result.Add(node!.GetValue<string>());
            }
        }
        return result;
    }

    [Fact]
    public void Smbus_dram_detectors_cover_every_name_the_forks_dimm_gate_reads()
    {
        // DetectionManager::IsAnyDimmDetectorEnabled runs the 0x50-0x57 SPD
        // scan while ANY REGISTER_I2C_DRAM_DETECTOR name is still enabled, so
        // one missing name leaves the whole scan running. These seven are that
        // set in the bundled fork; "Corsair Vengeance RGB DRAM" (DDR4) and
        // "Corsair DRAM" (DDR5) are different detectors, both needed.
        var gateNames = new[]
        {
            "Corsair Vengeance RGB DRAM", "HyperX DRAM", "Kingston Fury DDR4 DRAM",
            "Kingston Fury DDR5 DRAM", "Patriot Viper", "Patriot Viper Steel",
            "T-Force Xtreem DDR4 DRAM",
        };
        Assert.All(gateNames, name => Assert.Contains(name, OpenRgbProcessManager.SmbusDramDetectors));
        // Plain REGISTER_I2C_DETECTOR DRAM detectors probe DIMM addresses
        // outside that gate and are disabled too.
        Assert.All(new[] { "Corsair DRAM", "Crucial Ballistix", "ENE SMBus DRAM", "Gigabyte RGB Fusion 2 DRAM" },
            name => Assert.Contains(name, OpenRgbProcessManager.SmbusDramDetectors));
        // Motherboard and GPU detectors share the bus but are not this device.
        Assert.DoesNotContain("ASUS Aura SMBus Motherboard", OpenRgbProcessManager.SmbusDramDetectors);
        Assert.DoesNotContain("ASRock Motherboard SMBus Controllers", OpenRgbProcessManager.SmbusDramDetectors);
    }

    [Fact]
    public void Launch_disables_the_dram_detectors_exactly_while_the_device_is_off()
    {
        var gate = new Nexus.Service.Devices.DeviceControlGate(new InMemoryConfigStore());
        var proc = new OpenRgbProcessManager(overrideExePath: Path.Combine(_dir, "missing"), store: null, gate: gate);

        Assert.Empty(proc.ResolveBusDisabledDetectors());

        gate.SetEnabled(Nexus.Service.Devices.Handlers.SmbusDramHandler.HandlerId, false);
        Assert.Equal(OpenRgbProcessManager.SmbusDramDetectors, proc.ResolveBusDisabledDetectors());

        gate.SetEnabled(Nexus.Service.Devices.Handlers.SmbusDramHandler.HandlerId, true);
        Assert.Empty(proc.ResolveBusDisabledDetectors());
    }

    [Fact]
    public void Without_a_gate_no_detector_is_bus_disabled()
    {
        var proc = new OpenRgbProcessManager(overrideExePath: Path.Combine(_dir, "missing"));
        Assert.Empty(proc.ResolveBusDisabledDetectors());
    }

    [Fact]
    public void Bus_disabled_detectors_land_as_disabled_without_placeholders()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), OpenRgbProcessManager.SmbusDramDetectors);

        var root = ReadConfig();
        Assert.All(OpenRgbProcessManager.SmbusDramDetectors, name => Assert.False(DetectorEnabled(root, name)));
        Assert.Equal(OpenRgbProcessManager.SmbusDramDetectors, BusDisabled(root));
        Assert.Empty(PlaceholderOnly(root));
    }

    [Fact]
    public void Dropped_bus_disabled_detectors_reenable()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), OpenRgbProcessManager.SmbusDramDetectors);
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), Array.Empty<string>());

        var root = ReadConfig();
        Assert.All(OpenRgbProcessManager.SmbusDramDetectors, name => Assert.True(DetectorEnabled(root, name)));
        Assert.Empty(BusDisabled(root));
    }

    [Fact]
    public void Dropped_bus_disabled_detector_stays_off_while_a_user_exclusion_holds_it()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair DRAM" }, new[] { "Corsair DRAM" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair DRAM" }, Array.Empty<string>());

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair DRAM"));
        Assert.Equal(new[] { "Corsair DRAM" }, PlaceholderOnly(root));
    }

    [Fact]
    public void Null_bus_disabled_list_leaves_bus_state_untouched()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), new[] { "Corsair DRAM" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null);

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair DRAM"));
        Assert.Equal(new[] { "Corsair DRAM" }, BusDisabled(root));
    }

    [Fact]
    public void Null_placeholder_list_leaves_placeholder_state_untouched()
    {
        // Null = settings unreadable at launch; re-enabling detectors whose
        // exclusions still exist would hand the hardware back to OpenRGB while
        // the UI keeps showing it ignored.
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, null);

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Equal(new[] { "Corsair K70 RGB" }, PlaceholderOnly(root));
    }

    [Fact]
    public void Foreign_config_content_is_preserved()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "OpenRGB.json"),
            /*lang=json*/ """{"Detectors":{"detectors":{"Razer Huntsman":true},"hid_safe_mode":true},"Client":{"port":123}}""");

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });

        var root = ReadConfig();
        Assert.True(DetectorEnabled(root, "Razer Huntsman"));
        Assert.True(root["Detectors"]!["hid_safe_mode"]!.GetValue<bool>());
        Assert.Equal(123, root["Client"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void Unchanged_state_does_not_rewrite_the_file()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        var path = Path.Combine(_dir, "OpenRGB.json");
        var before = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, before.AddHours(-1));

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        Assert.Equal(before.AddHours(-1), File.GetLastWriteTimeUtc(path));
    }
}
