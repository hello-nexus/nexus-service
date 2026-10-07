using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class HydroShift2Tests
{
    // Replies captured from the Y70's HydroShift II LCD-S (firmware lianlih2s_0001_0027).
    private const string VersionReplyHex = "0AC8060000000000" + "6C69616E6C696832735F303030315F30303237";
    private const string ParamsReplyHex = "FAC8140000000000" + "0B00000000200000000000000A4E5ED6D8E566E100000113";

    // ── protocol ──

    [Fact]
    public void Decodes_the_captured_version_reply()
    {
        Assert.Equal("lianlih2s_0001_0027", HydroShift2Protocol.DecodeVersion(Reply(VersionReplyHex)));
    }

    [Fact]
    public void Decodes_the_captured_params_reply()
    {
        var reading = HydroShift2Protocol.DecodeParams(Reply(ParamsReplyHex));

        Assert.NotNull(reading);
        Assert.Equal(32, reading.CoolantC);
        Assert.Equal(2638, reading.PumpRpm);
        Assert.Equal(new[] { 0, 0, 0 }, reading.FanRpm);
        Assert.Equal("5ED6D8E566E1", reading.Mac);
    }

    [Fact]
    public void Rejects_a_reply_to_another_command()
    {
        Assert.Null(HydroShift2Protocol.DecodeParams(Reply(VersionReplyHex)));
        Assert.Null(HydroShift2Protocol.DecodeVersion(Reply(ParamsReplyHex)));
    }

    [Theory]
    [InlineData(1000, 1590)]
    [InlineData(1600, 1590)]
    [InlineData(2000, 1200)]
    [InlineData(2080, 1120)]
    [InlineData(2600, 600)]
    [InlineData(3200, 0)]
    [InlineData(4000, 0)]
    public void Pump_timer_follows_the_square_head_table(int rpm, int timer)
    {
        Assert.Equal(timer, HydroShift2Protocol.PumpTimer(rpm));
    }

    [Fact]
    public void Crc_is_ccitt_xmodem()
    {
        Assert.Equal(0x31C3, HydroShift2Protocol.Crc16Ccitt("123456789"u8));
    }

    [Fact]
    public void Sync_pump_fan_carries_the_timer_fans_and_crc()
    {
        var plain = Decrypt(HydroShift2Protocol.EncodeSyncPumpFan(2000, new byte[] { 10, 20, 30 }, 7));

        Assert.Equal(HydroShift2Protocol.CommandSyncPumpFan, plain[0]);
        var block = plain.AsSpan(8, 16).ToArray();
        Assert.Equal(new byte[] { 0xFF, 0x0F, 0xA2, 0x00, 0x04, 0xB0, 10, 20, 30 }, block[..9]);
        var crc = HydroShift2Protocol.Crc16Ccitt(block.AsSpan(0, 12));
        Assert.Equal(new[] { (byte)(crc >> 8), (byte)crc }, block[12..14]);
    }

    [Fact]
    public void Ring_packet_is_compressed_rgb_with_its_frame_trailer()
    {
        var rgb = Enumerable.Range(0, HydroShift2Protocol.RingLedCount * 3).Select(i => (byte)(i * 7)).ToArray();

        var packet = HydroShift2Protocol.EncodeRing(rgb, 1, 1, 9);

        var plain = Decrypt(packet);
        Assert.Equal(HydroShift2Protocol.CommandPushRgb, plain[0]);
        int length = (plain[12] << 24) | (plain[13] << 16) | (plain[14] << 8) | plain[15];
        var payload = packet.AsSpan(512).ToArray();
        Assert.Equal(length, payload.Length);
        Assert.Equal(new byte[] { 0, 1, 1, HydroShift2Protocol.RingLedCount }, payload[^4..]);
        var (_, decoded) = TinyUz.Decompress(payload.AsSpan(0, payload.Length - 4));
        Assert.Equal(rgb, decoded);
    }

    [Fact]
    public void Every_effect_fits_one_ring_upload()
    {
        foreach (var mode in HydroShift2RingEffects.Modes)
        {
            var (frames, interval) = HydroShift2RingEffects.Render(mode, [], 4, 0, false);
            var packed = frames.SelectMany(f => f).ToArray();

            var payload = HydroShift2Protocol.EncodeRing(packed, frames.Count, interval, 1).AsSpan(512).ToArray();

            Assert.Equal(new byte[] { (byte)(frames.Count >> 8), (byte)frames.Count, interval, HydroShift2Protocol.RingLedCount }, payload[^4..]);
            Assert.Equal(packed, TinyUz.Decompress(payload.AsSpan(0, payload.Length - 4)).Data);
        }
    }

    [Fact]
    public void Overlay_png_is_a_panel_sized_png()
    {
        var png = HydroShift2Protocol.EmptyOverlayPng();

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal(HydroShift2Protocol.Width, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(HydroShift2Protocol.Height, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
    }

    // ── driver ──

    [Fact]
    public void Connect_hides_the_firmware_overlay_and_reports_the_square_glass()
    {
        var driver = new HydroShift2LcdDriver();
        var pipe = new FirmwarePipe();

        var geometry = driver.Connect(pipe, null);

        Assert.Equal((480, 480), geometry);
        Assert.Equal("lianlih2s_0001_0027", driver.Firmware);
        Assert.Equal(
            new byte[]
            {
                HydroShift2Protocol.CommandGetVersion, HydroShift2Protocol.CommandStopPlay,
                HydroShift2Protocol.CommandFrameRate, HydroShift2Protocol.CommandSetClock,
                HydroShift2Protocol.CommandStopClock, HydroShift2Protocol.CommandPushPng,
            },
            pipe.Commands);
    }

    [Fact]
    public void Connect_fails_when_the_panel_never_answers()
    {
        var pipe = new FirmwarePipe { Silent = true };

        Assert.Null(new HydroShift2LcdDriver().Connect(pipe, null));
    }

    [Fact]
    public void Frame_is_one_transfer_with_increasing_timestamps()
    {
        var driver = new HydroShift2LcdDriver();
        var pipe = new FirmwarePipe();
        driver.Connect(pipe, null);
        pipe.Writes.Clear();

        Assert.True(driver.SendFrame(pipe, null, new byte[480 * 480 * 4]));
        Assert.True(driver.SendFrame(pipe, null, new byte[480 * 480 * 4]));

        Assert.All(pipe.Writes, w => Assert.Equal(new byte[] { 0xFF, 0xD8 }, w[512..514]));
        var stamps = pipe.Writes.Select(w => BitConverter.ToUInt32(Decrypt(w), 4)).ToArray();
        Assert.True(stamps[1] > stamps[0]);
    }

    [Fact]
    public void A_stale_reply_left_on_the_pipe_is_skipped()
    {
        var driver = new HydroShift2LcdDriver();
        var pipe = new FirmwarePipe();
        driver.Connect(pipe, null);
        pipe.Pending.Enqueue(Reply("7BC8"));

        var reading = driver.ReadParams(pipe);

        Assert.Equal(2638, reading?.PumpRpm);
    }

    [Fact]
    public void Disconnect_hands_a_driven_pump_back_to_the_defaults()
    {
        var driver = new HydroShift2LcdDriver();
        var pipe = new FirmwarePipe();
        driver.Connect(pipe, null);
        driver.SyncPumpFan(pipe, 3200, new byte[] { 0, 0, 0 }, driven: true);
        pipe.Writes.Clear();

        driver.Disconnect(pipe, null);

        var handBack = Assert.Single(pipe.SyncPumpFans);
        Assert.Equal(HydroShift2Protocol.PumpTimer(HydroShift2Protocol.DefaultPumpRpm), handBack.Timer);
        Assert.Equal(new byte[] { 80, 80, 80 }, handBack.Fans);
        Assert.Equal(HydroShift2Protocol.CommandStopPlay, pipe.Commands[^1]);
    }

    [Fact]
    public void Disconnect_leaves_an_undriven_or_handed_back_pump_alone()
    {
        var driver = new HydroShift2LcdDriver();
        var pipe = new FirmwarePipe();
        driver.Connect(pipe, null);
        driver.SyncPumpFan(pipe, 3200, new byte[] { 0, 0, 0 }, driven: true);
        driver.SyncPumpFan(pipe, 2080, new byte[] { 80, 80, 80 }, driven: false);
        pipe.Writes.Clear();

        driver.Disconnect(pipe, null);

        Assert.Equal(new[] { HydroShift2Protocol.CommandStopPlay }, pipe.Commands);
    }

    [Fact]
    public void Turning_nexus_control_off_hands_the_pump_back_through_the_hub()
    {
        var driver = new HydroShift2LcdDriver();
        var hub = new BulkPanelHub(driver);
        var pipe = new FirmwarePipe();
        hub.Attach(pipe, null);
        var aio = new HydroShift2Aio(hub, driver, _ => false);
        aio.SetPumpDuty(100);
        aio.Tick(10_000);
        pipe.Writes.Clear();

        hub.Detach();

        Assert.Equal(HydroShift2Protocol.PumpTimer(HydroShift2Protocol.DefaultPumpRpm), Assert.Single(pipe.SyncPumpFans).Timer);
    }

    [Theory]
    [InlineData(0, 0.5f, 0.08f)]
    [InlineData(3, 0.92f, 0.08f)]
    [InlineData(6, 0.92f, 0.5f)]
    [InlineData(9, 0.92f, 0.92f)]
    [InlineData(12, 0.5f, 0.92f)]
    [InlineData(15, 0.08f, 0.92f)]
    [InlineData(18, 0.08f, 0.5f)]
    [InlineData(21, 0.08f, 0.08f)]
    public void Ring_leds_sit_round_the_square_bezel(int index, float u, float v)
    {
        var (actualU, actualV) = Nexus.Service.Lighting.HydroShift2LightingDeviceProvider.SquareRingPosition(index);
        Assert.Equal(u, actualU, 3);
        Assert.Equal(v, actualV, 3);
    }

    // ── AIO loop ──

    [Fact]
    public void Polls_telemetry_and_leaves_the_pump_alone_until_driven()
    {
        var (aio, pipe) = Attached();

        aio.Tick(10_000);

        Assert.Equal(32, aio.Params?.CoolantC);
        Assert.DoesNotContain(HydroShift2Protocol.CommandSyncPumpFan, pipe.Commands);
        Assert.False(aio.FanPresent(0));
    }

    [Fact]
    public void A_driven_pump_is_resent_on_the_firmware_cadence_and_stops_on_release()
    {
        var (aio, pipe) = Attached();
        aio.SetPumpDuty(50);

        aio.Tick(10_000);
        aio.Tick(12_000);
        Assert.Single(pipe.SyncPumpFans);
        Assert.Equal(HydroShift2Protocol.PumpTimer(2400), pipe.SyncPumpFans[0].Timer);
        Assert.Equal(new byte[] { 80, 80, 80 }, pipe.SyncPumpFans[0].Fans);

        aio.Tick(13_700);
        Assert.Equal(2, pipe.SyncPumpFans.Count);

        aio.SetPumpDuty(null);
        aio.Tick(20_000);
        aio.Tick(30_000);
        Assert.Equal(3, pipe.SyncPumpFans.Count);
        Assert.Equal(HydroShift2Protocol.PumpTimer(2080), pipe.SyncPumpFans[2].Timer);
        Assert.Equal(new byte[] { 80, 80, 80 }, pipe.SyncPumpFans[2].Fans);
    }

    [Fact]
    public void A_fan_left_at_zero_is_handed_back_and_reports_what_was_sent()
    {
        var (aio, pipe) = Attached();
        aio.SetFanDuty(0, 0);
        aio.Tick(10_000);
        Assert.Equal(0, aio.SentFanDuty(0));
        Assert.Equal(31, aio.SentFanDuty(1));

        aio.SetFanDuty(0, null);
        aio.Tick(10_250);

        Assert.Equal(new byte[] { 80, 80, 80 }, pipe.SyncPumpFans[^1].Fans);
        Assert.Equal(31, aio.SentFanDuty(0));
    }

    [Fact]
    public void A_new_duty_goes_out_on_the_next_tick()
    {
        var (aio, pipe) = Attached();
        aio.SetFanDuty(1, 100);
        aio.Tick(10_000);

        aio.SetFanDuty(1, 0);
        aio.Tick(10_250);

        Assert.Equal(2, pipe.SyncPumpFans.Count);
        Assert.Equal(new byte[] { 80, 0, 80 }, pipe.SyncPumpFans[1].Fans);
    }

    [Fact]
    public void Ring_uploads_only_changes_and_no_faster_than_every_two_seconds()
    {
        var (aio, pipe) = Attached();
        var red = Solid(255, 0, 0);

        aio.SetRing(red);
        aio.Tick(10_000);
        aio.SetRing(red);
        aio.Tick(10_250);
        aio.SetRing(Solid(0, 255, 0));
        aio.Tick(10_500);
        Assert.Equal(1, pipe.Commands.Count(c => c == HydroShift2Protocol.CommandPushRgb));

        aio.Tick(11_900);
        Assert.Equal(1, pipe.Commands.Count(c => c == HydroShift2Protocol.CommandPushRgb));

        aio.Tick(12_000);
        Assert.Equal(2, pipe.Commands.Count(c => c == HydroShift2Protocol.CommandPushRgb));
    }

    [Fact]
    public void Only_this_units_wireless_binding_takes_it_over()
    {
        var (aio, _) = Attached(mac => mac is null || mac == "AABBCCDDEEFF");
        aio.Tick(10_000);

        Assert.True(aio.IsAvailable);
    }

    [Fact]
    public void An_unanswered_ring_upload_is_tried_again()
    {
        var (aio, pipe) = Attached();
        pipe.Silent = true;
        aio.SetRing(Solid(255, 0, 0));
        aio.Tick(10_000);
        pipe.Silent = false;

        aio.Tick(12_000);

        Assert.Equal(2, pipe.Commands.Count(c => c == HydroShift2Protocol.CommandPushRgb));
    }

    [Fact]
    public void The_ring_is_reuploaded_when_the_wireless_link_lets_go()
    {
        var owned = false;
        var (aio, pipe) = Attached(_ => owned);
        aio.SetRing(Solid(255, 0, 0));
        aio.Tick(10_000);
        owned = true;
        aio.Tick(12_000);
        owned = false;

        aio.Tick(14_000);

        Assert.Equal(2, pipe.Commands.Count(c => c == HydroShift2Protocol.CommandPushRgb));
    }

    [Fact]
    public void A_wirelessly_bound_aio_is_left_to_the_dongle()
    {
        var (aio, pipe) = Attached(_ => true);
        aio.SetPumpDuty(100);
        aio.SetRing(Solid(255, 0, 0));

        aio.Tick(10_000);

        Assert.False(aio.IsAvailable);
        Assert.DoesNotContain(HydroShift2Protocol.CommandSyncPumpFan, pipe.Commands);
        Assert.DoesNotContain(HydroShift2Protocol.CommandPushRgb, pipe.Commands);
        Assert.Empty(new HydroShift2CoolingProvider(aio).GetFanChannels());
    }

    [Fact]
    public void Cooling_exposes_the_pump_and_the_coolant_probe()
    {
        var (aio, _) = Attached();
        aio.Tick(10_000);
        var cooling = new HydroShift2CoolingProvider(aio);

        var pump = Assert.Single(cooling.GetFanChannels());
        Assert.Equal(FanKinds.Pump, pump.Kind);
        Assert.Equal(2638, pump.Rpm);
        Assert.Equal(FanModes.Auto, pump.Mode);
        var coolant = Assert.Single(cooling.GetTemperatureSources());
        Assert.Equal(32f, coolant.Value);

        cooling.SetFanSpeed(pump.Id, 75);
        Assert.Equal(75, aio.PumpDuty);
        Assert.Equal(HydroShift2Protocol.PumpDutyFloor, cooling.SetFanSpeed(pump.Id, 0));
        Assert.Equal(HydroShift2Protocol.PumpDutyFloor, aio.PumpDuty);
        cooling.ReleaseAll();
        Assert.Null(aio.PumpDuty);
    }

    private static (HydroShift2Aio Aio, FirmwarePipe Pipe) Attached(Func<string?, bool>? wirelessOwns = null)
    {
        var driver = new HydroShift2LcdDriver();
        var hub = new BulkPanelHub(driver);
        var pipe = new FirmwarePipe();
        Assert.True(hub.Attach(pipe, null));
        pipe.Writes.Clear();
        return (new HydroShift2Aio(hub, driver, wirelessOwns ?? (_ => false)), pipe);
    }

    private static byte[] Solid(byte r, byte g, byte b)
    {
        var rgb = new byte[HydroShift2Protocol.RingLedCount * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }
        return rgb;
    }

    private static byte[] Reply(string hex)
    {
        var reply = new byte[512];
        Convert.FromHexString(hex).CopyTo(reply, 0);
        return reply;
    }

    private static byte[] Decrypt(byte[] packet)
    {
#pragma warning disable CA5351 // Device firmware's cipher.
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = "slv3tuzx"u8.ToArray();
        des.IV = des.Key;
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        using var decryptor = des.CreateDecryptor();
        return decryptor.TransformFinalBlock(packet, 0, 504);
    }

    /// <summary>Stands in for the panel: answers every command with a reply echoing it.</summary>
    private sealed class FirmwarePipe : IBulkUsbPipe
    {
        public List<byte[]> Writes { get; } = new();
        public Queue<byte[]> Pending { get; } = new();
        public bool Silent { get; set; }

        public byte[] Commands => Writes.Select(w => Decrypt(w)[0]).ToArray();

        public List<(int Timer, byte[] Fans)> SyncPumpFans => Writes
            .Select(Decrypt)
            .Where(p => p[0] == HydroShift2Protocol.CommandSyncPumpFan)
            .Select(p => ((p[12] << 8) | p[13], p[14..17]))
            .ToList();

        public bool Write(ReadOnlySpan<byte> data)
        {
            var packet = data.ToArray();
            Writes.Add(packet);
            if (!Silent)
            {
                var command = Decrypt(packet)[0];
                Pending.Enqueue(command switch
                {
                    HydroShift2Protocol.CommandGetVersion => Reply(VersionReplyHex),
                    HydroShift2Protocol.CommandGetParams => Reply(ParamsReplyHex),
                    _ => Reply(command.ToString("X2") + "C8"),
                });
            }
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (!Pending.TryDequeue(out var reply))
            {
                return 0;
            }
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() { }
    }
}
