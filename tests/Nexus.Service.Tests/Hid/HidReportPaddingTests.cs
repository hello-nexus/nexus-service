using System;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.Hid;

public class HidReportPaddingTests
{
    // The Lian Li SL v1 case: a 7-byte E0 command against an 11-byte feature report.
    [Fact]
    public void Short_report_is_zero_padded_to_the_report_length()
    {
        var report = new byte[] { 0xE0, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var padded = HidReportPadding.Pad(report, 11);
        Assert.Equal(11, padded.Length);
        Assert.Equal(report, padded[..7]);
        Assert.All(padded[7..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Report_at_or_past_the_length_passes_through()
    {
        var exact = new byte[] { 0xE0, 1, 2, 3, 4, 5, 6 };
        Assert.Equal(exact, HidReportPadding.Pad(exact, 7));
        var longer = new byte[353];
        longer[0] = 0xE0;
        Assert.Equal(353, HidReportPadding.Pad(longer, 11).Length);
    }

    [Fact]
    public void Unknown_length_zero_leaves_the_report_alone()
    {
        var report = new byte[] { 0xE0, 0x50, 0x00 };
        Assert.Equal(report, HidReportPadding.Pad(report, 0));
    }
}
