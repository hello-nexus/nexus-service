using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.LianLi;
using Xunit;

namespace Nexus.Service.Tests.LianLi;

public class LianLiCoolingProviderTests
{
    private static LianLiCoolingProvider Connected(InMemoryConfigStore store) => Connected(store, out _, out _);

    private static LianLiCoolingProvider Connected(InMemoryConfigStore store, out LianLiHub hub, out HubTransportSpy spy)
    {
        hub = new LianLiHub();
        spy = new HubTransportSpy();
        LianLiFanProfiles.TryGet(0xA102, out var profile);
        hub.Attach(spy, profile);
        return new LianLiCoolingProvider(hub, store);
    }

    // Manual mode for port 0 is E0 10 62 10.
    private static bool IsManualMode(HubTransportSpy.Call c) => c.Bytes[1] == 0x10 && c.Bytes[2] == 0x62 && c.Bytes[3] == 0x10;

    [Fact]
    public void Reassert_refreshes_duty_only_until_a_resume_then_reenters_manual_mode_once()
    {
        var provider = Connected(new InMemoryConfigStore(), out var hub, out var spy);
        provider.SetFanSpeed("lianli:port0", 40);
        spy.Calls.Clear();

        provider.ReassertControl();
        Assert.DoesNotContain(spy.Calls, IsManualMode);

        hub.OnSystemResumed();
        provider.ReassertControl();
        Assert.Single(spy.Calls, IsManualMode);

        spy.Calls.Clear();
        provider.ReassertControl();
        Assert.DoesNotContain(spy.Calls, IsManualMode);
    }

    [Fact]
    public void A_failed_reentry_after_resume_is_retried_on_the_next_reassert()
    {
        var provider = Connected(new InMemoryConfigStore(), out var hub, out var spy);
        provider.SetFanSpeed("lianli:port0", 40);
        hub.OnSystemResumed();
        spy.RejectWrites = true;
        provider.ReassertControl();

        spy.RejectWrites = false;
        spy.Calls.Clear();
        provider.ReassertControl();

        Assert.Single(spy.Calls, IsManualMode);
    }

    [Fact]
    public void A_resume_with_no_port_under_control_is_consumed()
    {
        var provider = Connected(new InMemoryConfigStore(), out var hub, out var spy);
        hub.OnSystemResumed();
        provider.ReassertControl();
        provider.SetFanSpeed("lianli:port0", 40);
        spy.Calls.Clear();

        provider.ReassertControl();

        Assert.DoesNotContain(spy.Calls, IsManualMode);
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
