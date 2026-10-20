using System.Text.Json.Nodes;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

[CollectionDefinition("DetectorMap", DisableParallelization = true)]
public class DetectorMapCollection;

/// <summary>
/// An OpenRGB detector is held off only while a Nexus native driver is enabled for the same
/// hardware; every other detector keeps OpenRGB's own default (enabled).
/// </summary>
[Collection("DetectorMap")]
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

    private static readonly string[] AllClaimed = NativeDetectorClaims.All.SelectMany(c => c.Detectors).ToArray();

    private static List<string> Resolve(Func<string, bool> controlOn, bool macOS = false)
        => NativeDetectorClaims.ResolveDisabled(controlOn, macOS);

    [Fact]
    public void Control_off_holds_every_claimed_detector_off_on_every_os()
    {
        foreach (var macOS in new[] { false, true })
        {
            var held = Resolve(_ => false, macOS);
            foreach (var detector in AllClaimed)
            {
                Assert.Contains(detector, held);
            }
        }
    }

    [Fact]
    public void Control_on_with_a_native_driver_holds_the_detector_off()
    {
        var held = Resolve(_ => true);
        foreach (var detector in AllClaimed)
        {
            Assert.Contains(detector, held);
        }
    }

    [Fact]
    public void Only_the_handlers_that_are_on_matter_to_a_claim()
    {
        var held = Resolve(h => h == "keeb", macOS: true);
        Assert.Contains("HYTE Keeb TKL", held);
        Assert.Contains("Corsair iCUE Link System Hub", held);
    }

    [Theory]
    [InlineData("np50")]
    [InlineData("qseries")]
    public void Hyte_nexus_detector_is_on_only_where_control_is_on_and_no_native_driver_exists(string handler)
    {
        Assert.Contains("HYTE Nexus", Resolve(h => h == handler, macOS: false));
        Assert.DoesNotContain("HYTE Nexus", Resolve(h => h == handler, macOS: true));
        Assert.Contains("HYTE Nexus", Resolve(_ => false, macOS: true));
    }

    [Fact]
    public void The_original_uni_hub_detector_is_never_claimed()
    {
        Assert.DoesNotContain("Lian Li Uni Hub", Resolve(_ => false));
        Assert.DoesNotContain("Lian Li Uni Hub", Resolve(_ => true));
    }

    [Fact]
    public void Lamp_array_detector_is_always_held_off()
    {
        Assert.Contains("HID LampArray Device", Resolve(_ => true));
        Assert.Contains("HID LampArray Device", Resolve(_ => false));
        Assert.Contains("HID LampArray Device", Resolve(_ => true, macOS: true));
        Assert.Contains("HID LampArray Device", OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
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
            """{"Detectors":{"detectors":{"Nollie 8CH":false,"Unrelated":false}}}""");

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>(), null, Array.Empty<string>());

        var map = Map();
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
    public void Process_manager_holds_the_detector_off_with_control_on_or_off()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var proc = new OpenRgbProcessManager("missing-exe", store, gate);

        Assert.Contains("HYTE Keeb TKL", proc.ResolveNativeDisabledDetectors());
        gate.SetEnabled("keeb", false);
        Assert.Contains("HYTE Keeb TKL", proc.ResolveNativeDisabledDetectors());
    }

    [Fact]
    public void Without_a_gate_every_claimed_detector_stays_off()
    {
        var held = new OpenRgbProcessManager("missing-exe").ResolveNativeDisabledDetectors();
        foreach (var detector in AllClaimed)
        {
            Assert.Contains(detector, held);
        }
    }

    [Fact]
    public void A_locked_native_driver_keeps_its_detector_off_even_with_control_chosen_on_and_the_app_whitelisted()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.NexusControlEnabled = new List<string> { "lianli", "corsair" };
            s.Ui.ConflictAutoKillExclusions = new List<string> { "lian-li-l-connect", "icue" };
        });
        var gate = new DeviceControlGate(store);
        var proc = new OpenRgbProcessManager("missing-exe", store, gate);
        Nexus.Service.Devices.DeviceControlPolicy.ExperimentalUnlocked = false;
        try
        {
            var held = proc.ResolveNativeDisabledDetectors();
            Assert.Contains("Corsair iCUE Link System Hub", held);
            Assert.Contains("Lian Li Uni Hub - SL V2", held);
        }
        finally
        {
            Nexus.Service.Devices.DeviceControlPolicy.ExperimentalUnlocked = true;
        }
    }

    [Fact]
    public void A_vendor_app_pause_does_not_move_the_detector_set()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        var proc = new OpenRgbProcessManager("missing-exe", store, gate);
        gate.SetEnabled("corsair", true);
        var before = proc.ResolveNativeDisabledDetectors();

        gate.SetPausedByApp(new Dictionary<string, string> { ["corsair"] = "icue" });

        Assert.Equal(before, proc.ResolveNativeDisabledDetectors());
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

    // OpenRGB detectors that share a PID with a natively driven Kraken (NZXTHue2ControllerDetect.cpp).
    [Theory]
    [InlineData(0x3012, "NZXT Kraken 2024 ELITE Series RGB")]
    [InlineData(0x2007, "NZXT Kraken X3 Series")]
    [InlineData(0x2014, "NZXT Kraken X3 Series RGB")]
    public void Every_natively_lit_kraken_pid_with_an_openrgb_detector_is_claimed(int pid, string detector)
    {
        Assert.NotEqual(Nexus.Service.Peripherals.Nzxt.KrakenLightingProtocol.None, Nexus.Service.Peripherals.Nzxt.KrakenModel.Find(pid)!.Lighting);
        var map = new Dictionary<string, string> { ["Unit"] = detector };
        Assert.True(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("Unit"), map));
        Assert.Contains(detector, Resolve(_ => true));
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
    public void An_inverted_range_is_not_resizable()
    {
        Assert.False(Zone(1u << 1, min: 40, max: 34).IsResizable);
    }

    [Fact]
    public void A_remembered_detector_keeps_the_row_claimed_while_a_rescan_empties_the_map()
    {
        OpenRgbDetectorMap.OverrideForTests = null;
        OpenRgbDetectorMap.Remember(new Dictionary<string, string> { ["Held Kraken Unit"] = "NZXT Kraken 2024 ELITE Series RGB" });

        OpenRgbDetectorMap.EndHold();
        Assert.False(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("Held Kraken Unit")));

        OpenRgbDetectorMap.Remember(new Dictionary<string, string> { ["Held Kraken Unit"] = "NZXT Kraken 2024 ELITE Series RGB" });
        OpenRgbDetectorMap.BeginHold();
        try
        {
            Assert.True(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("Held Kraken Unit")));
            Assert.False(NativeDetectorClaims.Owns(NativeDetectorClaims.Kraken, Row("Never Mapped Unit")));
        }
        finally
        {
            OpenRgbDetectorMap.EndHold();
        }
    }

    [Fact]
    public void Resizes_wait_for_their_device_then_expire()
    {
        var now = DateTime.UtcNow.Ticks;
        var devices = new List<RgbDevice> { CommanderCore(3, "SERIAL-A") };
        var pending = new[]
        {
            new RgbBridge.PendingIdResize(devices[0].StableId, 1, 12, now),
            new RgbBridge.PendingIdResize("openrgb-s-SERIAL-B", 2, 12, now),
            new RgbBridge.PendingIdResize("openrgb-s-SERIAL-C", 2, 12, now - RgbBridge.PendingResizeLifetime.Ticks - 1),
        };
        var resolved = new List<(int physIdx, int zoneIdx, int newSize)>();
        var waiting = new List<RgbBridge.PendingIdResize>();
        var expired = new List<RgbBridge.PendingIdResize>();

        RgbBridge.PartitionPendingResizes(devices, pending, now, resolved, waiting, expired);

        Assert.Equal((3, 1, 12), Assert.Single(resolved));
        Assert.Equal("openrgb-s-SERIAL-B", Assert.Single(waiting).DeviceId);
        Assert.Equal("openrgb-s-SERIAL-C", Assert.Single(expired).DeviceId);
    }

    [Fact]
    public void Queued_resizes_for_one_zone_coalesce_to_the_newest()
    {
        var now = DateTime.UtcNow.Ticks;
        var devices = new List<RgbDevice> { CommanderCore(3, "SERIAL-A") };
        var id = devices[0].StableId;
        var pending = new[]
        {
            new RgbBridge.PendingIdResize(id, 1, 12, now),
            new RgbBridge.PendingIdResize(id, 1, 34, now + 5),
            new RgbBridge.PendingIdResize(id, 2, 8, now + 1),
        };
        var resolved = new List<(int physIdx, int zoneIdx, int newSize)>();

        RgbBridge.PartitionPendingResizes(devices, pending, now, resolved, new(), new());

        Assert.Equal(2, resolved.Count);
        Assert.Contains((3, 1, 34), resolved);
        Assert.Contains((3, 2, 8), resolved);
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

    [Fact]
    public void A_resize_counts_as_applied_only_when_the_reread_shows_the_new_count()
    {
        var d = CommanderCore(3, "SERIAL-A");
        d.Zones.Add(new RgbZone { Name = "Fan 1", LedCount = 12 });
        var devices = new List<RgbDevice> { d };

        var took = new[] { new RgbBridge.ZoneResizeAttempt(3, 0, "Fan 1", 0, 12, "queued") };
        var missed = new[] { new RgbBridge.ZoneResizeAttempt(3, 0, "Fan 1", 0, 20, "queued") };

        Assert.True(RgbBridge.AnyResizeTook(took, devices));
        Assert.False(RgbBridge.AnyResizeTook(missed, devices));
        Assert.False(RgbBridge.AnyResizeTook(Array.Empty<RgbBridge.ZoneResizeAttempt>(), devices));
    }

    [Fact]
    public void A_stale_end_does_not_clear_a_newer_hold()
    {
        var observed = OpenRgbDetectorMap.HoldGeneration;
        OpenRgbDetectorMap.BeginHold();
        try
        {
            OpenRgbDetectorMap.EndHold(observed);
            Assert.True(OpenRgbDetectorMap.HoldActive);
            OpenRgbDetectorMap.EndHold(OpenRgbDetectorMap.HoldGeneration);
            Assert.False(OpenRgbDetectorMap.HoldActive);

            var second = OpenRgbDetectorMap.BeginHold();
            Assert.True(second > observed);
            Assert.True(OpenRgbDetectorMap.HoldActive);
        }
        finally
        {
            OpenRgbDetectorMap.EndHold();
        }
    }
}
