using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Fan writes put the Q-series hub in software mode, which drops the pump off the cooler's
/// firmware curve. These pin the stored-curve pump wire (interpolation, clamps, floor,
/// fallback), that the pump is resent only when its wire byte changes, and the heartbeat refresh.
/// </summary>
public class QSeriesCoolerStoredCurvePumpTests
{
    private const string PumpId = "qseries:QTEST123:pump";
    private const string FanId = "qseries:QTEST123:p2:1";

    private static QSeriesFirmwareCurvePoint[] FactoryCurve() => new QSeriesFirmwareCurvePoint[]
    {
        new() { PumpTempC = 34, PumpDutyPercent = 32, FanTempC = 43, FanDutyPercent = 29 },
        new() { PumpTempC = 38, PumpDutyPercent = 37, FanTempC = 48, FanDutyPercent = 39 },
        new() { PumpTempC = 43, PumpDutyPercent = 45, FanTempC = 51, FanDutyPercent = 50 },
        new() { PumpTempC = 47, PumpDutyPercent = 56, FanTempC = 54, FanDutyPercent = 59 },
        new() { PumpTempC = 50, PumpDutyPercent = 91, FanTempC = 57, FanDutyPercent = 100 },
    };

    [Theory]
    [InlineData(40.5f, 41)]  // between 38/37 and 43/45
    [InlineData(46f, 53)]    // 45 + 11 * 3/4
    [InlineData(49f, 79)]    // 56 + 35 * 2/3
    [InlineData(50f, 91)]
    [InlineData(70f, 91)]    // clamp above the last point
    [InlineData(10f, 32)]    // clamp below the first point
    [InlineData(null, 91)]   // unknown coolant: highest curve point
    public void StoredCurvePumpWire_interpolates_and_clamps(float? coolant, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.StoredCurvePumpWire(FactoryCurve(), coolant));
    }

    [Fact]
    public void StoredCurvePumpWire_never_goes_below_the_lowest_running_wire()
    {
        var low = new QSeriesFirmwareCurvePoint[] { new() { PumpTempC = 10, PumpDutyPercent = 20 }, new() { PumpTempC = 50, PumpDutyPercent = 25 } };
        Assert.Equal(QSeriesCoolerProtocol.PumpMinRunningWire, QSeriesCoolerProtocol.StoredCurvePumpWire(low, 30f));
    }

    [Fact]
    public void StoredCurvePumpWire_empty_curve_is_full()
    {
        Assert.Equal(100, QSeriesCoolerProtocol.StoredCurvePumpWire(Array.Empty<QSeriesFirmwareCurvePoint>(), 30f));
    }

    [Fact]
    public void PumpMinRunningWire_is_the_first_nonzero_duty_table_byte()
    {
        Assert.Equal(0, QSeriesCoolerProtocol.MapPumpDutyToWire(45, true));
        Assert.Equal(QSeriesCoolerProtocol.PumpMinRunningWire, QSeriesCoolerProtocol.MapPumpDutyToWire(46, true));
    }

    [Fact]
    public void Fan_write_holds_the_pump_on_the_stored_curve_and_reports_the_commanded_value()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(79, Assert.Single(PumpFrames(t))[5]);
        var ch = provider.GetFanChannels().Single(c => c.Id == PumpId);
        Assert.Equal(79, ch.DutyPercent);
        Assert.Equal(FanModes.Auto, ch.Mode);
    }

    [Fact]
    public void Off_turbo_caps_the_pump_wire()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        t.TurboByte = QSeriesCoolerProtocol.TurboOffByte;
        hub.State.CoolantTempInC = 50f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(QSeriesCoolerProtocol.PumpOffTurboWireCap, Assert.Single(PumpFrames(t))[5]);
    }

    [Fact]
    public void Hotter_of_the_two_coolant_readings_is_used()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 30f;
        hub.State.CoolantTempOutC = 50f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(91, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    [Fact]
    public void Pump_is_resent_only_when_its_wire_byte_changes()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(FanId, 60);
        provider.SetFanSpeed(FanId, 70);
        provider.RefreshStoredCurvePump();
        Assert.Single(PumpFrames(t));

        hub.State.CoolantTempInC = 50f;
        provider.SetFanSpeed(FanId, 70);
        Assert.Equal(2, PumpFrames(t).Count);
        Assert.Equal(91, PumpFrames(t)[1][5]);
    }

    [Fact]
    public void Heartbeat_refresh_ramps_the_pump_when_the_engine_resends_nothing()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 40f;
        provider.SetFanSpeed(FanId, 100);
        var before = PumpFrames(t).Count;

        hub.State.CoolantTempInC = 49f;
        provider.RefreshStoredCurvePump();

        Assert.Equal(before + 1, PumpFrames(t).Count);
        Assert.Equal(79, PumpFrames(t).Last()[5]);
    }

    [Fact]
    public void Heartbeat_refresh_does_nothing_while_no_fan_is_driven()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;

        provider.RefreshStoredCurvePump();

        Assert.Empty(PumpFrames(t));
    }

    [Fact]
    public void Unreadable_curve_on_curve_firmware_falls_back_to_full()
    {
        var (hub, t, provider) = NewHub(null);
        hub.State.CoolantTempInC = 30f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(100, Assert.Single(PumpFrames(t))[5]);
    }

    [Fact]
    public void Firmware_without_a_stored_curve_uses_the_factory_curve()
    {
        var (hub, t, provider) = NewHub(null, firmware: "");
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(79, Assert.Single(PumpFrames(t))[5]);
    }

    [Fact]
    public void Missing_coolant_temp_uses_the_curves_highest_pump_point()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(91, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    [Fact]
    public void A_user_pump_duty_wins_over_the_stored_curve()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(PumpId, 60);
        provider.SetFanSpeed(FanId, 60);
        hub.State.CoolantTempInC = 50f;
        provider.RefreshStoredCurvePump();

        var pump = Assert.Single(PumpFrames(t));
        Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(60, true), pump[5]);
        var ch = provider.GetFanChannels().Single(c => c.Id == PumpId);
        Assert.Equal(60, ch.DutyPercent);
        Assert.Equal(FanModes.Manual, ch.Mode);
    }

    [Fact]
    public void Releasing_a_user_pump_duty_while_fans_stay_driven_puts_the_pump_back_on_the_curve()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;
        provider.SetFanSpeed(FanId, 60);
        provider.SetFanSpeed(PumpId, 60);
        var before = PumpFrames(t).Count;

        provider.ReleaseFan(PumpId);

        Assert.True(PumpFrames(t).Count > before);
        Assert.Equal(79, PumpFrames(t).Last()[5]);
    }

    [Fact]
    public void Software_mode_pick_carries_a_running_pump_wire()
    {
        var (hub, t, _) = NewHub(FactoryCurve());
        t.ControlMode = QSeriesCoolerProtocol.ControlModeMotherboard;

        Assert.True(hub.SetControlMode(QSeriesCoolerProtocol.ControlModeSoftware));

        var frames = PumpFrames(t);
        Assert.Equal(2, frames.Count);
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, frames[0][4]);
        Assert.True(frames[0][5] > 0);
        Assert.Equal(frames[0][5], frames[1][5]);
    }

    [Fact]
    public void A_curve_saved_mid_connection_is_read_again()
    {
        var (hub, t, provider) = NewHub(FactoryCurve());
        hub.State.CoolantTempInC = 49f;
        provider.SetFanSpeed(FanId, 60);
        Assert.Equal(79, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);

        var flat = Enumerable.Range(0, 5).Select(i => new QSeriesFirmwareCurvePoint { PumpTempC = 30 + i * 5, PumpDutyPercent = 60, FanTempC = 30 + i * 5, FanDutyPercent = 50 }).ToArray();
        Assert.True(hub.WriteFirmwareCurve(flat));
        t.Curve = flat;
        provider.RefreshStoredCurvePump();

        Assert.Equal(60, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    // SetControl frames (FF CC 02, channel byte 0), not per-channel fan frames.
    private static List<byte[]> PumpFrames(ScriptedTransport t) =>
        t.Writes.Where(w => w.Length >= 6 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0).ToList();

    private static (QSeriesCoolerHub hub, ScriptedTransport t, QSeriesCoolerCoolingProvider provider) NewHub(
        QSeriesFirmwareCurvePoint[]? curve, string firmware = "2.0.9.1")
    {
        var t = new ScriptedTransport(curve);
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        hub.State.FirmwareVersion = firmware;
        hub.State.TurboOn = true;
        hub.State.Channel2Devices = new List<QSeriesLinkDevice>
        {
            new() { Slot = 1, Model = "FP12", FanCount = 1, FanRpm = new[] { 700 }, LedCount = 0 },
        };
        return (hub, t, new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore()));
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
        public QSeriesFirmwareCurvePoint[]? Curve;
        public byte ControlMode = QSeriesCoolerProtocol.ControlModeSoftware;
        public byte TurboByte = QSeriesCoolerProtocol.TurboOnByte;
        public ScriptedTransport(QSeriesFirmwareCurvePoint[]? curve) => Curve = curve;
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
