using System;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Tests.Np50;

/// <summary>
/// Golden-vector coverage for the NP50 wire protocol. Bytes here come from
/// hyte-refs/hyte-documents/firmware-protocol/NP50/ - the authoritative
/// spec - not from the WPF test apps (which have a few buffer-handling
/// workarounds we shouldn't ossify).
///
/// The whole point of <see cref="Np50Protocol"/> is to be pure functions
/// callable without hardware, so these tests are fast (no IO) and cheap to
/// extend whenever a new command lands.
/// </summary>
public class Np50ProtocolTests
{
    // ── Builders ──

    [Fact]
    public void BuildGetInfo_emits_FF_CC_01_00()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, 0x00 }, Np50Protocol.BuildGetInfo());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BuildGetChannelInfo_emits_FF_CC_01_port(int port)
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x01, (byte)port }, Np50Protocol.BuildGetChannelInfo(port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void BuildGetChannelInfo_rejects_out_of_range_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Np50Protocol.BuildGetChannelInfo(port));
    }

    [Fact]
    public void BuildGetFirmwareVersion_emits_FF_DD_02_with_4_reserved_bytes()
    {
        Assert.Equal(new byte[] { 0xFF, 0xDD, 0x02, 0x00, 0x00, 0x00, 0x00 }, Np50Protocol.BuildGetFirmwareVersion());
    }

    [Fact]
    public void BuildGetWarningDetail_emits_FF_CC_06()
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x06 }, Np50Protocol.BuildGetWarningDetail());
    }

    [Theory]
    [InlineData(true, 0x01)]
    [InlineData(false, 0x00)]
    public void BuildSetStartAnimationOff_emits_FF_CC_05_flag(bool off, byte expectedFlag)
    {
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x05, expectedFlag }, Np50Protocol.BuildSetStartAnimationOff(off));
    }

    [Theory]
    [InlineData(true, 0x01)]
    [InlineData(false, 0x00)]
    public void BuildSetFirmwareLightingOff_emits_FF_CC_07_flag(bool off, byte expectedFlag)
    {
        // Reference: HYTE nexus-control-service NP50Command.SetFirmwareLightingOff
        // emits these exact bytes. Required to keep the firmware's default
        // rainbow off when we're streaming software lighting.
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x07, expectedFlag }, Np50Protocol.BuildSetFirmwareLightingOff(off));
    }

    [Fact]
    public void BuildWriteFirmwareAnimationToMcu_emits_FF_CC_0C_anim_RGB_brightness_save()
    {
        // Reference: HYTE nexus-control-service SmartHubCommandBase.WriteFwAnimationToMcu
        // emits these exact bytes. The trailing 0x01 is the SAVE flag.
        var bytes = Np50Protocol.BuildWriteFirmwareAnimationToMcu(
            animation: 0x02, r: 0xAA, g: 0xBB, b: 0xCC, brightness: 0x32);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x0C, 0x02, 0xAA, 0xBB, 0xCC, 0x32, 0x01 }, bytes);
    }

    [Fact]
    public void BuildWriteFirmwareAnimationToMcu_with_all_zeros_disables_animation()
    {
        // animation=0 + brightness=0 is what we send on first connect to
        // silence the live MCU firmware animation. The MCU stops driving
        // any LED with its default rainbow even if those LEDs aren't
        // addressed by the software LED stream.
        var bytes = Np50Protocol.BuildWriteFirmwareAnimationToMcu(0, 0, 0, 0, 0);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 }, bytes);
    }

    [Theory]
    [InlineData(Np50Protocol.ModeSoftware)]
    [InlineData(Np50Protocol.ModeMotherboard)]
    [InlineData(Np50Protocol.ModeStatic)]
    public void BuildSetCoolingMode_writes_v2_15_byte_buffer_with_all_parameters(byte mode)
    {
        var buf = Np50Protocol.BuildSetCoolingMode(
            mode,
            staticSpeedPercent: 75,
            turboOff: true,
            fwAnimation: 0x02,
            fwR: 0xAA, fwG: 0xBB, fwB: 0xCC,
            fwBrightness: 80);
        Assert.Equal(15, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xCC, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x00, buf[3]);
        Assert.Equal(mode, buf[4]);
        Assert.Equal(75, buf[5]);
        Assert.Equal(0x00, buf[6]);   // RPM mode
        Assert.Equal(0x00, buf[7]);   // reserved
        Assert.Equal(0x00, buf[8]);   // reserved
        Assert.Equal(0x01, buf[9]);   // turbo off
        Assert.Equal(0x02, buf[10]);  // fw animation
        Assert.Equal(0xAA, buf[11]);
        Assert.Equal(0xBB, buf[12]);
        Assert.Equal(0xCC, buf[13]);
        Assert.Equal(80, buf[14]);
    }

    [Fact]
    public void BuildSetCoolingMode_rejects_unknown_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => Np50Protocol.BuildSetCoolingMode(0x99));
    }

    [Theory]
    [InlineData(Np50Protocol.DefaultModeStatic, 75)]
    [InlineData(Np50Protocol.DefaultModeMotherboard, 0)]
    public void BuildSetDefaultMode_emits_18_byte_buffer_with_SAVE_byte(byte mode, byte percent)
    {
        // Spec command #6: FF CC 03 00 MODE FAN% [reserved×11] 01(SAVE).
        // This is the EEPROM-persisted "what the hub does when nexus isn't streaming" command.
        var buf = Np50Protocol.BuildSetDefaultMode(mode, percent);
        Assert.Equal(18, buf.Length);
        Assert.Equal(new byte[] { 0xFF, 0xCC, 0x03, 0x00, mode, percent }, buf.AsSpan(0, 6).ToArray());
        for (var i = 6; i <= 16; i++) Assert.Equal(0x00, buf[i]); // reserved
        Assert.Equal(0x01, buf[17]); // SAVE
    }

    [Fact]
    public void BuildSetDefaultMode_clamps_fan_percent_to_0_100()
    {
        Assert.Equal(100, Np50Protocol.BuildSetDefaultMode(Np50Protocol.DefaultModeStatic, 200)[5]);
        Assert.Equal(0, Np50Protocol.BuildSetDefaultMode(Np50Protocol.DefaultModeStatic, 0)[5]);
    }

    [Fact]
    public void BuildSetDefaultMode_rejects_invalid_mode_byte()
    {
        Assert.Throws<ArgumentException>(() => Np50Protocol.BuildSetDefaultMode(0x02, 50));
        Assert.Throws<ArgumentException>(() => Np50Protocol.BuildSetDefaultMode(0x99, 50));
    }

    [Fact]
    public void ParseFirmwareAnimation_decodes_anim_RGB_brightness()
    {
        // 8-byte response as fw 2.0.5.1 sends it (Y70 box, 2026-09-13):
        // [0..2] FF CC 0D echo, [3] anim, [4..6] RGB, [7] brightness.
        var response = new byte[] { 0xFF, 0xCC, 0x0D, 0x02, 0x10, 0x20, 0x30, 0x40 };
        var parsed = Np50Protocol.ParseFirmwareAnimation(response);
        Assert.Equal(Np50Protocol.FwAnimationRainbow, parsed.Animation);
        Assert.Equal(0x10, parsed.R);
        Assert.Equal(0x20, parsed.G);
        Assert.Equal(0x30, parsed.B);
        Assert.Equal(0x40, parsed.Brightness);
    }

    [Fact]
    public void ParseFirmwareAnimation_rejects_wrong_sub_opcode()
    {
        var response = new byte[] { 0xFF, 0xCC, 0x0E, 0x01, 0, 0, 0, 100 };
        Assert.Throws<InvalidOperationException>(() => Np50Protocol.ParseFirmwareAnimation(response));
    }

    [Fact]
    public void ParseFirmwareAnimation_rejects_short_response()
    {
        var response = new byte[] { 0xFF, 0xCC, 0x0D, 0x00 };
        Assert.Throws<ArgumentException>(() => Np50Protocol.ParseFirmwareAnimation(response));
    }

    [Fact]
    public void BuildSetLegacyFanSpeed_clamps_and_places_percent_at_byte_5()
    {
        var buf = Np50Protocol.BuildSetLegacyFanSpeed(75);
        Assert.Equal(12, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xCC, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x00, buf[3]);   // channel 0
        Assert.Equal(0x00, buf[4]);   // device 0 = 4-pin
        Assert.Equal(75, buf[5]);     // duty percent as a raw byte

        // Clamps
        Assert.Equal(100, Np50Protocol.BuildSetLegacyFanSpeed(150)[5]);
        Assert.Equal(0, Np50Protocol.BuildSetLegacyFanSpeed(-7)[5]);
    }

    [Fact]
    public void BuildSetPortFanSpeeds_pads_to_166_bytes_and_writes_per_slot_percentages()
    {
        var percents = new[] { 50, 60, 70 }; // only 3 fans connected; spec still wants all 18 slots
        var buf = Np50Protocol.BuildSetPortFanSpeeds(port: 2, percents);
        Assert.Equal(4 + 18 * 9, buf.Length); // 166
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xCC, buf[1]);
        Assert.Equal(0x02, buf[2]);
        Assert.Equal(0x02, buf[3]); // port 2

        for (var i = 0; i < 18; i++)
        {
            var off = 4 + i * 9;
            Assert.Equal((byte)(i + 1), buf[off + 0]); // 1-based device count
            Assert.Equal((byte)(i < percents.Length ? percents[i] : 0), buf[off + 2]);
        }
    }

    [Fact]
    public void BuildSetPortFanSpeeds_drops_entries_beyond_the_18_slot_limit()
    {
        var tooMany = new int[20];
        for (var i = 0; i < tooMany.Length; i++) tooMany[i] = 42;
        var buf = Np50Protocol.BuildSetPortFanSpeeds(1, tooMany);
        Assert.Equal(166, buf.Length); // still 166, didn't grow
        // Last slot's percent is still 42 (entry 17), entry 18+ silently dropped.
        var lastOff = 4 + 17 * 9;
        Assert.Equal(42, buf[lastOff + 2]);
    }

    [Fact]
    public void BuildLightingStream_writes_GRB_byte_order_per_LED()
    {
        var leds = new[]
        {
            new RgbColor(R: 0x11, G: 0x22, B: 0x33),
            new RgbColor(R: 0xAA, G: 0xBB, B: 0xCC),
        };
        var buf = Np50Protocol.BuildLightingStream(port: 3, leds);
        // HYTE pads to a 90-byte minimum (CoolingHubBaseController.SendToHardware
        // calls PadListWithZeros(90)). Frames shorter than 90 bytes appear to be
        // silently dropped by firmware 2.0.3.1, so we always emit ≥90.
        Assert.Equal(Np50Protocol.LightingStreamMinFrameBytes, buf.Length);
        Assert.Equal(0xFF, buf[0]);
        Assert.Equal(0xEE, buf[1]);
        Assert.Equal(0x01, buf[2]);
        Assert.Equal(0x03, buf[3]); // port 3
        // HYTE reference (CoolingHubBaseController.SendToHardware) hardcodes
        // these two bytes to the magic 0x01 0x68 regardless of the real
        // LED count - see the comment on LedCountMagicHigh/Low.
        Assert.Equal(0x01, buf[4]);
        Assert.Equal(0x68, buf[5]);
        Assert.Equal(0x00, buf[6]); // reserved

        // LED 0: GRB
        Assert.Equal(0x22, buf[7 + 0]); // G
        Assert.Equal(0x11, buf[7 + 1]); // R
        Assert.Equal(0x33, buf[7 + 2]); // B
        // LED 1: GRB
        Assert.Equal(0xBB, buf[7 + 3]);
        Assert.Equal(0xAA, buf[7 + 4]);
        Assert.Equal(0xCC, buf[7 + 5]);
    }

    [Fact]
    public void BuildLightingStream_always_emits_LedCount_magic_constant_0x0168()
    {
        // Reference: HYTE nexus-control-service CoolingHubBaseController.cs:336
        // emits 0x01 0x68 in bytes 4/5 for every frame, ignoring the real LED
        // count. Match that exactly so we behave identically against the same
        // firmware revisions HYTE qualified on.
        foreach (var ledCount in new[] { 0, 1, 6, 30, 90, 250, 300 })
        {
            var buf = Np50Protocol.BuildLightingStream(1, new RgbColor[ledCount]);
            Assert.Equal(0x01, buf[4]);
            Assert.Equal(0x68, buf[5]);
        }
    }

    [Fact]
    public void BuildLightingStream_pads_empty_port_frame_to_90_bytes()
    {
        // Empty-port frames (port 4 in the 4-port HYTE cycle when no devices
        // are attached) MUST still pad to the 90-byte minimum. The
        // 4-port-cycle dark-strips bug took a full day to find because
        // firmware 2.0.5.1 silently drops short frames and never commits the
        // LED latch. Lock this contract in.
        var buf = Np50Protocol.BuildLightingStream(port: 4, ReadOnlySpan<RgbColor>.Empty);
        Assert.Equal(Np50Protocol.LightingStreamMinFrameBytes, buf.Length);
        Assert.Equal(new byte[] { 0xFF, 0xEE, 0x01, 0x04, 0x01, 0x68, 0x00 }, buf[..7]);
        for (var i = 7; i < buf.Length; i++) Assert.Equal(0, buf[i]);
    }

    [Fact]
    public void BuildLightingStream_accepts_port_4_rejects_port_5()
    {
        // The lighting-stream cycle is 4 frames per tick (HYTE's
        // CoolingHubBaseController iterates devicePort 0..3) - port 4 must
        // be addressable even though NP50 only has 3 fan ports. Anything
        // past port 4 is out of contract.
        _ = Np50Protocol.BuildLightingStream(port: 4, ReadOnlySpan<RgbColor>.Empty);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Np50Protocol.BuildLightingStream(port: 5, ReadOnlySpan<RgbColor>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Np50Protocol.BuildLightingStream(port: 0, ReadOnlySpan<RgbColor>.Empty));
    }

    // ── Parsers ──

    [Fact]
    public void ParseFirmwareVersion_returns_dotted_version_string()
    {
        // FF DD 02 01 00 02 01 → "1.0.2.1" per the WPF reference + spec.
        var response = new byte[] { 0xFF, 0xDD, 0x02, 0x01, 0x00, 0x02, 0x01 };
        Assert.Equal("1.0.2.1", Np50Protocol.ParseFirmwareVersion(response));
    }

    [Fact]
    public void ParseFirmwareVersion_returns_empty_on_unexpected_header()
    {
        Assert.Equal("", Np50Protocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xCC, 0x02, 1, 0, 0, 0 }));
        Assert.Equal("", Np50Protocol.ParseFirmwareVersion(new byte[] { 0xFF, 0xDD })); // too short
    }

    [Fact]
    public void ParseHubInfo_decodes_mode_warning_RPM_and_temp()
    {
        // 20-byte response with:
        //   bytes 7..8  : pump temp ADC bytes (0x16, 0x0A → ~2.27V → ~50°C-ish, well inside the table)
        //   bytes 9..10 : RPM bytes (0x08, 0x00 → 187 RPM via the legacy formula)
        //   byte 12     : mode byte 0x02 = Motherboard
        //   byte 13     : warning summary 0x04
        //   bytes 15..19: FW animation block
        var response = new byte[]
        {
            0xFF, 0xCC, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x16, 0x0A,    // temp H, L
            0x08, 0x00,    // RPM H, L
            0x00,
            Np50Protocol.ModeMotherboard,
            0x04,          // warning summary (current overflow on some port)
            0x00,
            0x03,          // FW animation = Breathe
            0xAA, 0xBB, 0xCC,
            0x32,          // brightness = 50
        };
        var info = new Np50HubInfo();
        Np50Protocol.ParseHubInfo(response, info);

        Assert.Equal("Motherboard", info.CoolingMode);
        Assert.Equal(0x04, info.WarningSummary);
        Assert.NotNull(info.CableTempC);
        // Legacy formula: 60_000 / ((0x08*100 + 0)/10 * 4) = 60_000 / 320 = 187
        Assert.Equal(187, info.LegacyFanRpm);
        Assert.Equal(0x03, info.FirmwareAnimation);
        Assert.Equal(0xAA, info.FirmwareAnimR);
        Assert.Equal(0xBB, info.FirmwareAnimG);
        Assert.Equal(0xCC, info.FirmwareAnimB);
        Assert.Equal(0x32, info.FirmwareAnimBrightness);
    }

    [Fact]
    public void ParseHubInfo_rejects_wrong_header()
    {
        var info = new Np50HubInfo();
        var bad = new byte[20];
        bad[0] = 0xFE; bad[1] = 0xCC;
        Assert.Throws<InvalidOperationException>(() => Np50Protocol.ParseHubInfo(bad, info));
    }

    [Fact]
    public void ParseChannelInfo_decodes_two_fans_and_stops_when_type_byte_is_zero()
    {
        // Two populated slots followed by an empty one.
        // Slot 0: FF CC + 12-byte device frame. Slot 1: 00 00 prefix + frame.
        // Empty slot: Type byte (off+3) == 0 is the real stop signal - on
        // firmware 2.0.3.1 the device-count byte at off+2 stays non-zero
        // in trailing empty slots, so the spec-doc "stop at DC=0" was wrong.
        var resp = new byte[12 * 3];

        // Slot 0: LS30 fan, 62 LEDs.
        // Temp bytes 0x0B 0x05 → uint16 BE / 100 = 2821/100 = 28.21°C.
        // RPM bytes 0x05 0x00 → Nexus Link formula (60_000 / (5 + 0))/4 = 3000 RPM.
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[2] = 0x01;       // device count 1
        resp[3] = 0x02;       // LS30
        resp[4] = 0x10;       // hw version
        resp[5] = 62;         // led count
        resp[6] = 0x0B; resp[7] = 0x05; // floating probe input - 0.890V, off-table
        resp[8] = 0x05; resp[9] = 0x00; // rpm 3000
        resp[10] = 0x02;      // orientation Up
        resp[11] = 0x01;      // touch byte ignored for LS30

        // Slot 1: FP12 fan, touching, probe reading 2.717V → 35°C.
        resp[12] = 0x00; resp[13] = 0x00;
        resp[14] = 0x02;
        resp[15] = 0x03;      // FP12
        resp[16] = 0x05;
        resp[17] = 20;        // 20 LEDs reported (unusual for FP12 but we honor the wire)
        resp[18] = 33; resp[19] = 72;
        resp[20] = 0x02; resp[21] = 0x00;
        resp[22] = 0x00;      // orientation Back
        resp[23] = 0x00;      // touching

        // Slot 2: type byte 0x00 → parser stops. Device-count byte stays
        // non-zero just like real hardware does.
        resp[24] = 0x00; resp[25] = 0x00;
        resp[26] = 0x05;      // arbitrary non-zero device count
        resp[27] = 0x00;      // type byte zero - STOP signal

        var port = new Np50Port { Index = 2 };
        Np50Protocol.ParseChannelInfo(resp, port);

        Assert.Equal(2, port.Devices.Count);

        Assert.Equal("LS30", port.Devices[0].Model);
        Assert.Equal(62, port.Devices[0].LedCount);
        Assert.Null(port.Devices[0].TempC);       // light strips carry no probe
        Assert.Equal(3000, port.Devices[0].Rpm);
        Assert.Equal("Up", port.Devices[0].Orientation);
        Assert.False(port.Devices[0].Touching);   // LS30 ignores touch byte

        Assert.Equal("FP12", port.Devices[1].Model);
        Assert.Equal(35f, port.Devices[1].TempC);
        Assert.True(port.Devices[1].Touching);    // FP12 + 0x00 touch byte
    }

    [Theory]
    [InlineData(0x06, 40, "LN60")]   // LN60 noodle: 40 LEDs
    [InlineData(0x07, 44, "LN70")]   // LN70 noodle: 44 LEDs
    public void ParseChannelInfo_decodes_noodle(byte typeByte, int ledCount, string expectedModel)
    {
        // Noodles are daisy-chained on the same wire as fan modules; the
        // firmware reports each via its device-type byte and the strip's LED count.
        var resp = new byte[24];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[2] = 0x01;            // device count 1
        resp[3] = typeByte;
        resp[4] = 0x10;            // hw version
        resp[5] = (byte)ledCount;
        // resp[6..7] left at 0 → 0V, outside the thermistor table = "no probe".
        // resp[15] (next slot's type byte) stays 0 → parser stops.

        var port = new Np50Port { Index = 3 };
        Np50Protocol.ParseChannelInfo(resp, port);

        Assert.Single(port.Devices);
        Assert.Equal(expectedModel, port.Devices[0].Model);
        Assert.Equal(ledCount, port.Devices[0].LedCount);
        Assert.Equal(0x10, port.Devices[0].HardwareVersion);
        Assert.Null(port.Devices[0].TempC);     // noodles don't carry a temp probe
        Assert.False(port.Devices[0].Touching); // touch byte only honored for FP12
    }

    [Fact]
    public void ParseChannelInfo_clears_devices_before_repopulating()
    {
        var port = new Np50Port { Index = 1 };
        port.Devices.Add(new Np50FanDevice { Index = 99, Model = "Stale" });

        var resp = new byte[24];
        resp[0] = 0xFF; resp[1] = 0xCC; resp[2] = 0x01; resp[3] = 0x01; // one LS10
        resp[5] = 20;
        // resp[15] (next slot's type byte) is 0 → parser stops.
        Np50Protocol.ParseChannelInfo(resp, port);

        Assert.Single(port.Devices);
        Assert.Equal("LS10", port.Devices[0].Model);
    }

    [Theory]
    [InlineData(0x0B, 0x05)]   // observed: LS10 on Port 1 → 0.890V, under the table floor
    [InlineData(0x08, 0x16)]   // observed: LS10 on Port 2 #2 → 0.662V
    [InlineData(0x0E, 0x22)]   // observed: LS10 on Port 1 → 1.157V
    public void ParseChannelInfo_drops_probeless_module_readings(byte high, byte low)
    {
        // LS/LN modules float the probe input; the voltage lands outside the
        // thermistor table, which is not a temperature.
        Assert.Null(Np50Protocol.TryDecodeTempC(high, low, Np50Protocol.FanOrPump.Fan));
    }

    [Theory]
    [InlineData(33, 72, 35)]   // reported by a user's FP12s at idle (Port 1 #1)
    [InlineData(34, 6, 34)]    // Port 1 #2
    [InlineData(36, 24, 24)]   // Port 3 #3
    public void TryDecodeTempC_decodes_FP12_probe_bytes_as_ADC_voltage(byte high, byte low, float expected)
    {
        // V = 3.3 * (high*100 + low) / 4096 → fan thermistor table.
        var t = Np50Protocol.TryDecodeTempC(high, low, Np50Protocol.FanOrPump.Fan);
        Assert.NotNull(t);
        Assert.Equal(expected, t!.Value);
    }

    [Fact]
    public void ParseWarningDetail_decodes_each_port_bitfield()
    {
        // Port 1: bits 0+1 set (LED count exceeded + current overflow)
        // Port 2: bit 2 set (per-port device count exceeded)
        // Port 3: bit 3 set (total device count exceeded)
        var resp = new byte[] { 0xFF, 0xCC, 0x06, 0b0011, 0b0100, 0b1000 };
        var detail = new Np50WarningDetail();
        Np50Protocol.ParseWarningDetail(resp, detail);

        Assert.True(detail.Port1.LedCountExceeded);
        Assert.True(detail.Port1.CurrentOverflow);
        Assert.False(detail.Port1.PortDeviceCountExceeded);
        Assert.False(detail.Port1.TotalDeviceCountExceeded);
        Assert.Equal(0b0011, detail.Port1.Raw);

        Assert.True(detail.Port2.PortDeviceCountExceeded);
        Assert.False(detail.Port2.CurrentOverflow);

        Assert.True(detail.Port3.TotalDeviceCountExceeded);
    }

    [Fact]
    public void ParseWarningDetail_rejects_wrong_sub_opcode()
    {
        var resp = new byte[] { 0xFF, 0xCC, 0x07, 0, 0, 0 };
        Assert.Throws<InvalidOperationException>(() => Np50Protocol.ParseWarningDetail(resp, new Np50WarningDetail()));
    }

    // ── Math helpers ──

    [Fact]
    public void DecodeRpm_returns_zero_for_both_zero_bytes()
    {
        Assert.Equal(0, Np50Protocol.DecodeLegacyRpm(0, 0));
        Assert.Equal(0, Np50Protocol.DecodeFanRpm(0, 0));
    }

    [Fact]
    public void DecodeLegacyRpm_matches_spec_formula()
    {
        // Spec #1 / GetRPM: 60_000 / ((H*100 + L)/10 * 4)
        // H=0x05, L=0 → 60_000 / ((500)/10*4) = 60_000/200 = 300
        Assert.Equal(300, Np50Protocol.DecodeLegacyRpm(0x05, 0));
        // H=0x01, L=0x32 → 60_000 / ((150)/10*4) = 60_000/60 = 1000
        Assert.Equal(1000, Np50Protocol.DecodeLegacyRpm(0x01, 0x32));
    }

    [Fact]
    public void DecodeFanRpm_matches_spec_formula()
    {
        // Spec #2 / GetFanRPM default: (60_000 / (H + L/100)) / 4
        // H=0x05, L=0 → (60_000/5)/4 = 12000/4 = 3000
        Assert.Equal(3000, Np50Protocol.DecodeFanRpm(0x05, 0));
        // H=0x01, L=0x32 → (60_000/1.5)/4 = 40000/4 = 10000
        Assert.Equal(10000, Np50Protocol.DecodeFanRpm(0x01, 0x32));
    }

    [Fact]
    public void TryDecodeTempC_returns_null_for_fan_absent_sentinel()
    {
        Assert.Null(Np50Protocol.TryDecodeTempC(0, 1, Np50Protocol.FanOrPump.Fan));
    }

    [Fact]
    public void TryDecodeTempC_returns_a_real_reading_inside_the_table_range()
    {
        // Pump table around 50°C maps to ~2.26V. Construct bytes that produce
        // that voltage via V = 3.3 * (H*100 + L) / 4096 → V≈2.26 → H*100+L ≈ 2806
        // H=28, L=6 → V = 3.3 * 2806/4096 ≈ 2.261
        var t = Np50Protocol.TryDecodeTempC(28, 6, Np50Protocol.FanOrPump.Pump);
        Assert.NotNull(t);
        Assert.InRange(t!.Value, 48, 52);
    }

    [Theory]
    [InlineData(40, 95)]  // 3.30V, above the fan table's 0°C entry
    [InlineData(5, 0)]    // 0.40V, below its 75°C entry
    public void TryDecodeTempC_returns_null_outside_the_thermistor_table(byte high, byte low)
    {
        Assert.Null(Np50Protocol.TryDecodeTempC(high, low, Np50Protocol.FanOrPump.Fan));
    }

    [Theory]
    // Fan table ends: 3.22V = 0°C, 1.803V = 75°C. Bytes chosen to land ON each end.
    [InlineData(39, 96, Np50Protocol.FanOrPump.Fan)]  // 3.2200V
    [InlineData(22, 38, Np50Protocol.FanOrPump.Fan)]  // 1.8033V
    // Pump table ends: 3.04V = 0°C, 1.643V = 75°C.
    [InlineData(37, 74, Np50Protocol.FanOrPump.Pump)] // 3.0400V
    [InlineData(20, 40, Np50Protocol.FanOrPump.Pump)] // 1.6430V
    public void TryDecodeTempC_rejects_a_reading_that_saturates_either_table_end(
        byte high, byte low, Np50Protocol.FanOrPump kind)
    {
        // Saturation means the probe is outside its calibrated span, not that it is 0°C or 75°C.
        Assert.Null(Np50Protocol.TryDecodeTempC(high, low, kind));
    }

    [Theory]
    [InlineData(39, 84, Np50Protocol.FanOrPump.Fan, 1f)]   // 3.2098V → one step in from the cold end
    [InlineData(22, 69, Np50Protocol.FanOrPump.Fan, 74f)]  // 1.8281V → one step in from the hot end
    public void TryDecodeTempC_still_reads_the_entries_adjacent_to_each_end(
        byte high, byte low, Np50Protocol.FanOrPump kind, float expected)
    {
        // The rejection must be exactly the two end entries, not a wider band.
        Assert.Equal(expected, Np50Protocol.TryDecodeTempC(high, low, kind));
    }
}
