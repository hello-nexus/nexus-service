using System;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>Pump and fan speed plus the coolant probe, as one status reply reports them.</summary>
public sealed record LianLiAioStatus(int FanRpm, int PumpRpm, float? CoolantC);

/// <summary>
/// The A-command side of the Lian Li AIO LCD protocol (report 1): status readout and pump
/// speed. Layouts follow sgtaziz/lian-li-linux `hydroshift_lcd` (MIT), which names PID
/// 0x7395 the Galahad II Vision.
/// </summary>
public static class LianLiAioProtocol
{
    public const byte ReportIdA = 0x01;

    /// <summary>Reply: fan rpm (BE16), pump rpm (BE16), coolant-valid flag, coolant whole degrees, tenths.</summary>
    public const byte CmdStatus = 0x81;

    /// <summary>Payload <c>[source, pwm]</c>: source 0 is host control, 1 follows the motherboard header.</summary>
    public const byte CmdSetPumpPwm = 0x8A;

    /// <summary>Payload <c>[0, pwm]</c>: all three fans of a HydroShift LCD share one duty.</summary>
    public const byte CmdSetFanPwm = 0x8B;

    /// <summary>
    /// The pump-head ring, a firmware effect: scope (0 inner, 1 outer, 2 both), mode, brightness 0-4,
    /// speed 0-4, four RGB slots, direction, off flag, source (0 host, 1 motherboard ARGB).
    /// </summary>
    public const byte CmdSetPumpLight = 0x83;

    /// <summary>The fan LEDs' firmware effect; the per-LED path below is primed with it before the first write.</summary>
    public const byte CmdSetFanLight = 0x85;

    /// <summary>
    /// B-command carrying per-LED colors on the HydroShift LCD 360R: a selector byte, then RGB bytes
    /// for each LED in fan order. Rides the same sequenced header as a frame chunk.
    /// </summary>
    public const byte CmdSetPerLed = 0x14;

    private const byte PerLedSelector = 0x01;

    /// <summary>The 360R's fan LED zone size, per the reference driver.</summary>
    public const int HydroShiftFanLedCount = 24;

    private const byte PumpSourceHost = 0x00;

    /// <summary>Pump source byte that hands speed control back to the motherboard header.</summary>
    public const byte PumpSourceMotherboard = 0x01;

    /// <summary>The 360R pump's lowest PWM, the first entry of its rpm-to-PWM table.</summary>
    public const int HydroShiftPumpDutyFloor = 1;

    /// <summary>Below this the 360R remap writes a stop; a fan channel never asks for it.</summary>
    public const int HydroShiftFanDutyFloor = 10;

    /// <summary>The duty fans are parked at on release, because the firmware has no command that hands them back.</summary>
    public const int HydroShiftFanReleaseDuty = 75;

    /// <summary>The 360R pump's rpm-to-PWM table.</summary>
    private static readonly (int Rpm, int Pwm)[] HydroShiftPumpRpmToPwm =
    {
        (2000, 1), (2300, 25), (2600, 50), (2900, 75), (3200, 100),
    };

    private const byte LightScopeBoth = 2;
    private const byte LightModeStatic = 3;
    private const byte LightBrightnessFull = 4;
    /// <summary>The reference driver's default speed; a static ring ignores it.</summary>
    private const byte LightSpeedDefault = 2;
    private const byte LightSourceHost = 0;

    /// <summary>Report id, command, three pad bytes, payload length.</summary>
    public const int AHeaderLength = 6;

    /// <summary>The lowest PWM in <see cref="VisionRpmToPwm"/>; the reference driver never drives the pump below it.</summary>
    public const int GalahadPumpDutyFloor = 20;

    /// <summary>
    /// The Galahad II Vision's rpm-to-PWM table, from the reference driver's
    /// `PumpEnvelope::GALAHAD2_VISION` (lianli-shared/src/aio.rs).
    /// </summary>
    private static readonly (int Rpm, int Pwm)[] VisionRpmToPwm =
    {
        (800, 20), (900, 23), (1000, 25), (1100, 27), (1200, 30), (1300, 31), (1400, 34), (1500, 36),
        (1600, 39), (1700, 41), (1800, 44), (1900, 47), (2000, 49), (2100, 52), (2200, 55), (2300, 58),
        (2400, 60), (2500, 64), (2600, 67), (2700, 70), (2800, 73), (2900, 77), (3000, 80), (3100, 83),
        (3200, 87), (3300, 90), (3400, 94), (3500, 98), (3600, 100),
    };

    /// <summary>Writes an A-command frame zero-padded to the report: Windows wants exactly the collection's output report length.</summary>
    public static void FillACommand(Span<byte> report, byte command, ReadOnlySpan<byte> payload)
    {
        report.Clear();
        report[0] = ReportIdA;
        report[1] = command;
        report[5] = (byte)payload.Length;
        payload.CopyTo(report[AHeaderLength..]);
    }

    public static void FillSetPumpPwm(Span<byte> report, int dutyPercent)
    {
        Span<byte> payload = stackalloc byte[2];
        payload[0] = PumpSourceHost;
        payload[1] = (byte)Math.Clamp(dutyPercent, GalahadPumpDutyFloor, 100);
        FillACommand(report, CmdSetPumpPwm, payload);
    }

    /// <summary>Pump target on a 360R: <paramref name="source"/> 0 drives it at <paramref name="dutyPercent"/>, 1 follows the motherboard header.</summary>
    public static void FillHydroShiftPumpPwm(Span<byte> report, byte source, int dutyPercent)
    {
        Span<byte> payload = stackalloc byte[2];
        payload[0] = source;
        payload[1] = (byte)Math.Clamp(dutyPercent, HydroShiftPumpDutyFloor, 100);
        FillACommand(report, CmdSetPumpPwm, payload);
    }

    /// <summary>Fan target on a 360R, remapped the way its firmware expects.</summary>
    public static void FillHydroShiftFanPwm(Span<byte> report, int dutyPercent)
    {
        Span<byte> payload = stackalloc byte[2];
        payload[0] = PumpSourceHost;
        payload[1] = HydroShiftFanPwm(Math.Clamp(dutyPercent, HydroShiftFanDutyFloor, 100));
        FillACommand(report, CmdSetFanPwm, payload);
    }

    /// <summary>Maps a 10 to 100 duty onto the 360R's 12 to 95 PWM range; below 10 is a stop.</summary>
    public static byte HydroShiftFanPwm(int dutyPercent)
    {
        if (dutyPercent < 10)
        {
            return 0;
        }
        return (byte)Math.Min(12 + ((dutyPercent - 10) * 83 / 90), 95);
    }

    /// <summary>The PWM that runs a 360R pump at <paramref name="rpm"/>, interpolated and clamped to its table.</summary>
    public static int HydroShiftPumpPwmForRpm(int rpm)
    {
        var table = HydroShiftPumpRpmToPwm;
        if (rpm <= table[0].Rpm)
        {
            return table[0].Pwm;
        }
        for (int i = 1; i < table.Length; i++)
        {
            if (rpm <= table[i].Rpm)
            {
                var (r0, p0) = table[i - 1];
                var (r1, p1) = table[i];
                return (int)Math.Round(p0 + (double)(rpm - r0) * (p1 - p0) / (r1 - r0));
            }
        }
        return table[^1].Pwm;
    }

    /// <summary>Lights the whole ring one color from the host, at full firmware brightness so the color carries the level.</summary>
    public static void FillSetPumpLight(Span<byte> report, byte r, byte g, byte b)
    {
        Span<byte> payload = stackalloc byte[19];
        payload[0] = LightScopeBoth;
        payload[1] = LightModeStatic;
        payload[2] = LightBrightnessFull;
        payload[3] = LightSpeedDefault;
        for (int slot = 0; slot < 4; slot++)
        {
            payload[4 + (slot * 3)] = r;
            payload[5 + (slot * 3)] = g;
            payload[6 + (slot * 3)] = b;
        }
        payload[18] = LightSourceHost;
        FillACommand(report, CmdSetPumpLight, payload);
    }

    /// <summary>
    /// The two prime payloads the 360R's per-LED path is brought up with (static mode, full
    /// brightness, no color, then a shifted variant), each sent as <see cref="CmdSetFanLight"/>
    /// and then <see cref="CmdSetPumpLight"/>.
    /// </summary>
    private static readonly byte[] PrimeA =
    {
        0x03, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x32,
    };

    private static readonly byte[] PrimeB =
    {
        0x00, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>Prime packets in the order they go out: A then B under each command.</summary>
    public const int PrimePacketCount = 4;

    public static void FillFanLedPrime(Span<byte> report, int index)
    {
        var payload = (index & 1) == 0 ? PrimeA : PrimeB;
        FillACommand(report, index < 2 ? CmdSetFanLight : CmdSetPumpLight, payload);
    }

    /// <summary>
    /// One per-LED color set: <paramref name="rgb"/> is <see cref="HydroShiftFanLedCount"/> RGB triples
    /// in fan order. The sequenced header's total and chunk length both count selector plus colors.
    /// </summary>
    public static void FillPerLed(Span<byte> report, ReadOnlySpan<byte> rgb)
    {
        Span<byte> payload = stackalloc byte[1 + (HydroShiftFanLedCount * 3)];
        payload.Clear();
        payload[0] = PerLedSelector;
        rgb[..Math.Min(rgb.Length, payload.Length - 1)].CopyTo(payload[1..]);
        JpegPanelProtocol.FillChunk(report, JpegPanelHeaderStyle.LianLiSequenced, CmdSetPerLed, payload, 0, 0);
    }

    /// <summary>The PWM that runs the pump at <paramref name="rpm"/>, interpolated and clamped to the table.</summary>
    public static int GalahadPwmForRpm(int rpm)
    {
        var table = VisionRpmToPwm;
        if (rpm <= table[0].Rpm)
        {
            return table[0].Pwm;
        }
        for (int i = 1; i < table.Length; i++)
        {
            if (rpm <= table[i].Rpm)
            {
                var (r0, p0) = table[i - 1];
                var (r1, p1) = table[i];
                return (int)Math.Round(p0 + (double)(rpm - r0) * (p1 - p0) / (r1 - r0));
            }
        }
        return table[^1].Pwm;
    }

    /// <summary>Parses a status reply; null for any other report or one too short to carry both speeds.</summary>
    public static LianLiAioStatus? ParseStatus(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < AHeaderLength + 4 || reply[1] != CmdStatus)
        {
            return null;
        }
        int length = Math.Min(reply[5], reply.Length - AHeaderLength);
        if (length < 4)
        {
            return null;
        }
        var data = reply.Slice(AHeaderLength, length);
        int fanRpm = (data[0] << 8) | data[1];
        int pumpRpm = (data[2] << 8) | data[3];
        float? coolant = null;
        if (length >= 7 && data[4] != 0)
        {
            float value = data[5] + (data[6] % 10) / 10f;
            // The reference driver drops 1.0 C with both speeds at 0: a placeholder the firmware reports while it starts.
            if (value > 0 && !(value == 1f && fanRpm == 0 && pumpRpm == 0))
            {
                coolant = value;
            }
        }
        return new LianLiAioStatus(fanRpm, pumpRpm, coolant);
    }
}
