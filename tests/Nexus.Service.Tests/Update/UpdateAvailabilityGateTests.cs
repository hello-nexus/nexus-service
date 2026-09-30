using Nexus.Service.Update;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Tests for UpdateService.CanOfferUpdate (the download-tier offer decision,
/// which every platform gets once an asset resolves for it) and
/// UpdateService.ShouldAutoStage (the background download+install gate, closed
/// for an install that cannot apply updates itself).
/// </summary>
public class UpdateAvailabilityGateTests
{
    [Fact]
    public void CanOfferUpdate_True_WhenNewer()
    {
        Assert.True(UpdateService.CanOfferUpdate(isNewer: true));
    }

    [Fact]
    public void CanOfferUpdate_False_WhenNotNewer()
    {
        Assert.False(UpdateService.CanOfferUpdate(isNewer: false));
    }

    [Theory]
    [InlineData("always")]
    [InlineData("download")]
    public void ShouldAutoStage_True_WhenApplicableAndOfferedAndNotStaged(string mode)
    {
        Assert.True(UpdateService.ShouldAutoStage(offerUpdate: true, mode, alreadyStaged: false, canApply: true));
    }

    [Theory]
    [InlineData("always")]
    [InlineData("download")]
    public void ShouldAutoStage_False_WhenInstallCannotApply(string mode)
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode, alreadyStaged: false, canApply: false));
    }

    [Fact]
    public void ShouldAutoStage_False_InNotifyMode()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode: "notify", alreadyStaged: false, canApply: true));
    }

    [Fact]
    public void ShouldAutoStage_False_WhenAlreadyStaged()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: true, mode: "always", alreadyStaged: true, canApply: true));
    }

    [Fact]
    public void ShouldAutoStage_False_WhenNotOffered()
    {
        Assert.False(UpdateService.ShouldAutoStage(offerUpdate: false, mode: "always", alreadyStaged: false, canApply: true));
    }
}
