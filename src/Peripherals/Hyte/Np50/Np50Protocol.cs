using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Pure builders + parsers for the HYTE NP50 serial-over-USB protocol.
/// No IO; the transport layer (<see cref="INp50Transport"/>) does the
/// actual reads and writes. Wire spec lives in
/// hyte-refs/hyte-documents/firmware-protocol/NP50/.
///
/// Every command starts with <c>0xFF</c> and three opcode families:
///   <c>0xDD</c> = query (firmware version)
///   <c>0xCC</c> = control (fan/pump info, mode, RPM, warnings)
///   <c>0xEE</c> = lighting (LED streaming)
/// </summary>
public static class Np50Protocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int ProductId = 0x0901;

    /// <summary>Bootloader (DFU) PID.</summary>
    public const int DfuProductId = 0x0A00;

    /// <summary>Hub goes back to motherboard/firmware control if no Get-Info call within this window.</summary>
    public const int HeartbeatRevertMs = 5000;

    /// <summary>Recommended poll interval. Half of HeartbeatRevertMs so we have margin if a tick is delayed.</summary>
    public const int RecommendedPollMs = 2000;

    /// <summary>Number of Nexus Link Type-C ports on the hub.</summary>
    public const int PortCount = 3;

    /// <summary>
    /// Number of port frames HYTE emits per LED-stream cycle. NP50 has 3
    /// physical Nexus Link ports for fans, but the firmware-side lighting
    /// loop iterates 4 (per HYTE's <c>CoolingHubBaseController.SendToHardware</c>),
    /// and skipping the 4th appears to leave the latch un-committed on
    /// firmware 2.0.5.1 - strips stay dark even with valid 1..3 frames.
    /// </summary>
    public const int LightingCyclePortCount = 4;

    /// <summary>Maximum daisy-chained fans per port the protocol can address.</summary>
    public const int MaxDevicesPerPort = 18;

    // ── Cooling mode bytes (live; opcode #3) ──

    public const byte ModeSoftware = 0x01;
    public const byte ModeMotherboard = 0x02;
    public const byte ModeStatic = 0x03;

    // ── Firmware default-mode bytes (EEPROM; opcode #6 / #7) ──
    //
    // Distinct from the live cooling-mode bytes above - the firmware default
    // is what runs when nexus isn't streaming. "Software" isn't meaningful as
    // a default because by definition there's no software running then.
    public const byte DefaultModeStatic = 0x00;
    public const byte DefaultModeMotherboard = 0x01;

    // ── Firmware animation bytes (opcode #13 / #14) ──
    public const byte FwAnimationColor = 0x01;
    public const byte FwAnimationRainbow = 0x02;
    public const byte FwAnimationBreathe = 0x03;
    public const byte FwAnimationRainbowGradient = 0x04;

    // ── Wire constants used by parsers + builders ──

    private const byte Frame0 = 0xFF;
    private const byte OpQuery = 0xDD;     // get-firmware-version family
    private const byte OpControl = 0xCC;   // info / control / warning family
    private const byte OpLighting = 0xEE;  // LED streaming family

    private const byte SubGetStatus = 0x01;
    private const byte SubSetControl = 0x02;
    private const byte SubWarningDetail = 0x06;

    // ────────────────────────────────────────────────────────────────────
    // Builders
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Build the "Get NP50 Info" request (4 bytes). Doubles as the heartbeat.</summary>
    public static byte[] BuildGetInfo() => new byte[] { Frame0, OpControl, SubGetStatus, 0x00 };

    /// <summary>Build the "Get Channel Info" request for ports 1..3 (4 bytes).</summary>
    public static byte[] BuildGetChannelInfo(int port)
    {
        EnsurePort(port);
        return new byte[] { Frame0, OpControl, SubGetStatus, (byte)port };
    }

    /// <summary>Build the "Get Firmware Version" request (7 bytes, trailing reserved).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpQuery, SubSetControl, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Build the "Get Warning Detail" request (3 bytes).</summary>
    public static byte[] BuildGetWarningDetail() => new byte[] { Frame0, OpControl, SubWarningDetail };

    /// <summary>
    /// Build the "Set Start Animation Off" request (4 bytes). When passed
    /// <c>true</c>, the firmware's boot-up rainbow animation is suppressed
    /// so the LEDs don't briefly cycle when the hub powers on / our software
    /// takes over. Bytes: <c>0xFF 0xCC 0x05 [0x01 or 0x00]</c>. Matches
    /// HYTE's reference <c>NP50Command.SetStartAnimationOff</c>.
    /// </summary>
    public static byte[] BuildSetStartAnimationOff(bool off)
        => new byte[] { Frame0, OpControl, 0x05, off ? (byte)0x01 : (byte)0x00 };

    /// <summary>
    /// Build the "Set Firmware Lighting Off" request (4 bytes). When passed
    /// <c>true</c>, the firmware stops driving its built-in default animation
    /// on any LED. Critical when running in software lighting mode - without
    /// it, the firmware animation runs in parallel with our stream and shows
    /// through on any LED our wire frame doesn't update (observed
    /// symptom: the first strip LED on every port permanently cycles a
    /// rainbow, "permanent firmware rainbow mode", regardless of whether
    /// the user has the strips switched on or off in software). Bytes:
    /// <c>0xFF 0xCC 0x07 [0x01 or 0x00]</c>. Matches HYTE's reference
    /// <c>NP50Command.SetFirmwareLightingOff</c>.
    /// </summary>
    public static byte[] BuildSetFirmwareLightingOff(bool off)
        => new byte[] { Frame0, OpControl, 0x07, off ? (byte)0x01 : (byte)0x00 };

    /// <summary>
    /// Build the "Write Firmware Animation to MCU" request (9 bytes).
    /// Direct write to the microcontroller - bypasses the EEPROM-saving
    /// 0x02/0x07 paths and takes immediate effect on the live MCU state.
    /// Matches HYTE's reference <c>SmartHubCommandBase.WriteFwAnimationToMcu</c>:
    /// <c>0xFF 0xCC 0x0C animation R G B brightness 0x01(SAVE)</c>.
    /// Animation byte values per spec doc section 14:
    /// <c>0x01 Color</c>, <c>0x02 Rainbow</c>, <c>0x03 Breathe</c>,
    /// <c>0x04 Rainbow Gradient</c>. We pass <c>0x00</c> (undocumented
    /// but observed-effective) with brightness 0 to fully suppress the
    /// firmware animation while we're streaming software-controlled LED
    /// frames. The 0x05/0x07 commands appear to set the persistent
    /// "off" flag in EEPROM but do NOT clear the currently-running MCU
    /// animation - this 0x0C path is the one HYTE always pairs with
    /// any firmware-animation change in <c>SwitchFwAnimation</c>.
    /// </summary>
    public static byte[] BuildWriteFirmwareAnimationToMcu(
        byte animation, byte r, byte g, byte b, byte brightness)
        => new byte[] { Frame0, OpControl, 0x0C, animation, r, g, b, brightness, 0x01 };

    /// <summary>
    /// Build the "Get NP50 Firmware Animation" request (3 bytes). Returns
    /// <see cref="FirmwareAnimationResponseLength"/> bytes with the current
    /// firmware-driven animation state - opcode 0xCC 0x0D per HYTE's
    /// <c>ControlHubCommand.GetFwAnimation</c>.
    /// </summary>
    public static byte[] BuildGetFirmwareAnimation()
        => new byte[] { Frame0, OpControl, 0x0D };

    /// <summary>Bytes the NP50 sends back to <see cref="BuildGetFirmwareAnimation"/> (bench: fw 2.0.5.1).</summary>
    public const int FirmwareAnimationResponseLength = 8;

    /// <summary>
    /// Build the "Get NP50 Firmware Default Mode" request (4 bytes).
    /// Returns 17 bytes from the EEPROM-persisted default-mode block:
    /// [4]=DefaultMode (0=Static, 1=Motherboard), [5]=StaticFanPercentage,
    /// [6]=IsStartAnimationOff, [7]=IsFirmwareLightingOff. Bytes 0..3 are
    /// the standard FF CC 04 echo header. Matches HYTE's
    /// <c>SmartHubCommandBase.GetFirmwareDefaultMode</c>.
    /// </summary>
    public static byte[] BuildGetFirmwareDefaultMode()
        => new byte[] { Frame0, OpControl, 0x04, 0x00 };

    /// <summary>
    /// Build the "Set NP50 Default Cooling Mode" request (18 bytes, EEPROM
    /// SAVE). Spec command #6: <c>FF CC 03 00 MODE FAN% [reserved×11] 01</c>.
    /// Stores what the hub does when nexus isn't streaming (PC off, service
    /// down). <see cref="DefaultModeStatic"/> plays the stored fan-speed
    /// setpoint at <paramref name="fanPercent"/>; <see cref="DefaultModeMotherboard"/>
    /// passes through motherboard PWM and ignores fan-percent.
    ///
    /// EEPROM endurance is ~10 000 writes - callers MUST read current state
    /// via <see cref="BuildGetFirmwareDefaultMode"/> first and skip the
    /// write when the bytes already match.
    /// </summary>
    public static byte[] BuildSetDefaultMode(byte defaultMode, byte fanPercent)
    {
        if (defaultMode != DefaultModeStatic && defaultMode != DefaultModeMotherboard)
            throw new ArgumentException($"Unknown default mode 0x{defaultMode:X2}", nameof(defaultMode));
        var pct = (byte)Math.Clamp((int)fanPercent, 0, 100);
        var buf = new byte[18];
        buf[0] = Frame0; buf[1] = OpControl; buf[2] = 0x03;
        buf[3] = 0x00;
        buf[4] = defaultMode;
        buf[5] = pct;
        // bytes 6..16 reserved (zero)
        buf[17] = 0x01; // SAVE - commits to EEPROM
        return buf;
    }


    /// <summary>
    /// Build the "Set NP50 Cooling Mode" v2 request (15 bytes; all parameters
    /// filled). v1 (12-byte) form is silently ignored by firmware 2.0.3.1 on
    /// the bench. Pass the current firmware-animation state through so we
    /// don't accidentally clobber it when changing cooling mode.
    /// </summary>
    public static byte[] BuildSetCoolingMode(
        byte mode,
        byte staticSpeedPercent = 50,
        bool turboOff = true,
        byte fwAnimation = 0,
        byte fwR = 0, byte fwG = 0, byte fwB = 0,
        byte fwBrightness = 100)
    {
        if (mode != ModeSoftware && mode != ModeMotherboard && mode != ModeStatic)
            throw new ArgumentException($"Unknown cooling mode 0x{mode:X2}", nameof(mode));
        // Per spec v2: 15 bytes, "need to fill in all parameters". Order:
        //   [0]=FF [1]=CC [2]=0x02 [3]=0x00 [4]=MODE [5]=Speed [6]=RpmMode
        //   [7..8]=Reserve [9]=Turbo (0=on, 1=off) [10]=FwAnim
        //   [11..13]=RGB  [14]=FwBrightness
        var buf = new byte[15];
        buf[0] = Frame0; buf[1] = OpControl; buf[2] = SubSetControl;
        buf[3] = 0x00;                               // channel 0 = hub-level
        buf[4] = mode;
        buf[5] = staticSpeedPercent;                 // only meaningful in static mode; harmless otherwise
        buf[6] = 0x00;                               // RPM mode (spec: must be 0)
        buf[7] = 0x00; buf[8] = 0x00;                // reserved
        buf[9] = turboOff ? (byte)0x01 : (byte)0x00; // turbo off=1, on=0 per spec
        buf[10] = fwAnimation;
        buf[11] = fwR; buf[12] = fwG; buf[13] = fwB;
        buf[14] = fwBrightness;
        return buf;
    }

    /// <summary>
    /// Build the "Set 4-pin PWM" request (12 bytes). <paramref name="percent"/> clamped to 0..100.
    /// Hub must already be in software mode (call <see cref="BuildSetCoolingMode"/> first).
    /// </summary>
    public static byte[] BuildSetLegacyFanSpeed(int percent)
    {
        var pct = (byte)Math.Clamp(percent, 0, 100);
        var buf = new byte[12];
        buf[0] = Frame0; buf[1] = OpControl; buf[2] = SubSetControl;
        buf[3] = 0x00;       // channel 0
        buf[4] = 0x00;       // device 0 = 4-pin PWM
        buf[5] = pct;        // duty percentage as a raw byte 0..100
        return buf;
    }

    /// <summary>
    /// Build the "Set Fan RPM" request for one Nexus Link port (166 bytes).
    /// <paramref name="perFanPercent"/> must have at most <see cref="MaxDevicesPerPort"/> entries;
    /// entries beyond that are dropped. Each entry is clamped to 0..100. Pads to 18 fan slots so
    /// disconnected fan positions get an explicit 0% (matches the spec's all-slots payload).
    /// </summary>
    public static byte[] BuildSetPortFanSpeeds(int port, IReadOnlyList<int> perFanPercent)
    {
        EnsurePort(port);
        ArgumentNullException.ThrowIfNull(perFanPercent);
        var buf = new byte[4 + MaxDevicesPerPort * 9];
        buf[0] = Frame0; buf[1] = OpControl; buf[2] = SubSetControl;
        buf[3] = (byte)port;
        for (var i = 0; i < MaxDevicesPerPort; i++)
        {
            var off = 4 + i * 9;
            buf[off + 0] = (byte)(i + 1); // device count is 1-based per the spec
            // off+1 is 0x00
            buf[off + 2] = i < perFanPercent.Count
                ? (byte)Math.Clamp(perFanPercent[i], 0, 100)
                : (byte)0;
            // off+3 .. off+8 reserved (zeros)
        }
        return buf;
    }

    // HYTE's CoolingHubBaseController.SendToHardware writes 0x01 0x68 (= 360)
    // into the two LED-count header bytes regardless of how many LEDs the
    // frame actually carries. The spec says these are LedCount_H/L; the
    // shipping firmware ignores that and accepts whatever bytes follow up
    // to the wire-frame boundary. Match the reference exactly - sending the
    // real count has been observed to leave the firmware mid-latch in
    // some firmware revs.
    private const byte LedCountMagicHigh = 0x01;
    private const byte LedCountMagicLow = 0x68;

    /// <summary>
    /// Build an LED streaming frame for a port (7-byte header + 3 bytes per LED in GRB order).
    /// Channel 1's first 6 LEDs are the HYTE logo; callers that want to drive the logo prefix
    /// their port-1 buffer with 6 logo LEDs before the fan LEDs. The hub spec caps channel 3
    /// at 750 bytes of LED payload (250 LEDs); callers must enforce that.
    /// </summary>
    public const int LightingStreamMinFrameBytes = 90;

    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        EnsureLightingPort(port);
        if (leds.Length > ushort.MaxValue)
            throw new ArgumentException("LED buffer too large for two-byte length field.", nameof(leds));
        var dataBytes = 7 + leds.Length * 3;
        var frameSize = Math.Max(dataBytes, LightingStreamMinFrameBytes);
        var buf = new byte[frameSize];
        buf[0] = Frame0; buf[1] = OpLighting; buf[2] = 0x01;
        buf[3] = (byte)port;
        buf[4] = LedCountMagicHigh;
        buf[5] = LedCountMagicLow;
        for (var i = 0; i < leds.Length; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        return buf;
    }

    // ────────────────────────────────────────────────────────────────────
    // Parsers
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse the 20-byte response to "Get NP50 Info". Mutates <paramref name="target"/> in
    /// place so the heartbeat worker can reuse a single allocation.
    /// </summary>
    public static void ParseHubInfo(ReadOnlySpan<byte> response, Np50HubInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (response.Length < 14)
            throw new ArgumentException($"Hub info response too short: {response.Length} bytes", nameof(response));
        ExpectHeader(response, OpControl, "GetInfo");

        target.CableTempC = TryDecodeTempC(response[7], response[8], FanOrPump.Pump);
        target.LegacyFanRpm = DecodeLegacyRpm(response[9], response[10]);
        target.CoolingMode = response[12] switch
        {
            ModeSoftware => "Software",
            ModeMotherboard => "Motherboard",
            ModeStatic => "Static",
            _ => "Unknown",
        };
        target.WarningSummary = response[13];

        // Optional FW-animation block (bytes 15..19); older firmwares may not return it.
        if (response.Length >= 20)
        {
            target.FirmwareAnimation = response[15];
            target.FirmwareAnimR = response[16];
            target.FirmwareAnimG = response[17];
            target.FirmwareAnimB = response[18];
            target.FirmwareAnimBrightness = response[19];
        }
    }

    public readonly record struct Np50FirmwareDefaults(
        byte DefaultMode,
        byte StaticFanPercent,
        bool IsStartAnimationOff,
        bool IsFirmwareLightingOff);

    /// <summary>Decoded firmware-animation block (opcode #14 response).</summary>
    public readonly record struct Np50FwAnimation(
        byte Animation,
        byte R,
        byte G,
        byte B,
        byte Brightness);

    /// <summary>
    /// Parse the 8-byte response to <see cref="BuildGetFirmwareAnimation"/>:
    /// the 3-byte FF CC 0D echo, then <c>[3]=Animation</c>, <c>[4]=R</c>,
    /// <c>[5]=G</c>, <c>[6]=B</c>, <c>[7]=FwBrightness</c>. Same offsets as
    /// the SmartHub's 0x0D reply and HYTE's ControlHubInfo; fw 2.0.5.1 sends
    /// exactly 8 bytes (the SmartHub appends a fan byte, the NP50 does not).
    /// </summary>
    public static Np50FwAnimation ParseFirmwareAnimation(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareAnimationResponseLength)
            throw new ArgumentException($"Firmware-animation response too short: {response.Length} bytes", nameof(response));
        ExpectHeader(response, OpControl, "GetFirmwareAnimation");
        if (response[2] != 0x0D)
            throw new InvalidOperationException($"Unexpected fw-animation sub-opcode 0x{response[2]:X2}");
        return new Np50FwAnimation(
            Animation: response[3],
            R: response[4],
            G: response[5],
            B: response[6],
            Brightness: response[7]);
    }

    /// <summary>
    /// Parse the 17-byte response to "Get Firmware Default Mode". Layout per
    /// HYTE's <c>NP50DefaultInfoModel</c>: header at [0..3], DefaultMode at [4],
    /// StaticFanPercentage at [5], IsStartAnimationOff at [6],
    /// IsFirmwareLightingOff at [7]. Bytes 8..16 are reserved / not documented.
    /// </summary>
    public static Np50FirmwareDefaults ParseFirmwareDefaultMode(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
            throw new ArgumentException($"Firmware default-mode response too short: {response.Length} bytes", nameof(response));
        ExpectHeader(response, OpControl, "GetFirmwareDefaultMode");
        return new Np50FirmwareDefaults(
            DefaultMode: response[4],
            StaticFanPercent: response[5],
            IsStartAnimationOff: response[6] == 0x01,
            IsFirmwareLightingOff: response[7] == 0x01);
    }

    /// <summary>
    /// Parse the 240-byte response to "Get Channel Info". Fills <paramref name="target"/>
    /// with one <see cref="Np50FanDevice"/> per non-empty slot, stopping at the first slot
    /// whose Device Count byte is zero (which the spec uses to denote "no more fans").
    /// </summary>
    public static void ParseChannelInfo(ReadOnlySpan<byte> response, Np50Port target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ExpectHeader(response, OpControl, "GetChannelInfo");

        target.Devices.Clear();
        const int slotSize = 12;
        var maxSlots = response.Length / slotSize;
        for (var i = 0; i < maxSlots; i++)
        {
            var off = i * slotSize;
            // First slot carries the FF CC header; subsequent slots start with 00 00.
            // The spec doc says to stop when the Device Count byte (off+2) is 0,
            // but on real firmware (2.0.3.1) the device-count byte stays
            // non-zero in empty trailing slots - it's the Device Type byte
            // (off+3) that drops to 0x00 when no fan is present. That's the
            // reliable stop condition.
            var typeByte = response[off + 3];
            if (typeByte == 0x00) break;

            var deviceCount = response[off + 2];
            var fan = new Np50FanDevice
            {
                Index = deviceCount > 0 ? deviceCount : i + 1,
                Model = typeByte switch
                {
                    0x01 => "LS10",
                    0x02 => "LS30",
                    0x03 => "FP12",
                    0x06 => "LN60",
                    0x07 => "LN70",
                    _ => "Unknown",
                },
                HardwareVersion = response[off + 4],
                LedCount = response[off + 5],
                // Bytes 6-7 are thermistor ADC voltage, not °C. Only FP12
                // carries a probe; LS/LN modules float this input and decode
                // out of table range, which TryDecodeTempC rejects as null.
                TempC = TryDecodeTempC(response[off + 6], response[off + 7], FanOrPump.Fan),
                Rpm = DecodeFanRpm(response[off + 8], response[off + 9]),
                Orientation = response[off + 10] switch
                {
                    0x00 => "Back",
                    0x01 => "Down",
                    0x02 => "Up",
                    0x03 => "Front",
                    _ => "Back",
                },
                // FP12 touch byte: 0x00 = touching, 0x01 = not touching. For non-FP12 the
                // byte is whatever the fan happens to wire there; only honor it for FP12.
                Touching = typeByte == 0x03 && response[off + 11] == 0x00,
            };
            target.Devices.Add(fan);
        }
    }

    /// <summary>Parse the 7-byte firmware-version response. Returns "Major.Minor.Build.Hw" or empty on a short read.</summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7) return "";
        if (response[0] != Frame0 || response[1] != OpQuery) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>
    /// Parse the 6-byte "Get Warning Detail" response into the three per-port bitfields.
    /// Mutates <paramref name="target"/> in place.
    /// </summary>
    public static void ParseWarningDetail(ReadOnlySpan<byte> response, Np50WarningDetail target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (response.Length < 6)
            throw new ArgumentException($"Warning-detail response too short: {response.Length} bytes", nameof(response));
        ExpectHeader(response, OpControl, "GetWarningDetail");
        if (response[2] != SubWarningDetail)
            throw new InvalidOperationException($"Unexpected warning sub-opcode 0x{response[2]:X2}");

        DecodePortWarning(response[3], target.Port1);
        DecodePortWarning(response[4], target.Port2);
        DecodePortWarning(response[5], target.Port3);
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers - RPM and temperature decoding from raw bytes
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decode the legacy 4-pin / pump RPM from the Get-Info response (bytes
    /// 9 &amp; 10). Per spec command #1 and HYTE's <c>SmartDeviceMethods.GetRPM</c>:
    /// <c>RPM = 60_000 / ((H*100 + L) / 10 * 4)</c>. Note this is a DIFFERENT
    /// scaling than the Nexus Link per-fan reading - see <see cref="DecodeFanRpm"/>.
    /// Returns 0 when both bytes are zero (the hub's "no fan attached" sentinel).
    /// </summary>
    public static int DecodeLegacyRpm(byte rpmHigh, byte rpmLow)
    {
        if (rpmHigh == 0 && rpmLow == 0) return 0;
        var rpm = (int)(60_000 / ((rpmHigh * 100 + (float)rpmLow) / 10 * 4));
        return rpm < 0 ? 0 : rpm;
    }

    /// <summary>
    /// Decode a Nexus Link fan's RPM from the Get-Channel-Info response (bytes
    /// 8 &amp; 9 of each 12-byte slot). Per spec command #2 and the default
    /// branch of HYTE's <c>SmartDeviceMethods.GetFanRPM</c> (FP12 = single FT12):
    /// <c>RPM = (60_000 / (H + L/100)) / 4</c>. This yields ~10× the value the
    /// legacy decode would for the same raw bytes, because the firmware reports
    /// the Type-C fan period in a finer unit - applying the legacy formula here
    /// was the "fake RPM" bug. Returns 0 for the (0,0) "no fan" sentinel.
    /// </summary>
    public static int DecodeFanRpm(byte rpmHigh, byte rpmLow)
    {
        if (rpmHigh == 0 && rpmLow == 0) return 0;
        var rpm = (int)(60_000 / (rpmHigh + (float)rpmLow / 100)) / 4;
        return rpm < 0 ? 0 : rpm;
    }

    /// <summary>
    /// Per-spec voltage formula: <c>V = 3.3 * (H*100 + L) / 4096</c>. Public so callers
    /// debugging odd readings can see the underlying ADC voltage.
    /// </summary>
    public static double DecodeVoltage(byte high, byte low) => HyteThermistor.VoltageFromAdc(high, low);

    /// <summary>
    /// Convert ADC voltage bytes to °C using the right lookup table. Returns null when the
    /// reading is the hub's "no probe" sentinel (high=0, low=1 for fan probes; both 0 for pump)
    /// or when the voltage saturates either end of the thermistor table.
    /// </summary>
    public static float? TryDecodeTempC(byte high, byte low, FanOrPump kind)
    {
        // Per nexus-control-service: fan probe absent is encoded as (0, 1).
        if (kind == FanOrPump.Fan && high == 0 && low == 1) return null;
        // Pump cable probe absent: both zero (rough heuristic; the WPF reference treats it as 0°C).
        if (kind == FanOrPump.Pump && high == 0 && low == 0) return null;
        return HyteThermistor.NearestTempC(DecodeVoltage(high, low), fan: kind == FanOrPump.Fan);
    }

    private static void DecodePortWarning(byte raw, Np50PortWarning target)
    {
        target.Raw = raw;
        target.LedCountExceeded = (raw & 0x01) != 0;
        target.CurrentOverflow = (raw & 0x02) != 0;
        target.PortDeviceCountExceeded = (raw & 0x04) != 0;
        target.TotalDeviceCountExceeded = (raw & 0x08) != 0;
    }

    private static void EnsureLightingPort(int port)
    {
        if (port < 1 || port > LightingCyclePortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{LightingCyclePortCount}.");
    }

    private static void EnsurePort(int port)
    {
        if (port < 1 || port > PortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{PortCount}.");
    }

    private static void ExpectHeader(ReadOnlySpan<byte> response, byte op, string context)
    {
        if (response.Length < 2)
            throw new ArgumentException($"{context} response is empty", nameof(response));
        if (response[0] != Frame0 || response[1] != op)
        {
            throw new InvalidOperationException(
                $"{context}: unexpected header [0x{response[0]:X2} 0x{response[1]:X2}], wanted [0xFF 0x{op:X2}]");
        }
    }

    /// <summary>Which thermistor curve a probe sits on; the two are not interchangeable.</summary>
    public enum FanOrPump { Pump, Fan }
}

/// <summary>24-bit RGB color used by lighting writes. GRB byte-ordering is handled by the protocol.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
