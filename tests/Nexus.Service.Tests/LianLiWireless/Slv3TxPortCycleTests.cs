using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private sealed class FakeTxPortCycler : ISlv3TxPortCycler
    {
        public bool Refuse { get; set; }
        public int Resolves { get; private set; }
        public List<string> Cycled { get; } = new();

        public bool TryResolve(Slv3PortInfo txPort, out string instanceId, out string target)
        {
            Resolves++;
            instanceId = @"USB\VID_0416&PID_8040\a&1&0&3";
            target = "fake-hub port 3";
            if (Refuse)
            {
                target = "not a 0416:8040 device";
                return false;
            }
            return true;
        }

        public bool TryCycle(string instanceId, out string detail)
        {
            Cycled.Add(instanceId);
            detail = "ok";
            return true;
        }
    }

    // A TX the RX reset never revives, driven through connect attempts one reset window apart.
    private (Slv3Hub Hub, ManualClock Clock, FakeSlv3Network Net, FakeTxPortCycler Cycler, ToggleDiscovery Discovery) HungTxHub(bool answersWithZeroMac = false, Action<Action>? run = null, bool displayOn = true)
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network { TxWedged = !answersWithZeroMac, TxIgnoresReset = true };
        var rx = new FakeRxTransport(net);
        var discovery = new ToggleDiscovery();
        var cycler = new FakeTxPortCycler();
        var hub = new Slv3Hub(discovery, port => port.Role == Slv3DongleRole.Tx ? new FakeTxTransport(net) { MasterChannel = answersWithZeroMac ? (byte)250 : Slv3Protocol.DefaultChannel } : rx, clock.NowMs)
        {
            ConfirmNewChains = false,
            RunPortCycle = run ?? (work => work()),
            TxPortCycler = cycler,
        };
        if (displayOn)
        {
            hub.OnDisplayState(1);
        }
        return (hub, clock, net, cycler, discovery);
    }

    private static void Attempts(Slv3Hub hub, ManualClock clock, int count, long gapMs = 65_000)
    {
        for (var i = 0; i < count; i++)
        {
            clock.AdvanceMs(gapMs);
            hub.EnsureConnected();
        }
    }

    [Fact]
    public void A_sustained_hang_cycles_the_tx_port_once_the_threshold_is_met()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        Attempts(hub, clock, 4);
        Assert.Empty(cycler.Cycled);

        Attempts(hub, clock, 20);
        Assert.NotEmpty(cycler.Cycled);
    }

    [Fact]
    public void A_transient_never_qualifies_for_a_port_cycle()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        // Fewer resets than the threshold never qualify, however long they take.
        Attempts(hub, clock, 4, 400_000);
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void Resets_that_come_faster_than_the_minimum_hang_time_do_not_qualify()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        // The reset backoff is short, so the threshold count is reached inside the minimum hang time.
        var start = clock.NowMs();
        Attempts(hub, clock, 12, 10_000);
        Assert.True(clock.NowMs() - start < Slv3Hub.TxHangMinMsBeforePortCycle);
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void A_refused_identity_is_not_cycled_and_uses_no_cap_slot()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        cycler.Refuse = true;

        Attempts(hub, clock, 400);

        Assert.Empty(cycler.Cycled);
        Assert.True(cycler.Resolves > Slv3Hub.MaxPortCyclesPerWindow);

        cycler.Refuse = false;
        Attempts(hub, clock, 400);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow, cycler.Cycled.Count);
    }

    [Fact]
    public void Cycles_are_spaced_and_capped_per_episode()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        Attempts(hub, clock, 400);

        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow, cycler.Cycled.Count);
    }

    [Fact]
    public void A_cycle_waits_out_the_spacing_before_the_next()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        Attempts(hub, clock, 30);
        var first = cycler.Cycled.Count;
        Assert.Equal(1, first);
        var firstAt = clock.NowMs();

        while (cycler.Cycled.Count == first)
        {
            Attempts(hub, clock, 1);
        }
        Assert.True(clock.NowMs() - firstAt >= Slv3Hub.PortCycleSpacingMs - 65_000);
    }

    [Fact]
    public void An_answer_does_not_restore_the_cycle_budget()
    {
        var (hub, clock, net, cycler, _) = HungTxHub();

        Attempts(hub, clock, 400);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow, cycler.Cycled.Count);

        net.TxWedged = false;
        clock.AdvanceMs(65_000);
        Assert.True(hub.EnsureConnected());

        hub.Disconnect();
        net.TxWedged = true;
        Attempts(hub, clock, 400);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow, cycler.Cycled.Count);
    }

    [Fact]
    public void The_cycle_budget_returns_after_the_rolling_window()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        Attempts(hub, clock, 400);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerWindow, cycler.Cycled.Count);

        clock.AdvanceMs(Slv3Hub.PortCycleWindowMs);
        Attempts(hub, clock, 400);
        Assert.True(cycler.Cycled.Count > Slv3Hub.MaxPortCyclesPerWindow);
    }

    [Fact]
    public void A_tx_that_answers_with_no_master_is_never_cycled()
    {
        var (hub, clock, _, cycler, _) = HungTxHub(answersWithZeroMac: true);

        Attempts(hub, clock, 400);

        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void An_unknown_display_state_never_cycles()
    {
        var (hub, clock, _, cycler, _) = HungTxHub(displayOn: false);

        Attempts(hub, clock, 400);

        Assert.Empty(cycler.Cycled);
        Assert.Equal(0, cycler.Resolves);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_display_that_is_off_or_dimmed_holds_cycling(int state)
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        Attempts(hub, clock, 6);
        hub.OnDisplayState(state);

        Attempts(hub, clock, 100);

        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void Cycling_returns_only_after_the_quiet_period_once_the_display_is_on()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        Attempts(hub, clock, 6);
        hub.OnDisplayState(0);
        Attempts(hub, clock, 100);

        hub.OnDisplayState(1);
        Attempts(hub, clock, 1);
        Assert.Empty(cycler.Cycled);

        Attempts(hub, clock, 5);
        Assert.NotEmpty(cycler.Cycled);
    }

    [Fact]
    public void No_cycle_while_the_tx_is_absent()
    {
        var (hub, clock, _, cycler, discovery) = HungTxHub();
        Attempts(hub, clock, 3);
        discovery.TxPresent = false;

        Attempts(hub, clock, 100);

        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void A_tx_that_left_the_bus_restarts_the_hang_count()
    {
        var (hub, clock, _, cycler, discovery) = HungTxHub();
        Attempts(hub, clock, 6);
        discovery.TxPresent = false;
        Attempts(hub, clock, 1);
        discovery.TxPresent = true;

        Attempts(hub, clock, 3);
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void No_cycle_without_a_cycler()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        hub.TxPortCycler = null;

        Attempts(hub, clock, 400);

        Assert.Empty(cycler.Cycled);
        Assert.Equal(0, cycler.Resolves);
    }

    [Fact]
    public void No_cycle_around_suspend_and_resume_and_cycling_returns_afterwards()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        _ = hub.OnSystemSuspending();
        Attempts(hub, clock, 100);
        Assert.Empty(cycler.Cycled);

        hub.OnSystemResumed();
        Attempts(hub, clock, 1, Slv3Hub.PortCycleResumeQuietMs / 2);
        Assert.Empty(cycler.Cycled);

        Attempts(hub, clock, 30);
        Assert.NotEmpty(cycler.Cycled);
    }

    [Fact]
    public void A_cycle_pending_when_the_hub_is_disposed_is_not_run()
    {
        Action? pending = null;
        var (hub, clock, _, cycler, _) = HungTxHub(run: work => pending = work);

        Attempts(hub, clock, 30);
        Assert.NotNull(pending);

        hub.Dispose();
        pending!();

        Assert.Empty(cycler.Cycled);
    }

    [Theory]
    [InlineData(@"USB\VID_0416&PID_8040\a&717997b&0&3", true)]
    [InlineData(@"usb\vid_0416&pid_8040\5&2c780ec&0&3", true)]
    [InlineData(@"USB\VID_0416&PID_8041\a&717997b&0&2", false)]
    [InlineData(@"USB\VID_1A86&PID_8091\a&717997b&0&1", false)]
    [InlineData(@"USB\VID_0416&PID_8040&MI_00\a&1&0&0000", false)]
    [InlineData(@"USB\VID_0416&PID_8040\", false)]
    [InlineData(@"USB\VID_0416&PID_8040\a\b", false)]
    [InlineData(@"USB\VID_0416&PID_80401\a&1&0&3", false)]
    [InlineData(@"HID\VID_0416&PID_8040\a&1&0&3", false)]
    [InlineData("", false)]
    public void Only_the_plain_tx_devnode_passes_the_identity_check(string instanceId, bool expected) =>
        Assert.Equal(expected, Slv3TxPortIdentity.IsTxInstanceId(instanceId));

    [Theory]
    [InlineData(@"USB\ROOT_HUB30\4&1&0", true)]
    [InlineData(@"USB\VID_1A86&PID_8091\6&1&0&13", true)]
    [InlineData(@"PCI\VEN_8086&DEV_1\3&1", false)]
    public void The_parent_must_be_a_usb_hub_devnode(string instanceId, bool expected) =>
        Assert.Equal(expected, Slv3TxPortIdentity.IsUsbEnumeratedInstanceId(instanceId));
}
