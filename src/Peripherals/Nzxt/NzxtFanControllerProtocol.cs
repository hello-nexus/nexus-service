using System;

namespace Nexus.Service.Peripherals.Nzxt;

// Fan side of the NZXT RGB & Fan Controller. Lighting on this device is owned by OpenRGB, so
// nothing here encodes a lighting command, and the connect sequence deliberately leaves out the
// firmware and lighting-info queries.
//
// The first byte of every report is its HID report id. A status report carries, per channel, a
// mode byte (the Mode constants), a little-endian tach and the commanded duty percent, at the
// offsets below.
internal static class NzxtFanControllerProtocol
{
    public const int VendorId = 0x1E71;
    /// <summary>Three sibling revisions share the 3 fan channel profile and report layout.</summary>
    public static readonly int[] ProductIds = { 0x2011, 0x2019, 0x201F };
    public const int ReportLength = 64;
    public const int FanChannelCount = 3;

    private const byte ReportFanConfig = 0x60;
    private const byte ReportFanSet = 0x62;
    private const byte ReportFanStatus = 0x67;

    /// <summary>Mode byte: nothing connected to the header.</summary>
    public const int ModeNone = 0;
    /// <summary>Mode byte: a voltage-controlled fan.</summary>
    public const int ModeDc = 1;
    /// <summary>Mode byte: a PWM fan.</summary>
    public const int ModePwm = 2;

    private const byte SubCommandSetInterval = 0x02;
    private const byte SubCommandStartReporting = 0x03;
    private const byte SubCommandStatus = 0x02;
    private const byte SubCommandSetDuty = 0x01;

    // The two fixed bytes that precede the cadence index in the interval command, sent twice.
    private const byte IntervalByteA = 0x01;
    private const byte IntervalByteB = 0xE8;

    private const int ModeOffset = 16;
    private const int RpmOffset = 24;
    private const int DutyOffset = 40;

    /// <summary>Status cadence index sent with the interval command.</summary>
    private const byte StatusIntervalIndex = 0x01;

    /// <summary>Sets the status cadence for the fan channels.</summary>
    public static byte[] EncodeSetStatusInterval()
    {
        var report = NewReport(ReportFanConfig, SubCommandSetInterval);
        report[2] = IntervalByteA;
        report[3] = IntervalByteB;
        report[4] = StatusIntervalIndex;
        report[5] = IntervalByteA;
        report[6] = IntervalByteB;
        report[7] = StatusIntervalIndex;
        return report;
    }

    /// <summary>Starts the periodic status reports.</summary>
    public static byte[] EncodeStartReporting() => NewReport(ReportFanConfig, SubCommandStartReporting);

    /// <summary>Fixed duty for one channel: a channel mask, then one duty byte per channel; the other channels' bytes stay zero and are ignored through the mask.</summary>
    public static byte[] EncodeFixedDuty(int channel, int dutyPercent)
    {
        if ((uint)channel >= FanChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
        var report = NewReport(ReportFanSet, SubCommandSetDuty);
        report[2] = (byte)(1 << channel);
        report[3 + channel] = (byte)Math.Clamp(dutyPercent, 0, 100);
        return report;
    }

    public static bool IsStatusReport(ReadOnlySpan<byte> report) =>
        report.Length >= DutyOffset + FanChannelCount && report[0] == ReportFanStatus && report[1] == SubCommandStatus;

    /// <summary>Decodes a status report; false when it is some other report.</summary>
    public static bool TryDecodeStatus(ReadOnlySpan<byte> report, out NzxtFanReading[] fans)
    {
        fans = Array.Empty<NzxtFanReading>();
        if (!IsStatusReport(report))
        {
            return false;
        }
        var result = new NzxtFanReading[FanChannelCount];
        for (int i = 0; i < FanChannelCount; i++)
        {
            result[i] = new NzxtFanReading(
                report[ModeOffset + i],
                report[RpmOffset + 2 * i] | (report[RpmOffset + 2 * i + 1] << 8),
                report[DutyOffset + i]);
        }
        fans = result;
        return true;
    }

    private static byte[] NewReport(byte reportId, byte subCommand)
    {
        var report = new byte[ReportLength];
        report[0] = reportId;
        report[1] = subCommand;
        return report;
    }
}

/// <summary>One fan channel as the status report states it. <paramref name="Mode"/> is <see cref="NzxtFanControllerProtocol.ModeNone"/> when nothing is plugged in.</summary>
public readonly record struct NzxtFanReading(int Mode, int Rpm, int DutyPercent);
