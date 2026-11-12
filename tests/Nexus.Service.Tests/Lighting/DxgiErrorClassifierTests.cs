using Nexus.Service.Lighting.Capture;

namespace Nexus.Service.Tests.Lighting;

public class DxgiErrorClassifierTests
{
    [Theory]
    [InlineData(DxgiErrorClassifier.WaitTimeout, DxgiFailureAction.Timeout)]
    [InlineData(DxgiErrorClassifier.AccessLost, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.InvalidCall, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.SessionDisconnected, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.NotCurrentlyAvailable, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.ModeChangeInProgress, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.AccessDenied, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.Fail, DxgiFailureAction.RecreateDuplication)]
    [InlineData(DxgiErrorClassifier.DeviceRemoved, DxgiFailureAction.RebuildDevice)]
    [InlineData(DxgiErrorClassifier.DeviceReset, DxgiFailureAction.RebuildDevice)]
    [InlineData(DxgiErrorClassifier.DeviceHung, DxgiFailureAction.RebuildDevice)]
    [InlineData(DxgiErrorClassifier.DriverInternalError, DxgiFailureAction.RebuildDevice)]
    [InlineData(DxgiErrorClassifier.OutOfMemory, DxgiFailureAction.RebuildDevice)]
    public void Classify_PicksCheapestRecovery(int hr, DxgiFailureAction expected) =>
        Assert.Equal(expected, DxgiErrorClassifier.Classify(hr));

    [Fact]
    public void Describe_NamesKnownCodesAndHexes()
    {
        Assert.Equal("0x887A0026 DXGI_ERROR_ACCESS_LOST", DxgiErrorClassifier.Describe(DxgiErrorClassifier.AccessLost));
        Assert.Equal("0x80070005 E_ACCESSDENIED", DxgiErrorClassifier.Describe(DxgiErrorClassifier.AccessDenied));
        Assert.EndsWith("unknown", DxgiErrorClassifier.Describe(0x12345678));
    }
}
