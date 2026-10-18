using System.Text.Json.Nodes;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// An OpenRGB detector is held off only while a Nexus native driver is enabled for the same
/// hardware; every other detector keeps OpenRGB's own default (enabled).
/// </summary>
public class OpenRgbNativeDetectorPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openrgb-native-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonObject Map() => (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "OpenRGB.json")))!)["Detectors"]!["detectors"]!;

    private static bool IsOff(JsonObject map, string name) =>
        map[name] is JsonValue v && v.TryGetValue<bool>(out var b) && !b;

    [Fact]
    public void No_enabled_native_driver_holds_no_detector()
    {
        Assert.Empty(NativeDetectorClaims.ResolveDisabled(_ => false, macOS: false));
    }

    [Fact]
    public void Only_the_enabled_native_drivers_detectors_are_held()
    {
        var held = NativeDetectorClaims.ResolveDisabled(h => h == "keeb", macOS: false);
        Assert.Equal(new[] { "HYTE Keeb TKL" }, held);
    }

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli2")]
    [InlineData("lianli4")]
    public void Any_lian_li_hub_slot_holds_the_hid_hubs_but_never_the_original_uni_hub(string handler)
    {
        var held = NativeDetectorClaims.ResolveDisabled(h => h == handler, macOS: false);
        Assert.Contains("Lian Li Uni Hub - SL V2", held);
        Assert.DoesNotContain("Lian Li Uni Hub", held);
    }

    [Theory]
    [InlineData("np50")]
    [InlineData("qseries")]
    public void Hyte_nexus_detector_follows_either_native_driver_off_mac(string handler)
    {
        Assert.Contains("HYTE Nexus", NativeDetectorClaims.ResolveDisabled(h => h == handler, macOS: false));
        Assert.DoesNotContain("HYTE Nexus", NativeDetectorClaims.ResolveDisabled(h => h == handler, macOS: true));
    }

    [Fact]
    public void Lamp_array_detector_is_never_held()
    {
        Assert.DoesNotContain("HID LampArray Device", NativeDetectorClaims.ResolveDisabled(_ => true, macOS: false));
    }

    [Fact]
    public void Turning_a_native_driver_off_returns_its_detector_to_the_openrgb_default()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, new[] { "HYTE Keeb TKL" });
        Assert.True(IsOff(Map(), "HYTE Keeb TKL"));

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, Array.Empty<string>());
        Assert.False(Map().ContainsKey("HYTE Keeb TKL"));

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, new[] { "HYTE Keeb TKL" });
        Assert.True(IsOff(Map(), "HYTE Keeb TKL"));
    }

    [Fact]
    public void A_config_written_by_an_older_build_is_released_for_detectors_no_longer_held()
    {
        File.WriteAllText(Path.Combine(_dir, "OpenRGB.json"),
            """{"Detectors":{"detectors":{"HID LampArray Device":false,"Nollie 8CH":false,"Unrelated":false}}}""");

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, Array.Empty<string>());

        var map = Map();
        Assert.False(map.ContainsKey("HID LampArray Device"));
        Assert.False(map.ContainsKey("Nollie 8CH"));
        Assert.True(IsOff(map, "Unrelated"));
    }

    [Fact]
    public void A_user_exclusion_keeps_its_detector_off_when_the_native_driver_is_off()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Elgato Stream Deck MK.2" }, null, Array.Empty<string>());
        Assert.True(IsOff(Map(), "Elgato Stream Deck MK.2"));
    }

    [Fact]
    public void Unreadable_exclusions_leave_existing_overrides_alone()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, new[] { "HYTE Keeb TKL" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, null, null, Array.Empty<string>());
        Assert.True(IsOff(Map(), "HYTE Keeb TKL"));
    }

    [Fact]
    public void Held_off_mouse_defaults_are_not_released_by_the_native_cleanup()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair Katar Pro" }, null, Array.Empty<string>());
        Assert.True(IsOff(Map(), "Corsair Katar Pro"));
    }

    [Fact]
    public void Process_manager_resolves_from_the_users_choice_and_reports_toggles()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var proc = new OpenRgbProcessManager("missing-exe", store, gate);

        Assert.Contains("HYTE Keeb TKL", proc.ResolveNativeDisabledDetectors());
        gate.SetEnabled("keeb", false);
        Assert.DoesNotContain("HYTE Keeb TKL", proc.ResolveNativeDisabledDetectors());
        Assert.True(proc.NativeDetectorSetChanged());
    }

    [Fact]
    public void Native_handlers_are_the_ones_that_can_move_a_detector()
    {
        Assert.True(NativeDetectorClaims.AffectsDetectors("streamdeck"));
        Assert.True(NativeDetectorClaims.AffectsDetectors("lianli3"));
        Assert.False(NativeDetectorClaims.AffectsDetectors("cnvs"));
    }

    private static RgbDevice Row(string name) => new() { Name = name, Serial = "S" };

    [Fact]
    public void A_row_is_claimed_only_when_its_detector_belongs_to_the_native_driver()
    {
        var map = new Dictionary<string, string>
        {
            ["Lian Li Uni Hub SL V2"] = "Lian Li Uni Hub - SL V2",
            ["Lian Li Uni Hub"] = "Lian Li Uni Hub",
        };

        Assert.True(NativeDetectorClaims.Owns(NativeDetectorClaims.LianLi, Row("Lian Li Uni Hub SL V2"), map));
        Assert.False(NativeDetectorClaims.Owns(NativeDetectorClaims.LianLi, Row("Lian Li Uni Hub"), map));
    }

    [Fact]
    public void A_row_the_detector_map_does_not_name_is_never_claimed()
    {
        Assert.False(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("NZXT Kraken X63"), new Dictionary<string, string>()));
    }

    [Fact]
    public void Another_kraken_detector_is_not_claimed_by_the_2024_elite_driver()
    {
        var map = new Dictionary<string, string> { ["NZXT Kraken X3"] = "NZXT Kraken X3" };
        Assert.False(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("NZXT Kraken X3"), map));
    }

    [Fact]
    public void Detection_holds_through_a_long_silent_detector_only_after_a_seen_start()
    {
        var silent = TimeSpan.FromSeconds(20);
        Assert.True(RgbBridge.DetectionHolds(silent, startedSeen: true, connected: true));
        Assert.False(RgbBridge.DetectionHolds(silent, startedSeen: false, connected: true));
        Assert.False(RgbBridge.DetectionHolds(silent, startedSeen: true, connected: false));
        Assert.False(RgbBridge.DetectionHolds(TimeSpan.FromMinutes(5), startedSeen: true, connected: true));
        Assert.True(RgbBridge.DetectionHolds(TimeSpan.FromSeconds(1), startedSeen: false, connected: true));
    }

    private static RgbMode Mode(int index, string name, uint colorMode) => new() { Index = index, Name = name, ColorMode = colorMode };

    [Fact]
    public void Custom_mode_follows_upstream_name_priority_and_color_mode()
    {
        var d = new RgbDevice();
        d.Modes.Add(Mode(0, "Static", 1));
        d.Modes.Add(Mode(1, "Custom", 2));
        d.Modes.Add(Mode(2, "Direct", 1));
        Assert.Equal("Direct", d.FindCustomMode()!.Name);

        d.Modes.RemoveAt(2);
        Assert.Equal("Custom", d.FindCustomMode()!.Name);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    public void A_direct_mode_without_per_led_or_mode_specific_colors_is_skipped(uint colorMode)
    {
        var d = new RgbDevice();
        d.Modes.Add(Mode(0, "Direct", colorMode));
        d.Modes.Add(Mode(1, "Static", 2));
        Assert.Equal("Static", d.FindCustomMode()!.Name);
    }

    [Fact]
    public void No_custom_mode_yields_null()
    {
        var d = new RgbDevice();
        d.Modes.Add(Mode(0, "Rainbow", 3));
        Assert.Null(d.FindCustomMode());
    }

    private static RgbZone Zone(uint flags, uint min = 0, uint max = 34, int leds = 0) =>
        new() { Flags = flags, LedsMin = min, LedsMax = max, LedCount = leds };

    [Theory]
    [InlineData(1u << 1, true)]
    [InlineData(1u << 0, true)]
    [InlineData((1u << 1) | (1u << 12), false)]
    [InlineData(1u << 12, false)]
    [InlineData(0u, false)]
    public void Needs_size_mirrors_the_zone_initialization_rule(uint flags, bool needs)
    {
        Assert.Equal(needs, Zone(flags).NeedsSize);
    }

    [Fact]
    public void Needs_size_ignores_led_count_and_range()
    {
        Assert.True(Zone(1u << 1, min: 5, max: 5, leds: 12).NeedsSize);
    }

    [Fact]
    public void Resizable_needs_the_flag_and_a_range()
    {
        Assert.True(Zone(1u << 1, 0, 34).IsResizable);
        Assert.False(Zone(1u << 1, 29, 29, 29).IsResizable);
        Assert.False(Zone(0, 0, 34).IsResizable);
    }

    private static RgbDevice CommanderCore(int address, string serial) => new()
    {
        Address = address,
        Name = "Corsair Commander Core",
        Serial = serial,
        Location = "HID: " + serial,
    };

    [Fact]
    public void Two_same_model_units_keep_distinct_ids_and_a_resize_finds_only_its_own_unit()
    {
        var a = CommanderCore(0, "SERIAL-A");
        var b = CommanderCore(1, "SERIAL-B");
        Assert.NotEqual(a.StableId, b.StableId);

        var list = new List<RgbDevice> { a, b };
        Assert.Equal(1, RgbBridge.AddressOfDevice(list, b.StableId));

        // A restart reorders and renumbers the daemon's list; the id still finds the same unit.
        var restarted = new List<RgbDevice> { CommanderCore(0, "SERIAL-B"), CommanderCore(1, "SERIAL-A") };
        Assert.Equal(0, RgbBridge.AddressOfDevice(restarted, b.StableId));
        Assert.Equal(-1, RgbBridge.AddressOfDevice(new List<RgbDevice> { CommanderCore(0, "SERIAL-A") }, b.StableId));
    }
}
