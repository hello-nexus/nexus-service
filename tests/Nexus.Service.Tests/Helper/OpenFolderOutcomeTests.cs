using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class OpenFolderOutcomeTests
{
    [Theory]
    [InlineData(true, null, OpenFolderOutcome.Opened)]
    [InlineData(false, "timeout", OpenFolderOutcome.TimedOut)]
    [InlineData(false, "boom", OpenFolderOutcome.Failed)]
    [InlineData(false, null, OpenFolderOutcome.Failed)]
    public void FromResult_MapsAckAndTimeout(bool ok, string? error, OpenFolderOutcome expected)
    {
        Assert.Equal(expected, OpenFolderOutcomes.FromResult(ok, error));
    }

    [Theory]
    [InlineData(OpenFolderOutcome.Opened, false)]
    [InlineData(OpenFolderOutcome.TimedOut, false)]
    [InlineData(OpenFolderOutcome.NoHelper, true)]
    [InlineData(OpenFolderOutcome.Failed, true)]
    public void IsFailure_ReportsOnlyDefiniteFailures(OpenFolderOutcome outcome, bool expected)
    {
        Assert.Equal(expected, OpenFolderOutcomes.IsFailure(outcome));
    }
}
