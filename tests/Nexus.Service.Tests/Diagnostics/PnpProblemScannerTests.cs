using Nexus.Service.Diagnostics.SystemInfo;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class PnpProblemScannerTests
{
    [Fact]
    public void ParseJson_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(PnpProblemScanner.ParseJson(""));
    }

    [Fact]
    public void ParseJson_SingleObject_NotArray()
    {
        const string json = """{"Name":"Unknown device","DeviceID":"USB\\VID_0000","ConfigManagerErrorCode":43}""";

        var devices = PnpProblemScanner.ParseJson(json);

        Assert.Single(devices);
        Assert.Equal("Unknown device", devices[0].Name);
        Assert.Equal(43, devices[0].ProblemCode);
        Assert.Equal("CM_PROB_FAILED_POST_START", devices[0].ProblemText);
    }

    [Fact]
    public void ParseJson_Array_MultipleDevices()
    {
        const string json = """
            [
              {"Name":"Device A","DeviceID":"PCI\\A","ConfigManagerErrorCode":10},
              {"Name":"Device B","DeviceID":"PCI\\B","ConfigManagerErrorCode":45}
            ]
            """;

        var devices = PnpProblemScanner.ParseJson(json);

        Assert.Equal(2, devices.Count);
        Assert.Equal("CM_PROB_FAILED_START", devices[0].ProblemText);
        Assert.Equal("CM_PROB_PHANTOM", devices[1].ProblemText);
    }

    [Fact]
    public void ParseJson_DisabledDevice_IsNotReported()
    {
        const string json = """
            [
              {"Name":"Disabled device","DeviceID":"PCI\\A","ConfigManagerErrorCode":22},
              {"Name":"Device B","DeviceID":"PCI\\B","ConfigManagerErrorCode":45}
            ]
            """;

        var devices = PnpProblemScanner.ParseJson(json);

        Assert.Single(devices);
        Assert.Equal("Device B", devices[0].Name);
    }

    [Fact]
    public void ParseJson_UnknownCode_FallsBackToGenericLabel()
    {
        const string json = """{"Name":"Odd device","DeviceID":"X","ConfigManagerErrorCode":999}""";

        var devices = PnpProblemScanner.ParseJson(json);

        Assert.Equal("CM_PROB_999", devices[0].ProblemText);
    }

    [Fact]
    public void ParseJson_MalformedJson_ReturnsEmptyNotThrow()
    {
        Assert.Empty(PnpProblemScanner.ParseJson("not json"));
    }

    [Fact]
    public void ParseJson_ZeroErrorCode_Skipped()
    {
        const string json = """{"Name":"Healthy device","DeviceID":"X","ConfigManagerErrorCode":0}""";

        Assert.Empty(PnpProblemScanner.ParseJson(json));
    }
}
