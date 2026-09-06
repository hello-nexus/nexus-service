using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Peripherals.Hyte.MiniHub; // RgbColor: shared HYTE serial RGB triple

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Pure builders + parsers for the HYTE Q-series (Q60 / Q80 "THICC" AIO)
/// cooler-controller serial protocol - the STM32 controller that the bundled
/// <c>q60/*.hex</c> / <c>q80/*.hex</c> images flash. This is NOT the Android
/// LCD panel (that's the ADB-based <c>src/QSeries/</c> stack); the cooler
/// controller enumerates as a separate USB-CDC virtual COM port
/// (VID 3402, PID 0400 = Q60, PID 0403 = Q80).
///
/// Ported from HYTE's nexus-control-service <c>SmartHubCommandBase</c>. The
/// Q-series shares the "smart hub" command family with the MiniHub, so the
/// firmware-version exchange is byte-identical to
/// <c>MiniHubProtocol.BuildGetFirmwareVersion</c> (0xFF 0xDD 0x02 → 7 bytes,
/// version = bytes [3..6]).
/// </summary>
public static class QSeriesCoolerProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int Q60ProductId = 0x0400;
    public const int Q80ProductId = 0x0403;

    /// <summary>Firmware-catalog keys (bundled-.hex directory names).</summary>
    public const string VariantQ60 = "q60";
    public const string VariantQ80 = "q80";

    /// <summary>Operating USB PID for a variant key (for the OTA product key), or -1 if unknown.</summary>
    public static int ProductIdForVariant(string variant) => variant switch
    {
        VariantQ60 => Q60ProductId,
        VariantQ80 => Q80ProductId,
        _ => -1,
    };

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xDD;        // version / mode / fan / get
    private const byte OpCooler = 0xCC;         // cooler status/control: pump/fan info, mode, turbo, warnings
    private const byte OpSerialA = 0xAA;        // serial-number query prefix
    private const byte OpSerialB = 0xBB;
    private const byte SubGetFirmwareVersion = 0x02;
    private const byte SubGetSerial = 0x02;
    private const byte SubGetInfo = 0x01;       // port-0 status (port byte 0) / per-channel smart-device info
    private const byte SubGetPump2 = 0x09;      // Q80 second-pump RPM
    private const byte SubSetControl = 0x02;    // set pump speed + control mode + turbo (15-byte frame)
    private const byte SubSetFirmwareMode = 0x03;   // write EEPROM default mode + temperature curve (36-byte frame)
    private const byte SubGetFirmwareDefault = 0x04; // read EEPROM default mode + temperature curve
    private const byte SubSetTurboMcu = 0x0A;   // persist turbo state to the MCU
    private const byte SubWriteFirmwareAnimation = 0x0C; // write MCU + EEPROM firmware LED animation
    private const byte Port0 = 0x00;

    /// <summary>Hub control mode (Port-0 byte [12]; SetControl byte [4]).</summary>
    public const byte ControlModeKeep = 0x00;        // [4]=0: apply speed without re-asserting mode (HYTE SetPumpSpeedCommand)
    public const byte ControlModeSoftware = 0x01;    // host drives the pump
    public const byte ControlModeMotherboard = 0x02; // motherboard PWM drives the pump (power-on default)
    public const byte ControlModeFirmware = 0x03;    // onboard temperature curve drives the pump
    public const byte ControlModeMix = 0x04;

    /// <summary>Turbo byte convention shared by Port-0 [14] and SetControl [9]: 0x00 = on, 0x01 = off.</summary>
    public const byte TurboOnByte = 0x00;
    public const byte TurboOffByte = 0x01;

    /// <summary>
    /// EEPROM "default mode" byte for the firmware-curve frame (FF CC 03 byte [4],
    /// FF CC 04 response byte [4]). DISTINCT enumeration from the live-control
    /// <c>ControlMode*</c> bytes used by FF CC 02: here Temperature (the onboard
    /// curve) is 0x02, whereas the live control byte uses 0x03 for firmware.
    /// Mirrors HYTE's PQSeriesFirmwareMode.
    /// </summary>
    public const byte FwDefaultModeMotherboard = 0x01;
    public const byte FwDefaultModeTemperature = 0x02; // onboard pump+fan temperature curve
    public const byte FwDefaultModeMix = 0x03;

    /// <summary>Firmware-driven LED animation byte (FF CC 0C [3]; Port-0 [15]). No Off value, no speed param.</summary>
    public const byte FwAnimationColor = 0x01;
    public const byte FwAnimationRainbow = 0x02;
    public const byte FwAnimationBreathe = 0x03;
    public const byte FwAnimationRainbowGradient = 0x04;

    public const int SetControlFrameLength = 15;

    public const int FirmwareVersionResponseLength = 7;
    public const int SerialResponseLength = 37;

    /// <summary>Length of the Port-0 status response (pump tach in bytes [9..10]).</summary>
    public const int Port0ResponseLength = 20;

    /// <summary>Length of the Q80 second-pump response (tach in bytes [3..4]).</summary>
    public const int Pump2ResponseLength = 7;

    /// <summary>UART1/FAN1 connector's Type-M channel (GetInfo channel byte).</summary>
    public const byte LinkChannel1 = 0x01;

    /// <summary>UART2/FAN2 connector's Type-M channel (GetInfo channel byte); typically wired to the radiator fans.</summary>
    public const byte FanChannel = 0x02;

    /// <summary>
    /// Length of the per-channel device-info response (FF CC 01 &lt;channel&gt;):
    /// FF CC echo then up to 19 device blocks of 12 bytes each.
    /// </summary>
    public const int ChannelInfoResponseLength = 240;
    private const int ChannelDeviceStride = 12;

    // Device-category codes in the channel-info block (byte [3] of a device slot).
    // Maps HYTE SmartDeviceMethods.ByteToSmartComponent.
    private const byte CompLs10 = 0x01;
    private const byte CompLs30 = 0x02;
    private const byte CompFt12 = 0x03;      // 1 fan
    private const byte CompFt12Duo = 0x04;   // 2 fans (Q60 radiator default)
    private const byte CompFt12Trio = 0x05;  // 3 fans
    private const byte CompLn60 = 0x06;
    private const byte CompLn70 = 0x07;

    // LED counts used when the firmware's per-slot LED-count byte reads 0 (legacy
    // LS10/LS30/LN4060/LN70 component classes). FT12/FP12 carries no such constant in
    // nexus-control-service - its SmartComponents classes (FT12/FT12Duo/FT12Trio,
    // CoolingBase) have no LED-count field - so an FP12 slot's LED count stays 0.
    public const int Ls10LedCount = 20;
    public const int Ls30LedCount = 62;
    public const int Ln60LedCount = 40;
    public const int Ln70LedCount = 44;

    // ── Lighting wire constants ──
    //
    // Mirrors the legacy PQSeriesDeviceBase.SendToHardware flow: enable software
    // RGB control, then stream each of the 4 LED ports as a fixed 90-byte frame.
    // Bytes after the 7-byte header are G,R,B triples (HYTE firmware expects GRB,
    // same as MiniHub/NP50). The two LED-count header bytes are the hardcoded
    // magic 0x01 0x68 the reference agent always emits regardless of real count.
    private const byte OpLighting = 0xEE;          // LED streaming
    private const byte SubStreaming = 0x01;        // Q-series stream sub-op (MiniHub uses 0x03)
    private const byte SubSetRgbControlMode = 0x03;
    private const byte LedCountMagicHigh = 0x01;
    private const byte LedCountMagicLow = 0x68;

    public const byte RgbModeSoftware = 0x00;      // nexus drives the LEDs
    public const byte RgbModeMotherboard = 0x01;   // hand off to the mobo ARGB header (power-on default)

    /// <summary>The Q-series cooler hub streams over 4 LED ports (pump head + fan/strip channels).</summary>
    public const int LedPortCount = 4;

    /// <summary>Floor for a streamed port frame: 7-byte header + GRB data, zero-padded up to this length. A port carrying more LEDs than fit emits a longer frame - legacy PadListWithZeros(90) pads only, never truncates, and the 42-LED backlight frame is 133 bytes.</summary>
    public const int MinStreamFrameLength = 90;

    /// <summary>LED port the LCD backlight panel hangs off (legacy HubRGBChannels Channel3).</summary>
    public const int BacklightPort = 3;

    /// <summary>LED port the logo diamond hangs off (legacy HubRGBChannels Channel4).</summary>
    public const int LogoPort = 4;

    /// <summary>Backlight panel grid, legacy Q60LCDBacklight KEYBOARD_XAXIS_COUNTS/KEYBOARD_YAXIS_COUNTS. Shared by Q60 and Q80 - HubRGBChannels.InitChannels handles both in one case.</summary>
    public const int BacklightColumns = 5;
    public const int BacklightRows = 9;

    /// <summary>Column 2 is notched: it carries only rows 3..8, so the panel is 42 LEDs rather than 5x9=45.</summary>
    private const int BacklightNotchColumn = 2;
    private const int BacklightNotchFirstRow = 3;

    /// <summary>LEDs on the backlight panel: 9+9+6+9+9.</summary>
    public const int BacklightLedCount = 42;

    /// <summary>LEDs in the logo diamond (legacy Q60Logo COMMAND_LAYOUT).</summary>
    public const int LogoLedCount = 4;

    /// <summary>
    /// Wire order of the backlight panel as (column, row) pairs, from legacy
    /// Q60LCDBacklight.ProcessColor: columns walked right to left, rows
    /// alternating direction per column, and the notched column emitting only
    /// its lower rows. Index i of a streamed frame is this array's i-th cell.
    /// </summary>
    public static readonly (int Column, int Row)[] BacklightWireOrder = BuildBacklightWireOrder();

    private static (int Column, int Row)[] BuildBacklightWireOrder()
    {
        var order = new (int, int)[BacklightLedCount];
        var n = 0;
        for (var x = BacklightColumns - 1; x >= 0; x--)
        {
            if (x == BacklightNotchColumn)
            {
                for (var y = BacklightNotchFirstRow; y < BacklightRows; y++) order[n++] = (x, y);
            }
            else if (x % 2 == 0)
            {
                for (var y = 0; y < BacklightRows; y++) order[n++] = (x, y);
            }
            else
            {
                for (var y = BacklightRows - 1; y >= 0; y--) order[n++] = (x, y);
            }
        }
        return order;
    }

    /// <summary>
    /// Wire order of the logo diamond as (column, row) on its 3x3 grid, from
    /// legacy Q60Logo KEYS_LAYOUTS + COMMAND_LAYOUT: bottom, left, top, right.
    /// </summary>
    public static readonly (int Column, int Row)[] LogoWireOrder =
    {
        (1, 2), (0, 1), (1, 0), (2, 1),
    };

    // ── Builders ──

    /// <summary>Build the "Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpControl, SubGetFirmwareVersion };

    /// <summary>Build the "Get Serial Number" request (4 bytes).</summary>
    public static byte[] BuildGetSerial() => new byte[] { Frame0, OpSerialA, OpSerialB, SubGetSerial };

    /// <summary>Build the "Get Port-0 Information" request (4 bytes). Response carries pump tach + temps + hub mode.</summary>
    public static byte[] BuildGetPort0Info() => new byte[] { Frame0, OpCooler, SubGetInfo, Port0 };

    /// <summary>Build the Q80 second-pump info request (3 bytes).</summary>
    public static byte[] BuildGetPump2Info() => new byte[] { Frame0, OpCooler, SubGetPump2 };

    /// <summary>Build the per-channel device-info request (FF CC 01 &lt;channel&gt;). Channel 2 carries the radiator fans.</summary>
    public static byte[] BuildGetChannelInfo(byte channel) => new byte[] { Frame0, OpCooler, SubGetInfo, channel };

    /// <summary>
    /// Build the "Set RGB Control Mode" request (4 bytes). <see cref="RgbModeSoftware"/> gives
    /// nexus full control of the LEDs; <see cref="RgbModeMotherboard"/> hands off to the
    /// motherboard ARGB header (the default after a power cycle). Software mode must be set
    /// before any <see cref="BuildLightingStream"/> write actually reaches the LEDs.
    /// </summary>
    public static byte[] BuildSetRgbControlMode(byte mode)
    {
        if (mode != RgbModeSoftware && mode != RgbModeMotherboard)
            throw new ArgumentException($"Unknown RGB control mode 0x{mode:X2}", nameof(mode));
        return new byte[] { Frame0, OpControl, SubSetRgbControlMode, mode };
    }

    /// <summary>
    /// Build a LED streaming frame for one port (1..<see cref="LedPortCount"/>).
    /// Header is <c>FF EE 01 &lt;port&gt; 01 68 00</c>; the remaining bytes are G,R,B triples,
    /// zero-padded to <see cref="MinStreamFrameLength"/> so trailing/disconnected LEDs go dark.
    /// A port carrying more LEDs than fit in that floor emits a longer frame, matching legacy
    /// PQSeriesDeviceBase.SendToHardware (4 ports, PadListWithZeros(90), GRB order) - the
    /// 42-LED backlight is 133 bytes there and truncating it to 90 drops 15 LEDs.
    /// </summary>
    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port < 1 || port > LedPortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{LedPortCount}.");
        var buf = new byte[Math.Max(MinStreamFrameLength, 7 + leds.Length * 3)];
        buf[0] = Frame0;
        buf[1] = OpLighting;
        buf[2] = SubStreaming;
        buf[3] = (byte)port;
        buf[4] = LedCountMagicHigh;
        buf[5] = LedCountMagicLow;
        // buf[6] reserved (0)
        for (var i = 0; i < leds.Length; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        return buf;
    }

    // ── Parsers ──

    /// <summary>
    /// Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw"
    /// (e.g. "2.0.9.1"). Returns empty on a short or mis-echoed reply. The
    /// firmware echoes the command header (0xFF 0xDD) in bytes [0..1].
    /// </summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpControl) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>
    /// Parse the 37-byte serial-number response. The serial is a UTF-8 string
    /// in bytes [4..30); the legacy treats 0xFF in the trailing bytes as
    /// "unset" and returns empty.
    /// </summary>
    public static string ParseSerial(ReadOnlySpan<byte> response)
    {
        if (response.Length < SerialResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpSerialA) return "";
        if (response[30] == 0xFF && response[31] == 0xFF) return "";
        return Encoding.UTF8.GetString(response.Slice(4, 26)).TrimEnd('\0');
    }

    /// <summary>
    /// Decode a HYTE 2-byte tach reading to RPM. Both bytes zero is the
    /// firmware's "no sensor" sentinel → 0. Matches SmartDeviceMethods.GetRPM.
    /// </summary>
    public static int DecodeRpm(byte high, byte low)
    {
        if (high == 0x00 && low == 0x00) return 0;
        return (int)(60 * 1000 / ((high * 100 + (float)low) / 10 * 4));
    }

    /// <summary>
    /// Parse the pump RPM from the 20-byte Port-0 status response (tach in
    /// bytes [9..10]). Returns false on a short or mis-echoed reply.
    /// </summary>
    public static bool TryParsePort0PumpRpm(ReadOnlySpan<byte> response, out int pumpRpm)
    {
        pumpRpm = 0;
        if (response.Length < Port0ResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;
        pumpRpm = DecodeRpm(response[9], response[10]);
        return true;
    }

    /// <summary>
    /// Decode a live thermistor reading from its two ADC bytes: V = 3.3 * (high*100 + low) / 4096,
    /// mapped to the nearest °C in the pump table. Distinct from <see cref="DecodeCurveTemp"/>,
    /// which reads the EEPROM curve's own millivolt byte packing. Returns null when the voltage
    /// saturates either end of the table, which is how an unpopulated sensor input reads.
    /// </summary>
    public static float? TryDecodeLiveTempC(byte high, byte low) =>
        HyteThermistor.NearestTempC(HyteThermistor.VoltageFromAdc(high, low), fan: false);

    /// <summary>
    /// Coolant temperatures from the 20-byte Port-0 status response: inlet in bytes [5..6],
    /// outlet in [7..8]. Null per side when the response is short or the probe reads out of
    /// range. Mirrors HYTE PQSeriesPumpHead.PumpTempIn / PumpTempOut.
    /// </summary>
    public static (float? inC, float? outC) CoolantTempsOf(ReadOnlySpan<byte> port0)
    {
        if (port0.Length < Port0ResponseLength) return (null, null);
        return (TryDecodeLiveTempC(port0[5], port0[6]), TryDecodeLiveTempC(port0[7], port0[8]));
    }

    /// <summary>
    /// Parse the Q80 second-pump RPM from the 7-byte response (tach in bytes
    /// [3..4]). Returns false on a short or mis-echoed reply; pumpRpm is 0 when
    /// no second pump is present.
    /// </summary>
    public static bool TryParsePump2Rpm(ReadOnlySpan<byte> response, out int pumpRpm)
    {
        pumpRpm = 0;
        if (response.Length < Pump2ResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;
        pumpRpm = DecodeRpm(response[3], response[4]);
        return true;
    }

    /// <summary>
    /// Decode a HYTE FT12 fan tach pair to RPM. The encoding differs per fan-unit
    /// variant: solo divides by 4, duo additionally by 10, trio packs the value.
    /// Matches SmartDeviceMethods.GetFanRPM.
    /// </summary>
    private static int DecodeFanRpm(byte high, byte low, byte component)
    {
        if (high == 0x00 && low == 0x00) return 0;
        if (component == CompFt12Trio) return high % 10 * 1000 + low * 10;
        var rpm = (int)(60 * 1000 / (high + (float)low / 100)) / 4;
        return component == CompFt12Duo ? rpm / 10 : rpm;
    }

    /// <summary>
    /// Parse a Nexus Link channel's device chain (FF CC 01 &lt;channel&gt;: 1 is the
    /// FAN1/UART1 connector, 2 is FAN2/UART2) into a freshly allocated list - never mutates
    /// a previously returned list, so a caller that publishes it as the shared state
    /// reference never exposes a reader to an in-progress rebuild. A "00 00" header means
    /// the firmware has no list yet (not an error), and a short/mis-echoed reply is likewise
    /// rejected; both return false with <paramref name="devices"/> empty, so callers keep
    /// whatever they published last. Stops at the first slot whose type byte is 0 - the
    /// device-index byte at the same slot offset stays non-zero in empty trailing slots, so
    /// it cannot be the stop condition.
    /// </summary>
    public static bool TryParseChannelDevices(ReadOnlySpan<byte> response, out IReadOnlyList<QSeriesLinkDevice> devices)
    {
        devices = Array.Empty<QSeriesLinkDevice>();
        if (response.Length < ChannelInfoResponseLength) return false;
        if (response[0] == 0x00 && response[1] == 0x00) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;

        var list = new List<QSeriesLinkDevice>();
        var maxSlots = response.Length / ChannelDeviceStride;
        for (var i = 0; i < maxSlots; i++)
        {
            var slot = response.Slice(i * ChannelDeviceStride, ChannelDeviceStride);
            var typeByte = slot[3];
            if (typeByte == 0x00) break;

            var deviceIndex = slot[2];
            var isTrio = typeByte == CompFt12Trio;
            var fanCount = FanCountOf(typeByte);
            list.Add(new QSeriesLinkDevice
            {
                Slot = deviceIndex > 0 ? deviceIndex : i + 1,
                Model = ModelNameOf(typeByte),
                LedCount = LedCountOf(typeByte, slot[5]),
                FanCount = fanCount,
                FanRpm = FanRpmsOf(slot, typeByte, fanCount),
                TemperatureC = fanCount > 0
                    ? HyteThermistor.NearestTempC(HyteThermistor.VoltageFromAdc(slot[6], slot[7]), fan: true)
                    : null,
                Orientation = OrientationOf(isTrio ? (byte)(slot[4] / 10) : slot[10]),
                // Legacy SmartDeviceMethods.GetIsConnect: a trio packs the group-end flag into
                // the tens place of the same byte its 3rd fan's tach-high shares (slot[10]);
                // solo/duo read it straight from slot[11]. 0 = another device follows in this
                // group, nonzero = group end.
                GroupEnd = isTrio ? (byte)(slot[10] / 10) != 0 : slot[11] != 0,
            });
        }
        devices = list;
        return true;
    }

    private static int FanCountOf(byte typeByte) => typeByte switch
    {
        CompFt12 => 1,
        CompFt12Duo => 2,
        CompFt12Trio => 3,
        _ => 0,
    };

    private static string ModelNameOf(byte typeByte) => typeByte switch
    {
        CompLs10 => "LS10",
        CompLs30 => "LS30",
        CompFt12 => "FP12",
        CompFt12Duo => "FT12 Duo",
        CompFt12Trio => "FT12 Trio",
        CompLn60 => "LN60",
        CompLn70 => "LN70",
        _ => "Unknown",
    };

    private static int LedCountOf(byte typeByte, byte firmwareLedCount)
    {
        if (firmwareLedCount != 0) return firmwareLedCount;
        return typeByte switch
        {
            CompLs10 => Ls10LedCount,
            CompLs30 => Ls30LedCount,
            CompLn60 => Ln60LedCount,
            CompLn70 => Ln70LedCount,
            _ => 0,
        };
    }

    private static string OrientationOf(byte raw) => raw switch
    {
        0x00 => "Back",
        0x01 => "Down",
        0x02 => "Up",
        0x03 => "Front",
        _ => "Back",
    };

    private static int[] FanRpmsOf(ReadOnlySpan<byte> slot, byte typeByte, int fanCount)
    {
        if (fanCount == 0) return Array.Empty<int>();
        var rpm = new int[fanCount];
        rpm[0] = DecodeFanRpm(slot[8], slot[9], typeByte);
        if (fanCount >= 2) rpm[1] = DecodeFanRpm(slot[4], slot[5], typeByte);
        if (fanCount >= 3) rpm[2] = DecodeFanRpm(slot[10], slot[11], typeByte);
        return rpm;
    }

    /// <summary>The hub control mode the last Port-0 poll reported (byte [12]).</summary>
    public static byte ControlModeOf(ReadOnlySpan<byte> port0) =>
        port0.Length >= Port0ResponseLength ? port0[12] : ControlModeMotherboard;

    /// <summary>True when the last Port-0 poll reports turbo on (byte [14] == 0x00).</summary>
    public static bool TurboOnOf(ReadOnlySpan<byte> port0) =>
        port0.Length >= Port0ResponseLength && port0[14] == TurboOnByte;

    /// <summary>Firmware-driven LED animation state, decoded from Port-0 response bytes [15..19].</summary>
    public readonly record struct QSeriesFwAnimation(byte Animation, byte R, byte G, byte B, byte Brightness);

    /// <summary>
    /// Parse the firmware-animation block from a 20-byte Port-0 status response. There is
    /// no dedicated get-animation opcode on Q-series (unlike NP50's FF CC 0D); this is the
    /// only readback available. False on a short or mis-echoed reply.
    /// </summary>
    public static bool TryParseFirmwareAnimation(ReadOnlySpan<byte> port0, out QSeriesFwAnimation animation)
    {
        animation = default;
        if (port0.Length < Port0ResponseLength) return false;
        if (port0[0] != Frame0 || port0[1] != OpCooler) return false;
        animation = new QSeriesFwAnimation(port0[15], port0[16], port0[17], port0[18], port0[19]);
        return true;
    }

    // ── Control builders ──

    /// <summary>
    /// Build the 15-byte cooler control frame (FF CC 02). Sets control mode [4],
    /// pump speed [5] and turbo [9], echoing the firmware-animation state
    /// (anim mode + RGB + brightness) from the Port-0 response bytes [15..19] so a
    /// control write never clobbers the onboard LED state. Mirrors
    /// SmartHubCommandBase.SwitchControlMode / PQSeriesCommand.SetPumpSpeedCommand.
    /// </summary>
    public static byte[] BuildSetControl(byte mode, byte pumpWire, byte turboByte, ReadOnlySpan<byte> port0)
        => port0.Length >= Port0ResponseLength
            ? BuildSetControlWithAnimation(mode, pumpWire, turboByte, port0[15], port0[16], port0[17], port0[18], port0[19])
            : BuildSetControlWithAnimation(mode, pumpWire, turboByte, 0, 0, 0, 0, 0);

    /// <summary>
    /// FF CC 02 control frame. Bytes [10..14] carry the firmware animation: the
    /// 0x0C MCU write updates the stored copy only, so this frame is what changes
    /// the live state Port-0 reports. HYTE's SmartHubCommandBase sends it before
    /// the MCU write from every animation entry point.
    /// </summary>
    public static byte[] BuildSetControlWithAnimation(
        byte mode, byte pumpWire, byte turboByte, byte animation, byte r, byte g, byte b, byte brightness)
    {
        var cmd = new byte[SetControlFrameLength];
        cmd[0] = Frame0;
        cmd[1] = OpCooler;
        cmd[2] = SubSetControl;
        cmd[4] = mode;
        cmd[5] = pumpWire;
        cmd[9] = turboByte;
        cmd[10] = animation;
        cmd[11] = r;
        cmd[12] = g;
        cmd[13] = b;
        cmd[14] = brightness;
        return cmd;
    }

    /// <summary>Build the turbo-persist MCU write (FF CC 0A &lt;turbo&gt;).</summary>
    public static byte[] BuildSetTurboMcu(byte turboByte) => new byte[] { Frame0, OpCooler, SubSetTurboMcu, turboByte };

    /// <summary>
    /// Build the "Write Firmware Animation to MCU" request (9 bytes): FF CC 0C animation R G B
    /// brightness SAVE(0x01). Byte-identical to Np50Protocol.BuildWriteFirmwareAnimationToMcu -
    /// same HYTE SmartHubCommandBase.WriteFwAnimationToMcu reference, sibling opcode family.
    /// Unlike NP50 there is no separate get-animation opcode, so callers verify this took by
    /// re-reading Port-0 rather than a dedicated response.
    /// </summary>
    public static byte[] BuildWriteFirmwareAnimation(byte animation, byte r, byte g, byte b, byte brightness) =>
        new byte[] { Frame0, OpCooler, SubWriteFirmwareAnimation, animation, r, g, b, brightness, 0x01 };

    /// <summary>
    /// Fan duty ceiling (%) the firmware enforces when turbo is off. HYTE's client
    /// caps fan duty off-turbo (reference uses 70); we use 65 to match the duty
    /// shown by the cooling-card limit line and the turbo explainer (2000 RPM ≈ 65%).
    /// </summary>
    public const int FanTurboOffDutyCap = 65;

    /// <summary>Clamp a fan duty to 0-100 and apply the off-turbo ceiling. Mirrors the pump's off-turbo cap.</summary>
    public static int CapFanDutyForTurbo(int dutyPercent, bool turboOn)
    {
        var d = Math.Clamp(dutyPercent, 0, 100);
        return turboOn ? d : Math.Min(d, FanTurboOffDutyCap);
    }

    /// <summary>Length of the per-channel fan-speed frame: FF CC 02 &lt;channel&gt; + 18 nine-byte device blocks (per the set-fan spec, 18*9+4).</summary>
    public const int SetFanFrameLength = 4 + ChannelDeviceCount * FanDeviceBlock;
    private const int ChannelDeviceCount = 18;
    private const int FanDeviceBlock = 9;

    /// <summary>
    /// Per-slot fan duty for <see cref="BuildSetChannelFanSpeeds"/>. Fan2/Fan3 only apply to
    /// a Duo/Trio slot; pass 0 there for a solo unit.
    /// </summary>
    public readonly record struct QSeriesFanSlotDuty(int Fan1Percent, int Fan2Percent, int Fan3Percent);

    /// <summary>
    /// Build the per-channel fan-speed frame (FF CC 02 &lt;channel&gt;) per the set-fan
    /// spec: 18 nine-byte device blocks after the 4-byte header, one block per Nexus Link
    /// slot. Each block is [device index, 0x00, fan1%, RPM-mode (0), RPM_H, RPM_L, fan2%,
    /// fan3%, reserve]; fan2/fan3 carry a Duo's second fan or a Trio's second and third.
    /// Plain 0-100% duty, no voltage map. Slots beyond <paramref name="slotDuties"/> are
    /// zero-filled. The cooler must already be in software mode for this to take effect.
    /// </summary>
    public static byte[] BuildSetChannelFanSpeeds(byte channel, IReadOnlyList<QSeriesFanSlotDuty> slotDuties)
    {
        ArgumentNullException.ThrowIfNull(slotDuties);
        var cmd = new byte[SetFanFrameLength];
        cmd[0] = Frame0;
        cmd[1] = OpCooler;
        cmd[2] = SubSetControl;
        cmd[3] = channel;
        for (var i = 0; i < ChannelDeviceCount; i++)
        {
            var b = 4 + i * FanDeviceBlock;
            cmd[b + 0] = (byte)(i + 1); // 1-based device index
            if (i >= slotDuties.Count) continue;
            var s = slotDuties[i];
            cmd[b + 2] = (byte)Math.Clamp(s.Fan1Percent, 0, 100);
            cmd[b + 6] = (byte)Math.Clamp(s.Fan2Percent, 0, 100);
            cmd[b + 7] = (byte)Math.Clamp(s.Fan3Percent, 0, 100);
        }
        return cmd;
    }

    /// <summary>
    /// Map a 0-100 pump duty to the firmware's voltage-percentage wire byte.
    /// The pump is off below 46% and ramps non-linearly above; off-turbo the
    /// wire byte is additionally capped at 55. Matches PQSeriesCommand
    /// .SetPumpSpeedCommand over SmartDeviceMethods._pumpSpeedPercentageToVoltagePercentage.
    /// </summary>
    public static byte MapPumpDutyToWire(int dutyPercent, bool turboOn)
    {
        var d = Math.Clamp(dutyPercent, 0, 100);
        if (turboOn) return PumpDutyToWire[d];
        return d > 55 ? (byte)55 : PumpDutyToWire[d];
    }

    // Index = pump duty %, value = firmware voltage-% wire byte. Zero below 46%
    // (the pump's minimum). HYTE _pumpSpeedPercentageToVoltagePercentage.
    private static readonly byte[] PumpDutyToWire =
    {
        0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,                 //  0-19
        0,0,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,0,0,                 // 20-39
        0,0,0,0,0,0,                                             // 40-45
        31,32,33,34,35,36,36,37,38,39,                          // 46-55
        40,41,42,45,45,45,47,48,49,50,                          // 56-65
        51,52,54,55,56,58,59,60,61,61,                          // 66-75
        62,63,65,65,69,71,72,74,75,76,                          // 76-85
        77,78,80,80,81,85,87,88,92,94,                          // 86-95
        95,95,99,99,100,                                        // 96-100
    };

    // ── Firmware temperature curve (FF CC 03 set / FF CC 04 get) ──
    //
    // The cooler MCU stores a temperature→speed curve in EEPROM and drives the
    // pump + radiator fans from it when in firmware (Temperature) mode. Recent
    // firmware (Q60 ≥ 2.0.0.1, Q80 ≥ 1.0.4.1) holds 5 points, each carrying an
    // independent pump (temp, speed) and fan (temp, speed): a separate pump
    // curve and fan curve sharing one 5-slot array. Older firmware uses a 3-point
    // shared format we don't write. Ported from HYTE PQSeriesCommand.SetFirmwareMode
    // + FirmwareTemperatureModel.GetFirmwareTempModelBytes.

    /// <summary>Curve point count for the supported (V2) firmware format.</summary>
    public const int FirmwareCurvePointCount = 5;

    /// <summary>FF CC 03 set frame length (V2): 4-byte header, mode, 30-byte payload, SAVE byte.</summary>
    public const int SetFirmwareModeFrameLength = 36;

    /// <summary>FF CC 04 response length (V2): set frame minus the SAVE byte.</summary>
    public const int FirmwareDefaultResponseLength = 35;

    /// <summary>
    /// Coolant-temperature range the editor exposes (°C) = the full span of the
    /// thermistor tables. HYTE's client caps user curves at 50, but the device's
    /// own factory fan curve stores points up to 56, so clamping to 50 would
    /// mangle it on save; the firmware accepts the full table range.
    /// </summary>
    public const int FirmwareCurveTempMin = 0;
    public const int FirmwareCurveTempMax = 75;

    /// <summary>
    /// HYTE's factory pump/fan curves. The firmware has no reset command, so a
    /// reset writes these back.
    /// </summary>
    public static QSeriesFirmwareCurvePoint[] DefaultFirmwareCurve() => new[]
    {
        new QSeriesFirmwareCurvePoint { PumpTempC = 34, PumpDutyPercent = 32, FanTempC = 43, FanDutyPercent = 29 },
        new QSeriesFirmwareCurvePoint { PumpTempC = 38, PumpDutyPercent = 37, FanTempC = 48, FanDutyPercent = 39 },
        new QSeriesFirmwareCurvePoint { PumpTempC = 43, PumpDutyPercent = 45, FanTempC = 51, FanDutyPercent = 50 },
        new QSeriesFirmwareCurvePoint { PumpTempC = 47, PumpDutyPercent = 56, FanTempC = 54, FanDutyPercent = 59 },
        new QSeriesFirmwareCurvePoint { PumpTempC = 50, PumpDutyPercent = 91, FanTempC = 56, FanDutyPercent = 91 },
    };

    /// <summary>Factory firmware animation: solid white at full brightness.</summary>
    public const byte DefaultFwAnimation = FwAnimationColor;
    public const byte DefaultFwR = 255;
    public const byte DefaultFwG = 255;
    public const byte DefaultFwB = 255;
    public const byte DefaultFwBrightness = 100;

    // Per-slot frame offsets for the 5-point V2 curve. Pump/fan speeds are plain
    // 0-100 bytes; each temperature is two bytes (high, low) = TempH at the listed
    // offset, TempL at +1. Matches GetFirmwareTempModelBytes copied to frame[5..34].
    private static readonly int[] PumpSpeedOffsets = { 5, 6, 7, 17, 18 };
    private static readonly int[] FanSpeedOffsets = { 8, 9, 10, 19, 20 };
    private static readonly int[] PumpTempHighOffsets = { 11, 13, 15, 21, 23 };
    private static readonly int[] FanTempHighOffsets = { 25, 27, 29, 31, 33 };

    /// <summary>True when the connected variant's firmware supports the 5-point curve format.</summary>
    public static bool SupportsFirmwareCurve(string variant, string fwVersion)
    {
        if (string.IsNullOrEmpty(fwVersion)) return false;
        var threshold = variant == VariantQ80 ? "1.0.4.1"
            : variant == VariantQ60 ? "2.0.0.1"
            : "1.0.2.1";
        return CompareFwVersionAtLeast(fwVersion, threshold);
    }

    /// <summary>True when the connected variant's firmware supports the FF CC 0C firmware-animation write.</summary>
    public static bool SupportsFirmwareAnimation(string variant, string fwVersion)
    {
        if (string.IsNullOrEmpty(fwVersion)) return false;
        if (variant == VariantQ80) return CompareFwVersionAtLeast(fwVersion, "1.0.5.1");
        if (variant == VariantQ60) return CompareFwVersionAtLeast(fwVersion, "2.0.0.1");
        return false;
    }

    /// <summary>True when the connected variant's firmware supports the firmware-animation brightness byte.</summary>
    public static bool SupportsFirmwareAnimationBrightness(string variant, string fwVersion)
    {
        if (string.IsNullOrEmpty(fwVersion)) return false;
        if (variant == VariantQ80) return CompareFwVersionAtLeast(fwVersion, "1.0.5.1");
        if (variant == VariantQ60) return CompareFwVersionAtLeast(fwVersion, "2.0.3.1");
        return false;
    }

    // fwVersion >= reference, comparing dotted numeric parts left-to-right.
    // Equal counts as "at least". Mirrors HYTE Methods.CompareFwVersions == UpToDate.
    private static bool CompareFwVersionAtLeast(string fwVersion, string reference)
    {
        var a = fwVersion.Split('.');
        var b = reference.Split('.');
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var ai = i < a.Length && int.TryParse(a[i], out var av) ? av : 0;
            var bi = i < b.Length && int.TryParse(b[i], out var bv) ? bv : 0;
            if (ai > bi) return true;
            if (ai < bi) return false;
        }
        return true;
    }

    /// <summary>Build the "Get Firmware Default Mode" request (4 bytes). Response carries the default mode + stored curve.</summary>
    public static byte[] BuildGetFirmwareDefault() => new byte[] { Frame0, OpCooler, SubGetFirmwareDefault, Port0 };

    /// <summary>
    /// Build the 36-byte firmware-curve write (FF CC 03). Sets the EEPROM default
    /// mode [4], the 5-point pump+fan curve [5..34], and the SAVE byte [35]=0x01
    /// that commits it. Requires exactly <see cref="FirmwareCurvePointCount"/> points.
    /// </summary>
    public static byte[] BuildSetFirmwareMode(byte defaultMode, ReadOnlySpan<QSeriesFirmwareCurvePoint> points)
    {
        if (points.Length != FirmwareCurvePointCount)
            throw new ArgumentException($"Firmware curve needs exactly {FirmwareCurvePointCount} points.", nameof(points));
        var cmd = new byte[SetFirmwareModeFrameLength];
        cmd[0] = Frame0;
        cmd[1] = OpCooler;
        cmd[2] = SubSetFirmwareMode;
        cmd[4] = defaultMode;
        for (var i = 0; i < FirmwareCurvePointCount; i++)
        {
            var p = points[i];
            cmd[PumpSpeedOffsets[i]] = (byte)Math.Clamp(p.PumpDutyPercent, 0, 100);
            cmd[FanSpeedOffsets[i]] = (byte)Math.Clamp(p.FanDutyPercent, 0, 100);
            var (ph, pl) = EncodeCurveTemp(p.PumpTempC, fan: false);
            cmd[PumpTempHighOffsets[i]] = ph;
            cmd[PumpTempHighOffsets[i] + 1] = pl;
            var (fh, fl) = EncodeCurveTemp(p.FanTempC, fan: true);
            cmd[FanTempHighOffsets[i]] = fh;
            cmd[FanTempHighOffsets[i] + 1] = fl;
        }
        cmd[SetFirmwareModeFrameLength - 1] = 0x01; // SAVE to EEPROM
        return cmd;
    }

    /// <summary>The EEPROM default mode byte the firmware-curve response reports (byte [4]).</summary>
    public static byte FirmwareDefaultModeOf(ReadOnlySpan<byte> response) =>
        response.Length > 4 ? response[4] : FwDefaultModeMotherboard;

    /// <summary>
    /// Parse the 5-point pump+fan curve from a FF CC 04 response (or the bytes of a
    /// FF CC 03 echo). Returns false on a short or mis-echoed reply.
    /// </summary>
    public static bool TryParseFirmwareCurve(ReadOnlySpan<byte> response, out QSeriesFirmwareCurvePoint[] points)
    {
        points = Array.Empty<QSeriesFirmwareCurvePoint>();
        if (response.Length < FirmwareDefaultResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpCooler) return false;
        var result = new QSeriesFirmwareCurvePoint[FirmwareCurvePointCount];
        for (var i = 0; i < FirmwareCurvePointCount; i++)
        {
            result[i] = new QSeriesFirmwareCurvePoint
            {
                PumpDutyPercent = response[PumpSpeedOffsets[i]],
                FanDutyPercent = response[FanSpeedOffsets[i]],
                PumpTempC = DecodeCurveTemp(response[PumpTempHighOffsets[i]], response[PumpTempHighOffsets[i] + 1], fan: false),
                FanTempC = DecodeCurveTemp(response[FanTempHighOffsets[i]], response[FanTempHighOffsets[i] + 1], fan: true),
            };
        }
        points = result;
        return true;
    }

    /// <summary>
    /// Encode a coolant temperature (°C) to the firmware's two voltage bytes
    /// (high, low). The thermistor voltage is split as high = floor(v*1000)/100,
    /// low = floor(v*1000)%100, decoded back by <see cref="DecodeCurveTemp"/> as
    /// high/10 + low/1000. Pump and fan use separate thermistor tables.
    /// </summary>
    public static (byte high, byte low) EncodeCurveTemp(int tempC, bool fan)
    {
        // Replicates HYTE byte-for-byte: GetVoltageByTemp floors the voltage to 3
        // decimals, then the model getter scales by 1000 and truncates each byte.
        var voltage = Math.Floor(HyteThermistor.VoltageAt(tempC, fan) * 1000) / 1000;
        var milliVolts = voltage * 1000;
        return ((byte)(milliVolts / 100), (byte)(milliVolts % 100));
    }

    /// <summary>
    /// Decode the firmware curve's two voltage bytes back to the nearest °C. No saturation
    /// rejection: these are values we wrote, so they are in range by construction.
    /// </summary>
    public static int DecodeCurveTemp(byte high, byte low, bool fan) =>
        HyteThermistor.NearestIndex(high / 10.0 + low / 1000.0, fan);
}

/// <summary>
/// One slot of the HYTE Q-series firmware curve. Each slot carries an independent
/// pump (temp, speed) and fan (temp, speed) point; the device stores a pump curve
/// and a fan curve in one 5-slot array.
/// </summary>
public struct QSeriesFirmwareCurvePoint
{
    public int PumpTempC;
    public int PumpDutyPercent;
    public int FanTempC;
    public int FanDutyPercent;
}
