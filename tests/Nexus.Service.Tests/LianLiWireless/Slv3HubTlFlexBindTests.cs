using System;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public partial class Slv3HubTests
{
    private static void MarkWired(Slv3Hub hub)
    {
        var mac = FanKey;
        hub.WiredOwned = m => string.Equals(m, mac, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unbound_USB_connected_unit_is_bound_to_the_radio_and_then_driven_by_it()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        MarkWired(hub);
        Ticks(hub, 3);
        Assert.True(hub.State.Fans[0].WiredOwned);

        Assert.True(ManualBind(hub, FanKey, out var error), error);
        Ticks(hub, 3);

        var fan = hub.State.Fans[0];
        Assert.True(fan.BoundToUs);
        Assert.True(fan.RadioDrivesUsb);
        Assert.False(fan.WiredOwned);
        Assert.Equal("", fan.Pending);
    }

    [Fact]
    public void An_unbind_stays_pending_while_the_chain_has_not_echoed_it_and_clears_on_the_echo()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Ticks(hub, 1);
        Assert.True(ManualBind(hub, FanKey));
        Ticks(hub, 1);
        Assert.True(hub.State.Fans[0].BoundToUs);

        net.Fans.Clear();
        Assert.True(ManualUnbind(hub, FanKey));
        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(Slv3Hub.PendingUnbinding, hub.State.Fans[0].Pending);

        net.Fans.Add(fan);
        Ticks(hub, 3);
        Assert.False(hub.State.Fans[0].BoundToUs);
        Assert.Equal("", hub.State.Fans[0].Pending);
    }

    [Fact]
    public void A_pending_bind_is_reported_until_the_timeout_when_the_chain_never_answers()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Ticks(hub, Slv3Hub.ChainConfirmSightings + 1);

        Assert.True(ManualBind(hub, FanKey));
        // The chain went quiet before echoing: the record still shows it unbound.
        net.Fans.Clear();
        Ticks(hub, 1);
        Assert.Equal(Slv3Hub.PendingBinding, hub.State.Fans[0].Pending);

        clock.AdvanceMs(Slv3Hub.PendingOpTimeoutMs + 1);
        Ticks(hub, 1);
        // The silent chain may also have been dropped by now; either way nothing is reported pending.
        Assert.All(hub.State.Fans, f => Assert.Equal("", f.Pending));
    }

    [Fact]
    public void Unbinding_a_chain_clears_its_identify_and_drops_the_pending_select()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Ticks(hub, 1);
        Assert.True(ManualBind(hub, FanKey));
        Ticks(hub, 1);
        var released = new List<string>();
        hub.ChainReleased = released.Add;
        Assert.True(hub.Identify(FanKey));
        Assert.True(hub.HasOtherPendingCommand(FanKey, 0xFF));

        Assert.True(ManualUnbind(hub, FanKey));

        Assert.Equal(new[] { FanKey }, released);
        Assert.False(hub.HasOtherPendingCommand(FanKey, 0xFF));
    }

    [Fact]
    public void A_unit_that_turns_USB_owned_while_unbound_clears_its_identify_once()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        var released = new List<string>();
        hub.ChainReleased = released.Add;
        Ticks(hub, 2);
        Assert.Empty(released);

        MarkWired(hub);
        Ticks(hub, 3);

        Assert.Equal(new[] { FanKey }, released);
    }

    [Fact]
    public void The_identify_tracker_ends_the_flash_of_every_zone_of_a_chain_and_leaves_others()
    {
        var tracker = new Np50IdentifyTracker();
        tracker.Schedule("lianli-wireless:AA", 60_000);
        tracker.Schedule("lianli-wireless:AA:zone2", 60_000);
        tracker.Schedule("lianli-wireless:BB", 60_000);
        var now = DateTime.UtcNow.Ticks;

        tracker.ClearPrefix("lianli-wireless:AA");

        Assert.False(tracker.TryGetActive("lianli-wireless:AA", now, out _));
        Assert.False(tracker.TryGetActive("lianli-wireless:AA:zone2", now, out _));
        Assert.True(tracker.TryGetActive("lianli-wireless:BB", now, out _));
    }

    [Fact]
    public void Reconcile_never_sends_a_bind_frame_to_a_USB_connected_unit_nobody_asked_to_bind()
    {
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, RxType = 254 });
        MarkWired(hub);
        tx.SentFrames.Clear();

        Ticks(hub, 6);

        Assert.Equal(0, CountBindFrames(tx));
        Assert.False(hub.State.Fans[0].BoundToUs);
    }

    [Fact]
    public void A_bind_of_a_USB_unit_that_does_not_answer_is_paced_unsaved_and_given_up_at_the_timeout()
    {
        var (hub, net, tx, saved, clock) = CreateOwnershipHub();
        var fan = new SimulatedFan { Mac = FanMac, RxType = 254 };
        net.Fans.Add(fan);
        Ticks(hub, Slv3Hub.ChainConfirmSightings + 1);
        MarkWired(hub);
        Assert.True(ManualBind(hub, FanKey));
        net.Fans.Clear();
        tx.SentFrames.Clear();

        Ticks(hub, 10);
        Assert.Equal(0, CountBindFrames(tx));
        Assert.DoesNotContain(FanKey, saved.Keys);

        clock.AdvanceMs(Slv3Hub.WiredBindResendMs + 1);
        Ticks(hub, 5);
        Assert.Equal(1, CountBindFrames(tx));

        clock.AdvanceMs(Slv3Hub.PendingOpTimeoutMs);
        tx.SentFrames.Clear();
        Ticks(hub, 5);
        Assert.Equal(0, CountBindFrames(tx));
        Assert.DoesNotContain(FanKey, saved.Keys);
    }

    [Fact]
    public void A_USB_unit_joins_the_saved_list_when_its_record_echoes_the_bind()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, RxType = 254 });
        Ticks(hub, Slv3Hub.ChainConfirmSightings + 1);
        MarkWired(hub);
        Assert.True(ManualBind(hub, FanKey));
        Assert.DoesNotContain(FanKey, saved.Keys);

        Ticks(hub, 3);

        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Contains(FanKey, saved.Keys);
    }

    [Fact]
    public void A_stale_saved_slot_of_a_USB_unit_is_replaced_by_the_slot_the_bind_echoes()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub(new() { [FanKey] = 9 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, RxType = 254 });
        Ticks(hub, Slv3Hub.ChainConfirmSightings + 1);
        MarkWired(hub);
        Assert.True(ManualBind(hub, FanKey));

        Ticks(hub, 3);

        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(hub.State.Fans[0].Slot, saved[FanKey]);
    }
}
