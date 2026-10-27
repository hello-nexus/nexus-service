using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Xunit;

namespace Nexus.Service.Tests.Devices;

public class DeviceControlPolicyTests
{
    [Fact]
    public void ConflictAppFor_StreamDeck_ReturnsTheElgatoCatalogId()
    {
        Assert.Equal(StreamDeckHandler.ElgatoConflictAppId, DeviceControlPolicy.ConflictAppFor("streamdeck"));
    }

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli-tl")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli-aio")]
    [InlineData("lianli-hydroshift-lcd")]
    [InlineData("strimer")]
    [InlineData("corsair")]
    public void DefaultOn_CompetingAppHub_IsFalseAndExperimental(string handlerId)
    {
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
        Assert.True(DeviceControlPolicy.IsExperimental(handlerId));
    }

    [Theory]
    [InlineData("nzxt-kraken")]
    [InlineData("zmatrices-lcd")]
    public void DefaultOn_HintOnlyConflictHandler_WhoseAppStartsWhitelisted_IsFalse(string handlerId)
    {
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
    }

    [Theory]
    [InlineData("lianli")]
    [InlineData("lianli-tl")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli-aio")]
    [InlineData("strimer")]
    [InlineData("corsair")]
    [InlineData("tryx")]
    [InlineData("streamdeck")]
    [InlineData("nzxt-kraken")]
    [InlineData("zmatrices-lcd")]
    [InlineData("deepcool-mystique")]
    public void ConflictAppFor_KnownHandlers_ReturnsANonEmptyId(string handlerId)
    {
        Assert.False(string.IsNullOrEmpty(DeviceControlPolicy.ConflictAppFor(handlerId)));
    }

    [Fact]
    public void Asiahorse_Umbra_DefaultsOn_AndNamesItsAppWithoutWhitelistingIt()
    {
        Assert.True(DeviceControlPolicy.DefaultOn("asiahorse-umbra"));
        Assert.Equal("asiahorse-umbra", DeviceControlPolicy.ConflictAppFor("asiahorse-umbra"));
        Assert.DoesNotContain("asiahorse-umbra", Nexus.Service.Conflicts.ConflictAppCatalog.DefaultWhitelistedIds());
    }

    [Fact]
    public void ConflictAppFor_UnmappedHandler_ReturnsNull()
    {
        Assert.Null(DeviceControlPolicy.ConflictAppFor("keeb"));
    }

    [Fact]
    public void DefaultOn_UnmappedHandler_IsTrue()
    {
        Assert.True(DeviceControlPolicy.DefaultOn("keeb"));
    }
}
