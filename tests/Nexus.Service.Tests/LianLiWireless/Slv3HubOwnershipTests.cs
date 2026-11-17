using System;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private static (Slv3Hub Hub, FakeSlv3Network Net) ConfirmingHub()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        return (hub, net);
    }

    [Fact]
    public void Ownership_is_unsettled_until_a_new_chain_has_been_confirmed()
    {
        var (hub, net) = ConfirmingHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 4, DevType = 10 });

        Ticks(hub, Slv3Hub.ChainConfirmSightings - 1);
        Assert.False(hub.OwnershipSettled);
        Assert.Empty(hub.State.Fans);

        Ticks(hub, 1);
        Assert.Single(hub.State.Fans);
        Assert.True(hub.OwnershipSettled);
    }

    [Fact]
    public void Zero_device_replies_do_not_settle_ownership_before_the_confirm_window_has_run()
    {
        var (hub, _) = ConfirmingHub();

        Ticks(hub, Slv3Hub.ChainConfirmWindowPolls - 1);
        Assert.False(hub.OwnershipSettled);

        Ticks(hub, 1);
        Assert.True(hub.OwnershipSettled);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_half_enumerated_dongle_does_not_settle_ownership(bool txPresent, bool rxPresent)
    {
        var discovery = new ToggleDiscovery { TxPresent = txPresent, RxPresent = rxPresent };
        var hub = new Slv3Hub(discovery, _ => new SilentTransport(Slv3DongleRole.Tx));

        Assert.False(hub.EnsureConnected());
        hub.NoteConnectFailed();

        Assert.False(hub.OwnershipSettled);
    }

    [Fact]
    public void An_absent_dongle_settles_ownership()
    {
        var discovery = new ToggleDiscovery { TxPresent = false, RxPresent = false };
        var hub = new Slv3Hub(discovery, _ => new SilentTransport(Slv3DongleRole.Tx));

        Assert.False(hub.EnsureConnected());
        hub.NoteConnectFailed();

        Assert.True(hub.OwnershipSettled);
    }

    [Fact]
    public void A_switched_off_link_settles_ownership()
    {
        var hub = new Slv3Hub(new ToggleDiscovery(), _ => new SilentTransport(Slv3DongleRole.Tx));
        Assert.False(hub.OwnershipSettled);

        hub.NoteLinkDisabled();

        Assert.True(hub.OwnershipSettled);
    }

    [Fact]
    public void A_disconnect_makes_ownership_unsettled_again()
    {
        var (hub, _) = ConfirmingHub();
        Ticks(hub, Slv3Hub.ChainConfirmWindowPolls);
        Assert.True(hub.OwnershipSettled);

        hub.Disconnect();

        Assert.False(hub.OwnershipSettled);
    }
}
