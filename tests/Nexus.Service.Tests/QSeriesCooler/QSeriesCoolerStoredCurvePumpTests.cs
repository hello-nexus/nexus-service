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
/// firmware curve. These pin the stored-curve pump duty (interpolation, clamps, floor,
/// fallback) and that the provider resends the pump only when its wire byte changes.
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
    [InlineData(40.5f, 46)]  // 41 interpolated, raised to the floor
    [InlineData(46f, 53)]    // 45 + 11 * 3/4
    [InlineData(49f, 79)]    // 56 + 35 * 2/3
    [InlineData(50f, 91)]
    [InlineData(70f, 91)]    // clamp above the last point
    [InlineData(10f, 46)]    // clamp below the first point, then the floor
    [InlineData(null, 91)]   // unknown coolant: highest curve duty
    public void StoredCurvePumpDuty_interpolates_clamps_and_floors(float? coolant, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.StoredCurvePumpDuty(FactoryCurve(), coolant));
    }

    [Fact]
    public void StoredCurvePumpDuty_empty_curve_is_full_duty()
    {
        Assert.Equal(100, QSeriesCoolerProtocol.StoredCurvePumpDuty(Array.Empty<QSeriesFirmwareCurvePoint>(), 30f));
    }

    [Fact]
    public void PumpSoftwareFloorDuty_is_the_lowest_duty_with_a_running_wire_byte()
    {
        Assert.Equal(0, QSeriesCoolerProtocol.MapPumpDutyToWire(QSeriesCoolerProtocol.PumpSoftwareFloorDuty - 1, true));
        Assert.True(QSeriesCoolerProtocol.MapPumpDutyToWire(QSeriesCoolerProtocol.PumpSoftwareFloorDuty, true) > 0);
    }

    [Fact]
    public void Fan_write_holds_the_pump_on_the_stored_curve_and_reports_the_commanded_duty()
    {
        var (hub, t, provider) = NewHub(withCurve: true);
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(FanId, 60);

        var pump = Assert.Single(PumpFrames(t));
        Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(79, true), pump[5]);
        var ch = provider.GetFanChannels().Single(c => c.Id == PumpId);
        Assert.Equal(79, ch.DutyPercent);
        Assert.Equal(FanModes.Auto, ch.Mode);
    }

    [Fact]
    public void Hotter_of_the_two_coolant_readings_is_used()
    {
        var (hub, t, provider) = NewHub(withCurve: true);
        hub.State.CoolantTempInC = 30f;
        hub.State.CoolantTempOutC = 50f;

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(91, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    [Fact]
    public void Pump_is_resent_only_when_its_wire_byte_changes()
    {
        var (hub, t, provider) = NewHub(withCurve: true);
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(FanId, 60);
        provider.SetFanSpeed(FanId, 70);
        Assert.Single(PumpFrames(t));

        hub.State.CoolantTempInC = 50f;
        provider.SetFanSpeed(FanId, 70);
        Assert.Equal(2, PumpFrames(t).Count);
        Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(91, true), PumpFrames(t)[1][5]);
    }

    [Fact]
    public void Unreadable_curve_falls_back_to_full_duty()
    {
        var (hub, t, provider) = NewHub(withCurve: false);
        hub.State.CoolantTempInC = 30f;

        provider.SetFanSpeed(FanId, 60);

        var pump = Assert.Single(PumpFrames(t));
        Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(100, true), pump[5]);
        Assert.Equal(100, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    [Fact]
    public void Missing_coolant_temp_uses_the_curves_highest_pump_duty()
    {
        var (hub, t, provider) = NewHub(withCurve: true);

        provider.SetFanSpeed(FanId, 60);

        Assert.Equal(91, provider.GetFanChannels().Single(c => c.Id == PumpId).DutyPercent);
    }

    [Fact]
    public void A_user_pump_duty_wins_over_the_stored_curve()
    {
        var (hub, t, provider) = NewHub(withCurve: true);
        hub.State.CoolantTempInC = 49f;

        provider.SetFanSpeed(PumpId, 60);
        provider.SetFanSpeed(FanId, 60);
        hub.State.CoolantTempInC = 50f;
        provider.SetFanSpeed(FanId, 60);

        var pump = Assert.Single(PumpFrames(t));
        Assert.Equal(QSeriesCoolerProtocol.MapPumpDutyToWire(60, true), pump[5]);
        var ch = provider.GetFanChannels().Single(c => c.Id == PumpId);
        Assert.Equal(60, ch.DutyPercent);
        Assert.Equal(FanModes.Manual, ch.Mode);
    }

    // SetControl frames (FF CC 02, channel byte 0), not per-channel fan frames.
    private static List<byte[]> PumpFrames(ScriptedTransport t) =>
        t.Writes.Where(w => w.Length >= 6 && w[1] == 0xCC && w[2] == 0x02 && w[3] == 0).ToList();

    private static (QSeriesCoolerHub hub, ScriptedTransport t, QSeriesCoolerCoolingProvider provider) NewHub(bool withCurve)
    {
        var t = new ScriptedTransport(withCurve ? FactoryCurve() : null);
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        if (withCurve) hub.State.FirmwareVersion = "2.0.9.1";
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

    // Software mode with turbo on, so no mode-switch frame muddies the pump-frame count;
    // answers FF CC 04 with the given stored curve, or silence when null.
    private sealed class ScriptedTransport : INp50Transport
    {
        private readonly QSeriesFirmwareCurvePoint[]? _curve;
        private byte[]? _pending;
        public readonly List<byte[]> Writes = new();
        public ScriptedTransport(QSeriesFirmwareCurvePoint[]? curve) => _curve = curve;
        public bool IsOpen => true;
        public string Serial => "QTEST123";

        public void Write(ReadOnlySpan<byte> data)
        {
            Writes.Add(data.ToArray());
            var isHeader = data.Length >= 4 && data[0] == 0xFF && data[1] == 0xCC;
            if (isHeader && data[2] == 0x01 && data[3] == 0x00) _pending = BuildPort0();
            else if (isHeader && data[2] == 0x04 && _curve is not null)
                _pending = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeMix, _curve)
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

        private static byte[] BuildPort0()
        {
            var p = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
            p[0] = 0xFF; p[1] = 0xCC;
            p[9] = 1; p[10] = 0;
            p[12] = QSeriesCoolerProtocol.ControlModeSoftware;
            p[14] = QSeriesCoolerProtocol.TurboOnByte;
            return p;
        }
    }
}
