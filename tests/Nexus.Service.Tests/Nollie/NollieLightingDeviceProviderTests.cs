using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Nollie;

/// <summary>
/// Card emission and the LED-count declaration. The protocol reports no count,
/// so every channel stays resizable and the persisted user value is the only
/// source for how long a strip is.
/// </summary>
public class NollieLightingDeviceProviderTests
{
    private readonly NollieHub _hub = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly NollieLightingDeviceProvider _provider;

    public NollieLightingDeviceProviderTests()
    {
        _provider = new NollieLightingDeviceProvider(_hub, _store, new Np50IdentifyTracker());
    }

    /// <summary>Id of a single-channel port by card index, as the card list shows it.</summary>
    private static string PortId(string deviceId, int cardIndex) => $"{deviceId}:ch{cardIndex}";

    private NollieController Attach(int vid, int pid, string serial)
    {
        var spec = NollieProtocol.Lookup(vid, pid)!;
        var controller = new NollieController(new FakeHidDevice(vid, pid, $"path-{serial}", serial), spec);
        _hub.Attach(controller);
        return controller;
    }

    [Fact]
    public void GetAll_is_empty_with_nothing_attached()
    {
        Assert.Empty(_provider.GetAll().Devices);
        Assert.False(_provider.IsConnected);
    }

    [Fact]
    public void Emits_one_card_per_channel()
    {
        Attach(0x16D5, 0x2A16, "SIXTEEN");
        var devices = _provider.GetAll().Devices;
        Assert.Equal(16, devices.Count);
        Assert.Equal("Nollie 16_OS2_1 - Channel 1", devices[0].Name);
        Assert.Equal("Nollie 16_OS2_1 - Channel 16", devices[15].Name);
    }

    /// <summary>Four single-channel controllers is the shape the reporting user has.</summary>
    [Fact]
    public void Namespaces_cards_per_controller_so_identical_models_do_not_collide()
    {
        Attach(0x16D5, 0x2A01, "AAA");
        Attach(0x16D5, 0x2A01, "BBB");
        var ids = _provider.GetAll().Devices.Select(d => d.Id).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Contains("nollie-s-AAA:ch0", ids);
        Assert.Contains("nollie-s-BBB:ch0", ids);
    }

    /// <summary>A 32-channel board's 12 Strimer channels are two cards, one per connector, between the headers and the EXT channels.</summary>
    [Fact]
    public void A_thirty_two_channel_board_emits_two_strimer_cards()
    {
        var c = Attach(0x16D5, 0x2A32, "THIRTYTWO");
        var devices = _provider.GetAll().Devices;
        Assert.Equal(22, devices.Count);
        Assert.Equal("Nollie 32_OS2_1 - Channel 16", devices[15].Name);
        Assert.Equal("Nollie 32_OS2_1 - Strimer ATX", devices[16].Name);
        Assert.Equal($"{c.DeviceId}:strimer-atx", devices[16].Id);
        Assert.Equal("Nollie 32_OS2_1 - Strimer GPU", devices[17].Name);
        Assert.Equal($"{c.DeviceId}:strimer-gpu", devices[17].Id);
        Assert.Equal("Nollie 32_OS2_1 - Channel EXT 1", devices[18].Name);
        Assert.DoesNotContain(devices, d => d.Name.Contains("Channel ATX") || d.Name.Contains("Channel GPU"));
    }

    /// <summary>The port's ceiling is every lane full, not the per-channel driver limit.</summary>
    [Fact]
    public void Strimer_ports_clamp_to_their_lane_total()
    {
        var c = Attach(0x16D5, 0x2A32, "THIRTYTWO");
        var atx = $"{c.DeviceId}:strimer-atx";
        var gpu = $"{c.DeviceId}:strimer-gpu";
        _provider.SetZoneLedCount(atx, 999);
        _provider.SetZoneLedCount(gpu, 999);
        Assert.Equal(120, _store.Load().Devices.ZoneLedCounts[atx]);
        Assert.Equal(162, _store.Load().Devices.ZoneLedCounts[gpu]);

        var structures = _provider.GetStructures();
        Assert.Equal(120, structures.Single(s => s.DeviceId == atx).Segments[0].MaxLedCount);
        Assert.Equal(162, structures.Single(s => s.DeviceId == gpu).Segments[0].MaxLedCount);
        Assert.Equal(256, structures.Single(s => s.DeviceId == $"{c.DeviceId}:ch0").Segments[0].MaxLedCount);
    }

    /// <summary>The bundled channels have no card of their own, so their old ids resolve to nothing.</summary>
    [Fact]
    public void TryResolve_finds_ports_by_slug_and_not_bundled_channels()
    {
        var c = Attach(0x16D5, 0x2A32, "THIRTYTWO");
        Assert.True(_provider.TryResolve($"{c.DeviceId}:strimer-gpu", out var controller, out var port));
        Assert.Same(c, controller);
        Assert.Equal(22, port.FirstChannel);
        Assert.True(_provider.TryResolve($"{c.DeviceId}:ch28", out _, out port));
        Assert.Equal(28, port.FirstChannel);
        Assert.False(_provider.TryResolve($"{c.DeviceId}:ch16", out _, out _));
        Assert.False(_provider.TryResolve($"{c.DeviceId}:", out _, out _));
        Assert.False(_provider.TryResolve(c.DeviceId, out _, out _));
    }

    [Fact]
    public void Every_channel_is_resizable()
    {
        Attach(0x16D5, 0x2A16, "SIXTEEN");
        Assert.All(_provider.GetAll().Devices, d => Assert.True(d.ZoneResizable));
    }

    [Fact]
    public void Channel_led_count_is_zero_until_declared()
    {
        Attach(0x16D5, 0x2A16, "SIXTEEN");
        Assert.All(_provider.GetAll().Devices, d => Assert.Equal(0, d.LedCount));
    }

    [Fact]
    public void SetZoneLedCount_persists_and_surfaces_on_the_card()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        _provider.SetZoneLedCount(PortId(c.DeviceId, 2), 42);

        // By id, not by ZoneIndex: that is the zone's ordinal within its own
        // channel now, so every unchained channel reports 0.
        var card = _provider.GetAll().Devices
            .Single(d => d.Id == PortId(c.DeviceId, 2));
        Assert.Equal(42, card.LedCount);
        Assert.Equal(42, _store.Load().Devices.ZoneLedCounts[card.Id]);
    }

    [Fact]
    public void SetZoneLedCount_clamps_to_the_controller_ceiling()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        var id = PortId(c.DeviceId, 0);
        _provider.SetZoneLedCount(id, 99_999);
        Assert.Equal(256, _store.Load().Devices.ZoneLedCounts[id]);
    }

    [Fact]
    public void SetZoneLedCount_ignores_a_negative_count_and_an_unknown_id()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), -5);
        _provider.SetZoneLedCount("nollie-s-GHOST:ch0", 30);
        _provider.SetZoneLedCount($"{c.DeviceId}:ch99", 30);
        Assert.Empty(_store.Load().Devices.ZoneLedCounts);
    }

    [Fact]
    public void SetZoneLedCount_on_a_chained_channel_drops_the_chain_and_partition()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        var id = PortId(c.DeviceId, 0);
        _store.Update(s =>
        {
            s.Devices.ZoneLedCounts[id] = 76;
            s.Devices.ZonePartitions[id] = new()
            {
                new ZoneDef { Name = "FR12 Trio", Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 68 } } },
                new ZoneDef { Name = "Y50 Solo Fan", Slices = { new ZoneSlice { Segment = 0, Start = 68, Count = 8 } } },
            };
            s.Devices.PortChains[ZoneResolution.ChainKey(id, 0)] = new()
            {
                new ChainEntry { Key = "product:hyte-fr12-trio", LedCount = 68 },
                new ChainEntry { Key = "product:hyte-y50-solo", LedCount = 8 },
            };
        });

        _provider.SetZoneLedCount(id, 40);

        var settings = _store.Load();
        Assert.Equal(40, settings.Devices.ZoneLedCounts[id]);
        Assert.False(settings.Devices.PortChains.ContainsKey(ZoneResolution.ChainKey(id, 0)));
        Assert.False(settings.Devices.ZonePartitions.ContainsKey(id));
    }

    /// <summary>
    /// The chain POST writes ZoneLedCounts directly rather than through
    /// SetZoneLedCount, so the legacy 1CH controller needs this as a separate
    /// path to the same handshake or its firmware keeps the old count.
    /// </summary>
    [Fact]
    public void PushLedCountHandshakeFor_resends_a_count_written_outside_SetZoneLedCount()
    {
        var device = new FakeHidDevice(0x16D2, 0x1F11, "path-LEGACY", "LEGACY");
        var controller = new NollieController(device, NollieProtocol.Lookup(0x16D2, 0x1F11)!);
        _hub.Attach(controller);
        var id = PortId(controller.DeviceId, 0);
        _store.Update(s => s.Devices.ZoneLedCounts[id] = 76);

        _provider.PushLedCountHandshakeFor(id);

        // The handshake alone: this firmware takes its standalone colour only
        // before the first frame and at the hand-off, never mid-stream.
        var report = Assert.Single(device.Writes);
        Assert.Equal(0xFE, report[1]);
        Assert.Equal(0x03, report[2]);
        Assert.Equal(76, report[3]);
        Assert.Equal(0, report[4]);
    }

    /// <summary>A count persisted above the ceiling (older build, edited file) is clamped on read, never trusted raw.</summary>
    [Fact]
    public void Oversized_persisted_count_is_clamped_when_read()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        var id = PortId(c.DeviceId, 0);
        _store.Update(s => s.Devices.ZoneLedCounts[id] = 5000);
        Assert.Equal(256, _provider.GetAll().Devices.Single(d => d.Id == id).LedCount);
    }

    [Fact]
    public void Structures_expose_one_resizable_segment_per_channel()
    {
        var c = Attach(0x16D5, 0x2A01, "ONE");
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), 12);

        var structures = _provider.GetStructures();
        var s = Assert.Single(structures);
        var seg = Assert.Single(s.Segments);
        Assert.True(seg.Resizable);
        Assert.Equal(12, seg.LedCount);
        Assert.Equal(12, seg.FrameLedCount);
        Assert.Equal("linear", seg.ZoneType);
    }

    [Fact]
    public void Frames_match_the_declared_counts_and_get_contiguous_indices()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), 10);
        _provider.SetZoneLedCount(PortId(c.DeviceId, 1), 20);

        var frames = _provider.BuildFrames(startingIndex: 5);
        Assert.Equal(16, frames.Count);
        for (var i = 0; i < frames.Count; i++) Assert.Equal(5 + i, frames[i].Index);
        Assert.Equal(10, frames[0].LedCount);
        Assert.Equal(20, frames[1].LedCount);
        Assert.Equal(0, frames[2].LedCount);
    }

    /// <summary>A frame instance is reused across refreshes so the writer never sees a blank frame for one tick.</summary>
    [Fact]
    public void Frames_are_reused_when_shape_is_unchanged()
    {
        var c = Attach(0x16D5, 0x2A01, "ONE");
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), 8);
        var first = _provider.BuildFrames(0)[0];
        var second = _provider.BuildFrames(0)[0];
        Assert.Same(first, second);
    }

    [Fact]
    public void Detaching_drops_the_cards()
    {
        var c = Attach(0x16D5, 0x2A01, "ONE");
        Assert.Single(_provider.GetAll().Devices);
        _hub.Detach(c.DeviceId);
        Assert.Empty(_provider.GetAll().Devices);
        Assert.False(_provider.IsConnected);
    }

    [Fact]
    public void Controller_without_a_serial_falls_back_to_a_path_derived_id()
    {
        var spec = NollieProtocol.Lookup(0x16D5, 0x2A01)!;
        var controller = new NollieController(new FakeHidDevice(0x16D5, 0x2A01, @"\\?\hid#vid_16d5", serial: null), spec);
        Assert.StartsWith("nollie-p-", controller.DeviceId, StringComparison.Ordinal);
    }

    /// <summary>A resize must notify, or the bridge never rebuilds frames at the new length.</summary>
    [Fact]
    public void SetZoneLedCount_raises_DevicesChanged()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        _provider.OnHubStateUpdated();

        var fired = 0;
        _provider.DevicesChanged += () => fired++;
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), 30);
        Assert.Equal(1, fired);

        // Same value again is not a change.
        _provider.SetZoneLedCount(PortId(c.DeviceId, 0), 30);
        Assert.Equal(1, fired);
    }

    /// <summary>
    /// Cards must leave DeviceId empty so the composite fills DeviceId AND
    /// EnabledLedCount. Setting DeviceId claims partition-awareness, and the
    /// composite then skips EnabledLedCount, leaving it 0 - which the web's
    /// isCardFullyParked reads as "every LED disabled", hiding all but one card
    /// per device and blanking the LED map.
    /// </summary>
    [Fact]
    public void Every_card_names_the_channel_it_belongs_to()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        // The chain and zone editors address the channel, which is the thing
        // the user actually wired something to.
        Assert.All(_provider.GetAll().Devices,
            d => Assert.StartsWith(c.DeviceId + ":ch", d.DeviceId));
    }

    /// <summary>
    /// The protocol has no read command, so every channel's contents are the
    /// user's declaration: the zones are a chain they compose, not a fixed
    /// firmware list.
    /// </summary>
    [Fact]
    public void Cards_are_zone_customizable()
    {
        Attach(0x16D5, 0x2A16, "SIXTEEN");
        Assert.All(_provider.GetAll().Devices, d => Assert.True(d.ZoneCustomizable));
    }

    [Fact]
    public void Structures_are_partitionable()
    {
        Attach(0x16D5, 0x2A16, "SIXTEEN");
        Assert.All(_provider.GetStructures(), s => Assert.True(s.Partitionable));
    }

    /// <summary>A declared count must reach the card, or the LED map has nothing to draw.</summary>
    [Fact]
    public void Declared_count_surfaces_on_every_channel_card()
    {
        var c = Attach(0x16D5, 0x2A16, "SIXTEEN");
        for (var ch = 0; ch < 16; ch++)
        {
            _provider.SetZoneLedCount(PortId(c.DeviceId, ch), 60);
        }
        var cards = _provider.GetAll().Devices;
        Assert.Equal(16, cards.Count);
        Assert.All(cards, d => Assert.Equal(60, d.LedCount));
    }

    internal sealed class FakeHidDevice : IHidDevice
    {
        public FakeHidDevice(int vid, int pid, string path, string? serial)
        { VendorId = vid; ProductId = pid; Path = path; Serial = serial; }

        public List<byte[]> Writes { get; } = new();
        public int VendorId { get; }
        public int ProductId { get; }
        public string Path { get; }
        public string? Serial { get; }
        public int UsagePage => NollieProtocol.VendorUsagePage;
        public int Usage => NollieProtocol.VendorUsage;
        public bool WriteResult { get; set; } = true;

        public bool Write(ReadOnlySpan<byte> report) { Writes.Add(report.ToArray()); return WriteResult; }
        public bool SetFeature(ReadOnlySpan<byte> report) => throw new NotSupportedException();
        public bool GetFeature(Span<byte> buffer) => throw new NotSupportedException();
        public bool GetInputReport(Span<byte> buffer) => throw new NotSupportedException();
        public bool SetOutputReport(ReadOnlySpan<byte> report) => throw new NotSupportedException();
        public int Read(Span<byte> buffer, int timeoutMs) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
