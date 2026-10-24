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
    private (Slv3Hub Hub, ManualClock Clock, FakeSlv3Network Net, FakeTxPortCycler Cycler, ToggleDiscovery Discovery) HungTxHub()
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network { TxWedged = true, TxIgnoresReset = true };
        var rx = new FakeRxTransport(net);
        var discovery = new ToggleDiscovery();
        var cycler = new FakeTxPortCycler();
        var hub = new Slv3Hub(discovery, port => port.Role == Slv3DongleRole.Tx ? new FakeTxTransport(net) : rx, clock.NowMs)
        {
            ConfirmNewChains = false,
            RunPortCycle = work => work(),
            TxPortCycler = cycler,
        };
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

        // Four resets are below the reset threshold however long they take.
        Attempts(hub, clock, 4, 400_000);
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void Resets_that_come_faster_than_the_minimum_hang_time_do_not_qualify()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        // Reset backoff tops out at 60 s, so the fifth reset lands well inside the five-minute floor.
        Attempts(hub, clock, 12, 10_000);
        Assert.True(clock.NowMs() - 1_000_000 < Slv3Hub.TxHangMinMsBeforePortCycle);
        Assert.Empty(cycler.Cycled);
    }

    [Fact]
    public void An_identity_the_cycler_refuses_is_not_cycled_and_counts_toward_the_cap()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        cycler.Refuse = true;

        Attempts(hub, clock, 400);

        Assert.Empty(cycler.Cycled);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerEpisode, cycler.Resolves);
    }

    [Fact]
    public void Cycles_are_spaced_and_capped_per_episode()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();

        Attempts(hub, clock, 400);

        Assert.Equal(Slv3Hub.MaxPortCyclesPerEpisode, cycler.Cycled.Count);
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
    public void An_answering_tx_ends_the_episode_and_restores_the_cycle_budget()
    {
        var (hub, clock, net, cycler, _) = HungTxHub();

        Attempts(hub, clock, 400);
        Assert.Equal(Slv3Hub.MaxPortCyclesPerEpisode, cycler.Cycled.Count);

        net.TxWedged = false;
        clock.AdvanceMs(65_000);
        Assert.True(hub.EnsureConnected());

        hub.Disconnect();
        net.TxWedged = true;
        var before = cycler.Cycled.Count;
        Attempts(hub, clock, 400);
        Assert.Equal(before + Slv3Hub.MaxPortCyclesPerEpisode, cycler.Cycled.Count);
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
    public void No_cycle_around_suspend_and_resume_or_after_dispose()
    {
        var (hub, clock, _, cycler, _) = HungTxHub();
        _ = hub.OnSystemSuspending();
        Attempts(hub, clock, 100);
        Assert.Empty(cycler.Cycled);

        hub.OnSystemResumed();
        Attempts(hub, clock, 1, Slv3Hub.PortCycleResumeQuietMs - 1);
        Assert.Empty(cycler.Cycled);

        var (disposed, clock2, _, cycler2, _) = HungTxHub();
        Attempts(disposed, clock2, 3);
        disposed.Dispose();
        Attempts(disposed, clock2, 100);
        Assert.Empty(cycler2.Cycled);
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
        Assert.Equal(expected, Slv3TxPortIdentity.IsUsbHubInstanceId(instanceId));
}
