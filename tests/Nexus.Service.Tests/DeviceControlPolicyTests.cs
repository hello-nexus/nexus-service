using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests;

public class DeviceControlPolicyTests
{
    [Theory]
    [InlineData("cnvs")]
    [InlineData("keeb")]
    [InlineData("np50")]
    [InlineData("smarthub")]
    [InlineData("y70")]
    [InlineData("qseries")]
    [InlineData("fan-hub")]
    [InlineData("aw5")]
    public void IsExperimental_FirstPartyHardware_IsFalse(string handlerId)
    {
        Assert.False(DeviceControlPolicy.IsExperimental(handlerId));
    }

    [Theory]
    [InlineData("corsair")]
    [InlineData("lianli")]
    [InlineData("lianli-tl")]
    [InlineData("lianli-wireless")]
    [InlineData("lianli-aio")]
    [InlineData("strimer")]
    [InlineData("lianli-screen88")]
    [InlineData("lianli-hydroshift2-curve")]
    [InlineData("corsair-link-lcd")]
    [InlineData("corsair-xc7-lcd")]
    public void IsExperimental_LianLiOrCorsairHardware_IsTrue(string handlerId)
    {
        Assert.True(DeviceControlPolicy.IsExperimental(handlerId));
    }

    [Theory]
    [InlineData("nzxt-kraken")]
    [InlineData("nzxt-fan-controller")]
    [InlineData("streamdeck")]
    [InlineData("tryx")]
    [InlineData("zmatrices-lcd")]
    [InlineData("nollie")]
    [InlineData("asus-ryujin-lcd")]
    [InlineData("asus-glacial-lcd")]
    [InlineData("deepcool-mystique")]
    [InlineData("asrock-lcd")]
    [InlineData("asus-slc4-lcd")]
    [InlineData("thermalright-lcd")]
    [InlineData("idcooling-fx-lcd")]
    public void IsExperimental_OtherThirdPartyHardware_IsFalse(string handlerId)
    {
        Assert.False(DeviceControlPolicy.IsExperimental(handlerId));
    }

    [Fact]
    public void GetAll_FirstPartyHandlers_ReportSupportedAndNotExperimental()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub(), TestHandlers.QSeries(), new Aw5Handler() },
            new StubUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()));

        var items = manager.GetAll();

        Assert.All(items, item =>
        {
            Assert.True(item.SupportsNexusControl);
            Assert.False(item.Experimental);
        });
    }
}
