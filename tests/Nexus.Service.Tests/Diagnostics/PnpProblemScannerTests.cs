using Nexus.Service.Diagnostics.SystemInfo;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class PnpProblemScannerTests
{
    [Fact]
    public void Filter_Empty_ReturnsEmpty()
    {
        Assert.Empty(PnpProblemScanner.Filter(System.Array.Empty<(string, string, uint)>()));
    }

    [Fact]
    public void Filter_ProblemDevice_CarriesCodeAndLabel()
    {
        var devices = PnpProblemScanner.Filter(new[] { ("Unknown device", "USB\\VID_0000", 43u) });

        Assert.Single(devices);
        Assert.Equal("Unknown device", devices[0].Name);
        Assert.Equal(43, devices[0].ProblemCode);
        Assert.Equal("CM_PROB_FAILED_POST_START", devices[0].ProblemText);
    }

    [Fact]
    public void Filter_MultipleDevices_KeepsOrder()
    {
        var devices = PnpProblemScanner.Filter(new[]
        {
            ("Device A", "PCI\\A", 10u),
            ("Device B", "PCI\\B", 45u),
        });

        Assert.Equal(2, devices.Count);
        Assert.Equal("CM_PROB_FAILED_START", devices[0].ProblemText);
        Assert.Equal("CM_PROB_PHANTOM", devices[1].ProblemText);
    }

    [Fact]
    public void Filter_DisabledDevice_IsNotReported()
    {
        var devices = PnpProblemScanner.Filter(new[]
        {
            ("Disabled device", "PCI\\A", 22u),
            ("Device B", "PCI\\B", 45u),
        });

        Assert.Single(devices);
        Assert.Equal("Device B", devices[0].Name);
    }

    [Fact]
    public void Filter_UnknownCode_FallsBackToGenericLabel()
    {
        var devices = PnpProblemScanner.Filter(new[] { ("Odd device", "X", 999u) });

        Assert.Equal("CM_PROB_999", devices[0].ProblemText);
    }

    [Fact]
    public void Filter_ZeroErrorCode_Skipped()
    {
        Assert.Empty(PnpProblemScanner.Filter(new[] { ("Healthy device", "X", 0u) }));
    }
}
