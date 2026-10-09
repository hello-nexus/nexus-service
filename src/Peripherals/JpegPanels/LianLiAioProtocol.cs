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

    /// <summary>
    /// The pump-head ring, a firmware effect: scope (0 inner, 1 outer, 2 both), mode, brightness 0-4,
    /// speed 0-4, four RGB slots, direction, off flag, source (0 host, 1 motherboard ARGB).
    /// </summary>
    public const byte CmdSetPumpLight = 0x83;

    private const byte PumpSourceHost = 0x00;

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

    /// <summary>Lights the whole ring one colour from the host, at full firmware brightness so the colour carries the level.</summary>
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
