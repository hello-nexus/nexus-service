using System;
using System.Collections.Generic;
using System.Linq;
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

        p.RedriveIfControlDropped();

        var pumpFrames = t.Writes.Skip(before).Where(w => w.Length >= 6 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0).ToList();
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, pumpFrames[0][4]);
        Assert.All(pumpFrames, f => Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(100, true), f[5]));
        Assert.Single(FanFrames(t).Skip(1));
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
