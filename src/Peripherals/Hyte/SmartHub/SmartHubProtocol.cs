using System;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Pure builders + parsers for the HYTE Smart Hub serial-over-USB protocol
/// - a simple ARGB + PWM-fan hub (4 ARGB ports + 4 PWM-fan ports).
///
/// Every behaviour pinned here mirrors HYTE's shipping nexus-control-service
/// (the working production agent against the same firmware):
///   • <c>LightDancing/Hardware/Devices/HYTE/Hub/ControlHubController.cs</c>
///   • <c>LightDancing/Common/SmartDeviceCommon/Command/ControlHubCommand.cs</c>
/// where the legacy enum/device name for this VID/PID is
/// <c>USBDevices.ControlHub</c> / "HYTE Smart Hub".
///
/// The Smart Hub command alphabet is the same family NP50 uses:
/// <c>0xFF 0xCC</c> for control/query, <c>0xFF 0xDD</c> for firmware
/// version, <c>0xFF 0xEE</c> for LED streaming. LED bytes go out in
/// <b>G R B</b> order (HYTE's <c>LedStrip.ProcessColor</c> emits
/// <c>{ color.G, color.R, color.B }</c>, same as the MiniHub / NP50 rings).
/// </summary>
public static class SmartHubProtocol
{
    // ── USB identity ──

    public const int VendorId = 0x3402;
    public const int ProductId = 0x0904;

    /// <summary>
    /// PID encoded into the OTA boot-flag key (<c>FF DC 06 09 01 00 DD</c>).
    /// HYTE's <c>USBDevicesFactory</c> ControlHub entry ships NP50's key
    /// (0x0901), NOT this device's operating PID 0x0904 - the Smart Hub and
    /// NP50 share a bootloader identity.
    /// </summary>
    public const int OtaProductId = 0x0901;

    /// <summary>Number of PWM fan ports (1-indexed 1..4 on the wire, 0..3 in <see cref="SmartHubState.Fans"/>).</summary>
    public const int FanChannelCount = 4;

    /// <summary>Number of ARGB ports (1-indexed 1..4 on the wire).</summary>
    public const int ArgbPortCount = 4;

    /// <summary>
    /// Firmware-accepted LED ceiling per ARGB port. The reference
    /// <c>ControlHubDeviceBase.MAX_SUPPORT_LED_EACH_PORT</c> is 200, and
    /// <c>SendToHardware</c> pads every streamed frame to exactly that many
    /// LEDs (200×3 = 600 colour bytes) regardless of the declared count, so
    /// un-addressed LEDs go dark instead of holding a stale colour.
    /// </summary>
    public const int MaxLedsPerPort = 200;

    // ── Wire constants ──

    private const byte Frame0 = 0xFF;
    private const byte OpControl = 0xCC;    // info / fan speed / fw-animation
    private const byte OpVersion = 0xDD;    // firmware version
    private const byte OpLighting = 0xEE;   // LED streaming

    private const byte SubGetVersion = 0x02;       // FF DD 02
    private const byte SubGetInfo = 0x01;           // FF CC 01 00
    private const byte SubSetFanSpeed = 0x02;       // FF CC 02 <ch> <pct> <en>
    private const byte SubSetFwAnimation = 0x07;    // FF CC 07 <0 on | 1 off>
    private const byte SubGetFwAnimation = 0x08;    // FF CC 08
    private const byte SubSetMcuSetting = 0x0C;     // FF CC 0C <anim> <r> <g> <b> <brt> <fan> 01
    private const byte SubGetMcuSetting = 0x0D;     // FF CC 0D
    private const byte SubStreaming = 0x01;          // FF EE 01 <port> ...

    // ── Standalone-animation ids (FF CC 0C/0D byte [3], firmware FW_Animation) ──

    public const int McuAnimationColor = 1;
    public const int McuAnimationRainbow = 2;
    public const int McuAnimationBreathe = 3;
    public const int McuAnimationRainbowGradient = 4;

    /// <summary>20-byte response to <see cref="BuildGetInfo"/> carrying all four channels' tach + enabled flags.</summary>
    public const int GetInfoResponseLength = 20;

    /// <summary>7-byte response to <see cref="BuildGetFirmwareVersion"/>.</summary>
    public const int FirmwareVersionResponseLength = 7;

    /// <summary>10-byte "Set FW Setting" frame built by <see cref="BuildSetMcuSetting"/>.</summary>
    public const int McuSettingLength = 10;

    /// <summary>9-byte response to <see cref="BuildGetMcuSetting"/>.</summary>
    public const int McuSettingResponseLength = 9;

    /// <summary>7-byte response to <see cref="BuildGetFirmwareAnimation"/>.</summary>
    public const int FirmwareAnimationResponseLength = 7;

    /// <summary>Streamed frame = 7-byte header + MaxLedsPerPort×3 colour bytes.</summary>
    public const int LightingFrameLength = 7 + MaxLedsPerPort * 3; // 607

    public const int FanMinDutyPercent = 0;
    public const int FanMaxDutyPercent = 100;

    // ── Builders ──

    /// <summary>"Get Firmware Version" request (3 bytes).</summary>
    public static byte[] BuildGetFirmwareVersion() => new byte[] { Frame0, OpVersion, SubGetVersion };

    /// <summary>
    /// "Get Hub Info" request (4 bytes). The 20-byte reply carries every
    /// channel's tach + enabled byte in one shot - the reference
    /// <c>ControlHubCommand.GetFanChannelInfoBytes</c> sends this exact frame
    /// for every channel and reads the per-channel fields out of the single
    /// response (see <see cref="TryParseChannelInfo"/>).
    /// </summary>
    public static byte[] BuildGetInfo() => new byte[] { Frame0, OpControl, SubGetInfo, 0x00 };

    /// <summary>
    /// "Set Fan Speed" request for one PWM port (6 bytes):
    /// <c>FF CC 02 &lt;port 1..4&gt; &lt;duty%&gt; &lt;enabled&gt;</c>.
    /// The wire port is <b>1-based</b>: <c>ControlHubCommand.SetFanSpeedByChannel</c>
    /// sends <c>channel + 1</c> and <c>SetInitialFanSpeed</c> writes ports 1..4
    /// directly. <paramref name="channel"/> stays 0-based to match
    /// <see cref="SmartHubState.Fans"/> / the hub-info parse; the +1 happens here.
    /// </summary>
    public static byte[] BuildSetFanSpeed(int channel, int dutyPercent, bool enabled)
    {
        if (channel < 0 || channel >= FanChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in 0..{FanChannelCount - 1}.");
        var duty = (byte)Math.Clamp(dutyPercent, FanMinDutyPercent, FanMaxDutyPercent);
        return new byte[] { Frame0, OpControl, SubSetFanSpeed, (byte)(channel + 1), duty, (byte)(enabled ? 0x01 : 0x00) };
    }

    /// <summary>
    /// "Set Firmware Animation On/Off" request (4 bytes). The hub runs an
    /// onboard LED animation whenever no <see cref="BuildLightingStream"/>
    /// frame has arrived for 5 s; <b>off</b> disables that fallback for good
    /// (the byte lands in <c>Default_Off_FW_Animation</c> and the firmware
    /// saves it to flash), so the hub then stays dark whenever nothing
    /// streams. Nexus only ever sends <b>on</b>. Per
    /// <c>ControlHubCommand.SetFwAnimationOnOff</c> the on/off byte is
    /// inverted: <c>0x00</c> = animation ON, <c>0x01</c> = animation OFF.
    /// </summary>
    public static byte[] BuildSetFirmwareAnimation(bool on)
        => new byte[] { Frame0, OpControl, SubSetFwAnimation, (byte)(on ? 0x00 : 0x01) };

    /// <summary>
    /// "Set FW Setting" request (10 bytes):
    /// <c>FF CC 0C &lt;anim 1..4&gt; &lt;R&gt; &lt;G&gt; &lt;B&gt; &lt;brightness%&gt; &lt;fan%&gt; 01</c>.
    /// Persists the hub's standalone behaviour to flash: LED animation +
    /// colour + brightness, AND the fan duty it holds when no host is driving
    /// (the firmware watchdog reapplies that duty to all four PWM ports 5 s
    /// after the last host fan write). Reference: Y50 firmware
    /// <c>usbd_cdc_if.c</c> FW_Animation handler.
    /// </summary>
    public static byte[] BuildSetMcuSetting(int animation, byte r, byte g, byte b, int brightness, int fanPercent)
    {
        if (animation < McuAnimationColor || animation > McuAnimationRainbowGradient)
            throw new ArgumentOutOfRangeException(nameof(animation), animation, $"Animation must be in {McuAnimationColor}..{McuAnimationRainbowGradient}.");
        var brt = (byte)Math.Clamp(brightness, 0, 100);
        var fan = (byte)Math.Clamp(fanPercent, FanMinDutyPercent, FanMaxDutyPercent);
        // Trailing 0x01 is a firmware gate: the 0x0C handler requires buffer[9]==0x01.
        return new byte[] { Frame0, OpControl, SubSetMcuSetting, (byte)animation, r, g, b, brt, fan, 0x01 };
    }

    /// <summary>"Get FW Setting" request (3 bytes). The 9-byte reply echoes the flash-persisted setting (see <see cref="TryParseMcuSetting"/>).</summary>
    public static byte[] BuildGetMcuSetting() => new byte[] { Frame0, OpControl, SubGetMcuSetting };

    /// <summary>"Get Firmware Animation On/Off" request (3 bytes). See <see cref="TryParseFirmwareAnimation"/>.</summary>
    public static byte[] BuildGetFirmwareAnimation() => new byte[] { Frame0, OpControl, SubGetFwAnimation };

    /// <summary>
    /// Decode the 7-byte reply to <see cref="BuildGetFirmwareAnimation"/>:
    /// <c>FF CC 08 &lt;startAnimOff&gt; &lt;fwAnimOff&gt; 00 00</c> (Y50 firmware
    /// <c>main.c</c> Get_Default_Animation transmit). Byte 4 carries the same
    /// inverted flag <see cref="BuildSetFirmwareAnimation"/> writes, so
    /// <paramref name="on"/> is true when it is <c>0x00</c>.
    /// </summary>
    public static bool TryParseFirmwareAnimation(ReadOnlySpan<byte> response, out bool on)
    {
        on = false;
        if (response.Length < FirmwareAnimationResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpControl || response[2] != SubGetFwAnimation) return false;
        on = response[4] == 0x00;
        return true;
    }

    /// <summary>
    /// Build an LED streaming frame for one ARGB port (1..4). Always emits a
    /// fixed-length padded buffer (<see cref="LightingFrameLength"/> = 607
    /// bytes) matching the reference <c>ControlHubController.SendToHardware</c>,
    /// which pads each port's colour list to <see cref="MaxLedsPerPort"/>×3
    /// before writing. Bytes after the 7-byte header are <b>G, R, B</b> per
    /// LED; LEDs past the supplied count stay zero so they go dark.
    /// </summary>
    public static byte[] BuildLightingStream(int port, ReadOnlySpan<RgbColor> leds)
    {
        if (port < 1 || port > ArgbPortCount)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"Port must be in 1..{ArgbPortCount}.");
        var buf = new byte[LightingFrameLength];
        buf[0] = Frame0;
        buf[1] = OpLighting;
        buf[2] = SubStreaming;
        buf[3] = (byte)port;
        // buf[4..6] reserved (0) - header is `FF EE 01 <port> 00 00 00`.
        var count = Math.Min(leds.Length, MaxLedsPerPort);
        for (var i = 0; i < count; i++)
        {
            var off = 7 + i * 3;
            buf[off + 0] = leds[i].G;
            buf[off + 1] = leds[i].R;
            buf[off + 2] = leds[i].B;
        }
        return buf;
    }

    // ── Parsers ──

    /// <summary>Parse the 7-byte firmware-version response into "Major.Minor.Build.Hw". Empty on bad header.</summary>
    public static string ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        if (response.Length < FirmwareVersionResponseLength) return "";
        if (response[0] != Frame0 || response[1] != OpVersion || response[2] != SubGetVersion) return "";
        return $"{response[3]}.{response[4]}.{response[5]}.{response[6]}";
    }

    /// <summary>Per-port tach + enabled state decoded from the hub-info response.</summary>
    public readonly record struct SmartHubFanReading(int Rpm, bool Enabled);

    /// <summary>
    /// Parse the 20-byte hub-info response into the four PWM channels'
    /// tach + enabled state. Tach layout follows
    /// <c>ControlHubDeviceBase.CheckAndUpdateChannelInfo</c>
    /// (speedH/L pairs at [3,4] [5,6] [7,8] [9,10] for ch0..3), but the
    /// enabled flags read back REVERSED: bench-probed on fw 1.0.0.1,
    /// <c>FF CC 02 N … en</c> flips readback byte <c>[15-N]</c> for every
    /// N 1..4 - so enabled for ch (wire port ch+1) lives at <c>[14-ch]</c>,
    /// not the <c>[11+ch]</c> the legacy parser assumes. The flags are also
    /// <c>01</c> for all four ports regardless of fan presence (bookkeeping
    /// only), so presence detection must come from the tach.
    /// Returns false (and leaves <paramref name="channels"/> null) if the
    /// header isn't the expected <c>FF CC</c> echo or the response is short;
    /// the caller keeps the transport and retries next tick.
    /// </summary>
    public static bool TryParseChannelInfo(ReadOnlySpan<byte> response, out SmartHubFanReading[]? channels)
    {
        channels = null;
        if (response.Length < GetInfoResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpControl) return false;

        channels = new SmartHubFanReading[FanChannelCount];
        for (var ch = 0; ch < FanChannelCount; ch++)
        {
            var speedH = response[3 + ch * 2];
            var speedL = response[4 + ch * 2];
            var enabled = response[14 - ch] == 0x01;
            channels[ch] = new SmartHubFanReading(DecodeFanRpm(speedH, speedL), enabled);
        }
        return true;
    }

    /// <summary>Flash-persisted standalone setting decoded from the "Get FW Setting" response.</summary>
    public readonly record struct SmartHubMcuSetting(int Animation, byte R, byte G, byte B, int Brightness, int FanPercent);

    /// <summary>
    /// Parse the 9-byte "Get FW Setting" response:
    /// <c>FF CC 0D &lt;anim&gt; &lt;R&gt; &lt;G&gt; &lt;B&gt; &lt;brightness%&gt; &lt;fan%&gt;</c>
    /// (Y50 firmware <c>main.c</c> Get_FW_Animation transmit). Returns false
    /// (and leaves <paramref name="setting"/> null) on a short response or a
    /// header that isn't the <c>FF CC 0D</c> echo.
    /// </summary>
    public static bool TryParseMcuSetting(ReadOnlySpan<byte> response, out SmartHubMcuSetting? setting)
    {
        setting = null;
        if (response.Length < McuSettingResponseLength) return false;
        if (response[0] != Frame0 || response[1] != OpControl || response[2] != SubGetMcuSetting) return false;
        setting = new SmartHubMcuSetting(response[3], response[4], response[5], response[6], response[7], response[8]);
        return true;
    }

    /// <summary>
    /// Tach decode for the Smart Hub PWM ports. Mirrors
    /// <c>SmartDeviceMethods.GetFanRPM</c> default branch:
    /// <c>rpm = 60000 / (speedH + speedL/100) / 4</c>, and 0 when both bytes
    /// are zero (no tach signal / port empty / fan stopped).
    /// </summary>
    public static int DecodeFanRpm(byte speedH, byte speedL)
    {
        if (speedH == 0x00 && speedL == 0x00) return 0;
        var rpm = (int)(60_000 / (speedH + speedL / 100f)) / 4;
        return rpm > 0 ? rpm : 0;
    }
}

/// <summary>24-bit RGB colour - same wire-level triple as MiniHub / NP50, streamed GRB by <see cref="SmartHubProtocol.BuildLightingStream"/>.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
