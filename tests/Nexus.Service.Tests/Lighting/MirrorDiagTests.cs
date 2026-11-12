using Nexus.Service.Lighting.Capture;

namespace Nexus.Service.Tests.Lighting;

public class MirrorDiagTests
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

    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(100, 10)]
    public void Percentile_IsNearestRank(double p, double expected)
    {
        var samples = new double[] { 10, 1, 9, 2, 8, 3, 7, 4, 6, 5 };
        Assert.Equal(expected, MirrorDiagWindow.Percentile(samples, p));
    }

    [Fact]
    public void Percentile_EmptyIsZero() => Assert.Equal(0, MirrorDiagWindow.Percentile([], 95));

    [Fact]
    public void FormatAndReset_ReportsThenClears()
    {
        var w = new MirrorDiagWindow { AcquireCalls = 100, Timeouts = 40, Frames = 50, PointerOnly = 10, Copies = 50, Pushed = 100, Lost = 2, Recreated = 2, AccumulatedSum = 77 };
        w.RecordAcquire(1); w.RecordAcquire(3); w.RecordCopyMap(2); w.RecordBlit(0.5);
        var line = w.FormatAndReset(10);
        Assert.StartsWith("[mirror-diag] ", line);
        Assert.Contains("fps=10.0", line);
        Assert.Contains("accumulated=77", line);
        Assert.Contains("acquireMs=1.00/3.00/3.00", line);
        Assert.Contains("lost=2 recreated=2", line);
        var empty = w.FormatAndReset(10);
        Assert.Contains("acquire=0 timeouts=0", empty);
        Assert.Contains("acquireMs=0.00/0.00/0.00", empty);
    }
}
