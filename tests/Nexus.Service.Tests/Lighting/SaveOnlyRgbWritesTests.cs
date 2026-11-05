using System.Text;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting;

public class SaveOnlyRgbWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "save-only-" + Guid.NewGuid().ToString("N")[..8]);

    public SaveOnlyRgbWritesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private RgbBridge NewBridge() => new(
        new OpenRgbProcessManager(Path.Combine(_dir, "missing-openrgb")),
        new NullRgbController(),
        new Nexus.Service.Lighting.Engine.LightingEngine(),
        new TestableConfigStore(Path.Combine(_dir, "settings.json")),
        new StubUsbEnumerator());

    private const uint PerLed = RgbMode.FlagHasPerLedColor;
    private const uint Save = RgbMode.FlagAutomaticSave;

    private static RgbMode Mode(int index, string name, uint colorMode, uint flags) =>
        new() { Index = index, Name = name, ColorMode = colorMode, Flags = flags };

    private static RgbDevice Device(int address, string serial, string vendor, params RgbMode[] modes) => new()
    {
        Address = address,
        Name = "Dev " + serial,
        Vendor = vendor,
        Serial = serial,
        LedCount = 4,
        Modes = modes.ToList(),
    };

    [Fact]
    public void Save_only_static_frame_is_not_repushed_after_the_heartbeat_interval()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 2, 3) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000, saveOnly: true));
        Assert.False(bridge.ShouldPush(0, buf, 0, 2001, saveOnly: true));
        Assert.False(bridge.ShouldPush(0, buf, 0, 600_000, saveOnly: true));
    }

    [Fact]
    public void Save_only_changed_bytes_still_push()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 2, 3) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000, saveOnly: true));
        buf[0] = new RgbColor(9, 2, 3);
        Assert.True(bridge.ShouldPush(0, buf, 0, 1001, saveOnly: true));
        Assert.False(bridge.ShouldPush(0, buf, 0, 5000, saveOnly: true));
    }

    [Fact]
    public void Non_save_only_keeps_the_one_second_heartbeat()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 2, 3) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000));
        Assert.False(bridge.ShouldPush(0, buf, 0, 1999));
        Assert.True(bridge.ShouldPush(0, buf, 0, 2000));
    }

    [Fact]
    public void Device_reshuffle_clears_the_save_only_baseline()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 2, 3) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000, saveOnly: true));
        Assert.False(bridge.ShouldPush(0, buf, 0, 1500, saveOnly: true));

        bridge.SyncPhysicalBuffers(Array.Empty<RgbDevice>());
        Assert.True(bridge.ShouldPush(0, buf, 0, 1501, saveOnly: true));
    }

    [Fact]
    public void IsSaveOnly_true_when_the_only_per_led_mode_saves()
    {
        Assert.True(new RgbDevice { Modes = { Mode(0, "Custom", 1, PerLed | Save), Mode(1, "Wave", 0, Save) } }.IsSaveOnly);
    }

    [Fact]
    public void IsSaveOnly_false_when_a_direct_mode_does_not_save()
    {
        Assert.False(new RgbDevice { Modes = { Mode(0, "Direct", 1, PerLed), Mode(1, "Custom", 1, PerLed | Save) } }.IsSaveOnly);
    }

    [Fact]
    public void IsSaveOnly_false_when_a_non_driven_per_led_mode_does_not_save()
    {
        Assert.False(new RgbDevice { Modes = { Mode(0, "Custom", 1, PerLed | Save), Mode(1, "Other", 1, PerLed) } }.IsSaveOnly);
    }

    [Fact]
    public void IsSaveOnly_false_when_per_led_color_mode_lacks_the_flag_bit()
    {
        Assert.False(new RgbDevice { Modes = { Mode(0, "Custom", 1, PerLed | Save), Mode(1, "Other", 1, 0) } }.IsSaveOnly);
    }

    [Fact]
    public void IsSaveOnly_is_false_without_a_per_led_mode()
    {
        Assert.False(new RgbDevice().IsSaveOnly);
        Assert.False(new RgbDevice { Modes = { Mode(0, "Rainbow", 0, Save) } }.IsSaveOnly);
    }

    [Fact]
    public void Sync_registers_save_only_devices_and_caps_only_the_evision_family()
    {
        using var bridge = NewBridge();
        var evision = Device(0, "EV", "EVision", Mode(0, "Custom", 1, PerLed | Save));
        var otherSaveOnly = Device(1, "AK", "Akko", Mode(0, "Custom", 1, PerLed | Save));
        var direct = Device(2, "DR", "EVision", Mode(0, "Direct", 1, PerLed));

        bridge.SyncPhysicalBuffers(new[] { evision, otherSaveOnly, direct });

        Assert.True(bridge.IsSaveOnlyPhysical(0));
        Assert.True(bridge.IsSaveOnlyPhysical(1));
        Assert.False(bridge.IsSaveOnlyPhysical(2));
        var rates = bridge.DefaultRatesHz;
        Assert.Equal(RgbBridge.EVisionDefaultCapHz, rates[evision.StableId]);
        Assert.False(rates.ContainsKey(otherSaveOnly.StableId));
        Assert.False(rates.ContainsKey(direct.StableId));
    }

    [Fact]
    public void Default_cap_applies_and_a_user_cap_wins()
    {
        var frames = new[]
        {
            new Nexus.Service.Lighting.Engine.DeviceFrame(0, "openrgb-s-EV", 4, physicalIndex: 0),
            new Nexus.Service.Lighting.Engine.DeviceFrame(1, "openrgb-s-OTHER", 4, physicalIndex: 1),
        };
        var bridgeIds = new HashSet<string> { "openrgb-s-EV", "openrgb-s-OTHER" };
        var stable = new Dictionary<int, string> { [0] = "openrgb-s-EV", [1] = "openrgb-s-OTHER" };
        var defaults = new Dictionary<string, int> { ["openrgb-s-EV"] = 2 };
        var caps = new Dictionary<int, int>();

        RgbBridge.ComputePhysicalCaps(frames, bridgeIds, stable, new Dictionary<string, int>(), caps, defaults);
        Assert.Equal(new Dictionary<int, int> { [0] = 2 }, caps);

        RgbBridge.ComputePhysicalCaps(frames, bridgeIds, stable, new Dictionary<string, int> { ["openrgb-s-EV"] = 15 }, caps, defaults);
        Assert.Equal(new Dictionary<int, int> { [0] = 15 }, caps);
    }

    [Fact]
    public void Evision_cap_limits_a_changing_frame_to_the_default_rate()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, RgbBridge.EVisionDefaultCapHz, 1000, saveOnly: true));
        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, RgbBridge.EVisionDefaultCapHz, 1100, saveOnly: true));
        Assert.True(bridge.ShouldPush(0, buf, RgbBridge.EVisionDefaultCapHz, 1500, saveOnly: true));
    }

    [Fact]
    public void ParseControllerData_reads_mode_flags_at_v4_and_v6()
    {
        foreach (var version in new uint[] { 4, 6 })
        {
            var body = new List<byte>();
            void U16(int v) { body.Add((byte)(v & 0xFF)); body.Add((byte)((v >> 8) & 0xFF)); }
            void U32(uint v) { for (int s = 0; s < 32; s += 8) body.Add((byte)((v >> s) & 0xFF)); }
            void BStr(string s) { U16(s.Length + 1); body.AddRange(Encoding.ASCII.GetBytes(s)); body.Add(0); }

            U32(0); U32(5); BStr("K"); BStr("EVision"); BStr(""); BStr(""); BStr(""); BStr("");
            U16(1); U32(0);
            BStr("Custom");
            if (version < 6) { U32(0); }   // value, dropped at v6
            U32(PerLed | Save | (1u << 4)); // flags
            U32(0); U32(0);                // speed min/max
            U32(0); U32(255);              // brightness min/max
            U32(0); U32(0);                // colors min/max
            U32(0); U32(255);              // speed, brightness
            U32(0); U32(1);                // direction, color_mode
            U16(0);                        // mode colors
            U16(0); U16(0);                // zones, leds

            var dev = OpenRgbProtocol.ParseControllerData(0, body.ToArray(), version);

            var mode = Assert.Single(dev.Modes);
            Assert.Equal(PerLed | Save | (1u << 4), mode.Flags);
            Assert.Equal(1u, mode.ColorMode);
            Assert.True(dev.IsSaveOnly);
            Assert.True(dev.IsEVisionKeyboard);
        }
    }

    [Fact]
    public void IsSaveOnly_false_for_a_saving_mode_specific_mode_without_per_led_color()
    {
        Assert.False(new RgbDevice { Modes = { Mode(0, "Static", 2, Save) } }.IsSaveOnly);
    }

    [Fact]
    public void Returning_to_control_forgets_the_baseline_so_the_first_frame_pushes()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 2, 3) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000, saveOnly: true));
        Assert.False(bridge.ShouldPush(0, buf, 0, 2000, saveOnly: true));

        bridge.ForgetBaseline(0);
        Assert.True(bridge.ShouldPush(0, buf, 0, 2001, saveOnly: true));
    }

    [Fact]
    public void Cards_report_the_enforced_default_and_an_explicit_choice_wins()
    {
        var ev = Device(0, "EV", "EVision", Mode(0, "Custom", 1, PerLed | Save));
        var other = Device(1, "AK", "Akko", Mode(0, "Custom", 1, PerLed | Save));
        var settings = new Nexus.Service.Persistence.NexusSettings();

        var cards = OpenRgbZoneSupport.BuildCards(new[] { ev, other }, settings, isInit: true).Devices;
        Assert.Equal(2, cards.First(c => c.Id.StartsWith(ev.StableId)).MaxRateHz);
        Assert.Null(cards.First(c => c.Id.StartsWith(other.StableId)).MaxRateHz);

        settings.Devices.LightingMaxRates[ev.StableId] = 10;
        cards = OpenRgbZoneSupport.BuildCards(new[] { ev, other }, settings, isInit: true).Devices;
        Assert.Equal(10, cards.First(c => c.Id.StartsWith(ev.StableId)).MaxRateHz);
    }

    [Fact]
    public void Default_cap_rate_is_a_selectable_value()
    {
        Assert.True(Nexus.Service.Lighting.DeviceRateCap.IsValid(RgbDevice.EVisionDefaultCapHz));
    }
}
