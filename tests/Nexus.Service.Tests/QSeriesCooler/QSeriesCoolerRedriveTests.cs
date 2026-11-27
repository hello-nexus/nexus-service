using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>The heartbeat re-drive: a cooler that leaves software mode while Nexus drives it gets its stored duties replayed.</summary>
public class QSeriesCoolerRedriveTests
{
    private const string PumpId = "qseries:QTEST123:pump";
    private const string FanId = "qseries:QTEST123:p2:1";

    private static List<byte[]> FanFrames(ScriptedTransport t) =>
        t.Writes.Where(w => w.Length >= 4 && w[1] == 0xCC && w[2] == 0x02 && w[3] == QSeriesCoolerProtocol.FanChannel).ToList();

    [Fact]
    public void Mode_flip_replays_the_fan_frame_once()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(FanId, 45);
        var before = FanFrames(t).Count;

        t.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        p.RedriveIfControlDropped();

        Assert.Equal(before + 1, FanFrames(t).Count);
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, hub.State.ControlMode);
        Assert.Contains(t.Writes, w => w.Length >= 5 && w[2] == 0x02 && w[3] == 0 && w[4] == QSeriesCoolerProtocol.ControlModeSoftware);
    }

    [Fact]
    public void Repeated_flips_inside_the_interval_are_rate_limited()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);
        var before = FanFrames(t).Count;

        void Flip()
        {
            t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            p.RedriveIfControlDropped();
        }
        Flip();
        clock.Now += 9_000;
        Flip();
        Assert.Equal(before + 1, FanFrames(t).Count);

        clock.Now += 1_500;
        Flip();
        Assert.Equal(before + 2, FanFrames(t).Count);
    }

    [Fact]
    public void Nothing_is_resent_when_nexus_drives_no_channel()
    {
        var (hub, t, p, _) = New();
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        t.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;

        p.RedriveIfControlDropped();

        Assert.DoesNotContain(t.Writes, w => w.Length >= 4 && w[1] == 0xCC && w[2] == 0x02);
    }

    [Fact]
    public void Nothing_is_resent_when_the_mode_is_still_software()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(FanId, 45);
        var before = t.Writes.Count;

        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
        p.RedriveIfControlDropped();

        Assert.Equal(before, t.Writes.Count);
    }

    [Fact]
    public void A_user_pinned_non_software_mode_is_left_alone()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(FanId, 45);
        hub.SetControlMode(QSeriesCoolerProtocol.ControlModeMotherboard);
        t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
        var before = t.Writes.Count;

        p.RedriveIfControlDropped();

        Assert.Equal(before, t.Writes.Count);
    }

    [Fact]
    public void A_driven_pump_duty_is_replayed_with_the_fans()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(PumpId, 100);
        p.SetFanSpeed(FanId, 45);
        t.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        var before = t.Writes.Count;
        var fanBefore = FanFrames(t).Count;

        p.RedriveIfControlDropped();

        var pumpFrames = t.Writes.Skip(before).Where(w => w.Length >= 6 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0).ToList();
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, pumpFrames[0][4]);
        Assert.Equal(2, pumpFrames.Count);
        Assert.All(pumpFrames, f => Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(100, true), f[5]));
        Assert.Equal(fanBefore + 1, FanFrames(t).Count);
    }

    [Fact]
    public void Reconnect_with_assignments_replays_the_driven_fans()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(FanId, 45);
        var before = FanFrames(t).Count;

        p.OnHubConnected();

        Assert.Equal(before, FanFrames(t).Count);  // no persisted assignment in this store: hand-back path, no replay
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds[FanId] = 45);
        var p2 = new QSeriesCoolerCoolingProvider(hub, store);
        p2.SetFanSpeed(FanId, 45);
        var n = FanFrames(t).Count;

        p2.OnHubConnected();

        Assert.Equal(n + 1, FanFrames(t).Count);
    }

    [Fact]
    public void Reconnect_with_a_non_software_pin_does_not_flip_to_software()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds[FanId] = 45);
        var (hub, t, _, _) = New();
        var p = new QSeriesCoolerCoolingProvider(hub, store);
        p.SetFanSpeed(FanId, 45);
        hub.SetControlMode(QSeriesCoolerProtocol.ControlModeFirmware);
        t.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        var before = t.Writes.Count;

        p.OnHubConnected();
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeFirmware;
        p.RedriveIfControlDropped();

        Assert.DoesNotContain(t.Writes.Skip(before), w => w.Length >= 6 && w[1] == 0xCC && w[2] == 0x02
            && (w[3] == QSeriesCoolerProtocol.FanChannel || w[4] == QSeriesCoolerProtocol.ControlModeSoftware));
    }

    [Fact]
    public void No_replay_after_every_channel_was_released()
    {
        var (hub, t, p, _) = New();
        p.SetFanSpeed(FanId, 45);
        p.ReleaseAll();
        t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
        var before = t.Writes.Count;

        p.RedriveIfControlDropped();

        Assert.Equal(before, t.Writes.Count);
    }

    [Fact]
    public void A_device_that_keeps_flipping_is_replayed_on_a_doubling_interval()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);

        int Flip()
        {
            var n = FanFrames(t).Count;
            t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            p.RedriveIfControlDropped();
            return FanFrames(t).Count - n;
        }
        Assert.Equal(1, Flip());          // first replay
        clock.Now += 10_500;
        Assert.Equal(1, Flip());          // second, interval now 20 s
        clock.Now += 10_500;
        Assert.Equal(0, Flip());          // inside the doubled interval
        clock.Now += 10_000;
        Assert.Equal(1, Flip());

        // Software mode holding long enough resets the interval.
        hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
        p.RedriveIfControlDropped();
        clock.Now += 61_000;
        p.RedriveIfControlDropped();
        Assert.Equal(1, Flip());
        clock.Now += 10_500;
        Assert.Equal(1, Flip());
        clock.Now += 10_500;
        Assert.Equal(0, Flip());
    }

    [Fact]
    public async Task Reconnect_replay_and_engine_duty_writes_do_not_deadlock()
    {
        var curve = Enumerable.Range(0, 5).Select(i => new QSeriesFirmwareCurvePoint { PumpTempC = 30 + i * 5, PumpDutyPercent = 50, FanTempC = 30 + i * 5, FanDutyPercent = 50 }).ToArray();
        var (hub, t, _, _) = New();
        t.Curve = curve;
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds[FanId] = 45);
        var p = new QSeriesCoolerCoolingProvider(hub, store);
        p.SetFanSpeed(FanId, 45);

        var reconnects = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) p.OnHubConnected();
        });
        var engine = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) p.SetFanSpeed(FanId, 30 + i % 40);
        });

        await Task.WhenAll(reconnects, engine).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Reconnect_resets_the_backoff()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.ManualSpeeds[FanId] = 45);
        var (hub, t, _, clock) = New();
        var p = new QSeriesCoolerCoolingProvider(hub, store, clock: () => clock.Now);
        p.SetFanSpeed(FanId, 45);

        int Flip()
        {
            var n = FanFrames(t).Count;
            t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            hub.State.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;
            p.RedriveIfControlDropped();
            return FanFrames(t).Count - n;
        }
        Assert.Equal(1, Flip());
        clock.Now += 10_500;
        Assert.Equal(1, Flip());   // interval is now doubled

        p.OnHubConnected();
        clock.Now += 10_500;
        Assert.Equal(1, Flip());
        clock.Now += 10_500;
        Assert.Equal(1, Flip());   // a stale doubled interval would skip this one
    }

    private static List<byte[]> LightFrames(ScriptedTransport t, int port) =>
        t.Writes.Where(w => w.Length >= 4 && w[0] == 0xFF && w[1] == 0xEE && w[3] == port).ToList();

    [Fact]
    public void An_empty_fan_port_gets_one_blank_frame_and_nothing_per_tick()
    {
        var (hub, t, _, _) = New();
        for (var i = 0; i < 10; i++) hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);

        Assert.Single(LightFrames(t, QSeriesCoolerProtocol.FanChannel));
    }

    [Fact]
    public void A_port_with_leds_is_written_every_tick()
    {
        var (hub, t, _, _) = New();
        for (var i = 0; i < 5; i++) hub.WriteLinkLighting(QSeriesCoolerProtocol.LinkChannel1, new[] { new RgbColor(1, 2, 3) });

        Assert.Equal(5, LightFrames(t, QSeriesCoolerProtocol.LinkChannel1).Count);
    }

    [Fact]
    public void A_lit_chain_going_empty_sends_one_blank_then_stops()
    {
        var (hub, t, _, _) = New();
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, new[] { new RgbColor(1, 2, 3) });
        for (var i = 0; i < 5; i++) hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);

        Assert.Equal(2, LightFrames(t, QSeriesCoolerProtocol.FanChannel).Count);
        // Lit again later: the blank state resets, so a later empty chain blanks once more.
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, new[] { new RgbColor(1, 2, 3) });
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);
        Assert.Equal(4, LightFrames(t, QSeriesCoolerProtocol.FanChannel).Count);
    }

    [Fact]
    public void A_reconnect_blanks_an_empty_port_again()
    {
        var (hub, t, _, _) = New();
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);
        hub.Disconnect();
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);
        hub.WriteLinkLighting(QSeriesCoolerProtocol.FanChannel, ReadOnlySpan<RgbColor>.Empty);

        Assert.Equal(2, LightFrames(t, QSeriesCoolerProtocol.FanChannel).Count);
    }

    private static void SetRpm(QSeriesCoolerHub hub, int rpm) =>
        hub.State.Channel2Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "FP12", FanCount = 1, FanRpm = new[] { rpm }, LedCount = 0 },
        };

    // Duty set, then three settled polls at the given rpm: the baseline is in place.
    private static void SettleBaseline(QSeriesCoolerCoolingProvider p, QSeriesCoolerHub hub, Clock clock, int rpm)
    {
        SetRpm(hub, rpm);
        p.RedriveIfFanRpmAbnormal();
        for (var i = 0; i < 4; i++)
        {
            clock.Now += 10_500;
            p.RedriveIfFanRpmAbnormal();
        }
    }

    [Fact]
    public void An_rpm_spike_at_unchanged_duty_replays_once_then_backs_off()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);
        SettleBaseline(p, hub, clock, 1200);
        var before = FanFrames(t).Count;

        SetRpm(hub, 2400);
        clock.Now += 3_000; p.RedriveIfFanRpmAbnormal();
        Assert.Equal(before, FanFrames(t).Count);       // one strike only
        clock.Now += 3_000; p.RedriveIfFanRpmAbnormal();
        Assert.Equal(before + 1, FanFrames(t).Count);   // second strike replays

        clock.Now += 3_000; p.RedriveIfFanRpmAbnormal();
        clock.Now += 3_000; p.RedriveIfFanRpmAbnormal();
        Assert.Equal(before + 1, FanFrames(t).Count);   // still spiking, inside the interval
    }

    [Fact]
    public void A_small_rpm_rise_does_not_replay()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);
        SettleBaseline(p, hub, clock, 1200);
        var before = FanFrames(t).Count;

        SetRpm(hub, 1700);   // above 1.5x is false, and below baseline plus 400 is false
        for (var i = 0; i < 4; i++) { clock.Now += 3_000; p.RedriveIfFanRpmAbnormal(); }

        Assert.Equal(before, FanFrames(t).Count);
    }

    [Fact]
    public void A_spike_with_no_baseline_does_nothing()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);
        var before = FanFrames(t).Count;

        SetRpm(hub, 2400);
        for (var i = 0; i < 4; i++) { clock.Now += 3_000; p.RedriveIfFanRpmAbnormal(); }

        Assert.Equal(before, FanFrames(t).Count);
    }

    [Fact]
    public void A_duty_change_restarts_the_baseline()
    {
        var (hub, t, p, clock) = New();
        p.SetFanSpeed(FanId, 45);
        SettleBaseline(p, hub, clock, 1200);
        p.SetFanSpeed(FanId, 80);
        var before = FanFrames(t).Count;

        SetRpm(hub, 2400);
        for (var i = 0; i < 4; i++) { clock.Now += 3_000; p.RedriveIfFanRpmAbnormal(); }

        Assert.Equal(before, FanFrames(t).Count);
    }

    [Fact]
    public void An_undriven_fan_is_never_replayed()
    {
        var (hub, t, p, clock) = New();
        SetRpm(hub, 2400);
        for (var i = 0; i < 12; i++) { clock.Now += 3_000; p.RedriveIfFanRpmAbnormal(); }

        Assert.Empty(FanFrames(t));
    }

    private sealed class Clock { public long Now = 100_000; }

    private static (QSeriesCoolerHub hub, ScriptedTransport t, QSeriesCoolerCoolingProvider provider, Clock clock) New()
    {
        var t = new ScriptedTransport();
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        hub.State.FirmwareVersion = "2.0.9.1";
        hub.State.TurboOn = true;
        hub.State.Channel2Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "FP12", FanCount = 1, FanRpm = new[] { 700 }, LedCount = 0 },
        };
        var clock = new Clock();
        return (hub, t, new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore(), clock: () => clock.Now), clock);
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    // Software mode with turbo on by default, so no mode-switch frame muddies the pump-frame
    // count; answers FF CC 04 with the stored curve, or silence when null.
    private sealed class ScriptedTransport : INp50Transport
    {
        private byte[]? _pending;
        public readonly List<byte[]> Writes = new();
        public QSeriesFirmwareCurvePoint[]? Curve = null;
        public byte ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
        public byte TurboByte = QSeriesCoolerProtocol.TurboOnByte;
        public bool IsOpen => true;
        public string Serial => "QTEST123";

        public void Write(ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
            var isHeader = data.Length >= 4 && data[0] == 0xFF && data[1] == 0xCC;
            if (isHeader && data.Length >= 5 && data[2] == 0x02 && data[3] == 0 && data[4] != 0) ControlMode = data[4];
            if (isHeader && data[2] == 0x01 && data[3] == 0x00) _pending = BuildPort0();
            else if (isHeader && data[2] == 0x04 && Curve is not null)
                _pending = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeMix, Curve)
                    .AsSpan(0, QSeriesCoolerProtocol.FirmwareDefaultResponseLength).ToArray();
            else _pending = null;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_pending is null) return 0;
            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, n).CopyTo(buffer);
            _pending = null;
            return n;
        }

        public void DiscardInput() { }
        public void Dispose() { }

        private byte[] BuildPort0()
        {
            var p = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
            p[0] = 0xFF; p[1] = 0xCC;
            p[9] = 1; p[10] = 0;
            p[12] = ControlMode;
            p[14] = TurboByte;
            return p;
        }
    }
}
