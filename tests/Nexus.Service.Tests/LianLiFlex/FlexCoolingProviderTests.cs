using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexCoolingProviderTests
{
    private static (FlexCoolingProvider Provider, FlexHub Hub, FakeFlexTransport Tx, FlexClock Clock) Create(FakeFlexTransport? tx = null)
    {
        tx ??= new FakeFlexTransport();
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, null, _ => { });
        hub.Refresh();
        return (new FlexCoolingProvider(hub), hub, tx, clock);
    }

    [Fact]
    public void IsFlexId_matches_only_the_flex_prefix()
    {
        Assert.True(FlexCoolingProvider.IsFlexId("lianli-flex:6D1007F517E1:port0"));
        Assert.False(FlexCoolingProvider.IsFlexId("lianli-wireless:6D1007F517E1:port0"));
    }

    [Fact]
    public void One_channel_per_unit_with_rpm_floor_and_manual_mode()
    {
        var (provider, _, _, _) = Create(new FakeFlexTransport(pwm: 0x3B));
        var ch = Assert.Single(provider.GetFanChannels());
        Assert.Equal("lianli-flex:6D1007F517E1:port0", ch.Id);
        Assert.Equal(661, ch.Rpm);
        Assert.Equal(11, ch.MinDuty);
        Assert.Equal(FanModes.Manual, ch.Mode);
        Assert.Equal(23, ch.DutyPercent);
    }

    [Fact]
    public void A_receiver_echoing_the_follow_value_reads_as_auto()
    {
        var (provider, _, _, _) = Create(new FakeFlexTransport(pwm: FlexProtocol.PwmFollowMotherboard));
        Assert.Equal(FanModes.Auto, provider.GetFanChannels().Single().Mode);
    }

    [Fact]
    public void Set_and_release_drive_the_hub_duty()
    {
        var (provider, hub, tx, clock) = Create();
        provider.SetFanSpeed("lianli-flex:6D1007F517E1:port0", 60);
        clock.Now += 1000;
        hub.Tick();
        Assert.Equal(153, tx.Of(FlexProtocol.CmdSetPwm).Single()[1]);
        provider.ReleaseFan("lianli-flex:6D1007F517E1:port0");
        clock.Now += 1000;
        hub.Tick();
        Assert.Equal(6, tx.Of(FlexProtocol.CmdSetPwm).Last()[1]);
    }

    [Fact]
    public void An_rf_bound_unit_exposes_no_channel()
    {
        var (provider, _, _, _) = Create(new FakeFlexTransport(rxType: 4));
        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public void Unknown_ids_are_ignored()
    {
        var (provider, hub, tx, clock) = Create();
        provider.SetFanSpeed("lianli-flex:nonsense", 50);
        provider.SetFanSpeed("lianli-wireless:6D1007F517E1:port0", 50);
        clock.Now += 1000;
        hub.Tick();
        Assert.Empty(tx.Of(FlexProtocol.CmdSetPwm));
    }
}
