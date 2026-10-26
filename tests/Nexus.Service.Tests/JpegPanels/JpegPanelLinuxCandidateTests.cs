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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Model_with_control_channel_stays_off_linux(bool linux, bool expected)
    {
        var model = JpegPanelModel.CorsairNautilusCap;
        Assert.NotNull(model.Handshake);
        Assert.Equal(expected, JpegPanelConnectionWorker.IsCandidate(model, Info(model.ReportLength), linux));
    }
}
