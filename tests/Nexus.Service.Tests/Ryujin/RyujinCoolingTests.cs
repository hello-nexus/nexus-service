using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Ryujin;

namespace Nexus.Service.Tests.Ryujin;

internal sealed class RyujinDeviceFake : IHidDevice
{
    private readonly Queue<byte[]> _replies = new();

    public List<byte[]> Writes { get; } = new();
    public Action<byte[]>? OnWrite { get; set; }
    public bool WriteResult { get; set; } = true;
    public int ReadResultWhenEmpty { get; set; }

    public int VendorId => 0x0B05;
    public int ProductId => 0x1BCB;
    public string Path => "fake";
    public string? Serial => null;
    public int UsagePage => 0xFF72;
    public int Usage => 0x01;

    public void QueueReply(byte[] reply) => _replies.Enqueue(reply);

    public bool Write(ReadOnlySpan<byte> report)
    {
        var copy = report.ToArray();
        Writes.Add(copy);
        OnWrite?.Invoke(copy);
        return WriteResult;
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_replies.Count == 0)
        {
            return ReadResultWhenEmpty;
        }
        var reply = _replies.Dequeue();
        reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
        return Math.Min(reply.Length, buffer.Length);
    }

    public bool SetFeature(ReadOnlySpan<byte> report) => false;
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
    public void Dispose() { }
}

public class RyujinCoolingTests
{
    private static byte[] StatusReply(int tempWhole, int tempTenth, int pumpRpm, int fanRpm)
    {
        var r = new byte[65];
        r[0] = 0xEC;
        r[1] = 0x19;
        r[5] = (byte)tempWhole;
        r[6] = (byte)tempTenth;
        r[7] = (byte)(pumpRpm & 0xFF);
        r[8] = (byte)(pumpRpm >> 8);
        r[10] = (byte)(fanRpm & 0xFF);
        r[11] = (byte)(fanRpm >> 8);
        return r;
    }

    private static byte[] DutyReply(int pump, int fan)
    {
        var r = new byte[65];
        r[0] = 0xEC;
        r[1] = 0x1A;
        r[4] = (byte)pump;
        r[5] = (byte)fan;
        return r;
    }

    private static (RyujinCoolingHub hub, RyujinCoolingProvider provider, RyujinDeviceFake fake) Connected()
    {
        var fake = new RyujinDeviceFake();
        var hub = new RyujinCoolingHub();
        hub.Attach(fake);
        return (hub, new RyujinCoolingProvider(hub), fake);
    }

    // ---- wire bytes ----

    [Theory]
    [InlineData(20, 19, 0x14, 0x13)]
    [InlineData(40, 30, 0x28, 0x1E)]
    [InlineData(55, 40, 0x37, 0x28)]
    [InlineData(20, 20, 0x14, 0x14)]
    [InlineData(20, 21, 0x14, 0x15)]
    [InlineData(100, 100, 0x64, 0x64)]
    public void Duty_report_matches_the_armoury_crate_capture(int pump, int fan, byte pumpByte, byte fanByte)
    {
        var report = RyujinCoolingProtocol.EncodeSetDuty(pump, fan);

        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, pumpByte, fanByte }, report[0..5]);
        Assert.All(report[5..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Requests_are_the_status_and_duty_headers_and_never_the_external_fan_command()
    {
        Assert.Equal(new byte[] { 0xEC, 0x99, 0x00 }, RyujinCoolingProtocol.EncodeStatusRequest()[0..3]);
        Assert.Equal(new byte[] { 0xEC, 0x9A, 0x00 }, RyujinCoolingProtocol.EncodeDutyRequest()[0..3]);
        Assert.Equal(65, RyujinCoolingProtocol.EncodeStatusRequest().Length);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10, 20)]
    [InlineData(19, 20)]
    [InlineData(20, 20)]
    [InlineData(64, 64)]
    [InlineData(250, 100)]
    [InlineData(-5, 20)]
    public void Pump_duty_is_clamped_to_the_floor_and_a_hundred(int request, int expected)
    {
        Assert.Equal(expected, RyujinCoolingProtocol.EncodeSetDuty(request, 50)[3]);
        Assert.Equal(expected, RyujinCoolingProtocol.ClampPump(request));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150, 100)]
    [InlineData(-1, 0)]
    public void Fan_duty_is_clamped_to_zero_through_a_hundred(int request, int expected)
    {
        Assert.Equal(expected, RyujinCoolingProtocol.EncodeSetDuty(50, request)[4]);
    }

    // ---- response parsing (liquidctl offsets for the III family) ----

    [Fact]
    public void Status_reply_parses_temperature_and_both_speeds()
    {
        var ok = RyujinCoolingProtocol.TryParseStatus(StatusReply(32, 7, 0x0A8C, 0x04B0), out var temp, out var pump, out var fan);

        Assert.True(ok);
        Assert.Equal(32.7, temp, 3);
        Assert.Equal(2700, pump);
        Assert.Equal(1200, fan);
    }

    [Fact]
    public void Duty_reply_parses_pump_then_fan()
    {
        Assert.True(RyujinCoolingProtocol.TryParseDuty(DutyReply(55, 40), out var pump, out var fan));
        Assert.Equal(55, pump);
        Assert.Equal(40, fan);
    }

    [Fact]
    public void Replies_with_a_foreign_header_or_a_short_body_are_rejected()
    {
        var wrongHeader = StatusReply(30, 0, 1000, 1000);
        wrongHeader[1] = 0x1A;
        Assert.False(RyujinCoolingProtocol.TryParseStatus(wrongHeader, out _, out _, out _));
        Assert.False(RyujinCoolingProtocol.TryParseStatus(new byte[8], out _, out _, out _));
        Assert.False(RyujinCoolingProtocol.TryParseDuty(DutyReply(101, 40), out _, out _));
        Assert.False(RyujinCoolingProtocol.TryParseDuty(new byte[4], out _, out _));
    }

    [Fact]
    public void Poll_sends_status_then_duty_request_and_fills_the_snapshot()
    {
        var (hub, _, fake) = Connected();
        fake.QueueReply(StatusReply(31, 5, 2400, 900));
        fake.QueueReply(DutyReply(60, 45));

        Assert.True(hub.Poll());

        Assert.Equal(2, fake.Writes.Count);
        Assert.Equal(0x99, fake.Writes[0][1]);
        Assert.Equal(0x9A, fake.Writes[1][1]);
        var snap = hub.Snapshot;
        Assert.True(snap.StatusFresh);
        Assert.Equal(31.5, snap.LiquidTempC, 3);
        Assert.Equal(2400, snap.PumpRpm);
        Assert.Equal(900, snap.FanRpm);
        Assert.Equal(60, snap.PumpDuty);
        Assert.Equal(45, snap.FanDuty);
    }

    [Fact]
    public void Poll_skips_an_unrelated_report_and_a_silent_device_is_not_a_failure()
    {
        var (hub, _, fake) = Connected();
        fake.QueueReply(DutyReply(10, 10));
        fake.QueueReply(StatusReply(30, 0, 1500, 800));

        Assert.True(hub.Poll());
        Assert.Equal(1500, hub.Snapshot.PumpRpm);

        var (silentHub, _, _) = Connected();
        Assert.True(silentHub.Poll());
        Assert.False(silentHub.Snapshot.StatusFresh);
    }

    [Fact]
    public void Poll_fails_when_a_write_fails_or_the_device_is_gone()
    {
        var (hub, _, fake) = Connected();
        fake.WriteResult = false;
        Assert.False(hub.Poll());

        var (goneHub, _, goneFake) = Connected();
        goneFake.ReadResultWhenEmpty = -1;
        Assert.False(goneHub.Poll());
    }

    // ---- provider ----

    [Fact]
    public void Provider_lists_nothing_until_connected()
    {
        var provider = new RyujinCoolingProvider(new RyujinCoolingHub());

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetTemperatureSources());
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public void Provider_exposes_pump_and_fan_channels_and_the_liquid_probe()
    {
        var (hub, provider, fake) = Connected();
        fake.QueueReply(StatusReply(33, 2, 2500, 1100));
        fake.QueueReply(DutyReply(70, 50));
        hub.Poll();

        var channels = provider.GetFanChannels();
        var pump = Assert.Single(channels, c => c.Kind == FanKinds.Pump);
        var fan = Assert.Single(channels, c => c.Kind == FanKinds.Fan);
        Assert.Equal("asus-ryujin-lcd:pump", pump.Id);
        Assert.Equal("asus-ryujin-lcd:fan", fan.Id);
        Assert.Equal(20, pump.MinDuty);
        Assert.Equal(2500, pump.Rpm);
        Assert.Equal(70, pump.DutyPercent);
        Assert.Equal(1100, fan.Rpm);
        Assert.All(channels, c => Assert.Equal(FanModes.Auto, c.Mode));

        var source = Assert.Single(provider.GetTemperatureSources());
        Assert.Equal("asus-ryujin-lcd:liquid", source.Id);
        Assert.Equal(33.2f, source.Value, 2);
        Assert.Equal(source.Value, provider.ReadTemperature("asus-ryujin-lcd:liquid"));
    }

    [Fact]
    public void Provider_has_no_liquid_temperature_before_a_status_reply()
    {
        var (_, provider, _) = Connected();

        Assert.Null(provider.ReadTemperature("asus-ryujin-lcd:liquid"));
        Assert.Empty(provider.GetTemperatureSources());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(5, 20)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    public void Pump_writes_are_clamped_and_never_reach_zero(int request, int expected)
    {
        var (hub, provider, fake) = Connected();

        int result = provider.SetFanSpeed("asus-ryujin-lcd:pump", request);
        hub.Flush();
        provider.DriveFanSpeed("asus-ryujin-lcd:pump", request);
        hub.Flush();

        Assert.Equal(expected, result);
        Assert.All(fake.Writes, w => Assert.Equal((byte)expected, w[3]));
    }

    [Fact]
    public void A_fan_write_carries_the_pump_at_its_reported_duty_not_a_guess()
    {
        var (hub, provider, fake) = Connected();
        fake.QueueReply(StatusReply(30, 0, 2000, 800));
        fake.QueueReply(DutyReply(64, 30));
        hub.Poll();
        fake.Writes.Clear();

        provider.SetFanSpeed("asus-ryujin-lcd:fan", 40);
        hub.Flush();

        Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, 64, 40 }, fake.Writes[0][0..5]);
    }

    [Fact]
    public void Before_any_reply_the_undriven_channel_rides_at_the_safe_duty()
    {
        var (hub, provider, fake) = Connected();

        provider.SetFanSpeed("asus-ryujin-lcd:pump", 55);
        hub.Flush();

        Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, 55, 100 }, fake.Writes[0][0..5]);
    }

    [Fact]
    public void Writes_are_dropped_while_disconnected()
    {
        var hub = new RyujinCoolingHub();
        var provider = new RyujinCoolingProvider(hub);

        Assert.Equal(55, provider.SetFanSpeed("asus-ryujin-lcd:pump", 55));
        Assert.False(hub.SetDuty(55, 30));
    }

    // ---- keepalive ----

    [Fact]
    public void Keepalive_cadence_matches_armoury_crate()
    {
        Assert.Equal(3500, RyujinCoolingProtocol.KeepaliveMs);
    }

    [Fact]
    public void Reassert_resends_the_commanded_duty_and_is_silent_when_nothing_is_driven()
    {
        var (hub, provider, fake) = Connected();

        provider.ReassertControl();
        hub.Flush();
        Assert.Empty(fake.Writes);

        provider.SetFanSpeed("asus-ryujin-lcd:pump", 55);
        hub.Flush();
        fake.Writes.Clear();
        provider.ReassertControl();
        hub.Flush();
        provider.ReassertControl();
        hub.Flush();

        Assert.Equal(2, fake.Writes.Count);
        Assert.All(fake.Writes, w => Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, 55 }, w[0..4]));
    }

    // ---- release ----

    [Fact]
    public void Releasing_a_driven_channel_hands_back_the_safe_duty_and_leaves_the_other_alone()
    {
        var (hub, provider, fake) = Connected();
        fake.QueueReply(StatusReply(30, 0, 2000, 800));
        fake.QueueReply(DutyReply(60, 30));
        hub.Poll();
        provider.SetFanSpeed("asus-ryujin-lcd:pump", 40);
        hub.Flush();
        fake.Writes.Clear();

        provider.ReleaseFan("asus-ryujin-lcd:pump");
        hub.Flush();

        Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, 100, 30 }, fake.Writes[0][0..5]);
        provider.ReassertControl();
        hub.Flush();
        Assert.Single(fake.Writes);
        Assert.All(provider.GetFanChannels(), c => Assert.Equal(FanModes.Auto, c.Mode));
    }

    [Fact]
    public void Release_all_writes_nothing_when_nothing_was_driven()
    {
        var (hub, provider, fake) = Connected();

        provider.ReleaseAll();
        hub.Flush();

        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void Release_all_raises_the_pump_and_leaves_the_fan_at_its_duty()
    {
        var (hub, provider, fake) = Connected();
        provider.SetFanSpeed("asus-ryujin-lcd:pump", 40);
        provider.SetFanSpeed("asus-ryujin-lcd:fan", 25);
        hub.Flush();
        fake.Writes.Clear();

        provider.ReleaseAll();
        hub.Flush();

        Assert.Single(fake.Writes);
        Assert.Equal(new byte[] { 0xEC, 0x1A, 0x01, 100, 25 }, fake.Writes[0][0..5]);
    }

    [Fact]
    public void Releasing_the_fan_alone_writes_nothing_so_off_is_never_louder()
    {
        var (hub, provider, fake) = Connected();
        provider.SetFanSpeed("asus-ryujin-lcd:fan", 25);
        hub.Flush();
        fake.Writes.Clear();

        provider.ReleaseFan("asus-ryujin-lcd:fan");
        hub.Flush();

        Assert.Empty(fake.Writes);
        Assert.All(provider.GetFanChannels(), c => Assert.Equal(FanModes.Auto, c.Mode));
    }

    [Fact]
    public void Liquid_temperature_expires_when_the_status_stops_arriving()
    {
        long now = 1000;
        var (hub, provider, fake) = Connected();
        hub.NowMs = () => now;
        fake.QueueReply(StatusReply(30, 0, 2000, 800));
        fake.QueueReply(DutyReply(60, 30));
        hub.Poll();
        Assert.NotNull(provider.ReadTemperature("asus-ryujin-lcd:liquid"));

        now += 3L * RyujinCoolingProtocol.KeepaliveMs + 1;

        Assert.Null(provider.ReadTemperature("asus-ryujin-lcd:liquid"));
        Assert.Empty(provider.GetTemperatureSources());
    }

    [Fact]
    public void Implausible_liquid_temperature_is_rejected()
    {
        Assert.False(RyujinCoolingProtocol.TryParseStatus(StatusReply(101, 0, 1000, 1000), out _, out _, out _));
        Assert.True(RyujinCoolingProtocol.TryParseStatus(StatusReply(100, 0, 1000, 1000), out _, out _, out _));
    }

    [Fact]
    public void Undriven_channels_show_the_safe_duty_before_any_reply()
    {
        var (_, provider, _) = Connected();

        Assert.All(provider.GetFanChannels(), c => Assert.Equal(100, c.DutyPercent));
    }

    [Fact]
    public void Ids_and_gate_key_line_up_with_the_lcd_driver()
    {
        Assert.Equal(RyujinCoolingProtocol.HandlerId, new RyujinPanelDriver().HandlerId);
        Assert.Equal(RyujinCoolingProtocol.HandlerId, RyujinCoolingProvider.DeviceId);
        Assert.Equal(RyujinProtocol.ProductIdExtreme, RyujinCoolingProtocol.ProductId);
        Assert.True(RyujinCoolingProvider.IsRyujinId("asus-ryujin-lcd:pump"));
        Assert.False(RyujinCoolingProvider.IsRyujinId("asus-slc4-lcd:pump"));
    }

    // ---- coexistence with the LCD ----

    [Fact]
    public async Task A_duty_report_never_lands_inside_an_lcd_upload_and_its_commit()
    {
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var cooling = new RyujinDeviceFake { OnWrite = _ => order.Enqueue("duty") };
        var hub = new RyujinCoolingHub();
        hub.Attach(cooling);
        Assert.True(hub.SetDuty(55, 40));
        var driver = new RyujinPanelDriver();
        var lcdHid = new RyujinDeviceFake { OnWrite = _ => order.Enqueue("commit") };
        var pipe = new SlowPipe(order);
        var frame = new byte[RyujinProtocol.Width * RyujinProtocol.Height * 4];

        var upload = Task.Run(() => driver.SendFrame(pipe, lcdHid, frame));
        Assert.True(pipe.UploadStarted.Wait(5000));
        var flusher = new Thread(() => hub.Flush());
        flusher.Start();
        // Blocked on the write gate the upload still holds.
        while (!flusher.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin))
        {
            Thread.Yield();
        }
        Assert.Empty(cooling.Writes);
        pipe.Release.Set();

        Assert.True(await upload);
        flusher.Join();
        var sequence = order.ToArray();
        Assert.Equal("commit", sequence[^2]);
        Assert.Equal("duty", sequence[^1]);
        Assert.DoesNotContain("duty", sequence[..^1]);
    }

    [Fact]
    public void Setting_a_duty_never_waits_on_the_lcd_write_gate()
    {
        var (hub, provider, _) = Connected();
        bool done;
        lock (RyujinProtocol.HidWriteGate)
        {
            var caller = new Thread(() => provider.SetFanSpeed("asus-ryujin-lcd:pump", 60));
            caller.Start();
            done = caller.Join(5000);
        }

        Assert.True(done);
        Assert.True(hub.Wake.Wait(0));
    }

    [Fact]
    public void Pump_and_fan_work_with_no_lcd_attached()
    {
        var (hub, provider, fake) = Connected();

        provider.SetFanSpeed("asus-ryujin-lcd:pump", 60);
        hub.Flush();

        Assert.Single(fake.Writes);
    }

    private sealed class SlowPipe : IBulkUsbPipe
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _order;
        private int _writes;

        public SlowPipe(System.Collections.Concurrent.ConcurrentQueue<string> order)
        {
            _order = order;
        }

        public ManualResetEventSlim UploadStarted { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public int ProductId => 0x1BCB;
        public bool IsGone => false;

        public bool Write(ReadOnlySpan<byte> data)
        {
            _order.Enqueue("bulk");
            if (Interlocked.Increment(ref _writes) == 1)
            {
                UploadStarted.Set();
                Release.Wait(5000);
            }
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
