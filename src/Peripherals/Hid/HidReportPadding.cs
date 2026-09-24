using System;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Zero-pads a report to the collection's HIDP_CAPS report length: the Windows
/// HID class driver rejects a shorter HidD_Set*/Get*/WriteFile buffer (error 87)
/// before it reaches the device. A buffer at or past the length passes through.
/// </summary>
internal static class HidReportPadding
{
    public static byte[] Pad(ReadOnlySpan<byte> report, int reportLength)
    {
        var buf = new byte[Math.Max(report.Length, reportLength)];
        report.CopyTo(buf);
        return buf;
    }
}
