using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private (Slv3Hub Hub, ManualClock Clock, FakeSlv3Network Net, FakeRxTransport Rx, FakeTxPortCycler Cycler) RestartHub(bool withCycler = true)
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network();
        var rx = new FakeRxTransport(net);
        var cycler = new FakeTxPortCycler();
        var hub = new Slv3Hub(new ToggleDiscovery(), port => port.Role == Slv3DongleRole.Tx ? new FakeTxTransport(net) : rx, clock.NowMs)
        {
            ConfirmNewChains = false,
            RunPortCycle = work => work(),
            TxPortCycler = withCycler ? cycler : null,
        };
        Assert.True(hub.EnsureConnected());
        Assert.True(hub.DriveTick());
        return (hub, clock, net, rx, cycler);
    }

    [Fact]
    public void A_restart_resets_the_tx_via_the_rx_and_skips_the_port_cycle_when_the_tx_answers()
    {
        var (hub, clock, _, rx, cycler) = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
        Assert.Equal(1, ResetFrames(rx));

        clock.AdvanceMs(Slv3Hub.ManualRestartSettleMs + 1_000);
        Assert.True(hub.DriveTick());
        clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs);
        Assert.True(hub.DriveTick());

        Assert.Empty(cycler.Cycled);
        Assert.Equal(0, cycler.Resolves);
    }

    [Fact]
    public void A_tx_that_stays_silent_after_the_reset_gets_its_port_cycled()
    {
        var (hub, clock, net, rx, cycler) = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
        net.TxWedged = true;
        net.TxIgnoresReset = true;
        clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
        Assert.True(hub.DriveTick());

        Assert.Single(cycler.Cycled);
        Assert.Equal(1, ResetFrames(rx));
    }

    [Fact]
    public void A_manual_restart_does_not_use_the_automatic_cap_or_spacing()
    {
        var (hub, clock, net, _, cycler) = RestartHub();
        net.TxWedged = true;
        net.TxIgnoresReset = true;

        for (var i = 0; i < Slv3Hub.MaxPortCyclesPerWindow + 2; i++)
        {
            Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
            clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
            hub.DriveTick();
            clock.AdvanceMs(Slv3Hub.ManualRestartCooldownMs);
        }

        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow + 2, cycler.Cycled.Count);
    }

    [Fact]
    public void A_second_request_inside_the_cooldown_or_while_one_runs_is_busy()
    {
        var (hub, clock, _, rx, _) = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
        Assert.Equal(Slv3RestartResult.Busy, hub.RestartController());
        clock.AdvanceMs(Slv3Hub.ManualRestartCooldownMs + 1);
        Assert.Equal(Slv3RestartResult.Busy, hub.RestartController());
        Assert.Equal(1, ResetFrames(rx));

        clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs);
        Assert.True(hub.DriveTick());
        Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
    }

    [Fact]
    public void A_restart_is_held_back_around_suspend_and_resume_but_not_by_the_display()
    {
        var (hub, _, _, rx, _) = RestartHub();

        hub.OnSystemResumed();
        Assert.Equal(Slv3RestartResult.HeldBack, hub.RestartController());
        Assert.Equal(0, ResetFrames(rx));

        var (hub2, _, _, rx2, _) = RestartHub();
        hub2.OnDisplayState(Slv3Hub.DisplayOff);
        Assert.Equal(Slv3RestartResult.Started, hub2.RestartController());
        Assert.Equal(1, ResetFrames(rx2));
    }

    [Fact]
    public void Without_a_cycler_the_restart_is_the_rx_reset_alone()
    {
        var (hub, clock, net, rx, cycler) = RestartHub(withCycler: false);

        Assert.Equal(Slv3RestartResult.Started, hub.RestartController());
        net.TxWedged = true;
        clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
        Assert.True(hub.DriveTick());

        Assert.Equal(1, ResetFrames(rx));
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void A_closed_rx_with_no_cycler_is_unavailable_and_with_one_cycles_the_port_directly()
    {
        var (hub, _, _, rx, _) = RestartHub(withCycler: false);
        rx.Dead = true;
        Assert.Equal(Slv3RestartResult.Unavailable, hub.RestartController());

        var (hub2, clock2, _, rx2, cycler2) = RestartHub();
        rx2.Dead = true;
        Assert.Equal(Slv3RestartResult.Started, hub2.RestartController());
        clock2.AdvanceMs(1);
        hub2.EnsureConnected();
        hub2.DriveTick();
        Assert.Single(cycler2.Cycled);
            }
}
