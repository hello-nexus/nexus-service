using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.Galahad2;

public sealed class Galahad2LcdLightingTests
{
    [Fact]
    public void Provider_exposes_one_fixed_twelve_led_pump_zone()
    {
        using var hub = new JpegPanelHub(Model());
        using var device = new RecordingHidDevice();
        Assert.True(hub.Attach(device));

        var provider = new Nexus.Service.Lighting.Galahad2LcdLightingDeviceProvider(
            hub,
            new InMemoryConfigStoreForGalahad2());

        var card = Assert.Single(provider.GetAll().Devices);
        Assert.Equal("lianli-galahad2-lcd:pump", card.Id);
        Assert.Equal(12, card.LedCount);
        Assert.Equal(12, card.EnabledLedCount);
        Assert.False(card.ZoneResizable);
        Assert.False(card.ZoneCustomizable);
    }

    [Fact]
    public void Provider_builds_a_twelve_led_structure_and_frame()
    {
        using var hub = new JpegPanelHub(Model());
        using var device = new RecordingHidDevice();
        Assert.True(hub.Attach(device));

        var provider = new Nexus.Service.Lighting.Galahad2LcdLightingDeviceProvider(
            hub,
            new InMemoryConfigStoreForGalahad2());

        var structure = Assert.Single(provider.GetStructures());
        var segment = Assert.Single(structure.Segments);
        Assert.False(structure.Partitionable);
        Assert.Equal(12, segment.LedCount);
        Assert.Equal(12, segment.FrameLedCount);
        Assert.Equal(12, segment.DefaultU!.Length);
        Assert.Equal(12, segment.DefaultV!.Length);
        Assert.Equal(0f, segment.DefaultU[0]);
        Assert.Equal(1f, segment.DefaultU[11]);
        Assert.All(segment.DefaultV, v => Assert.Equal(0.5f, v));

        var frame = Assert.Single(provider.BuildFrames(7));
        Assert.Equal(7, frame.Index);
        Assert.Equal("lianli-galahad2-lcd:pump", frame.Id);
        Assert.Equal(12, frame.LedCount);
    }

    [Fact]
    public void Provider_stays_empty_until_the_panel_hub_is_attached()
    {
        using var hub = new JpegPanelHub(Model());
        var provider = new Nexus.Service.Lighting.Galahad2LcdLightingDeviceProvider(
            hub,
            new InMemoryConfigStoreForGalahad2());

        Assert.Empty(provider.GetAll().Devices);
        Assert.Empty(provider.GetStructures());
        Assert.Empty(provider.BuildFrames(0));
    }

    [Fact]
    public void Canvas_writer_sends_engine_colors_through_the_reversed_pump_mapping()
    {
        using var hub = new JpegPanelHub(Model());
        using var device = new RecordingHidDevice();
        Assert.True(hub.Attach(device));

        var store = new InMemoryConfigStoreForGalahad2();
        var provider = new Nexus.Service.Lighting.Galahad2LcdLightingDeviceProvider(hub, store);
        using var engine = new LightingEngine();
        var frame = Assert.Single(provider.BuildFrames(0));
        for (var i = 0; i < frame.LedCount; i++)
        {
            frame.SetLed(i, (byte)(i + 1), (byte)(i + 21), (byte)(i + 41));
        }
        frame.Publish();
        engine.UpdateDevices(new[] { frame });

        using var writer = new Nexus.Service.Lighting.Galahad2LcdLightingFrameWriter(
            engine, hub, store, provider);
        device.Writes.Clear();
        writer.Tick();

        Assert.Equal(2, device.Writes.Count);
        var bootstrap = device.Writes[0];
        Assert.Equal(0x01, bootstrap[0]);
        Assert.Equal(0x83, bootstrap[1]);
        Assert.Equal(0, bootstrap[6]);
        Assert.Equal(0x03, bootstrap[7]);
        Assert.Equal(4, bootstrap[8]);
        Assert.Equal(new byte[] { 1, 21, 41 }, bootstrap[10..13]);

        var report = device.Writes[1];
        Assert.Equal(0x02, report[0]);
        Assert.Equal(0x14, report[1]);
        Assert.Equal(new byte[] { 12, 32, 52 }, report[36..39]);
        Assert.Equal(new byte[] { 1, 21, 41 }, report[69..72]);
    }

    private static JpegPanelModel Model() => JpegPanelModel.GalahadIiLcd with
    {
        Handshake = new LianLiAioHandshake(
            "test-lcd", 24, LianLiAioHandshake.Galahad2BrightnessMode),
    };

    private sealed class RecordingHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => 0x0416;
        public int ProductId => 0x7395;
        public string Path => "/dev/fake-galahad-lcd";
        public string? Serial => "FAKE-SERIAL";
        public int UsagePage => 0xFF1A;
        public int Usage => 1;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return true;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
