using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using Nexus.Service.Tests;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Covers the lighting side of the Nexus Link channels: <see cref="QSeriesLightingDeviceProvider"/>
/// lists one card per channel device with LEDs (light strips, but not an FP12 fan - firmware
/// always reports LED count 0 for that family, and nexus-control-service carries no fallback
/// constant), and <see cref="QSeriesLightingFrameWriter"/> concatenates a channel's device
/// frames in slot order onto that channel's Nexus Link port.
/// </summary>
public class QSeriesCoolerLinkLightingTests
{
    private static QSeriesCoolerHub NewConnectedHub(out FakeTransport transport)
    {
        var t = new FakeTransport();
        transport = t;
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        Assert.True(hub.EnsureConnected());
        hub.State.Channel1Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "LS10", LedCount = 20, FanCount = 0 },
            new() { Slot = 2, Model = "LN70", LedCount = 44, FanCount = 0 },
        };
        hub.State.Channel2Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "FP12", LedCount = 0, FanCount = 1 },
            new() { Slot = 2, Model = "FP12", LedCount = 0, FanCount = 1 },
        };
        return hub;
    }

    [Fact]
    public void GetAll_lists_panel_plus_logo_and_the_LS10_LN70_zones_but_no_LED_less_FP12_card()
    {
        var hub = NewConnectedHub(out _);
        var provider = new QSeriesLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var devices = provider.GetAll().Devices;

        Assert.Equal(3, devices.Count); // Panel + Logo, LS10, LN70 - no FP12 card
        Assert.Contains(devices, d => d.Id == hub.DeviceId && d.ParentDeviceId is null or "");

        var ls10 = devices.Single(d => d.Id == $"{hub.DeviceId}:p1:1");
        Assert.Equal("HYTE Q60 - LS10 (Port 1 #1)", ls10.Name);
        Assert.Equal(20, ls10.LedCount);
        Assert.Equal("strip", ls10.IconType);
        Assert.Equal(hub.DeviceId, ls10.ParentDeviceId);
        Assert.True(ls10.ZoneResizable);

        var ln70 = devices.Single(d => d.Id == $"{hub.DeviceId}:p1:2");
        Assert.Equal(44, ln70.LedCount);
        Assert.Equal(hub.DeviceId, ln70.ParentDeviceId);

        Assert.DoesNotContain(devices, d => d.Id.StartsWith($"{hub.DeviceId}:p2:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFrames_returns_the_panel_frame_then_channel_1_devices_in_slot_order()
    {
        var hub = NewConnectedHub(out _);
        var provider = new QSeriesLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var frames = provider.BuildFrames(0);

        // Order matters: the frame writer trusts this array order as slot order when it
        // concatenates devices onto one port stream (device 0's LEDs first).
        Assert.Collection(frames,
            f => { Assert.Equal(hub.DeviceId, f.Id); Assert.Equal(QSeriesCoolerHub.LedCount, f.LedCount); },
            f => { Assert.Equal($"{hub.DeviceId}:p1:1", f.Id); Assert.Equal(20, f.LedCount); },
            f => { Assert.Equal($"{hub.DeviceId}:p1:2", f.Id); Assert.Equal(44, f.LedCount); });
    }

    [Fact]
    public void FrameWriter_concatenates_channel_1_LEDs_into_port_1_in_slot_order_and_leaves_ports_3_4_intact()
    {
        var hub = NewConnectedHub(out var transport);
        var hubId = hub.DeviceId;
        // Synthetic: real FP12 hardware reports 0 LEDs, but the writer's channel-routing
        // logic must work for any device frame on that channel, so override the shared
        // fixture's 0-LED FP12s to match the lit frames built below (firmware count == frame
        // count here - the padding-on-trim behavior has its own dedicated test).
        hub.State.Channel2Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "FP12", LedCount = 2, FanCount = 1 },
            new() { Slot = 2, Model = "FP12", LedCount = 3, FanCount = 1 },
        };
        var engine = new LightingEngine();

        var panel = new DeviceFrame(0, hubId, QSeriesCoolerHub.LedCount);
        panel.Fill(9, 9, 9);
        panel.Publish();

        var ls10 = new DeviceFrame(1, $"{hubId}:p1:1", 20);
        ls10.Fill(10, 20, 30);
        ls10.Publish();

        var ln70 = new DeviceFrame(2, $"{hubId}:p1:2", 44);
        ln70.Fill(40, 50, 60);
        ln70.Publish();

        var fan1 = new DeviceFrame(3, $"{hubId}:p2:1", 2);
        fan1.Fill(70, 80, 90);
        fan1.Publish();
        var fan2 = new DeviceFrame(4, $"{hubId}:p2:2", 3);
        fan2.Fill(100, 110, 120);
        fan2.Publish();

        engine.UpdateDevices(new[] { panel, ls10, ln70, fan1, fan2 });

        var store = new InMemoryConfigStore();
        var writer = new QSeriesLightingFrameWriter(engine, hub, store, new Np50IdentifyTracker());
        writer.Tick();

        var port1 = SingleLedFrame(transport.Writes, QSeriesCoolerProtocol.LinkChannel1);
        Assert.Equal(7 + 64 * 3, port1.Length);
        AssertGrb(port1, 7 + 0 * 3, g: 20, r: 10, b: 30);   // LS10 first (device 0 of the chain)
        AssertGrb(port1, 7 + 20 * 3, g: 50, r: 40, b: 60);  // LN70 follows immediately after

        var port2 = SingleLedFrame(transport.Writes, QSeriesCoolerProtocol.FanChannel);
        Assert.Equal(QSeriesCoolerProtocol.MinStreamFrameLength, port2.Length); // 5 LEDs pads up to the 90-byte floor
        AssertGrb(port2, 7 + 0 * 3, g: 80, r: 70, b: 90);
        AssertGrb(port2, 7 + 2 * 3, g: 110, r: 100, b: 120);

        var port3 = SingleLedFrame(transport.Writes, QSeriesCoolerProtocol.BacklightPort);
        Assert.Equal(7 + QSeriesCoolerProtocol.BacklightLedCount * 3, port3.Length);
        var port4 = SingleLedFrame(transport.Writes, QSeriesCoolerProtocol.LogoPort);
        Assert.Equal(90, port4.Length);
    }

    [Fact]
    public void FrameWriter_pads_a_trimmed_devices_block_to_its_firmware_LED_count_so_later_devices_dont_shift()
    {
        var hub = NewConnectedHub(out var transport);
        var hubId = hub.DeviceId;
        // Hub state still reports LS10's FIRMWARE count (20) even though the engine frame
        // below is trimmed to 10 by a ZoneLedCounts override.
        var engine = new LightingEngine();

        var panel = new DeviceFrame(0, hubId, QSeriesCoolerHub.LedCount);
        panel.Publish();

        var ls10Trimmed = new DeviceFrame(1, $"{hubId}:p1:1", 10);
        ls10Trimmed.Fill(10, 20, 30);
        ls10Trimmed.Publish();

        var ln70 = new DeviceFrame(2, $"{hubId}:p1:2", 44);
        ln70.Fill(40, 50, 60);
        ln70.Publish();

        engine.UpdateDevices(new[] { panel, ls10Trimmed, ln70 });

        var store = new InMemoryConfigStore();
        var writer = new QSeriesLightingFrameWriter(engine, hub, store, new Np50IdentifyTracker());
        writer.Tick();

        var port1 = SingleLedFrame(transport.Writes, QSeriesCoolerProtocol.LinkChannel1);
        // Total wire width is still 20 + 44 = 64 LEDs - the trim shortens what lights up
        // inside LS10's block, not the block itself.
        Assert.Equal(7 + 64 * 3, port1.Length);
        AssertGrb(port1, 7 + 0 * 3, g: 20, r: 10, b: 30); // LS10's first (and only lit) LED
        for (var i = 10; i < 20; i++)
        {
            AssertGrb(port1, 7 + i * 3, g: 0, r: 0, b: 0); // trimmed-off LEDs go dark, not stale
        }
        // LN70's first LED still lands at offset 20 (LS10's firmware count), not offset 10.
        AssertGrb(port1, 7 + 20 * 3, g: 50, r: 40, b: 60);
    }

    private static byte[] SingleLedFrame(IReadOnlyList<byte[]> writes, int port) =>
        Assert.Single(writes, w => w.Length > 3 && w[1] == 0xEE && w[3] == (byte)port);

    private static void AssertGrb(byte[] frame, int offset, byte g, byte r, byte b)
    {
        Assert.Equal(g, frame[offset + 0]);
        Assert.Equal(r, frame[offset + 1]);
        Assert.Equal(b, frame[offset + 2]);
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool IsOpen => true;
        public string Serial => "QTEST123";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
