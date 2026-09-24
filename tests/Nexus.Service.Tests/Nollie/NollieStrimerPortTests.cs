using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Nollie;

/// <summary>
/// The 32-channel board's two Strimer connectors: one card each, pre-wired
/// with the Lian Li cable the connector takes, and cut into per-strip lanes
/// on the wire, sized per the vendor's own OpenRGB guidance for those channels.
/// </summary>
public class NollieStrimerPortTests
{
    private const int Vid = 0x16D5;
    private const int Pid = 0x2A32;

    private readonly NollieHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly NollieLightingDeviceProvider _provider;

    public NollieStrimerPortTests()
    {
        _provider = new NollieLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
    }

    private static NollieLightingDeviceProviderTests.FakeHidDevice NewDevice(string serial)
        => new(Vid, Pid, $"path-{serial}", serial);

    private NollieController Attach(NollieLightingDeviceProviderTests.FakeHidDevice device)
    {
        var controller = new NollieController(device, NollieProtocol.Lookup(Vid, Pid)!);
        _hub.Attach(controller);
        return controller;
    }

    private NollieConnectionWorker NewWorker(IHidEnumerator hid)
        => new(hid, _hub, _provider, new DeviceControlGate(_store), _store, new HardwarePresence(new NoUsb()));

    // ── Seeding at attach ──

    [Fact]
    public void Attach_pre_wires_each_strimer_port_with_its_lian_li_cable()
    {
        var device = NewDevice("SEED");
        var worker = NewWorker(new OneDevice(device));

        Assert.True(worker.Reconcile());

        var controller = Assert.Single(_hub.Controllers);
        var s = _store.Load().Devices;
        var atx = $"{controller.DeviceId}:strimer-atx";
        var gpu = $"{controller.DeviceId}:strimer-gpu";

        var atxChain = Assert.Single(s.PortChains[ZoneResolution.ChainKey(atx, 0)]);
        Assert.Equal(NollieProtocol.StrimerAtxProductKey, atxChain.Key);
        Assert.Equal(120, atxChain.LedCount);
        Assert.Equal(120, s.ZoneLedCounts[atx]);
        var atxZone = Assert.Single(s.ZonePartitions[atx]);
        Assert.Equal("Lian Li ATX 24 Pin Strimer", atxZone.Name);
        Assert.Equal(120, Assert.Single(atxZone.Slices).Count);
        Assert.Equal(NollieProtocol.StrimerAtxProductKey, s.AppliedMappings[ZoneResolution.CustomZoneId(atx, 0)].MappingId);

        var gpuChain = Assert.Single(s.PortChains[ZoneResolution.ChainKey(gpu, 0)]);
        Assert.Equal(NollieProtocol.StrimerGpuProductKey, gpuChain.Key);
        Assert.Equal(162, gpuChain.LedCount);
        Assert.Equal(162, s.ZoneLedCounts[gpu]);
        Assert.Equal(NollieProtocol.StrimerGpuProductKey, s.AppliedMappings[ZoneResolution.CustomZoneId(gpu, 0)].MappingId);

        // The plain headers and EXT channels keep the bare 60-LED seed, and
        // the bundled channels get no entry of their own.
        Assert.Equal(60, s.ZoneLedCounts[$"{controller.DeviceId}:ch0"]);
        Assert.Equal(60, s.ZoneLedCounts[$"{controller.DeviceId}:ch31"]);
        Assert.False(s.ZoneLedCounts.ContainsKey($"{controller.DeviceId}:ch16"));
        Assert.False(s.ZoneLedCounts.ContainsKey($"{controller.DeviceId}:ch22"));
        Assert.Equal(22, s.ZoneLedCounts.Count);
    }

    /// <summary>The seeded chain is a real one: the card is the product, sized by it, and the editor can swap it.</summary>
    [Fact]
    public void A_pre_wired_port_lists_as_the_cable()
    {
        var device = NewDevice("CARD");
        NewWorker(new OneDevice(device)).Reconcile();
        var controller = Assert.Single(_hub.Controllers);

        var cards = _provider.GetAll().Devices;
        Assert.Equal(22, cards.Count);
        var atx = cards.Single(d => d.Id == ZoneResolution.CustomZoneId($"{controller.DeviceId}:strimer-atx", 0));
        Assert.Equal("Nollie 32_OS2_1 - Strimer ATX - Lian Li ATX 24 Pin Strimer", atx.Name);
        Assert.Equal(120, atx.LedCount);
        // Sized by the product, so no free resize; the chain editor is the way to change it.
        Assert.False(atx.ZoneResizable);
        var gpu = cards.Single(d => d.Id == ZoneResolution.CustomZoneId($"{controller.DeviceId}:strimer-gpu", 0));
        Assert.Equal("Nollie 32_OS2_1 - Strimer GPU - Lian Li GPU Triple 8 Pin Strimers", gpu.Name);
        Assert.Equal(162, gpu.LedCount);
    }

    /// <summary>A port the user set, cleared or emptied keeps that across replugs; the seed only fills what was never configured.</summary>
    [Fact]
    public void Reattach_leaves_a_configured_port_alone()
    {
        var device = NewDevice("KEEP");
        var enumerator = new OneDevice(device);
        NewWorker(enumerator).Reconcile();
        var controller = Assert.Single(_hub.Controllers);
        var atx = $"{controller.DeviceId}:strimer-atx";
        var gpu = $"{controller.DeviceId}:strimer-gpu";

        // Cleared chain on ATX (as the editor's empty-chain save leaves it), a
        // hand-set count on GPU.
        _store.Update(s =>
        {
            s.Devices.PortChains.Remove(ZoneResolution.ChainKey(atx, 0));
            s.Devices.ZonePartitions.Remove(atx);
            s.Devices.ZoneLedCounts[atx] = 0;
            s.Devices.ZoneLedCounts[gpu] = 108;
        });

        _hub.Detach(controller.DeviceId);
        enumerator.Device = NewDevice("KEEP");
        Assert.True(NewWorker(enumerator).Reconcile());

        var d = _store.Load().Devices;
        Assert.Equal(0, d.ZoneLedCounts[atx]);
        Assert.False(d.PortChains.ContainsKey(ZoneResolution.ChainKey(atx, 0)));
        Assert.Equal(108, d.ZoneLedCounts[gpu]);
    }

    // ── Dev-tools simulated board ──

    /// <summary>A simulated board goes through the same attach path and outlives a bus scan that never sees it.</summary>
    [Fact]
    public void A_simulated_board_seeds_like_a_real_one_and_survives_a_rescan()
    {
        var worker = NewWorker(new NoDevices());
        Assert.True(worker.AttachSimulated(Vid, Pid));
        Assert.True(worker.AttachSimulated(Vid, Pid));
        var controller = Assert.Single(_hub.Controllers);
        Assert.StartsWith(SimulatedNollieDevice.PathPrefix, controller.Path, StringComparison.Ordinal);
        Assert.Equal(120, _store.Load().Devices.ZoneLedCounts[$"{controller.DeviceId}:strimer-atx"]);
        Assert.Equal(22, _provider.GetAll().Devices.Count);

        Assert.False(worker.Reconcile());
        Assert.Single(_hub.Controllers);

        // Edits made against the simulated board leave with it.
        _store.Update(s => s.Devices.Nollie.Standalone[controller.DeviceId] = new NollieStandaloneSettings { Color = "#123456" });
        worker.DetachSimulated();
        Assert.Empty(_hub.Controllers);
        var d = _store.Load().Devices;
        Assert.DoesNotContain(d.ZoneLedCounts.Keys, k => k.StartsWith(controller.DeviceId, StringComparison.Ordinal));
        Assert.DoesNotContain(d.PortChains.Keys, k => k.StartsWith(controller.DeviceId, StringComparison.Ordinal));
        Assert.DoesNotContain(d.ZonePartitions.Keys, k => k.StartsWith(controller.DeviceId, StringComparison.Ordinal));
        Assert.DoesNotContain(d.AppliedMappings.Keys, k => k.StartsWith(controller.DeviceId, StringComparison.Ordinal));
        Assert.False(d.Nollie.Standalone.ContainsKey(controller.DeviceId));
        Assert.False(worker.AttachSimulated(0x1234, 0x5678));
    }

    // ── PortChainWriter ──

    [Fact]
    public void WireProduct_rejects_an_unknown_key_or_a_cable_longer_than_the_port()
    {
        var settings = new NexusSettings();
        Assert.False(PortChainWriter.WireProduct(settings, "x:strimer-atx", "product:nobody-makes-this", 120));
        Assert.False(PortChainWriter.WireProduct(settings, "x:strimer-atx", NollieProtocol.StrimerGpuProductKey, 120));
        Assert.Empty(settings.Devices.PortChains);
        Assert.Empty(settings.Devices.ZoneLedCounts);
        Assert.Empty(settings.Devices.ZonePartitions);
        Assert.Empty(settings.Devices.AppliedMappings);
    }

    // ── The wire ──

    /// <summary>
    /// The port buffer is the lanes back to back: LED 20k of the ATX cable is
    /// the first LED of strip k, on card 16 + k. With the dual 8-pin on the
    /// GPU port, lanes 5 and 6 send nothing.
    /// </summary>
    [Fact]
    public void Port_frames_are_cut_into_lanes_on_their_own_channels()
    {
        var device = NewDevice("WIRE");
        var controller = Attach(device);
        var atx = $"{controller.DeviceId}:strimer-atx";
        var gpu = $"{controller.DeviceId}:strimer-gpu";
        _store.Update(s =>
        {
            Assert.True(PortChainWriter.WireProduct(s, atx, NollieProtocol.StrimerAtxProductKey, 120));
            Assert.True(PortChainWriter.WireProduct(s, gpu, "product:lianli-lian-li-gpu-dual-8-pin-strimers", 162));
        });

        var engine = new LightingEngine();
        var frames = _provider.BuildFrames(0).ToArray();
        foreach (var frame in frames)
        {
            // R = LED index, G = 1 for ATX / 2 for GPU, so a report says
            // which cable and which LED it carries.
            var tag = frame.Id.StartsWith(atx, StringComparison.Ordinal) ? (byte)1
                : frame.Id.StartsWith(gpu, StringComparison.Ordinal) ? (byte)2 : (byte)0;
            for (var i = 0; i < frame.LedCount; i++) frame.SetLed(i, (byte)i, tag, 0);
            frame.Publish();
        }
        engine.UpdateDevices(frames);

        var writer = new NollieLightingFrameWriter(engine, _hub, _store, new Np50IdentifyTracker());
        writer.Tick();

        var spec = controller.Spec;
        var reports = device.Writes.Select(w => (Hw: (int)w[1], Count: w[3] * 256 + w[4], R0: w[6], G0: w[5])).ToList();

        // ATX lanes: cards 16..21, 20 LEDs each, lane k starting at LED 20k.
        for (var lane = 0; lane < 6; lane++)
        {
            var r = Assert.Single(reports, r => r.Hw == spec.HardwareChannel(16 + lane));
            Assert.Equal(20, r.Count);
            Assert.Equal((byte)(20 * lane), r.R0);
            Assert.Equal(1, r.G0);
        }
        // GPU dual 8-pin: four lanes of 27, the last two channels untouched.
        for (var lane = 0; lane < 4; lane++)
        {
            var r = Assert.Single(reports, r => r.Hw == spec.HardwareChannel(22 + lane));
            Assert.Equal(27, r.Count);
            Assert.Equal((byte)(27 * lane), r.R0);
            Assert.Equal(2, r.G0);
        }
        Assert.DoesNotContain(reports, r => r.Hw == spec.HardwareChannel(26));
        Assert.DoesNotContain(reports, r => r.Hw == spec.HardwareChannel(27));

        // Nothing else declared: only the wide transport's two flag channels
        // ride along, empty, and the whole batch is in hardware order.
        Assert.Equal(12, reports.Count);
        Assert.Equal(new[] { 15, 31 }, reports.Where(r => r.Count == 0).Select(r => r.Hw).OrderBy(h => h).ToArray());
        Assert.Equal(reports.Select(r => r.Hw).OrderBy(h => h).ToArray(), reports.Select(r => r.Hw).ToArray());
    }

    private sealed class NoUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    /// <summary>One 32-channel board on the bus, opened by path.</summary>
    private sealed class OneDevice : IHidEnumerator
    {
        public OneDevice(NollieLightingDeviceProviderTests.FakeHidDevice device) { Device = device; }
        public NollieLightingDeviceProviderTests.FakeHidDevice Device { get; set; }

        private HidDeviceInfo Info => new()
        {
            VendorId = Device.VendorId,
            ProductId = Device.ProductId,
            Path = Device.Path,
            Serial = Device.Serial,
            UsagePage = Device.UsagePage,
            Usage = Device.Usage,
            OutputReportByteLength = NollieProtocol.WideReportSize,
        };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
            => vendorId == Device.VendorId && productId == Device.ProductId ? new[] { Info } : Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[] { Info };
        public IHidDevice? Open(string path, bool forInput = false) => path == Device.Path ? Device : null;
    }
}
