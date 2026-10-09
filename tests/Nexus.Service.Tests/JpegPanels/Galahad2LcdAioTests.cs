using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// Byte layouts are checked against sgtaziz/lian-li-linux `hydroshift_lcd` (MIT), which drives
/// PID 0x7395 as the Galahad II Vision.
/// </summary>
public class Galahad2LcdAioTests
{
    private const int ReportLength = 1024;

    private static byte[] StatusReply(int fanRpm, int pumpRpm, byte valid, byte whole, byte tenths) =>
        new byte[] { 0x01, 0x81, 0, 0, 0, 7, (byte)(fanRpm >> 8), (byte)fanRpm, (byte)(pumpRpm >> 8), (byte)pumpRpm, valid, whole, tenths };

    [Fact]
    public void Status_reply_carries_big_endian_speeds_and_the_coolant_in_tenths()
    {
        var status = LianLiAioProtocol.ParseStatus(StatusReply(1450, 3580, 1, 31, 4));

        Assert.Equal(new LianLiAioStatus(1450, 3580, 31.4f), status);
    }

    [Fact]
    public void Coolant_is_dropped_when_flagged_invalid_or_the_startup_placeholder()
    {
        Assert.Null(LianLiAioProtocol.ParseStatus(StatusReply(0, 2100, 0, 30, 0))!.CoolantC);
        Assert.Null(LianLiAioProtocol.ParseStatus(StatusReply(0, 0, 1, 1, 0))!.CoolantC);
        Assert.Equal(1f, LianLiAioProtocol.ParseStatus(StatusReply(0, 2100, 1, 1, 0))!.CoolantC);
    }

    [Fact]
    public void Any_other_reply_or_a_short_one_is_not_a_status()
    {
        Assert.Null(LianLiAioProtocol.ParseStatus(new byte[] { 0x01, 0x86, 0, 0, 0, 4, 0, 0, 0, 0 }));
        Assert.Null(LianLiAioProtocol.ParseStatus(new byte[] { 0x01, 0x81, 0, 0, 0, 2, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Pump_write_is_a_host_sourced_pwm_clamped_to_the_vision_floor()
    {
        var report = new byte[ReportLength];
        LianLiAioProtocol.FillSetPumpPwm(report, 100);
        Assert.Equal(new byte[] { 0x01, 0x8A, 0, 0, 0, 2, 0x00, 100 }, report[..8]);
        Assert.All(report[8..], b => Assert.Equal(0, b));

        LianLiAioProtocol.FillSetPumpPwm(report, 5);
        Assert.Equal(LianLiAioProtocol.GalahadPumpDutyFloor, report[7]);
    }

    [Fact]
    public void A_tick_reads_status_past_queued_frame_acks()
    {
        var (aio, device) = Attached();
        device.Replies.Enqueue(new byte[] { 0x02, 0x00 });
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 32, 5));

        aio.Tick(Environment.TickCount64);

        var write = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0x01, 0x81, 0, 0, 0, 0 }, write[..6]);
        Assert.Equal(new LianLiAioStatus(0, 2127, 32.5f), aio.Status);
    }

    [Fact]
    public void Vision_table_maps_speed_to_pwm_and_clamps_at_both_ends()
    {
        Assert.Equal(20, LianLiAioProtocol.GalahadPwmForRpm(0));
        Assert.Equal(20, LianLiAioProtocol.GalahadPwmForRpm(800));
        Assert.Equal(53, LianLiAioProtocol.GalahadPwmForRpm(2127));
        Assert.Equal(100, LianLiAioProtocol.GalahadPwmForRpm(3600));
        Assert.Equal(100, LianLiAioProtocol.GalahadPwmForRpm(5000));
    }

    [Fact]
    public void The_pump_is_not_driven_until_its_own_speed_has_been_read()
    {
        var (aio, device) = Attached();
        aio.SetPumpDuty(100);

        aio.Tick(1000);

        Assert.Equal(new[] { (byte)0x81 }, device.Writes.Select(w => w[1]));
    }

    [Fact]
    public void A_driven_pump_is_resent_each_poll_and_handed_its_own_speed_back_on_release()
    {
        var (aio, device) = Attached();
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 30, 0));
        aio.Tick(1000);

        aio.SetPumpDuty(100);
        device.Writes.Clear();
        device.Replies.Enqueue(new byte[] { 0x01, 0x8A });
        device.Replies.Enqueue(StatusReply(0, 3600, 1, 30, 0));
        aio.Tick(2000);
        aio.Tick(3000);

        Assert.Equal(new byte[] { 0x8A, 0x81, 0x8A, 0x81 }, device.Writes.Select(w => w[1]));
        Assert.Equal(new byte[] { 0x00, 100 }, device.Writes[0][6..8]);

        aio.SetPumpDuty(null);
        device.Writes.Clear();
        aio.Tick(4000);
        aio.Tick(5000);

        Assert.Equal(new byte[] { 0x8A, 0x81, 0x81 }, device.Writes.Select(w => w[1]));
        Assert.Equal(53, device.Writes[0][7]);
    }

    [Fact]
    public void A_failed_hand_back_is_retried_on_the_next_tick()
    {
        var (aio, device) = Attached();
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 30, 0));
        aio.Tick(1000);
        aio.SetPumpDuty(100);
        aio.Tick(2000);

        aio.SetPumpDuty(null);
        device.FailWrites = true;
        aio.Tick(3000);
        device.FailWrites = false;
        device.Writes.Clear();
        aio.Tick(4000);

        Assert.Equal(0x8A, device.Writes[0][1]);
        Assert.Equal(53, device.Writes[0][7]);
    }

    [Fact]
    public void A_detach_while_driven_hands_the_pump_back_before_the_handle_closes()
    {
        var (aio, device, hub) = AttachedWithHub();
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 30, 0));
        aio.Tick(1000);
        aio.SetPumpDuty(60);
        aio.Tick(2000);
        device.Writes.Clear();

        hub.Detach();

        Assert.Equal(0x8A, device.Writes[0][1]);
        Assert.Equal(53, device.Writes[0][7]);
    }

    [Fact]
    public void A_detach_without_driving_writes_no_pump_target()
    {
        var (aio, device, hub) = AttachedWithHub();
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 30, 0));
        aio.Tick(1000);
        device.Writes.Clear();

        hub.Detach();

        Assert.DoesNotContain(device.Writes, w => w[0] == 0x01 && w[1] == 0x8A);
    }

    [Fact]
    public void A_unit_that_never_answered_is_polled_again_only_after_the_retry_interval()
    {
        var (aio, device) = Attached();

        aio.Tick(1000);
        aio.Tick(2000);
        Assert.Single(device.Writes);
        Assert.Null(aio.Status);

        aio.Tick(11_000);
        Assert.Equal(2, device.Writes.Count);
    }

    [Fact]
    public void A_unit_that_answered_is_retried_each_tick_then_backs_off_after_repeated_misses()
    {
        var (aio, device) = Attached();
        long now = Environment.TickCount64;
        device.Replies.Enqueue(StatusReply(0, 2127, 1, 32, 5));
        aio.Tick(now);

        for (int i = 1; i <= 4; i++)
        {
            aio.Tick(now + i * 1000);
        }

        Assert.Equal(4, device.Writes.Count);
    }

    [Fact]
    public void Provider_lists_the_pump_and_coolant_only_once_the_unit_answered()
    {
        var (aio, device) = Attached();
        var provider = new Galahad2LcdCoolingProvider(aio);
        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetTemperatureSources());

        device.Replies.Enqueue(StatusReply(0, 1800, 1, 29, 9));
        aio.Tick(Environment.TickCount64);

        var pump = Assert.Single(provider.GetFanChannels());
        Assert.Equal("lianli-galahad2-lcd:pump", pump.Id);
        Assert.Equal(FanKinds.Pump, pump.Kind);
        Assert.Equal(FanModes.Auto, pump.Mode);
        Assert.Equal(1800, pump.Rpm);
        Assert.Equal(LianLiAioProtocol.GalahadPumpDutyFloor, pump.MinDuty);
        var coolant = Assert.Single(provider.GetTemperatureSources());
        Assert.Equal(29.9f, coolant.Value);
        Assert.Equal("lianli-galahad2-lcd", coolant.DeviceId);
        Assert.Equal(29.9f, provider.ReadTemperature("lianli-galahad2-lcd:coolant"));
    }

    [Fact]
    public void Provider_clamps_a_pump_write_to_the_floor_and_release_stops_driving()
    {
        var (aio, _) = Attached();
        var provider = new Galahad2LcdCoolingProvider(aio);

        Assert.Equal(LianLiAioProtocol.GalahadPumpDutyFloor, provider.SetFanSpeed("lianli-galahad2-lcd:pump", 0));
        Assert.Equal(LianLiAioProtocol.GalahadPumpDutyFloor, aio.PumpDuty);

        provider.ReleaseFan("lianli-galahad2-lcd:pump");
        Assert.Null(aio.PumpDuty);
        Assert.True(Galahad2LcdCoolingProvider.IsGalahad2LcdId("lianli-galahad2-lcd:pump"));
        Assert.False(Galahad2LcdCoolingProvider.IsGalahad2LcdId("lianli-aio:pump"));
    }

    [Fact]
    public void Nothing_is_polled_while_the_panel_is_detached()
    {
        using var hub = new JpegPanelHub(JpegPanelModel.GalahadIiLcd);
        var aio = new Galahad2LcdAio(hub);

        aio.Tick(1000);

        Assert.Null(aio.Status);
    }

    [Fact]
    public void Ring_write_is_a_host_sourced_static_colour_on_both_scopes()
    {
        var report = new byte[ReportLength];
        LianLiAioProtocol.FillSetPumpLight(report, 0x10, 0x20, 0x30);

        Assert.Equal(new byte[] { 0x01, 0x83, 0, 0, 0, 19, 2, 3, 4, 2 }, report[..10]);
        for (int slot = 0; slot < 4; slot++)
        {
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, report[(10 + (slot * 3))..(13 + (slot * 3))]);
        }
        Assert.Equal(new byte[] { 0, 0, 0 }, report[22..25]);
        Assert.All(report[25..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_ring_write_drains_its_own_reply()
    {
        var (aio, device) = Attached();
        device.Replies.Enqueue(new byte[] { 0x01, 0x83 });

        Assert.True(aio.SetRing(1, 2, 3));

        Assert.Empty(device.Replies);
    }

    [Fact]
    public void Ring_colour_is_sent_once_per_change()
    {
        var (writer, frame, device, _) = RingWriter();

        frame.Fill(255, 0, 0);
        frame.Publish();
        writer.Tick();
        writer.Tick();
        frame.Fill(0, 0, 255);
        frame.Publish();
        writer.Tick();

        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(new byte[] { 255, 0, 0 }, device.Writes[0][10..13]);
        Assert.Equal(new byte[] { 0, 0, 255 }, device.Writes[1][10..13]);
    }

    [Fact]
    public void A_ring_switched_off_goes_dark_and_an_uncontrolled_one_is_left_alone()
    {
        var (writer, frame, device, store) = RingWriter();
        frame.Fill(255, 255, 255);
        frame.Publish();

        store.Update(s => s.Devices.UncontrolledLightingDevices.Add(Galahad2LcdLightingProvider.RingZoneId));
        writer.Tick();
        Assert.Empty(device.Writes);

        store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices.Clear();
            s.Devices.DisabledLightingDevices.Add(Galahad2LcdLightingProvider.RingZoneId);
        });
        writer.Tick();
        var write = Assert.Single(device.Writes);
        Assert.Equal(new byte[] { 0, 0, 0 }, write[10..13]);
    }

    private static (Galahad2LcdLightingFrameWriter Writer, DeviceFrame Frame, QueuedHidDevice Device, InMemoryConfigStore Store) RingWriter()
    {
        var (aio, device) = Attached();
        var store = new InMemoryConfigStore();
        var engine = new LightingEngine();
        var frame = new DeviceFrame(0, Galahad2LcdLightingProvider.RingZoneId, 1);
        engine.UpdateDevices(new[] { frame });
        return (new Galahad2LcdLightingFrameWriter(engine, aio, store, new Np50IdentifyTracker()), frame, device, store);
    }

    private static (Galahad2LcdAio Aio, QueuedHidDevice Device) Attached()
    {
        var (aio, device, _) = AttachedWithHub();
        return (aio, device);
    }

    private static (Galahad2LcdAio Aio, QueuedHidDevice Device, JpegPanelHub Hub) AttachedWithHub()
    {
        var hub = new JpegPanelHub(JpegPanelModel.GalahadIiLcd);
        var device = new QueuedHidDevice();
        Assert.True(hub.Attach(device));
        device.Writes.Clear();
        device.Replies.Clear();
        return (new Galahad2LcdAio(hub), device, hub);
    }

    private sealed class QueuedHidDevice : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public Queue<byte[]> Replies { get; } = new();
        public bool FailWrites { get; set; }

        public int VendorId => 0x0416;
        public int ProductId => 0x7395;
        public string Path => "/dev/fake-galahad2-lcd";
        public string? Serial => "FAKE";
        public int UsagePage => 0xFF1A;
        public int Usage => 0x93;

        public bool Write(ReadOnlySpan<byte> report)
        {
            Writes.Add(report.ToArray());
            return !FailWrites;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!Replies.TryDequeue(out var reply))
            {
                return 0;
            }
            int copy = Math.Min(reply.Length, buffer.Length);
            reply.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public void Dispose() { }
    }
}
