using Nexus.Service.Conflicts;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class SelfSavingControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "self-saving-" + Guid.NewGuid().ToString("N")[..8]);

    public SelfSavingControllerTests() => Directory.CreateDirectory(_dir);

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

    private static RgbMode Mode(string name, uint flags) => new() { Name = name, Flags = flags, ColorMode = 1 };

    private static RgbDevice Board(params RgbMode[] modes) => new()
    {
        Address = 4,
        Name = "ASRock X570 Taichi",
        Type = 0,
        LedCount = 23,
        Serial = "TAICHI",
        Modes = modes.ToList(),
        Zones = new()
        {
            new RgbZone { Name = "PCH", ZoneType = 1, LedCount = 1, LedsMin = 1, LedsMax = 1, Flags = RgbZone.FlagConfigurableSize },
            new RgbZone { Name = "RGB LED Header", ZoneType = 1, LedCount = 10, LedsMin = 10, LedsMax = 10, Flags = RgbZone.FlagConfigurableSize },
            new RgbZone { Name = "Addressable Header", ZoneType = 1, LedCount = 10, LedsMin = 1, LedsMax = 100, Flags = RgbZone.FlagConfigurableSize },
        },
    };

    private static RgbDevice Polychrome() => Board(
        Mode("Off", RgbMode.FlagAutomaticSave), Mode("Static", RgbMode.FlagAutomaticSave), Mode("Breathing", RgbMode.FlagAutomaticSave));

    [Fact]
    public void Detection_requires_no_direct_mode_and_automatic_save_on_every_mode()
    {
        Assert.True(Polychrome().PersistsEveryWrite);
        Assert.False(Board(Mode("Direct", RgbMode.FlagAutomaticSave), Mode("Static", RgbMode.FlagAutomaticSave)).PersistsEveryWrite);
        Assert.False(Board(Mode("Direct", 0), Mode("Static", 0)).PersistsEveryWrite);
        Assert.False(Board(Mode("Static", RgbMode.FlagAutomaticSave), Mode("Breathing", 0)).PersistsEveryWrite);
        Assert.False(Board().PersistsEveryWrite);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(6u)]
    public void Parser_reads_mode_flags(uint protocol)
    {
        var body = new List<byte>();
        void U16(int v) { body.Add((byte)(v & 0xFF)); body.Add((byte)((v >> 8) & 0xFF)); }
        void U32(uint v) { for (int s = 0; s < 32; s += 8) body.Add((byte)((v >> s) & 0xFF)); }
        void BStr(string s) { U16(s.Length + 1); body.AddRange(System.Text.Encoding.ASCII.GetBytes(s)); body.Add(0); }
        U32(0);
        U32(0);
        BStr("B");
        BStr("");
        BStr("");
        BStr("");
        BStr("");
        BStr("");
        U16(1);
        U32(0);
        BStr("Static");
        if (protocol < 6)
        {
            U32(0);
        }
        U32(RgbMode.FlagAutomaticSave | 1u);
        for (int i = 0; i < 9; i++) { U32(0); }
        U32(2);
        U16(0);
        U16(0);
        U16(0);

        var dev = OpenRgbProtocol.ParseControllerData(0, body.ToArray(), protocol);

        Assert.Equal(RgbMode.FlagAutomaticSave | 1u, Assert.Single(dev.Modes).Flags);
        Assert.Equal(2u, dev.Modes[0].ColorMode);
        Assert.True(dev.PersistsEveryWrite);
    }

    [Fact]
    public void Self_saving_controller_skips_identical_frames_past_the_heartbeat()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 15, 1000, persistsEveryWrite: true));
        Assert.False(bridge.ShouldPush(0, buf, 15, 2500, persistsEveryWrite: true));
        Assert.False(bridge.ShouldPush(0, buf, 15, 60_000, persistsEveryWrite: true));

        buf[0] = new RgbColor(2, 0, 0);
        Assert.True(bridge.ShouldPush(0, buf, 15, 60_100, persistsEveryWrite: true));
    }

    [Fact]
    public void Self_saving_controller_respects_its_cap_and_delivers_the_newest_frame_later()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 15, 1000, persistsEveryWrite: true));

        buf[0] = new RgbColor(2, 0, 0);
        Assert.False(bridge.ShouldPush(0, buf, 15, 1033, persistsEveryWrite: true));
        Assert.True(bridge.ShouldPush(0, buf, 15, 1070, persistsEveryWrite: true));
    }

    [Fact]
    public void Normal_device_still_gets_its_heartbeat()
    {
        using var bridge = NewBridge();
        var buf = new RgbColor[] { new(1, 0, 0) };
        Assert.True(bridge.ShouldPush(0, buf, 0, 1000));
        Assert.False(bridge.ShouldPush(0, buf, 0, 1500));
        Assert.True(bridge.ShouldPush(0, buf, 0, 2000));
    }

    [Fact]
    public void Self_saving_physical_devices_get_the_default_cap_unless_the_user_set_one()
    {
        var frames = new[]
        {
            new DeviceFrame(0, "openrgb-s-TAICHI-0", 4, physicalIndex: 4),
            new DeviceFrame(1, "openrgb-s-OTHER", 4, physicalIndex: 5),
            new DeviceFrame(2, "openrgb-s-USER", 4, physicalIndex: 6),
        };
        var bridgeIds = new HashSet<string> { "openrgb-s-TAICHI-0", "openrgb-s-OTHER", "openrgb-s-USER" };
        var stable = new Dictionary<int, string> { [4] = "openrgb-s-TAICHI", [5] = "openrgb-s-OTHER", [6] = "openrgb-s-USER" };
        var caps = new Dictionary<int, int>();

        RgbBridge.ComputePhysicalCaps(frames, bridgeIds, stable, new Dictionary<string, int>(), caps, new HashSet<int> { 4 });
        Assert.Equal(new Dictionary<int, int> { [4] = RgbDevice.SelfSavingDefaultCapHz }, caps);

        RgbBridge.ComputePhysicalCaps(frames, bridgeIds, stable, new Dictionary<string, int> { ["openrgb-s-TAICHI"] = 5 }, caps, new HashSet<int> { 4, 6 });
        Assert.Equal(new Dictionary<int, int> { [4] = 5, [6] = RgbDevice.SelfSavingDefaultCapHz }, caps);
    }

    [Fact]
    public void Fixed_zones_of_a_self_saving_controller_are_not_resizable_and_ignore_a_saved_count()
    {
        var dev = Polychrome();
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[$"{dev.StableId}-1"] = 8;
        settings.Devices.ZoneLedCounts[$"{dev.StableId}-2"] = 20;

        var structure = OpenRgbZoneSupport.BuildStructure(dev, settings);

        Assert.False(structure.Segments[0].Resizable);
        Assert.False(structure.Segments[1].Resizable);
        Assert.Equal(10, structure.Segments[1].LedCount);
        Assert.True(structure.Segments[2].Resizable);
        Assert.Equal(20, structure.Segments[2].LedCount);
    }

    [Fact]
    public void Fixed_zones_of_other_controllers_keep_resize_and_saved_counts()
    {
        var dev = Board(Mode("Direct", 0), Mode("Static", 0));
        var settings = new NexusSettings();
        settings.Devices.ZoneLedCounts[$"{dev.StableId}-1"] = 8;

        var structure = OpenRgbZoneSupport.BuildStructure(dev, settings);

        Assert.True(structure.Segments[1].Resizable);
        Assert.Equal(8, structure.Segments[1].LedCount);
    }

    private static IEnumerable<RgbDevice> One(RgbDevice d) => new[] { d };

    private (OpenRgbLightingDeviceProvider Provider, TestableConfigStore Store) ProviderFor(RgbDevice dev)
    {
        var store = new TestableConfigStore(Path.Combine(_dir, "provider-settings.json"));
        var bridge = NewBridge();
        typeof(RgbBridge).GetField("_devices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(bridge, One(dev).ToList());
        return (new OpenRgbLightingDeviceProvider(bridge, store), store);
    }

    [Fact]
    public void Resize_route_refuses_a_fixed_zone_of_a_self_saving_controller_but_not_other_zones()
    {
        var dev = Polychrome();
        var (provider, store) = ProviderFor(dev);

        provider.SetZoneLedCount($"{dev.StableId}-1", 8);
        Assert.False(store.Load().Devices.ZoneLedCounts.ContainsKey($"{dev.StableId}-1"));

        provider.SetZoneLedCount($"{dev.StableId}-2", 20);
        Assert.Equal(20, store.Load().Devices.ZoneLedCounts[$"{dev.StableId}-2"]);
    }

    [Fact]
    public void Resize_route_still_saves_a_fixed_zone_of_another_controller()
    {
        var dev = Board(Mode("Direct", 0));
        var (provider, store) = ProviderFor(dev);

        provider.SetZoneLedCount($"{dev.StableId}-1", 8);
        Assert.Equal(8, store.Load().Devices.ZoneLedCounts[$"{dev.StableId}-1"]);
    }

    [Fact]
    public void Refreshes_do_not_repush_an_unchanged_self_saving_controller_and_a_layout_change_does()
    {
        using var bridge = NewBridge();
        var dev = Polychrome();
        var buf = new RgbColor[dev.LedCount];
        var devices = new[] { dev };

        bridge.SyncPhysicalBuffers(devices);
        Assert.True(bridge.ShouldPush(dev.Address, buf, 15, 1000, persistsEveryWrite: true));
        for (var i = 1; i <= 5; i++)
        {
            bridge.SyncPhysicalBuffers(new[] { Polychrome() });
            Assert.False(bridge.ShouldPush(dev.Address, buf, 15, 1000 + i * 3000, persistsEveryWrite: true));
        }

        var resized = Polychrome();
        resized.Zones[2].LedCount = 12;
        bridge.SyncPhysicalBuffers(new[] { resized });
        Assert.True(bridge.ShouldPush(dev.Address, buf, 15, 30_000, persistsEveryWrite: true));
    }

    [Fact]
    public void Refreshes_still_reset_the_baseline_of_a_normal_device()
    {
        using var bridge = NewBridge();
        var dev = Board(Mode("Direct", 0));
        var buf = new RgbColor[dev.LedCount];

        bridge.SyncPhysicalBuffers(new[] { dev });
        Assert.True(bridge.ShouldPush(dev.Address, buf, 0, 1000));
        bridge.SyncPhysicalBuffers(new[] { dev });
        Assert.True(bridge.ShouldPush(dev.Address, buf, 0, 1100));
    }

    private static string Expand(string p) => p.Replace("%ProgramFiles(x86)%", "C:\\Program Files (x86)");

    [Fact]
    public void Polychrome_install_is_detected_from_its_exe_path()
    {
        var def = ConflictWatcher.FindById("asrock-polychrome")!;
        Assert.True(ConflictAppInstallProbe.HasInstallPath(def, Expand, p => p.EndsWith("AsrPolychromeRGB.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.False(ConflictAppInstallProbe.HasInstallPath(def, Expand, _ => false));
        Assert.False(ConflictAppInstallProbe.HasInstallPath(ConflictWatcher.FindById("icue")!, Expand, _ => true));
    }
}
