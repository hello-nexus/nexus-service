using System;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// Pump and pump-head fan control for the ASUS ROG Ryujin III Extreme over HID report 0xEC.
/// The duty report is taken from an Armoury Crate capture; the status and duty responses are
/// parsed per liquidctl's asus_ryujin driver, since the capture holds no device replies.
/// The external fan controller commands (0x21, 0xA0, 0xA1) are deliberately not used.
/// </summary>
internal static class RyujinCoolingProtocol
{
    /// <summary>The Nexus Control gate key and device id for the Extreme, apart from the 360's live-panel key.</summary>
    public const string HandlerId = "asus-ryujin-extreme";

    /// <summary>
    /// Prefix of the channel and sensor ids. It predates <see cref="HandlerId"/>, and curves and
    /// manual speeds saved under it stay bound to the channels.
    /// </summary>
    public const string ChannelPrefix = "asus-ryujin-lcd";

    public const int VendorId = 0x0B05;
    public const int ProductId = 0x1BCB;

    public const int ReportLength = 65;
    public const byte ReportId = 0xEC;

    private const byte CmdSetDuty = 0x1A;
    private const byte DutyChannel = 1;
    private const byte RequestStatus = 0x99;
    private const byte ResponseStatus = 0x19;
    private const byte RequestDuty = 0x9A;
    private const byte ResponseDuty = 0x1A;

    private const double MaxPlausibleTempC = 100;
    private const int TempOffset = 5;
    private const int PumpRpmOffset = 7;
    private const int FanRpmOffset = 10;

    /// <summary>Armoury Crate resends the duty on about this cadence, in a cycle with the two requests.</summary>
    public const int KeepaliveMs = 3500;

    /// <summary>Lowest pump duty ever written; Armoury Crate's lowest seen setting.</summary>
    public const int PumpDutyFloor = 20;

    /// <summary>Armoury Crate's default duty, handed back for a channel Nexus lets go of.</summary>
    public const int SafeDuty = 100;

    /// <summary>
    /// Stands in for a channel Nexus is not driving when the cooler never answers a duty request,
    /// so a write for the other channel still goes out without ever sending the safe duty to it.
    /// </summary>
    public const int FallbackDuty = 50;

    /// <summary>Polls without a parsed duty reply before the fallback applies.</summary>
    public const int DutyReplyGraceCycles = 3;

    public static byte[] EncodeSetDuty(int pumpPercent, int fanPercent)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = CmdSetDuty;
        report[2] = DutyChannel;
        report[3] = (byte)ClampPump(pumpPercent);
        report[4] = (byte)ClampFan(fanPercent);
        return report;
    }

    public static byte[] EncodeStatusRequest() => Request(RequestStatus);

    public static byte[] EncodeDutyRequest() => Request(RequestDuty);

    public static int ClampPump(int percent) => Math.Clamp(percent, PumpDutyFloor, 100);

    public static int ClampFan(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>Liquid temperature in degrees C and the two speeds in rpm.</summary>
    public static bool TryParseStatus(ReadOnlySpan<byte> report, out double liquidTempC, out int pumpRpm, out int fanRpm)
    {
        liquidTempC = 0;
        pumpRpm = 0;
        fanRpm = 0;
        if (report.Length < FanRpmOffset + 2 || report[0] != ReportId || report[1] != ResponseStatus)
        {
            return false;
        }
        double temp = report[TempOffset] + report[TempOffset + 1] / 10.0;
        if (temp > MaxPlausibleTempC)
        {
            return false;
        }
        liquidTempC = temp;
        pumpRpm = report[PumpRpmOffset] | (report[PumpRpmOffset + 1] << 8);
        fanRpm = report[FanRpmOffset] | (report[FanRpmOffset + 1] << 8);
        return true;
    }

    public static bool TryParseDuty(ReadOnlySpan<byte> report, out int pumpPercent, out int fanPercent)
    {
        pumpPercent = 0;
        fanPercent = 0;
        if (report.Length < 6 || report[0] != ReportId || report[1] != ResponseDuty || report[4] > 100 || report[5] > 100)
        {
            return false;
        }
        pumpPercent = report[4];
        fanPercent = report[5];
        return true;
    }

    private static byte[] Request(byte header)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = header;
        return report;
    }
}
