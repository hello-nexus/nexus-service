using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Displays;

public class DdcEligibilityTests
{
    [Theory]
    [InlineData(5u, @"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000&REV_A1", true)]   // HDMI on a GPU
    [InlineData(10u, @"PCI\VEN_8086&DEV_46A6", true)]                          // DisplayPort external
    [InlineData(0x80000000u, @"PCI\VEN_8086&DEV_46A6", true)]                  // internal panel
    [InlineData(null, @"PCI\VEN_10DE&DEV_2684", true)]                         // unknown technology, PCI adapter
    [InlineData(5u, "", true)]                                                 // no adapter id
    [InlineData(5u, null, true)]
    [InlineData(null, null, true)]
    [InlineData(18u, @"PCI\VEN_8086&DEV_46A6", true)]                          // DisplayPort over USB tunnel: a real DP link
    [InlineData(16u, @"PCI\VEN_10DE&DEV_2684", false)]                         // indirect wired
    [InlineData(17u, @"PCI\VEN_10DE&DEV_2684", false)]                         // indirect virtual
    [InlineData(5u, @"USB\VID_33C3&PID_0E02&MI_00\6&1F2E3D4C&0&0000", false)]  // driver reports HDMI but the adapter is USB
    [InlineData(null, @"usb\vid_33c3&pid_0e02", false)]
    [InlineData(16u, @"USB\VID_33C3&PID_0E02", false)]
    public void CanCarryDdc(uint? technology, string? adapterId, bool expected)
    {
        Assert.Equal(expected, DdcEligibility.CanCarryDdc(technology, adapterId));
    }
}
