using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.Hyte.MiniHub;        // RgbColor
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Wire-protocol coverage for the HYTE Q-series cooler RGB path. Reference is
/// HYTE's shipping nexus-control-service -
/// <c>LightDancing/Hardware/Devices/HYTE/Cooler/PQSeriesDeviceBase.SendToHardware</c>:
/// software RGB control (FF DD 03 00) then 4 per-port LED streams
/// <c>FF EE 01 &lt;port&gt; 01 68 00</c> + GRB triples, each PadListWithZeros(90).
/// </summary>
public class QSeriesCoolerProtocolTests
{
    // ── RGB control mode ──

    [Theory]
    [InlineData(QSeriesCoolerProtocol.RgbModeSoftware)]
    [InlineData(QSeriesCoolerProtocol.RgbModeMotherboard)]
    public void BuildSetRgbControlMode_emits_FF_DD_03_mode(byte mode)
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x03, mode }, QSeriesCoolerProtocol.BuildSetRgbControlMode(mode));
    }

    [Fact]
    public void BuildSetRgbControlMode_rejects_unknown_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => QSeriesCoolerProtocol.BuildSetRgbControlMode(0x99));
    }

    // ── LED streaming framing ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BuildLightingStream_pads_a_short_frame_to_90_bytes_with_header(int port)
    {
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port, new[] { new RgbColor(1, 2, 3) });
        Assert.Equal(QSeriesCoolerProtocol.MinStreamFrameLength, buf.Length);
        Assert.Equal(90, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xEE, buf[1]);
        Assert.Equal(0x01, buf[2]);              // Q-series stream sub-op (NOT MiniHub's 0x03)
        Assert.Equal((byte)port, buf[3]);
        Assert.Equal(0x01, buf[4]);              // LED-count magic high
        Assert.Equal(0x68, buf[5]);              // LED-count magic low
        Assert.Equal(0x00, buf[6]);              // reserved
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void BuildLightingStream_rejects_out_of_range_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QSeriesCoolerProtocol.BuildLightingStream(port, ReadOnlySpan<RgbColor>.Empty));
    }

    [Fact]
    public void BuildLightingStream_writes_GRB_byte_order_per_LED()
    {
        // GetDisplayColors emits G,R,B (PQSeriesDeviceBase / Q60LCDBacklight).
        var leds = new[]
        {
            new RgbColor(R: 0x11, G: 0x22, B: 0x33),
            new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC),
        };
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port: 2, leds);
        Assert.Equal(0x22, buf[7 + 0]); // G
        Assert.Equal(0x11, buf[7 + 1]); // R
        Assert.Equal(0x33, buf[7 + 2]); // B
        Assert.Equal(0xBB, buf[7 + 3]);
        Assert.Equal(0xAA, buf[7 + 4]);
        Assert.Equal(0xCC, buf[7 + 5]);
    }

    [Fact]
    public void BuildLightingStream_always_emits_LedCount_magic_0x0168()
    {
        foreach (var port in new[] { 1, 2, 3, 4 })
        {
            foreach (var ledCount in new[] { 0, 1, 8, 27, 50 })
            {
                var buf = QSeriesCoolerProtocol.BuildLightingStream(port, new RgbColor[ledCount]);
                Assert.Equal(0x01, buf[4]);
                Assert.Equal(0x68, buf[5]);
                Assert.Equal(Math.Max(90, 7 + ledCount * 3), buf.Length);
            }
        }
    }

    [Fact]
    public void BuildLightingStream_zero_pads_trailing_LEDs()
    {
        var leds = new[] { new RgbColor(0x10, 0x20, 0x30) };
        var buf = QSeriesCoolerProtocol.BuildLightingStream(port: 1, leds);
        Assert.Equal(0x20, buf[7]); // G
        Assert.Equal(0x10, buf[8]); // R
        Assert.Equal(0x30, buf[9]); // B
        for (var i = 10; i < buf.Length; i++)
            Assert.Equal(0x00, buf[i]);
    }

    /// <summary>
    /// Legacy PadListWithZeros(90) pads and never truncates, so the 42-LED
    /// backlight rides a 133-byte frame. Capping it at 90 silently drops 15 LEDs.
    /// </summary>
    [Fact]
    public void BuildLightingStream_grows_past_90_bytes_for_the_42_led_backlight()
    {
        var leds = new RgbColor[QSeriesCoolerProtocol.BacklightLedCount];
        for (var i = 0; i < leds.Length; i++) leds[i] = new RgbColor(0xFF, 0xFF, 0xFF);

        var buf = QSeriesCoolerProtocol.BuildLightingStream(QSeriesCoolerProtocol.BacklightPort, leds);

        Assert.Equal(7 + QSeriesCoolerProtocol.BacklightLedCount * 3, buf.Length);
        Assert.Equal(133, buf.Length);
        for (var i = 0; i < leds.Length; i++)
        {
            var off = 7 + i * 3;
            Assert.Equal(0xFF, buf[off + 0]);
            Assert.Equal(0xFF, buf[off + 1]);
            Assert.Equal(0xFF, buf[off + 2]);
        }
    }

    [Fact]
    public void Backlight_wire_order_walks_the_notched_serpentine()
    {
        var order = QSeriesCoolerProtocol.BacklightWireOrder;

        Assert.Equal(42, order.Length);
        Assert.Equal(QSeriesCoolerProtocol.BacklightLedCount, order.Length);
        // Every cell distinct, and the notched column contributes only its lower rows.
        Assert.Equal(order.Length, new HashSet<(int, int)>(order).Count);
        Assert.Equal(9, System.Array.FindAll(order, c => c.Column == 4).Length);
        Assert.Equal(9, System.Array.FindAll(order, c => c.Column == 3).Length);
        Assert.Equal(6, System.Array.FindAll(order, c => c.Column == 2).Length);
        Assert.Equal(9, System.Array.FindAll(order, c => c.Column == 1).Length);
        Assert.Equal(9, System.Array.FindAll(order, c => c.Column == 0).Length);
        Assert.DoesNotContain(order, c => c.Column == 2 && c.Row < 3);

        // Columns right-to-left, direction alternating, per legacy Q60LCDBacklight.ProcessColor.
        Assert.Equal((4, 0), order[0]);
        Assert.Equal((4, 8), order[8]);
        Assert.Equal((3, 8), order[9]);
        Assert.Equal((3, 0), order[17]);
        Assert.Equal((2, 3), order[18]);
        Assert.Equal((2, 8), order[23]);
        Assert.Equal((1, 8), order[24]);
        Assert.Equal((0, 8), order[41]);
    }

    /// <summary>Legacy Q60Logo KEYS_LAYOUTS: bottom, left, top, right.</summary>
    [Fact]
    public void Logo_wire_order_is_the_four_point_diamond()
    {
        Assert.Equal(
            new[] { (1, 2), (0, 1), (1, 0), (2, 1) },
            QSeriesCoolerProtocol.LogoWireOrder);
        Assert.Equal(QSeriesCoolerProtocol.LogoLedCount, QSeriesCoolerProtocol.LogoWireOrder.Length);
    }

    // ── Pump telemetry reads ──
    // Reference: SmartHubCommandBase.GetPort0InformationBytes (FF CC 01 00 → 20 B),
    // PQSeriesCommand.GetPump2InfoBytes (FF CC 09 → 7 B), CoolerHubDevice
    // .UpdateOnboardCoolingDeviceInfo (pump tach = data[9],[10]) and
    // SmartDeviceMethods.GetRPM.

    [Fact]
    public void BuildGetPort0Info_emits_FF_CC_01_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x00 }, QSeriesCoolerProtocol.BuildGetPort0Info());
    }

    [Fact]
    public void BuildGetPump2Info_emits_FF_CC_09()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x09 }, QSeriesCoolerProtocol.BuildGetPump2Info());
    }

    [Theory]
    [InlineData(0, 0, 0)]       // no-sensor sentinel
    [InlineData(0, 50, 3000)]   // (0*100+50)/10*4 = 20 → 60000/20
    [InlineData(1, 0, 1500)]    // (100)/10*4 = 40 → 60000/40
    public void DecodeRpm_matches_reference_formula(byte high, byte low, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.DecodeRpm(high, low));
    }

    [Fact]
    public void TryParsePort0PumpRpm_reads_bytes_9_and_10()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[9] = 1; resp[10] = 0;   // → 1500 RPM
        Assert.True(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out var rpm));
        Assert.Equal(1500, rpm);
    }

    [Theory]
    [InlineData(19)]                 // one byte short
    public void TryParsePort0PumpRpm_rejects_short_response(int length)
    {
        var resp = new byte[length];
        resp[0] = 0xFF; resp[1] = 0xCC;
        Assert.False(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out _));
    }

    [Fact]
    public void TryParsePort0PumpRpm_rejects_mis_echoed_header()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xDD;   // wrong op byte
        Assert.False(QSeriesCoolerProtocol.TryParsePort0PumpRpm(resp, out _));
    }

    [Fact]
    public void CoolantTempsOf_reads_inlet_from_5_6_and_outlet_from_7_8()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        // V = 3.3 * (high*100 + low) / 4096. Pump table: 2.261V → 50°C, 2.755V → 25°C.
        resp[5] = 28; resp[6] = 6;    // 2.261V
        resp[7] = 34; resp[8] = 20;   // 2.755V
        var (inC, outC) = QSeriesCoolerProtocol.CoolantTempsOf(resp);
        Assert.Equal(50f, inC);
        Assert.Equal(25f, outC);
    }

    [Fact]
    public void CoolantTempsOf_returns_null_for_out_of_range_and_short_responses()
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        // Both pairs left at 0 → 0V, under the table's hottest entry.
        var (inC, outC) = QSeriesCoolerProtocol.CoolantTempsOf(resp);
        Assert.Null(inC);
        Assert.Null(outC);

        Assert.Equal((null, null), QSeriesCoolerProtocol.CoolantTempsOf(new byte[8]));
    }

    [Theory]
    // Pump table ends: 3.04V = 0°C, 1.643V = 75°C. A saturated reading is not a temperature.
    [InlineData(37, 73)]   // 3.0398V
    [InlineData(20, 39)]   // 1.6427V
    public void TryDecodeLiveTempC_rejects_a_reading_that_saturates_either_table_end(byte high, byte low)
    {
        Assert.Null(QSeriesCoolerProtocol.TryDecodeLiveTempC(high, low));
    }

    [Theory]
    [InlineData(37, 65, 1f)]    // 3.0333V → one step in from the cold end
    [InlineData(20, 53, 74f)]   // 1.6540V → one step in from the hot end
    public void TryDecodeLiveTempC_still_reads_the_entries_adjacent_to_each_end(byte high, byte low, float expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.TryDecodeLiveTempC(high, low));
    }

    [Fact]
    public void TryParsePump2Rpm_reads_bytes_3_and_4()
    {
        var resp = new byte[QSeriesCoolerProtocol.Pump2ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[3] = 0; resp[4] = 50;   // → 3000 RPM
        Assert.True(QSeriesCoolerProtocol.TryParsePump2Rpm(resp, out var rpm));
        Assert.Equal(3000, rpm);
    }

    [Fact]
    public void TryParsePump2Rpm_returns_zero_when_no_second_pump()
    {
        var resp = new byte[QSeriesCoolerProtocol.Pump2ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;   // bytes [3],[4] stay 0
        Assert.True(QSeriesCoolerProtocol.TryParsePump2Rpm(resp, out var rpm));
        Assert.Equal(0, rpm);
    }

    // ── Pump control ──
    // Reference: SmartHubCommandBase.SwitchControlMode / PQSeriesCommand
    // .SetPumpSpeedCommand (15-byte FF CC 02, fw-anim echoed from Port-0 [15..19])
    // and SmartDeviceMethods._pumpSpeedPercentageToVoltagePercentage.

    [Fact]
    public void BuildSetControl_lays_out_mode_speed_turbo_and_echoes_fw_anim()
    {
        var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        port0[15] = 0x02; port0[16] = 0xAA; port0[17] = 0xBB; port0[18] = 0xCC; port0[19] = 0x40; // anim, R, G, B, brightness
        var cmd = QSeriesCoolerProtocol.BuildSetControl(
            QSeriesCoolerProtocol.ControlModeSoftware, pumpWire: 35, QSeriesCoolerProtocol.TurboOnByte, port0);
        Assert.Equal(QSeriesCoolerProtocol.SetControlFrameLength, cmd.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x02 }, cmd[0..3]);
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, cmd[4]);
        Assert.Equal(35, cmd[5]);
        Assert.Equal(QSeriesCoolerProtocol.TurboOnByte, cmd[9]);
        // fw-animation echoed from Port-0 [15..19] into command [10..14]
        Assert.Equal(new byte[] { 0x02, 0xAA, 0xBB, 0xCC, 0x40 }, cmd[10..15]);
    }

    [Fact]
    public void BuildSetTurboMcu_emits_FF_CC_0A_turbo()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x0A, 0x01 }, QSeriesCoolerProtocol.BuildSetTurboMcu(0x01));
    }

    [Theory]
    [InlineData(45, true, 0)]    // pump off below 46%
    [InlineData(46, true, 31)]
    [InlineData(50, true, 35)]
    [InlineData(100, true, 100)]
    public void MapPumpDutyToWire_turbo_follows_voltage_table(int duty, bool turbo, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.MapPumpDutyToWire(duty, turbo));
    }

    [Theory]
    [InlineData(50, 35)]   // <=55 maps via the table
    [InlineData(80, 55)]   // off-turbo the wire byte caps at 55
    public void MapPumpDutyToWire_offTurbo_caps_at_55(int duty, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.MapPumpDutyToWire(duty, turboOn: false));
    }

    // ── Firmware animation (FF CC 0C write; Port-0 [15..19] readback) ──
    // Reference: SmartHubCommandBase.WriteFwAnimationToMcu, PQSeriesHubInfo (Port-0
    // [15]=anim, [16..18]=RGB, [19]=brightness), FirmwareFunctionCheckManager.

    [Fact]
    public void BuildWriteFirmwareAnimation_emits_FF_CC_0C_anim_rgb_brightness_save()
    {
        var cmd = QSeriesCoolerProtocol.BuildWriteFirmwareAnimation(
            QSeriesCoolerProtocol.FwAnimationBreathe, 0x11, 0x22, 0x33, 75);
        Assert.Equal(
            new byte[] { 0xFF, 0xCC, 0x0C, QSeriesCoolerProtocol.FwAnimationBreathe, 0x11, 0x22, 0x33, 75, 0x01 },
            cmd);
    }

    [Fact]
    public void TryParseFirmwareAnimation_reads_bytes_15_to_19()
    {
        var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        port0[0] = 0xFF; port0[1] = 0xCC;
        port0[15] = QSeriesCoolerProtocol.FwAnimationRainbow;
        port0[16] = 0xAA; port0[17] = 0xBB; port0[18] = 0xCC; port0[19] = 42;

        Assert.True(QSeriesCoolerProtocol.TryParseFirmwareAnimation(port0, out var anim));
        Assert.Equal(QSeriesCoolerProtocol.FwAnimationRainbow, anim.Animation);
        Assert.Equal(0xAA, anim.R);
        Assert.Equal(0xBB, anim.G);
        Assert.Equal(0xCC, anim.B);
        Assert.Equal(42, anim.Brightness);
    }

    [Fact]
    public void TryParseFirmwareAnimation_rejects_short_or_misframed()
    {
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareAnimation(new byte[19], out _));
        var wrongHeader = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        wrongHeader[0] = 0xFF; wrongHeader[1] = 0xDD;
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareAnimation(wrongHeader, out _));
    }

    [Theory]
    [InlineData("q60", "2.0.0.1", true)]    // exactly the Q60 threshold
    [InlineData("q60", "1.9.9.9", false)]   // below threshold
    [InlineData("q80", "1.0.5.1", true)]    // exactly the Q80 threshold
    [InlineData("q80", "1.0.5.0", false)]   // below threshold
    [InlineData("q60", "", false)]          // no version polled yet
    [InlineData("unknown", "9.9.9.9", false)] // unrecognized variant never supports it
    public void SupportsFirmwareAnimation_gates_on_version(string variant, string version, bool expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.SupportsFirmwareAnimation(variant, version));
    }

    [Theory]
    [InlineData("q60", "2.0.3.1", true)]    // exactly the Q60 threshold
    [InlineData("q60", "2.0.2.9", false)]   // below threshold
    [InlineData("q80", "1.0.5.1", true)]    // exactly the Q80 threshold
    [InlineData("q80", "1.0.5.0", false)]   // below threshold
    [InlineData("q80", "", false)]          // no version polled yet
    public void SupportsFirmwareAnimationBrightness_gates_on_version(string variant, string version, bool expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.SupportsFirmwareAnimationBrightness(variant, version));
    }

    // ── Firmware temperature curve (FF CC 03 / FF CC 04) ──

    [Theory]
    [InlineData("q60", "2.0.0.1", true)]    // exactly the Q60 threshold
    [InlineData("q60", "2.0.9.1", true)]
    [InlineData("q60", "1.9.9.9", false)]   // below threshold
    [InlineData("q80", "1.0.4.1", true)]    // exactly the Q80 threshold
    [InlineData("q80", "1.0.3.9", false)]
    [InlineData("q60", "", false)]          // no version polled yet
    public void SupportsFirmwareCurve_gates_on_version(string variant, string version, bool expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.SupportsFirmwareCurve(variant, version));
    }

    [Fact]
    public void BuildGetFirmwareDefault_emits_FF_CC_04_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x04, 0x00 }, QSeriesCoolerProtocol.BuildGetFirmwareDefault());
    }

    [Theory]
    [InlineData(false)]   // pump thermistor table
    [InlineData(true)]    // fan thermistor table
    public void CurveTemp_round_trips_for_every_editor_temperature(bool fan)
    {
        for (var t = QSeriesCoolerProtocol.FirmwareCurveTempMin; t <= QSeriesCoolerProtocol.FirmwareCurveTempMax; t++)
        {
            var (high, low) = QSeriesCoolerProtocol.EncodeCurveTemp(t, fan);
            Assert.Equal(t, QSeriesCoolerProtocol.DecodeCurveTemp(high, low, fan));
        }
    }

    [Fact]
    public void BuildSetFirmwareMode_lays_out_header_mode_speeds_and_save()
    {
        var pts = new QSeriesFirmwareCurvePoint[]
        {
            new() { PumpTempC = 0,  PumpDutyPercent = 50, FanTempC = 0,  FanDutyPercent = 35 },
            new() { PumpTempC = 20, PumpDutyPercent = 60, FanTempC = 20, FanDutyPercent = 45 },
            new() { PumpTempC = 30, PumpDutyPercent = 70, FanTempC = 30, FanDutyPercent = 55 },
            new() { PumpTempC = 40, PumpDutyPercent = 85, FanTempC = 40, FanDutyPercent = 75 },
            new() { PumpTempC = 50, PumpDutyPercent = 100, FanTempC = 50, FanDutyPercent = 100 },
        };
        var cmd = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeTemperature, pts);

        Assert.Equal(QSeriesCoolerProtocol.SetFirmwareModeFrameLength, cmd.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x03, 0x00 }, cmd[0..4]);
        Assert.Equal(QSeriesCoolerProtocol.FwDefaultModeTemperature, cmd[4]);
        // Pump speeds: slots 0-2 at [5..7], slots 3-4 at [17..18].
        Assert.Equal(new byte[] { 50, 60, 70 }, cmd[5..8]);
        Assert.Equal(new byte[] { 85, 100 }, cmd[17..19]);
        // Fan speeds: slots 0-2 at [8..10], slots 3-4 at [19..20].
        Assert.Equal(new byte[] { 35, 45, 55 }, cmd[8..11]);
        Assert.Equal(new byte[] { 75, 100 }, cmd[19..21]);
        // SAVE byte commits to EEPROM.
        Assert.Equal(0x01, cmd[35]);
    }

    [Fact]
    public void BuildSetFirmwareMode_rejects_wrong_point_count()
    {
        var three = new QSeriesFirmwareCurvePoint[3];
        Assert.Throws<ArgumentException>(() =>
            QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeTemperature, three));
    }

    [Fact]
    public void TryParseFirmwareCurve_round_trips_a_built_frame()
    {
        var pts = new QSeriesFirmwareCurvePoint[]
        {
            new() { PumpTempC = 5,  PumpDutyPercent = 50, FanTempC = 10, FanDutyPercent = 35 },
            new() { PumpTempC = 18, PumpDutyPercent = 62, FanTempC = 22, FanDutyPercent = 48 },
            new() { PumpTempC = 30, PumpDutyPercent = 75, FanTempC = 33, FanDutyPercent = 60 },
            new() { PumpTempC = 41, PumpDutyPercent = 88, FanTempC = 44, FanDutyPercent = 80 },
            new() { PumpTempC = 50, PumpDutyPercent = 100, FanTempC = 50, FanDutyPercent = 95 },
        };
        var cmd = QSeriesCoolerProtocol.BuildSetFirmwareMode(QSeriesCoolerProtocol.FwDefaultModeMix, pts);

        // FF CC 04 response carries the same [4..34] layout (no SAVE byte).
        Assert.True(QSeriesCoolerProtocol.TryParseFirmwareCurve(cmd.AsSpan(0, QSeriesCoolerProtocol.FirmwareDefaultResponseLength), out var parsed));
        Assert.Equal(QSeriesCoolerProtocol.FwDefaultModeMix, QSeriesCoolerProtocol.FirmwareDefaultModeOf(cmd));
        for (var i = 0; i < pts.Length; i++)
        {
            Assert.Equal(pts[i].PumpDutyPercent, parsed[i].PumpDutyPercent);
            Assert.Equal(pts[i].FanDutyPercent, parsed[i].FanDutyPercent);
            Assert.Equal(pts[i].PumpTempC, parsed[i].PumpTempC);
            Assert.Equal(pts[i].FanTempC, parsed[i].FanTempC);
        }
    }

    [Fact]
    public void TryParseFirmwareCurve_rejects_short_or_misframed()
    {
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareCurve(new byte[10], out _));
        var wrongHeader = new byte[QSeriesCoolerProtocol.FirmwareDefaultResponseLength];
        wrongHeader[0] = 0xFF; wrongHeader[1] = 0xDD;
        Assert.False(QSeriesCoolerProtocol.TryParseFirmwareCurve(wrongHeader, out _));
    }

    // ── Nexus Link channel devices (FF CC 01 &lt;channel&gt;) ──
    // Fixtures are the raw Y70-box Q60 fw 2.0.9.1 captures from 2026-09-03:
    // channel 1 = LS10 (20 LEDs) chained to LN70 (44 LEDs); channel 2 = five solo
    // FP12 fans, tachs 621/705/715/731/727 rpm.

    [Fact]
    public void BuildGetChannelInfo_emits_FF_CC_01_channel()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x02 },
            QSeriesCoolerProtocol.BuildGetChannelInfo(QSeriesCoolerProtocol.FanChannel));
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x01 },
            QSeriesCoolerProtocol.BuildGetChannelInfo(QSeriesCoolerProtocol.LinkChannel1));
    }

    private static byte[] Channel1Fixture()
    {
        var r = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        r[0] = 0xFF; r[1] = 0xCC;
        // Slot 0: LS10, 20 LEDs, orientation Down, chain continues.
        r[2] = 0x01; r[3] = 0x01; r[4] = 0x01; r[5] = 0x14;
        r[6] = 0x0D; r[7] = 0x1E; r[8] = 0x00; r[9] = 0x00;
        r[10] = 0x01; r[11] = 0x00;
        // Slot 1: LN70, 44 LEDs, orientation Front, chain ends.
        r[14] = 0x02; r[15] = 0x07; r[16] = 0x01; r[17] = 0x2C;
        r[18] = 0x11; r[19] = 0x2C; r[20] = 0x00; r[21] = 0x00;
        r[22] = 0x03; r[23] = 0x01;
        // Slot 2: empty - type byte 0x00, index byte lingers non-zero.
        r[26] = 0x02;
        return r;
    }

    private static byte[] Channel2Fixture()
    {
        var r = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        r[0] = 0xFF; r[1] = 0xCC;
        // Five solo FP12 slots. Tachs decode to 621/705/715/731/727 rpm.
        r[2] = 0x01; r[3] = 0x03; r[4] = 0x01; r[5] = 0x00;
        r[6] = 0x21; r[7] = 0x3E; r[8] = 0x18; r[9] = 0x0F;
        r[10] = 0x02; r[11] = 0x00;
        r[14] = 0x02; r[15] = 0x03; r[16] = 0x01; r[17] = 0x00;
        r[18] = 0x21; r[19] = 0x3B; r[20] = 0x15; r[21] = 0x1B;
        r[22] = 0x02; r[23] = 0x01;
        r[26] = 0x03; r[27] = 0x03; r[28] = 0x01; r[29] = 0x00;
        r[30] = 0x22; r[31] = 0x63; r[32] = 0x14; r[33] = 0x61;
        r[34] = 0x00; r[35] = 0x00;
        r[38] = 0x04; r[39] = 0x03; r[40] = 0x01; r[41] = 0x00;
        r[42] = 0x22; r[43] = 0x3D; r[44] = 0x14; r[45] = 0x33;
        r[46] = 0x00; r[47] = 0x00;
        r[50] = 0x05; r[51] = 0x03; r[52] = 0x01; r[53] = 0x00;
        r[54] = 0x21; r[55] = 0x58; r[56] = 0x14; r[57] = 0x3E;
        r[58] = 0x00; r[59] = 0x01;
        // Slot 5: empty - type byte 0x00, index byte lingers non-zero.
        r[62] = 0x05;
        return r;
    }

    [Fact]
    public void TryParseChannelDevices_parses_the_channel1_LS10_LN70_fixture()
    {
        Assert.True(QSeriesCoolerProtocol.TryParseChannelDevices(Channel1Fixture(), out var devices));

        Assert.Collection(devices,
            d =>
            {
                Assert.Equal(1, d.Slot);
                Assert.Equal("LS10", d.Model);
                Assert.Equal(20, d.LedCount);
                Assert.Equal(0, d.FanCount);
                Assert.Empty(d.FanRpm);
                Assert.Equal("Down", d.Orientation);
                Assert.False(d.GroupEnd);
                Assert.Null(d.TemperatureC); // only an FT12 unit carries a probe
            },
            d =>
            {
                Assert.Equal(2, d.Slot);
                Assert.Equal("LN70", d.Model);
                Assert.Equal(44, d.LedCount);
                Assert.Equal(0, d.FanCount);
                Assert.Equal("Front", d.Orientation);
                Assert.True(d.GroupEnd);
            });
    }

    [Fact]
    public void TryParseChannelDevices_parses_the_channel2_five_solo_FP12_fixture()
    {
        Assert.True(QSeriesCoolerProtocol.TryParseChannelDevices(Channel2Fixture(), out var devices));

        Assert.Equal(5, devices.Count);
        var expectedRpm = new[] { 621, 705, 715, 731, 727 };
        var expectedOrientation = new[] { "Up", "Up", "Back", "Back", "Back" };
        var expectedGroupEnd = new[] { false, true, false, false, true };
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(i + 1, devices[i].Slot);
            Assert.Equal("FP12", devices[i].Model);
            Assert.Equal(1, devices[i].FanCount);
            Assert.Equal(0, devices[i].LedCount); // firmware reports 0 for FT12/FP12, no fallback constant exists
            Assert.Equal(new[] { expectedRpm[i] }, devices[i].FanRpm);
            Assert.Equal(expectedOrientation[i], devices[i].Orientation);
            Assert.Equal(expectedGroupEnd[i], devices[i].GroupEnd);
        }
    }

    [Fact]
    public void TryParseChannelDevices_00_00_header_means_no_data_yet()
    {
        var noData = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength]; // [0..1] left 0x00 0x00

        Assert.False(QSeriesCoolerProtocol.TryParseChannelDevices(noData, out var devices));
        Assert.Empty(devices);
    }

    [Fact]
    public void TryParseChannelDevices_type_0_at_slot_0_is_an_empty_list()
    {
        var r = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        r[0] = 0xFF; r[1] = 0xCC; // valid header, but slot 0's type byte (r[3]) stays 0x00

        Assert.True(QSeriesCoolerProtocol.TryParseChannelDevices(r, out var devices));
        Assert.Empty(devices);
    }

    [Fact]
    public void TryParseChannelDevices_rejects_short_or_misframed()
    {
        Assert.False(QSeriesCoolerProtocol.TryParseChannelDevices(new byte[20], out _));
        var wrong = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        wrong[0] = 0xFF; wrong[1] = 0xDD;
        Assert.False(QSeriesCoolerProtocol.TryParseChannelDevices(wrong, out _));
    }

    [Fact]
    public void TryParseChannelDevices_duo_slot_expands_to_two_tachs_divided_by_10()
    {
        var r = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        r[0] = 0xFF; r[1] = 0xCC;
        r[2] = 0x01; r[3] = 0x04; // FT12 Duo
        r[8] = 1; r[9] = 0;       // fan 1 -> 1500 rpm
        r[4] = 2; r[5] = 0;       // fan 2 -> 750 rpm

        Assert.True(QSeriesCoolerProtocol.TryParseChannelDevices(r, out var devices));
        var dev = Assert.Single(devices);
        Assert.Equal("FT12 Duo", dev.Model);
        Assert.Equal(2, dev.FanCount);
        Assert.Equal(new[] { 1500, 750 }, dev.FanRpm);
    }

    [Fact]
    public void TryParseChannelDevices_trio_slot_expands_to_three_tachs_with_packed_decode()
    {
        var r = new byte[QSeriesCoolerProtocol.ChannelInfoResponseLength];
        r[0] = 0xFF; r[1] = 0xCC;
        r[2] = 0x01; r[3] = 0x05;  // FT12 Trio
        r[8] = 1; r[9] = 50;       // fan 1 -> 1%10*1000 + 50*10 = 1500 rpm
        r[4] = 2; r[5] = 0;        // fan 2 -> 2000 rpm
        r[10] = 3; r[11] = 25;     // fan 3 -> 3250 rpm

        Assert.True(QSeriesCoolerProtocol.TryParseChannelDevices(r, out var devices));
        var dev = Assert.Single(devices);
        Assert.Equal("FT12 Trio", dev.Model);
        Assert.Equal(3, dev.FanCount);
        Assert.Equal(new[] { 1500, 2000, 3250 }, dev.FanRpm);
    }

    [Fact]
    public void BuildSetChannelFanSpeeds_lays_out_per_slot_duties_and_zero_fills_unused_slots()
    {
        var duties = new[]
        {
            new QSeriesCoolerProtocol.QSeriesFanSlotDuty(60, 0, 0),      // solo
            new QSeriesCoolerProtocol.QSeriesFanSlotDuty(40, 55, 0),     // duo
            new QSeriesCoolerProtocol.QSeriesFanSlotDuty(20, 30, 45),    // trio
        };
        var cmd = QSeriesCoolerProtocol.BuildSetChannelFanSpeeds(QSeriesCoolerProtocol.FanChannel, duties);

        Assert.Equal(166, cmd.Length); // 18 device blocks * 9 + 4-byte header
        Assert.Equal(QSeriesCoolerProtocol.SetFanFrameLength, cmd.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x02, 0x02 }, cmd[0..4]);

        // Block 0 (solo): [idx=1, 0, 60, 0, 0, 0, 0, 0, 0].
        Assert.Equal(new byte[] { 1, 0, 60, 0, 0, 0, 0, 0, 0 }, cmd[4..13]);
        // Block 1 (duo): [idx=2, 0, 40, 0, 0, 0, 55, 0, 0].
        Assert.Equal(new byte[] { 2, 0, 40, 0, 0, 0, 55, 0, 0 }, cmd[13..22]);
        // Block 2 (trio): [idx=3, 0, 20, 0, 0, 0, 30, 45, 0].
        Assert.Equal(new byte[] { 3, 0, 20, 0, 0, 0, 30, 45, 0 }, cmd[22..31]);
        // Block 3 (unused slot): index still stamped, every duty byte zero.
        Assert.Equal(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0, 0 }, cmd[31..40]);
        // Last block (17, 0-based) is still index-stamped and zero-filled.
        Assert.Equal(18, cmd[cmd.Length - 9]);
        for (var i = cmd.Length - 8; i < cmd.Length; i++) Assert.Equal(0, cmd[i]);
    }

    [Fact]
    public void BuildSetChannelFanSpeeds_clamps_each_fan_duty()
    {
        var duties = new[] { new QSeriesCoolerProtocol.QSeriesFanSlotDuty(150, -5, 250) };
        var cmd = QSeriesCoolerProtocol.BuildSetChannelFanSpeeds(QSeriesCoolerProtocol.FanChannel, duties);
        Assert.Equal(100, cmd[6]); // fan1 clamped up
        Assert.Equal(0, cmd[10]);  // fan2 clamped down
        Assert.Equal(100, cmd[11]); // fan3 clamped up
    }

    [Theory]
    [InlineData(90, true, 90)]    // turbo on: full range
    [InlineData(100, true, 100)]
    [InlineData(90, false, 65)]   // turbo off: capped at the fan ceiling
    [InlineData(50, false, 50)]   // below the cap: unchanged
    [InlineData(150, false, 65)]
    public void CapFanDutyForTurbo_ceilings_off_turbo(int duty, bool turbo, int expected)
    {
        Assert.Equal(expected, QSeriesCoolerProtocol.CapFanDutyForTurbo(duty, turbo));
    }

    // ── Firmware animation live-apply frame (NEX-62) ──

    [Fact]
    public void BuildSetControlWithAnimation_carries_the_new_animation_in_bytes_10_to_14()
    {
        var frame = QSeriesCoolerProtocol.BuildSetControlWithAnimation(
            QSeriesCoolerProtocol.ControlModeSoftware, pumpWire: 42,
            QSeriesCoolerProtocol.TurboOffByte,
            QSeriesCoolerProtocol.FwAnimationBreathe, 5, 6, 7, 25);

        Assert.Equal(QSeriesCoolerProtocol.SetControlFrameLength, frame.Length);
        Assert.Equal(0xFF, frame[0]);
        Assert.Equal(0xCC, frame[1]);
        Assert.Equal(0x02, frame[2]);
        Assert.Equal(QSeriesCoolerProtocol.ControlModeSoftware, frame[4]);
        Assert.Equal(42, frame[5]);
        Assert.Equal(QSeriesCoolerProtocol.TurboOffByte, frame[9]);
        Assert.Equal(QSeriesCoolerProtocol.FwAnimationBreathe, frame[10]);
        Assert.Equal(5, frame[11]);
        Assert.Equal(6, frame[12]);
        Assert.Equal(7, frame[13]);
        Assert.Equal(25, frame[14]);
    }

    /// <summary>The animation-carrying frame differs from the echoing one only in [10..14].</summary>
    [Fact]
    public void BuildSetControlWithAnimation_matches_BuildSetControl_outside_the_animation_bytes()
    {
        var port0 = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        port0[0] = 0xFF; port0[1] = 0xCC;
        port0[15] = 9; port0[16] = 9; port0[17] = 9; port0[18] = 9; port0[19] = 9;

        var echoed = QSeriesCoolerProtocol.BuildSetControl(1, 42, QSeriesCoolerProtocol.TurboOffByte, port0);
        var carried = QSeriesCoolerProtocol.BuildSetControlWithAnimation(
            1, 42, QSeriesCoolerProtocol.TurboOffByte, 2, 3, 4, 5, 6);

        for (var i = 0; i < QSeriesCoolerProtocol.SetControlFrameLength; i++)
        {
            if (i >= 10 && i <= 14) continue;
            Assert.Equal(echoed[i], carried[i]);
        }
    }
}
