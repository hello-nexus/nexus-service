using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

public class JpegPanelLinuxCandidateTests
{
    private static HidDeviceInfo Info(int outLen) => new() { VendorId = 0x1B1C, ProductId = 0x0C39, OutputReportByteLength = outLen };

    [Theory]
    [InlineData(false, 1024, true)]
    [InlineData(true, 1024, true)]
    [InlineData(false, 65, false)]
    [InlineData(true, 65, false)]
    public void Model_without_control_channel_attaches_on_every_os(bool linux, int outLen, bool expected)
    {
        Assert.Null(JpegPanelModel.CorsairCapellix.Handshake);
        Assert.Equal(expected, JpegPanelConnectionWorker.IsCandidate(JpegPanelModel.CorsairCapellix, Info(outLen), linux));
    }

    public static TheoryData<string> AllHandlers()
    {
        var data = new TheoryData<string>();
        foreach (var m in JpegPanelModel.All)
        {
            data.Add(m.HandlerId);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void On_linux_only_models_without_a_control_channel_attach(string handlerId)
    {
        var m = JpegPanelModel.ByHandlerId(handlerId)!;
        Assert.Equal(m.Handshake == null, JpegPanelConnectionWorker.IsCandidate(m, Info(m.ReportLength), true));
        Assert.True(JpegPanelConnectionWorker.IsCandidate(m, Info(m.ReportLength), false));
    }

    [Theory]
    [InlineData("corsair-capellix-lcd", true)]
    [InlineData("corsair-xc7-lcd", true)]
    [InlineData("idcooling-fx-lcd", true)]
    [InlineData("asrock-lcd", false)]
    [InlineData("asus-slc4-lcd", false)]
    [InlineData("corsair-nautilus-lcd", false)]
    public void Named_panels_on_linux(string handlerId, bool attaches)
    {
        var m = JpegPanelModel.ByHandlerId(handlerId)!;
        Assert.Equal(attaches, JpegPanelConnectionWorker.IsCandidate(m, Info(m.ReportLength), true));
    }
}
