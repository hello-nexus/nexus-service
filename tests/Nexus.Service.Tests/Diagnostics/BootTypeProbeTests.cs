using Nexus.Service.Diagnostics;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class BootTypeProbeTests
{
    private static string BootEvent(string bootType) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>"
        + "<Provider Name='Microsoft-Windows-Kernel-Boot' Guid='{15ca44ff-4d7a-4baa-bba5-0998955e531e}'/><EventID>27</EventID>"
        + "<TimeCreated SystemTime='2026-10-11T16:14:59.0920163Z'/><Channel>System</Channel></System>"
        + $"<EventData><Data Name='BootType'>{bootType}</Data><Data Name='LoadOptions'> NOEXECUTE=OPTIN</Data></EventData></Event>";

    [Theory]
    [InlineData("0", "full boot (BootType=0 at 2026-10-11T16:14:59.0920163Z)")]
    [InlineData("1", "fast startup (BootType=1 at 2026-10-11T16:14:59.0920163Z)")]
    [InlineData("2", "resume from hibernation (BootType=2 at 2026-10-11T16:14:59.0920163Z)")]
    [InlineData("7", "unknown (BootType=7 at 2026-10-11T16:14:59.0920163Z)")]
    public void Boot_type_is_named(string raw, string expected)
    {
        Assert.Equal(expected, BootTypeProbe.DescribeBootEvent(BootEvent(raw)));
    }

    [Fact]
    public void Missing_or_broken_events_never_throw()
    {
        Assert.Equal("no Kernel-Boot 27 event", BootTypeProbe.DescribeBootEvent(null));
        Assert.Equal("unreadable Kernel-Boot 27 event", BootTypeProbe.DescribeBootEvent("<Event"));
        Assert.Equal("Kernel-Boot 27 without a BootType", BootTypeProbe.DescribeBootEvent(BootEvent("x")));
    }

    [Fact]
    public void Line_carries_every_raw_input()
    {
        Assert.Equal(
            "last boot: fast startup (BootType=1 at t); HiberbootEnabled=1 policy HiberbootEnabled=unset HibernateEnabled=unset HibernateEnabledDefault=1",
            BootTypeProbe.Describe("fast startup (BootType=1 at t)", 1, null, null, 1));
    }
}
