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
    [InlineData("lianli-hydroshift-lcd")]
    [InlineData("strimer")]
    [InlineData("corsair")]
    public void DefaultOn_CompetingAppHub_IsFalseAndAdopts(string handlerId)
    {
        Assert.False(DeviceControlPolicy.DefaultOn(handlerId));
        Assert.NotNull(DeviceControlPolicy.AdoptionConflictAppFor(handlerId));
    }

    [Theory]
    [InlineData("nzxt-kraken")]
    [InlineData("zmatrices-lcd")]
    public void DefaultOn_HintOnlyConflictHandler_IsTrueAndNeverAdopts(string handlerId)
    {
        Assert.True(DeviceControlPolicy.DefaultOn(handlerId));
        Assert.Null(DeviceControlPolicy.AdoptionConflictAppFor(handlerId));
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
    public void ConflictAppFor_KnownHandlers_ReturnsANonEmptyId(string handlerId)
    {
        Assert.False(string.IsNullOrEmpty(DeviceControlPolicy.ConflictAppFor(handlerId)));
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
