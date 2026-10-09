using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.Panel.Streams;

/// <summary>
/// The iCUE LINK LCD as a streamed panel: discovery only offers glass that is actually
/// open, and the transport turns the overlay's byte stream into whole JPEG pushes.
/// </summary>
public class CorsairLinkLcdPanelTests
{
    [Fact]
    public void Discover_reports_nothing_until_hub_and_lcd_are_both_up()
    {
        var hub = new CorsairLinkHub();
        var (lcd, _) = AttachLcd();
        var hubs = new CorsairLinkHubs();
        hubs.Add(hub);
        var discovery = new CorsairLinkPanelDiscovery(hubs, lcd);

        // Hub down, LCD open: the chain is what carries the topology, so nothing to offer.
        Assert.Empty(discovery.Discover());

        hub.State.IsConnected = true;
        Assert.Empty(discovery.Discover()); // no LCD in the chain

        hub.State.HasLcd = true;
        Assert.Single(discovery.Discover());
    }

    [Fact]
    public void Discover_offers_a_480_round_panel_keyed_by_the_lcd_serial()
    {
        var hub = new CorsairLinkHub();
        hub.State.IsConnected = true;
        hub.State.HasLcd = true;
        var (lcd, _) = AttachLcd();
        var hubs = new CorsairLinkHubs();
        hubs.Add(hub);
        var discovery = new CorsairLinkPanelDiscovery(hubs, lcd);

        var info = Assert.Single(discovery.Discover());

        Assert.Equal("S1", info.Serial);
        Assert.Equal("corsair-link-lcd", info.Profile.Kind);
        Assert.Equal("corsair-link-lcd", info.Profile.Family);
        Assert.Equal(PanelSurfaces.LcdRound, info.Profile.Surface);
        Assert.Equal(480, info.Profile.CssWidth);
        Assert.Equal(480, info.Profile.CssHeight);
        Assert.Equal(StreamCodec.RawBgra, info.Profile.Codec);
        // Its own gate, not the hub's: claiming the cooler for fans must not repaint the glass.
        Assert.Equal("corsair-link-lcd", discovery.HandlerId);
    }

    [Fact]
    public void Transport_pushes_one_jpeg_once_a_whole_frame_has_arrived()
    {
        var (lcd, device) = AttachLcd();
        using var transport = new CorsairLinkLcdStreamTransport(lcd, "S1");
        transport.Open();

        var frame = new byte[CorsairLinkLcd.PanelWidth * CorsairLinkLcd.PanelHeight * 4];
        // The paced writer may split a frame across writes; a partial frame must not push.
        transport.Write(frame.AsSpan(0, frame.Length - 16));
        Assert.Empty(device.Writes);

        transport.Write(frame.AsSpan(frame.Length - 16));
        Assert.NotEmpty(device.Writes);

        var first = device.Writes[0];
        Assert.Equal(1024, first.Length);
        Assert.Equal(0x02, first[0]); // HID report id
        Assert.Equal(0x05, first[1]);
        Assert.Equal(0x01, first[2]);
        Assert.Equal(0xFF, first[8]); // JPEG SOI lands at the payload offset
        Assert.Equal(0xD8, first[9]);
    }

    [Fact]
    public void Transport_holds_the_glass_only_while_frames_flow()
    {
        var (lcd, _) = AttachLcd();
        Assert.False(lcd.IsStreamLive);

        var transport = new CorsairLinkLcdStreamTransport(lcd, "S1");
        transport.Open();
        Assert.True(lcd.IsStreamLive); // the media worker stands down

        transport.Dispose();
        Assert.False(lcd.IsStreamLive); // and takes it back
    }

    [Fact]
    public void Transport_release_is_by_identity_so_a_stale_dispose_cannot_steal_the_glass()
    {
        var (lcd, _) = AttachLcd();
        var faulted = new CorsairLinkLcdStreamTransport(lcd, "S1");
        faulted.Open();

        // The coordinator reopens after a fault, then the faulted transport finally disposes.
        var live = new CorsairLinkLcdStreamTransport(lcd, "S1");
        live.Open();
        faulted.Dispose();

        Assert.True(lcd.IsStreamLive); // still the live session's glass
        live.Dispose();
        Assert.False(lcd.IsStreamLive);
    }

    [Fact]
    public void Attach_generation_moves_so_brightness_is_re_applied_after_a_reconnect()
    {
        var (lcd, _) = AttachLcd();
        var first = lcd.AttachGeneration;

        lcd.Detach();
        lcd.DiscoverAndAttach(
            new CorsairLinkDevice[] { new() { Channel = 1, Type = 6, Serial = "S1" } },
            new SingleDeviceHidEnumerator(
                new HidDeviceInfo
                {
                    VendorId = CorsairLinkLcd.LcdVendorId,
                    ProductId = CorsairLinkLcd.AioPid,
                    Path = "/dev/hid-lcd",
                    Serial = "S1",
                    OutputReportByteLength = 1024,
                },
                new RecordingHidDevice()));

        Assert.NotEqual(first, lcd.AttachGeneration);
    }

    [Fact]
    public void Detach_stays_silent_on_glass_nexus_never_painted()
    {
        var (lcd, device) = AttachLcd();

        // Nexus Control off: the handle is open, but nothing was ever pushed, so there is
        // no claim to hand back and the shutdown sequence is not ours to send.
        lcd.Detach();

        Assert.Empty(device.Features);
    }

    [Fact]
    public void Transport_open_throws_when_the_lcd_is_gone()
    {
        var lcd = new CorsairLinkLcd();
        using var transport = new CorsairLinkLcdStreamTransport(lcd, "S1");

        Assert.Throws<System.IO.IOException>(transport.Open);
        Assert.False(lcd.IsStreamLive);
    }

    private static (CorsairLinkLcd lcd, RecordingHidDevice device) AttachLcd()
    {
        var device = new RecordingHidDevice();
        var info = new HidDeviceInfo
        {
            VendorId = CorsairLinkLcd.LcdVendorId,
            ProductId = CorsairLinkLcd.AioPid,
            Path = "/dev/hid-lcd",
            Serial = "S1",
            OutputReportByteLength = 1024,
        };
        var lcd = new CorsairLinkLcd();
        lcd.DiscoverAndAttach(
            new CorsairLinkDevice[] { new() { Channel = 1, Type = 6, Serial = "S1" } },
            new SingleDeviceHidEnumerator(info, device));
        return (lcd, device);
    }

    private sealed class RecordingHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public List<byte[]> Features { get; } = new();

        public int VendorId => CorsairLinkLcd.LcdVendorId;
        public int ProductId => CorsairLinkLcd.AioPid;
        public string Path => "/dev/hid-lcd";
        public string? Serial => "S1";
        public int UsagePage => 0;
        public int Usage => 0;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Features.Add(report.ToArray());
            return true;
        }
        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return true;
        }

        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class SingleDeviceHidEnumerator : IHidEnumerator
    {
        private readonly HidDeviceInfo _info;
        private readonly IHidDevice _device;

        public SingleDeviceHidEnumerator(HidDeviceInfo info, IHidDevice device)
        {
            _info = info;
            _device = device;
        }

        public IReadOnlyList<HidDeviceInfo> FindAll() => new[] { _info };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            _info.VendorId == vendorId && _info.ProductId == productId
                ? new[] { _info }
                : Array.Empty<HidDeviceInfo>();

        public IHidDevice? Open(string path, bool forInput) => path == _info.Path ? _device : null;
    }
}
