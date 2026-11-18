using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class LConnectServiceDetectionTests
{
    [Fact]
    public void LConnectConflictProcessNamesAreUnchanged()
    {
        var def = ConflictWatcher.FindById("lian-li-l-connect");
        Assert.NotNull(def);
        Assert.Equal(new[] { "L-Connect 3", "L-Connect", "LConnect3", "LConnect" }, def!.ProcessNames);
        Assert.True(def.DefaultWhitelisted);
    }

    [Theory]
    [InlineData("L-Connect-Service")]
    [InlineData("L-Connect-Service-Watcher")]
    [InlineData("LConnect3")]
    public void ARunningLConnectProcessIsTheHolderOfALianLiHandler(string running)
    {
        Assert.Equal("lian-li-l-connect", AccessDeniedHolder.Find("lianli-hydroshift2", n => n == running));
    }

    [Fact]
    public void NothingRunningOrAnUnmappedHandlerHasNoHolder()
    {
        Assert.Null(AccessDeniedHolder.Find("lianli-hydroshift2", _ => false));
        Assert.Null(AccessDeniedHolder.Find("not-a-handler", _ => true));
    }

    [Fact]
    public void TheHolderIsLoggedOncePerStateNotPerRetry()
    {
        string? logged = null;
        Assert.True(Nexus.Service.Peripherals.BulkPanels.BulkPanelConnectionWorker.ShouldLogHolder(ref logged, "lian-li-l-connect"));
        Assert.False(Nexus.Service.Peripherals.BulkPanels.BulkPanelConnectionWorker.ShouldLogHolder(ref logged, "lian-li-l-connect"));
        Assert.True(Nexus.Service.Peripherals.BulkPanels.BulkPanelConnectionWorker.ShouldLogHolder(ref logged, null));
        Assert.True(Nexus.Service.Peripherals.BulkPanels.BulkPanelConnectionWorker.ShouldLogHolder(ref logged, "lian-li-l-connect"));
    }
}
