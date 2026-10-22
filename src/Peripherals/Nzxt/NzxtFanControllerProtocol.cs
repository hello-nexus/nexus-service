using System;

namespace Nexus.Service.Peripherals.Nzxt;

// Fan side of the NZXT RGB & Fan Controller (1E71:2011). Lighting on this device is owned by
// OpenRGB, so nothing here encodes a lighting command, and the connect sequence deliberately
// leaves out the firmware and lighting-info queries.
//
// The first byte of every report is its HID report id. Status reports arrive on report 0x67
// sub-command 0x02 carrying, per channel, a mode byte (0 = nothing connected, 1 = DC, 2 = PWM),
// a little-endian tach and the commanded duty percent; the offsets are the constants below.
internal static class NzxtFanControllerProtocol
{
    public const int VendorId = 0x1E71;
    public const int ProductId = 0x2011;
    public const int ReportLength = 64;
    public const int FanChannelCount = 3;

    private const byte ReportFanConfig = 0x60;
    private const byte ReportFanSet = 0x62;
    private const byte ReportFanStatus = 0x67;

    private const int ModeOffset = 16;
    private const int RpmOffset = 24;
    private const int DutyOffset = 40;

    /// <summary>Status cadence index sent with the interval command.</summary>
    private const byte StatusIntervalIndex = 0x01;

    /// <summary>Sets the status cadence for the fan channels (60 02).</summary>
    public static byte[] EncodeSetStatusInterval()
    {
        var report = NewReport(ReportFanConfig, 0x02);
        report[2] = 0x01;
        report[3] = 0xE8;
        report[4] = StatusIntervalIndex;
        report[5] = 0x01;
        report[6] = 0xE8;
        report[7] = StatusIntervalIndex;
        return report;
    }

    /// <summary>Starts the periodic status reports (60 03).</summary>
    public static byte[] EncodeStartReporting() => NewReport(ReportFanConfig, 0x03);

    /// <summary>Fixed duty for one channel (62 01 mask d0 d1 d2); the other channels' bytes stay zero and are ignored through the mask.</summary>
    public static byte[] EncodeFixedDuty(int channel, int dutyPercent)
    {
        if ((uint)channel >= FanChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
        var report = NewReport(ReportFanSet, 0x01);
        report[2] = (byte)(1 << channel);
        report[3 + channel] = (byte)Math.Clamp(dutyPercent, 0, 100);
        return report;
    }

    public static bool IsStatusReport(ReadOnlySpan<byte> report) =>
        report.Length >= DutyOffset + FanChannelCount && report[0] == ReportFanStatus && report[1] == 0x02;

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

/// <summary>One fan channel as the status report states it. <paramref name="Mode"/> is zero when nothing is plugged in.</summary>
public readonly record struct NzxtFanReading(int Mode, int Rpm, int DutyPercent);
