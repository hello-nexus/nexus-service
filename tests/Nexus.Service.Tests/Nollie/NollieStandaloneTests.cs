using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Nollie;

/// <summary>
/// What a board runs once Nexus lets go: the settings report each firmware
/// family takes, the MOS bit that follows the GPU harness, and the hand-off
/// that ends colour writes.
/// </summary>
public class NollieStandaloneTests
{
    private readonly NollieHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly NollieLightingDeviceProvider _provider;

    public NollieStandaloneTests()
    {
        _provider = new NollieLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
    }

    private (NollieController Controller, NollieLightingDeviceProviderTests.FakeHidDevice Device) Attach(int vid, int pid, string serial)
    {
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(vid, pid, $"path-{serial}", serial);
        var controller = new NollieController(device, NollieProtocol.Lookup(vid, pid)!);
        _hub.Attach(controller);
        return (controller, device);
    }

    [Theory]
    [InlineData(0x3061, 0x4714, true, true, true)]
    [InlineData(0x3061, 0x4716, true, true, true)]
    [InlineData(0x16D2, 0x1F01, true, false, false)]
    [InlineData(0x16D2, 0x1F11, true, false, false)]
    [InlineData(0x16D2, 0x1616, false, false, false)]
    [InlineData(0x16D2, 0x1617, false, false, false)]
    [InlineData(0x16D2, 0x1618, false, false, false)]
    [InlineData(0x16D5, 0x4714, false, false, false)]
    [InlineData(0x16D5, 0x2A32, false, false, false)]
    [InlineData(0x16D5, 0x2A08, false, false, false)]
    public void Support_follows_the_firmware_family(int vid, int pid, bool standalone, bool builtIn, bool midStream)
    {
        var spec = NollieProtocol.Lookup(vid, pid)!;
        Assert.Equal(standalone, NollieProtocol.SupportsStandalone(spec));
        Assert.Equal(builtIn, NollieProtocol.SupportsBuiltInEffect(spec));
        Assert.Equal(midStream, NollieProtocol.TakesStandaloneMidStream(spec));
    }

    /// <summary>The 28-series shares the legacy VID but has no reference for the colour command, so it gets neither report.</summary>
    [Fact]
    public void Nollie_28_gets_nothing()
    {
        var (controller, device) = Attach(0x16D2, 0x1617, "TWENTYEIGHT");
        Assert.False(NollieStandalone.Apply(controller, _store.Load()));
        Assert.False(NollieStandalone.Release(controller, _store.Load()));
        Assert.Empty(device.Writes);
    }

    /// <summary>Legacy firmware takes the colour before the first frame and at the hand-off, never in between.</summary>
    [Fact]
    public void Legacy_firmware_is_not_refreshed_mid_stream()
    {
        var (controller, device) = Attach(0x16D2, 0x1F01, "LEGMID");
        Assert.False(NollieStandalone.Refresh(controller, _store.Load()));
        Assert.Empty(device.Writes);
        Assert.True(NollieStandalone.Apply(controller, _store.Load()));
        Assert.Single(device.Writes);
    }

    [Fact]
    public void Original_firmware_takes_one_settings_report_with_the_colour()
    {
        var (controller, device) = Attach(0x3061, 0x4714, "ORIG");
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Mode = "static", Color = "#12AB34" });

        Assert.True(NollieStandalone.Apply(controller, _store.Load()));

        var report = Assert.Single(device.Writes);
        Assert.Equal(NollieProtocol.WideReportSize, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x80, 0x00, 0x03, 0x12, 0xAB, 0x34, 0x00 }, report[..8]);
    }

    [Fact]
    public void Original_firmware_built_in_effect_replaces_the_static_byte()
    {
        var (controller, device) = Attach(0x3061, 0x4716, "ORIG16");
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Mode = "builtin", Color = "#FFFFFF" });

        NollieStandalone.Apply(controller, _store.Load());

        var report = Assert.Single(device.Writes);
        Assert.Equal(0x80, report[1]);
        Assert.Equal(0x01, report[3]);
    }

    [Fact]
    public void Legacy_firmware_takes_the_static_colour_on_its_command_family()
    {
        var (controller, device) = Attach(0x16D2, 0x1F01, "LEGACY8");
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Color = "#010203" });

        NollieStandalone.Apply(controller, _store.Load());

        var report = Assert.Single(device.Writes);
        Assert.Equal(NollieProtocol.ChunkedReportSize, report.Length);
        Assert.Equal(new byte[] { 0x00, 0xFE, 0x02, 0x00, 0x01, 0x02, 0x03, 0x64, 0x0A, 0x00, 0x01 }, report[..11]);
    }

    /// <summary>A "builtin" choice persisted against a board that lacks it falls back to the static colour rather than an unknown byte.</summary>
    [Fact]
    public void Legacy_firmware_ignores_a_built_in_choice()
    {
        var (controller, device) = Attach(0x16D2, 0x1F11, "LEGACY1");
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Mode = "builtin", Color = "#0A0B0C" });

        NollieStandalone.Apply(controller, _store.Load());

        var report = Assert.Single(device.Writes);
        Assert.Equal(0x02, report[2]);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, report[4..7]);
    }

    [Fact]
    public void Os2_firmware_gets_nothing()
    {
        var (controller, device) = Attach(0x16D5, 0x2A32, "OS2");
        Assert.False(NollieStandalone.Apply(controller, _store.Load()));
        Assert.False(NollieStandalone.Release(controller, _store.Load()));
        Assert.Empty(device.Writes);
        Assert.False(controller.IsReleased);
    }

    [Fact]
    public void Defaults_are_a_held_black()
    {
        var (controller, device) = Attach(0x3061, 0x4714, "DEFAULT");
        NollieStandalone.Apply(controller, _store.Load());
        var report = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x03, 0x00, 0x00, 0x00 }, report[1..7]);
    }

    /// <summary>The vendor driver sets MOS for the dual 8-pin harness only: a triple, or an empty port, clears it.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(27, true)]
    [InlineData(108, true)]
    [InlineData(109, false)]
    [InlineData(162, false)]
    public void Mos_follows_the_gpu_harness(int gpuLeds, bool mos)
    {
        var (controller, device) = Attach(0x3061, 0x4714, "MOS");
        var gpu = controller.Spec.Ports.Single(p => p.Slug == "strimer-gpu");
        _store.Update(s => s.Devices.ZoneLedCounts[NollieLightingDeviceProvider.PortId(controller.DeviceId, gpu)] = gpuLeds);

        NollieStandalone.Apply(controller, _store.Load());

        Assert.Equal(mos ? 1 : 0, Assert.Single(device.Writes)[2]);
    }

    /// <summary>The chain POST's follow-up re-sends the settings, so a swapped GPU cable flips MOS without a replug.</summary>
    [Fact]
    public void A_gpu_port_count_write_resends_the_settings()
    {
        var (controller, device) = Attach(0x3061, 0x4714, "RESEND");
        var gpu = controller.Spec.Ports.Single(p => p.Slug == "strimer-gpu");
        var id = NollieLightingDeviceProvider.PortId(controller.DeviceId, gpu);

        _store.Update(s => s.Devices.ZoneLedCounts[id] = 108);
        _provider.PushLedCountHandshakeFor(id);
        Assert.Equal(1, device.Writes[^1][2]);

        _provider.SetZoneLedCount(id, 162);
        Assert.Equal(0, device.Writes[^1][2]);
        Assert.All(device.Writes, w => Assert.Equal(0x80, w[1]));
    }

    [Fact]
    public void Release_sends_settings_then_the_hand_off_and_refuses_later_frames()
    {
        var (controller, device) = Attach(0x3061, 0x4714, "RELEASE");
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Color = "#FF0000" });

        Assert.True(NollieStandalone.Release(controller, _store.Load()));

        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x03, 0xFF, 0x00, 0x00 }, device.Writes[0][1..7]);
        Assert.Equal(0xFF, device.Writes[1][1]);
        Assert.Equal(0x00, device.Writes[1][2]);
        Assert.True(controller.IsReleased);

        Assert.False(controller.SendChannel(0, new byte[] { 1, 2, 3 }));
        Assert.False(NollieStandalone.Release(controller, _store.Load()));
        Assert.Equal(2, device.Writes.Count);
    }

    [Fact]
    public void Legacy_release_uses_its_own_hand_off_command()
    {
        var (controller, device) = Attach(0x16D2, 0x1F01, "LEGREL");
        NollieStandalone.Release(controller, _store.Load());
        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(new byte[] { 0xFE, 0x02 }, device.Writes[0][1..3]);
        Assert.Equal(new byte[] { 0xFE, 0x01, 0x00 }, device.Writes[1][1..4]);
        Assert.False(controller.SendLatch());
    }

    [Theory]
    [InlineData("#12ab34", 0x12, 0xAB, 0x34)]
    [InlineData("12AB34", 0x12, 0xAB, 0x34)]
    public void Hex_colours_parse_with_or_without_the_hash(string input, int r, int g, int b)
    {
        Assert.True(NollieStandalone.TryParseHexColor(input, out var pr, out var pg, out var pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void Bad_hex_colours_are_rejected(string input)
    {
        Assert.False(NollieStandalone.TryParseHexColor(input, out _, out _, out _));
    }

    // ── Worker paths ──

    private NollieConnectionWorker NewWorker(NollieLightingDeviceProviderTests.FakeHidDevice device, int reportSize)
        => new(new OneDevice(device, reportSize), _hub, _provider, new DeviceControlGate(_store), _store, new HardwarePresence(new NoUsb()));

    [Fact]
    public void Attach_applies_the_standalone_settings_after_seeding()
    {
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(0x3061, 0x4714, "path-ATTACH", "ATTACH");
        NewWorker(device, NollieProtocol.WideReportSize).Reconcile();

        var report = Assert.Single(device.Writes);
        Assert.Equal(0x80, report[1]);
        // The GPU port seeds as the triple harness, so MOS is clear.
        Assert.Equal(0x00, report[2]);
    }

    /// <summary>The path both Nexus Control off and the fast teardown take: settings, hand-off, handles dropped.</summary>
    [Fact]
    public void ReleaseAll_hands_every_board_over_and_empties_the_hub()
    {
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(0x3061, 0x4714, "path-BYE", "BYE");
        var worker = NewWorker(device, NollieProtocol.WideReportSize);
        worker.Reconcile();
        var controller = Assert.Single(_hub.Controllers);
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Mode = "builtin" });
        device.Writes.Clear();

        worker.ReleaseAllForShutdown();

        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x01 }, device.Writes[0][1..4]);
        Assert.Equal(0xFF, device.Writes[1][1]);
        Assert.True(controller.IsReleased);
        Assert.Empty(_hub.Controllers);
        Assert.False(_hub.IsConnected);

        // Running it again, as the shutdown set may, sends nothing more.
        worker.ReleaseAllForShutdown();
        Assert.Equal(2, device.Writes.Count);
    }

    /// <summary>
    /// A build that listed the Strimer channels one card each left state under
    /// per-channel ids; the first attach on the port model drops it, then seeds
    /// the port fresh.
    /// </summary>
    [Fact]
    public void Attach_drops_state_left_under_the_bundled_channel_ids()
    {
        var device = new NollieLightingDeviceProviderTests.FakeHidDevice(0x16D5, 0x2A32, "path-OLD", "OLD");
        var controller = new NollieController(device, NollieProtocol.Lookup(0x16D5, 0x2A32)!);
        var stale = $"{controller.DeviceId}:ch16";
        var staleZone = ZoneResolution.CustomZoneId(stale, 0);
        _store.Update(s =>
        {
            for (var ch = 16; ch < 28; ch++) s.Devices.ZoneLedCounts[$"{controller.DeviceId}:ch{ch}"] = 60;
            s.Devices.ZoneLedCounts[$"{controller.DeviceId}:ch0"] = 33;
            s.Devices.PortChains[ZoneResolution.ChainKey(stale, 0)] = new() { new ChainEntry { Key = "product:hyte-fr12", LedCount = 33 } };
            s.Devices.ZonePartitions[stale] = new() { new ZoneDef { Name = "FR12", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 33 } } } };
            s.Devices.LightingDevicePrefs[staleZone] = new LightingDevicePreference { Brightness = 40 };
            s.Devices.DisabledLightingDevices = new List<string> { staleZone };
            s.Lighting.DeviceNames[stale] = "old name";
        });

        NewWorker(device, NollieProtocol.WideReportSize).Reconcile();

        var d = _store.Load();
        for (var ch = 16; ch < 28; ch++) Assert.False(d.Devices.ZoneLedCounts.ContainsKey($"{controller.DeviceId}:ch{ch}"));
        Assert.Equal(33, d.Devices.ZoneLedCounts[$"{controller.DeviceId}:ch0"]);
        Assert.False(d.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(stale, 0)));
        Assert.False(d.Devices.ZonePartitions.ContainsKey(stale));
        Assert.False(d.Devices.LightingDevicePrefs.ContainsKey(staleZone));
        Assert.DoesNotContain(staleZone, d.Devices.DisabledLightingDevices);
        Assert.False(d.Lighting.DeviceNames.ContainsKey(stale));
        Assert.Equal(120, d.Devices.ZoneLedCounts[$"{controller.DeviceId}:strimer-atx"]);
    }

    private sealed class NoUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class OneDevice : IHidEnumerator
    {
        private readonly NollieLightingDeviceProviderTests.FakeHidDevice _device;
        private readonly int _reportSize;
        public OneDevice(NollieLightingDeviceProviderTests.FakeHidDevice device, int reportSize) { _device = device; _reportSize = reportSize; }

        private HidDeviceInfo Info => new()
        {
            VendorId = _device.VendorId,
            ProductId = _device.ProductId,
            Path = _device.Path,
            Serial = _device.Serial,
            UsagePage = _device.UsagePage,
            Usage = _device.Usage,
            OutputReportByteLength = _reportSize,
        };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
            => vendorId == _device.VendorId && productId == _device.ProductId ? new[] { Info } : Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[] { Info };
        public IHidDevice? Open(string path, bool forInput = false) => path == _device.Path ? _device : null;
    }
}
