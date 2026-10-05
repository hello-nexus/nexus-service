using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.LianLi;
using Xunit;

namespace Nexus.Service.Tests.LianLi;

public class LianLiCoolingProviderTests
{
    private static LianLiCoolingProvider Connected(InMemoryConfigStore store)
    {
        var hub = new LianLiHub();
        LianLiFanProfiles.TryGet(0xA102, out var profile);
        hub.Attach(new HubTransportSpy(), profile);
        return new LianLiCoolingProvider(hub, store);
    }

    [Fact]
    public void Surfaces_only_ports_that_have_fans()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 4);
            s.Devices.LianLi.SetFans(1, 0);
            s.Devices.LianLi.SetFans(2, 2);
            s.Devices.LianLi.SetFans(3, 0);
        });
        var provider = Connected(store);

        Assert.Equal(new[] { "lianli:port0", "lianli:port2" },
            provider.GetFanChannels().Select(c => c.Id).ToArray());
        Assert.Equal(new[] { "lianli:port0", "lianli:port2" },
            Assert.Single(provider.GetAll()).Devices.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Surfaces_no_ports_when_all_empty()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => { for (var p = 0; p < 4; p++) s.Devices.LianLi.SetFans(p, 0); });
        var provider = Connected(store);

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(Assert.Single(provider.GetAll()).Devices);
    }

    [Fact]
    public void Surfaces_all_four_ports_by_default()
    {
        var provider = Connected(new InMemoryConfigStore());

        Assert.Equal(4, provider.GetFanChannels().Count);
        Assert.Equal(4, Assert.Single(provider.GetAll()).Devices.Count);
    }

    [Fact]
    public void Labels_ports_one_based_like_the_hub()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => { for (var p = 0; p < 4; p++) s.Devices.LianLi.SetFans(p, p == 1 ? 3 : 0); });
        var provider = Connected(store);

        var channel = Assert.Single(provider.GetFanChannels());
        Assert.Equal("lianli:port1", channel.Id);
        Assert.Equal("Port 2", channel.PortLabel);
        Assert.EndsWith(" Port 2", channel.Name);
        Assert.EndsWith(" Port 2", Assert.Single(Assert.Single(provider.GetAll()).Devices).Name);
    }
}
