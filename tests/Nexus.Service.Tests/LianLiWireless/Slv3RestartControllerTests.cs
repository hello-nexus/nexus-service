using System.Collections.Generic;
using System.Reflection;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private sealed class RestartRig
    {
        public required Slv3Hub Hub { get; init; }
        public required ManualClock Clock { get; init; }
        public required FakeSlv3Network Net { get; init; }
        public required FakeTxPortCycler Cycler { get; init; }
        public required List<FakeRxTransport> Rxs { get; init; }
        public required List<FakeTxTransport> Txs { get; init; }

        public int RxResets => Rxs.Sum(ResetFrames);
    }

    private static RestartRig RestartHub(bool withCycler = true, bool connect = true)
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network();
        var rxs = new List<FakeRxTransport>();
        var txs = new List<FakeTxTransport>();
        var cycler = new FakeTxPortCycler();
        var hub = new Slv3Hub(new ToggleDiscovery(), port =>
        {
            if (port.Role == Slv3DongleRole.Tx)
            {
                var tx = new FakeTxTransport(net);
                txs.Add(tx);
                return tx;
            }
            var rx = new FakeRxTransport(net);
            rxs.Add(rx);
            return rx;
        }, clock.NowMs)
        {
            ConfirmNewChains = false,
            RunPortCycle = work => work(),
            TxPortCycler = withCycler ? cycler : null,
        };
        if (connect)
        {
            Assert.True(hub.EnsureConnected());
            Assert.True(hub.PollTick());
        }
        return new RestartRig { Hub = hub, Clock = clock, Net = net, Cycler = cycler, Rxs = rxs, Txs = txs };
    }

    private static T Field<T>(Slv3Hub hub, string name) =>
        (T)typeof(Slv3Hub).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(hub)!;

    [Fact]
    public void A_restart_resets_the_tx_via_the_rx_and_skips_the_port_cycle_when_the_tx_answers()
    {
        var r = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        Assert.Equal(1, r.RxResets);

        r.Clock.AdvanceMs(Slv3Hub.ManualRestartSettleMs + 1_000);
        Assert.True(r.Hub.DriveTick());
        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs);
        Assert.True(r.Hub.DriveTick());

        Assert.Empty(r.Cycler.Cycled);
        Assert.Equal(0, r.Cycler.Resolves);
    }

    [Fact]
    public void A_tx_that_stays_silent_after_the_reset_gets_its_port_cycled()
    {
        var r = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        r.Net.TxWedged = true;
        r.Net.TxIgnoresReset = true;
        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
        Assert.True(r.Hub.PollTick());

        Assert.Single(r.Cycler.Cycled);
        Assert.Equal(1, r.RxResets);
    }

    [Fact]
    public void A_manual_restart_does_not_use_the_automatic_cap_or_spacing()
    {
        var r = RestartHub();
        r.Net.TxWedged = true;
        r.Net.TxIgnoresReset = true;

        for (var i = 0; i < Slv3Hub.MaxPortCyclesPerWindow + 2; i++)
        {
            Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
            r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
            r.Hub.DriveTick();
            r.Clock.AdvanceMs(Slv3Hub.ManualRestartCooldownMs);
        }

        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow + 2, r.Cycler.Cycled.Count);
    }

    [Fact]
    public void A_manual_restart_leaves_the_automatic_episode_state_alone()
    {
        var r = RestartHub();
        var recoveries = Field<int>(r.Hub, "_txRecoveries");
        var hungResets = Field<int>(r.Hub, "_txHungResets");
        var nextCycle = Field<long>(r.Hub, "_nextPortCycleMs");
        var cycles = Field<int>(r.Hub, "_portCycles");

        r.Net.TxWedged = true;
        r.Net.TxIgnoresReset = true;
        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
        r.Hub.DriveTick();

        Assert.Single(r.Cycler.Cycled);
        Assert.Equal(recoveries, Field<int>(r.Hub, "_txRecoveries"));
        Assert.Equal(hungResets, Field<int>(r.Hub, "_txHungResets"));
        Assert.Equal(nextCycle, Field<long>(r.Hub, "_nextPortCycleMs"));
        Assert.Equal(cycles, Field<int>(r.Hub, "_portCycles"));
    }

    [Fact]
    public void A_tx_that_drops_its_handle_after_the_reset_is_not_reset_a_second_time()
    {
        var r = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        r.Txs[0].Dead = true;
        for (var i = 0; i < 12; i++)
        {
            r.Clock.AdvanceMs(Slv3ConnectionWorker.PollPeriodMs);
            r.Hub.PollTick();
        }

        Assert.Equal(1, r.RxResets);
        Assert.Equal(0, Field<int>(r.Hub, "_txRecoveries"));
        Assert.Equal(0, Field<int>(r.Hub, "_txHungResets"));
    }

    [Fact]
    public void A_second_request_inside_the_cooldown_or_while_one_runs_is_busy()
    {
        var r = RestartHub();

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        Assert.Equal(Slv3RestartResult.Busy, r.Hub.RestartController());
        r.Clock.AdvanceMs(Slv3Hub.ManualRestartCooldownMs + 1);
        Assert.Equal(Slv3RestartResult.Busy, r.Hub.RestartController());
        Assert.Equal(1, r.RxResets);

        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs);
        Assert.True(r.Hub.DriveTick());
        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
    }

    [Fact]
    public void A_restart_is_held_back_around_suspend_and_resume_but_not_by_the_display()
    {
        var r = RestartHub();

        r.Hub.OnSystemResumed();
        Assert.Equal(Slv3RestartResult.HeldBack, r.Hub.RestartController());
        Assert.Equal(0, r.RxResets);

        var r2 = RestartHub();
        r2.Hub.OnDisplayState(Slv3Hub.DisplayOff);
        Assert.Equal(Slv3RestartResult.Started, r2.Hub.RestartController());
        Assert.Equal(1, r2.RxResets);
    }

    [Fact]
    public void Without_a_cycler_the_restart_is_the_rx_reset_alone()
    {
        var r = RestartHub(withCycler: false);

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        r.Net.TxWedged = true;
        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + 1);
        Assert.True(r.Hub.PollTick());

        Assert.Equal(1, r.RxResets);
        Assert.Empty(r.Cycler.Cycled);
    }

    [Fact]
    public void A_restart_from_a_link_that_never_connected_opens_the_rx_for_the_reset()
    {
        var r = RestartHub(connect: false);
        Assert.False(r.Hub.IsConnected);

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());

        Assert.Equal(1, r.RxResets);
        Assert.Empty(r.Cycler.Cycled);
    }

    [Fact]
    public void A_restart_from_a_link_that_never_connected_is_the_rx_reset_alone_without_a_cycler()
    {
        var r = RestartHub(withCycler: false, connect: false);

        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        Assert.Equal(1, r.RxResets);
    }

    [Fact]
    public void A_restart_with_no_rx_to_open_and_no_cycler_is_unavailable_and_with_one_cycles_directly()
    {
        var r = RestartHub(withCycler: false, connect: false);
        // Discovery is the only source of an RX to open; without one the reset cannot go out.
        var noRx = new Slv3Hub(new ToggleDiscovery { RxPresent = false }, _ => new FakeTxTransport(new FakeSlv3Network()), r.Clock.NowMs);
        Assert.Equal(Slv3RestartResult.Unavailable, noRx.RestartController());

        var discovery = new ToggleDiscovery { RxPresent = false };
        var cycler = new FakeTxPortCycler();
        var net = new FakeSlv3Network();
        var clock = new ManualClock();
        var cycling = new Slv3Hub(discovery, _ => new FakeTxTransport(net), clock.NowMs)
        {
            RunPortCycle = work => work(),
            TxPortCycler = cycler,
        };
        Assert.Equal(Slv3RestartResult.Started, cycling.RestartController());
        clock.AdvanceMs(1);
        cycling.PollTick();
        Assert.Single(cycler.Cycled);
    }

    [Fact]
    public void A_restart_is_refused_while_the_control_gate_is_off()
    {
        var r = RestartHub();
        r.Hub.ControlEnabled = () => false;

        Assert.Equal(Slv3RestartResult.Unavailable, r.Hub.RestartController());

        Assert.Equal(0, r.RxResets);
    }

    [Fact]
    public void A_pending_escalation_no_tick_serviced_expires_instead_of_cycling_later()
    {
        var r = RestartHub();
        r.Net.TxWedged = true;
        r.Net.TxIgnoresReset = true;
        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());

        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + Slv3Hub.ManualRestartExpiryMs + 1);
        Assert.Equal(Slv3RestartResult.Started, r.Hub.RestartController());
        Assert.Empty(r.Cycler.Cycled);

        r.Clock.AdvanceMs(Slv3Hub.ManualRestartWaitMs + Slv3Hub.ManualRestartExpiryMs + 1);
        r.Hub.PollTick();
        Assert.Empty(r.Cycler.Cycled);
    }
}
