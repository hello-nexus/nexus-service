using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>
/// Drives Slv3Hub against a fake TX/RX pair that models the RX device-list
/// report as a function of what the TX has been sent, so the bind/unbind
/// state machine converges (or fails to) exactly as it would against real
/// firmware: a bind/unbind frame takes effect on the fake network, and the
/// hub only sees it on its NEXT device-list refresh, matching the two-tick
/// convergence the fake network models.
/// </summary>
public class Slv3HubTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    private const int PendingOpTickBudget = Slv3Hub.PendingOpTickBudget;

    // Slv3OpenException reads "held by another app" per OS: EBUSY on Linux, ERROR_ACCESS_DENIED elsewhere.
    private static readonly int BusyOpenError = OperatingSystem.IsLinux() ? 16 : 5;

    [Fact]
    public void EnsureConnected_learns_master_mac()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        Assert.True(hub.State.IsConnected);
        Assert.Equal(Convert.ToHexString(net.MasterMac), hub.State.MasterMac);
        Assert.Equal(Slv3LinkStatus.Ok, hub.State.LinkStatus);
    }

    // The device page renders the disconnected reason from LinkStatus, so each
    // way a connect attempt can fail has to leave its own value behind.
    [Fact]
    public void EnsureConnected_reports_none_when_neither_dongle_enumerates()
    {
        var hub = new Slv3Hub(new RoleDiscovery(), _ => new SilentTransport(Slv3DongleRole.Tx));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.None, hub.State.LinkStatus);
    }

    [Fact]
    public void EnsureConnected_reports_txMissing_when_only_the_receiver_enumerates()
    {
        var hub = new Slv3Hub(new RoleDiscovery(Slv3DongleRole.Rx), port => new SilentTransport(port.Role));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.TxMissing, hub.State.LinkStatus);
    }

    [Fact]
    public void EnsureConnected_reports_rxMissing_when_only_the_transmitter_enumerates()
    {
        var hub = new Slv3Hub(new RoleDiscovery(Slv3DongleRole.Tx), port => new SilentTransport(port.Role));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.RxMissing, hub.State.LinkStatus);
    }

    [Fact]
    public void EnsureConnected_reports_busy_when_another_app_holds_the_dongle()
    {
        var hub = new Slv3Hub(
            new RoleDiscovery(Slv3DongleRole.Tx, Slv3DongleRole.Rx),
            _ => throw new Slv3OpenException("open failed for fake-tx", BusyOpenError));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.Busy, hub.State.LinkStatus);
    }

    [Fact]
    public void EnsureConnected_reports_openFailed_on_any_other_open_error()
    {
        var hub = new Slv3Hub(
            new RoleDiscovery(Slv3DongleRole.Tx, Slv3DongleRole.Rx),
            _ => throw new Slv3OpenException("WinUsb_Initialize failed for fake-tx: 31", 31));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.OpenFailed, hub.State.LinkStatus);
    }

    [Fact]
    public void EnsureConnected_withholds_noResponse_until_the_channel_scan_is_exhausted()
    {
        var hub = new Slv3Hub(
            new RoleDiscovery(Slv3DongleRole.Tx, Slv3DongleRole.Rx),
            port => new SilentTransport(port.Role));

        // One attempt probes a slice of the ~39-channel order. A dongle parked
        // on a channel this attempt never reached is not an unresponsive one, so
        // the hardware-blaming reason waits for a full pass.
        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.Unknown, hub.State.LinkStatus);

        for (var attempt = 0; attempt < Slv3Protocol.ChannelScanOrder().Length; attempt++)
        {
            if (hub.State.LinkStatus == Slv3LinkStatus.NoResponse)
            {
                break;
            }
            Assert.False(hub.EnsureConnected());
        }

        Assert.Equal(Slv3LinkStatus.NoResponse, hub.State.LinkStatus);
    }

    [Fact]
    public void Disconnect_drops_the_ok_status_so_it_never_outlives_the_link()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.Equal(Slv3LinkStatus.Ok, hub.State.LinkStatus);

        // The worker tears the link down without a connect attempt (control gate
        // turned off), so nothing would recompute a stale 'ok'.
        hub.Disconnect();

        Assert.False(hub.State.IsConnected);
        Assert.Equal(Slv3LinkStatus.Unknown, hub.State.LinkStatus);
    }

    [Fact]
    public void A_failed_connect_keeps_its_own_reason_through_teardown()
    {
        var hub = new Slv3Hub(
            new RoleDiscovery(Slv3DongleRole.Tx, Slv3DongleRole.Rx),
            _ => throw new Slv3OpenException("open failed for fake-tx", BusyOpenError));

        Assert.False(hub.EnsureConnected());
        Assert.Equal(Slv3LinkStatus.Busy, hub.State.LinkStatus);
    }

    [Fact]
    public void DriveTick_surfaces_discovered_fan_as_unbound()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Assert.True(hub.DriveTick());

        var fan = Assert.Single(hub.State.Fans);
        Assert.Equal(Convert.ToHexString(FanMac), fan.Mac);
        Assert.False(fan.BoundToUs);
    }

    [Fact]
    public void DriveTick_surfaces_more_than_one_page_of_fans()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        // 12 bound chains > one 10-record device-list page. A single-page poll
        // truncates the overflow, so those chains vanish from the list and can't
        // be seen, paired, or confirm a bind (the "many sets" report). The poll
        // must request a second page.
        const int fanCount = 12;
        for (var i = 0; i < fanCount; i++)
        {
            var mac = new byte[6];
            mac[5] = (byte)(0x10 + i);
            net.Fans.Add(new SimulatedFan { Mac = mac, MasterMac = net.MasterMac, RxType = (byte)(i + 1) });
        }

        // First poll seeds pageCount from a zero count (one page, so it still sees
        // only 10); the reported count then tunes the next poll up to two pages.
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Equal(fanCount, hub.State.Fans.Length);
        Assert.All(hub.State.Fans, f => Assert.True(f.BoundToUs));
    }

    [Fact]
    public void Transient_empty_poll_keeps_a_multi_page_fan_list_whole()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        const int fanCount = 12;
        for (var i = 0; i < fanCount; i++)
        {
            var mac = new byte[6];
            mac[5] = (byte)(0x10 + i);
            net.Fans.Add(new SimulatedFan { Mac = mac, MasterMac = net.MasterMac, RxType = (byte)(i + 1) });
        }
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);

        // A transient empty poll (RF hiccup) is debounced - it must not reset the
        // learned page count, or the recovery poll would request one page and
        // re-truncate the list back to 10.
        var saved = net.Fans.ToArray();
        net.Fans.Clear();
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);   // debounce holds the list

        net.Fans.AddRange(saved);
        Assert.True(hub.DriveTick());
        Assert.Equal(fanCount, hub.State.Fans.Length);   // recovery poll stays 2 pages
    }

    [Fact]
    public void Bind_converges_on_the_first_tick_via_the_immediate_frame()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // Bind() sends the first bind frame itself; the next poll confirms.
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(1, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Bind_converges_via_tick_resend_when_the_immediate_frame_is_lost()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());

        // Fan drops off the RF network (beacon starved): the merged device list
        // still carries it, so Bind() is accepted, but the immediate frame is
        // lost (nothing on the fake network to apply it to).
        net.Fans.Clear();
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].BoundToUs);

        // Fan reappears; the pending op's per-tick re-send binds it.
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(1, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Unbind_converges_once_device_list_confirms()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.False(hub.State.Fans[0].BoundToUs);
        Assert.Equal("", hub.State.Fans[0].MasterMac);
    }

    [Fact]
    public void Bind_on_already_bound_fan_does_not_reassign_slot()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 5 });
        Assert.True(hub.DriveTick());
        Assert.Equal(5, hub.State.Fans[0].Slot);

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Equal(5, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void Bind_rejects_mac_never_seen_in_device_list()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.Bind(Convert.ToHexString(FanMac)));
    }

    private static readonly byte[] OtherMasterMac = Convert.FromHexString("0102030405FF");

    // Hub plus a saved owned list the test can inspect; the clock is injected for backoff.
    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, Dictionary<string, int> Saved, ManualClock Clock) CreateOwnershipHub(
        Dictionary<string, int>? initial = null)
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        var saved = new Dictionary<string, int>(initial ?? new());
        hub.OwnedDevicesLoad = () => new Dictionary<string, int>(saved);
        hub.OwnedDevicesSave = owned =>
        {
            saved.Clear();
            foreach (var (mac, slot) in owned)
            {
                saved[mac] = slot;
            }
        };
        return (hub, net, tx, saved, clock);
    }

    private const int Established = Slv3Hub.EstablishedChainPolls;

    private static void Ticks(Slv3Hub hub, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(hub.DriveTick());
        }
    }

    [Fact]
    public void Devices_seen_bound_to_our_master_seed_the_owned_list_with_their_slot()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 4 });
        net.Fans.Add(new SimulatedFan { Mac = Convert.FromHexString("A1A2A3A4A5A6"), MasterMac = OtherMasterMac, RxType = 2 });

        Ticks(hub, Established - 1);
        Assert.Empty(saved);
        Ticks(hub, 1);

        Assert.Equal(new Dictionary<string, int> { [Convert.ToHexString(FanMac)] = 4 }, saved);
    }

    [Fact]
    public void Owned_device_that_loses_its_binding_is_bound_back_into_its_slot()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, tx, _, _) = CreateOwnershipHub(new() { [mac] = 6 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, RxType = 0 });

        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(6, hub.State.Fans[0].Slot);
        Assert.Equal(6, LastBindFrame(tx, FanMac)[18]);
    }

    [Fact]
    public void Owned_device_falls_back_to_the_first_free_slot_when_its_slot_is_taken()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, _, _, _) = CreateOwnershipHub(new() { [mac] = 3 });
        net.Fans.Add(new SimulatedFan { Mac = Convert.FromHexString("A1A2A3A4A5A6"), MasterMac = net.MasterMac, RxType = 3 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Ticks(hub, 2);

        Assert.Equal(1, Array.Find(hub.State.Fans, f => f.Mac == mac)!.Slot);
    }

    [Fact]
    public void Explicit_unbind_removes_the_device_and_it_is_not_rebound()
    {
        var (hub, net, tx, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        Ticks(hub, Established);
        Assert.Single(saved);

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));
        Ticks(hub, 3);
        tx.SentFrames.Clear();
        Ticks(hub, 3);

        Assert.Empty(saved);
        Assert.False(hub.State.Fans[0].BoundToUs);
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void Owned_device_bound_to_another_master_is_never_touched()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, tx, saved, _) = CreateOwnershipHub(new() { [mac] = 2 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = OtherMasterMac, RxType = 5 });

        Ticks(hub, 3);

        Assert.Equal(0, CountBindFrames(tx));
        Assert.Equal(Convert.ToHexString(OtherMasterMac), hub.State.Fans[0].MasterMac);
        Assert.Contains(mac, saved.Keys);
    }

    [Fact]
    public void Unowned_unbound_device_is_not_bound()
    {
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Ticks(hub, 3);

        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void Auto_rebind_backs_off_30_60_120_up_to_300_seconds_and_never_stops()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, tx, _, clock) = CreateOwnershipHub(new() { [mac] = 2 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, IgnoresBind = true });

        long[] waitsMs = { 30_000, 60_000, 120_000, 240_000, 300_000, 300_000, 300_000 };
        foreach (var wait in waitsMs)
        {
            var before = CountBindFrames(tx);
            Ticks(hub, 1);
            Assert.True(CountBindFrames(tx) > before, "attempt sent no bind frame");

            // The pending op spends its budget; the backoff holds the next attempt back.
            Ticks(hub, PendingOpTickBudget + 2);
            var settled = CountBindFrames(tx);
            clock.AdvanceMs(wait - 1_000);
            Ticks(hub, 3);
            Assert.Equal(settled, CountBindFrames(tx));
            clock.AdvanceMs(1_000 + 1_500);
        }
    }

    [Fact]
    public void Owned_device_with_a_zero_master_on_another_channel_is_rebound_to_our_channel()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, tx, _, _) = CreateOwnershipHub(new() { [mac] = 3 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, Channel = 11 });

        Ticks(hub, 2);

        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Fans[0].Channel);
        Assert.Equal(3, hub.State.Fans[0].Slot);
        Assert.True(CountBindFrames(tx) > 0);
    }

    [Fact]
    public void Owned_device_on_our_master_but_a_stale_channel_is_retargeted_keeping_its_slot()
    {
        var mac = Convert.ToHexString(FanMac);
        var (hub, net, _, _, _) = CreateOwnershipHub(new() { [mac] = 4 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 4, Channel = 11 });

        Ticks(hub, 2);

        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Fans[0].Channel);
        Assert.Equal(4, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void SetChannel_retargets_every_bound_device_to_the_new_channel()
    {
        var (hub, net, _, _, _) = CreateOwnershipHub();
        var other = Convert.FromHexString("A1A2A3A4A5A6");
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        net.Fans.Add(new SimulatedFan { Mac = other, MasterMac = net.MasterMac, RxType = 2 });
        Ticks(hub, 1);

        Assert.True(hub.SetChannel(21));
        Ticks(hub, 3);

        Assert.All(hub.State.Fans, f => Assert.Equal(21, f.Channel));
        Assert.Equal(new[] { 1, 2 }, Array.ConvertAll(hub.State.Fans, f => f.Slot));
    }

    [Fact]
    public void Bind_persists_ownership_at_request_time()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, IgnoresBind = true });
        Ticks(hub, 1);

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));

        Assert.Equal(1, saved[Convert.ToHexString(FanMac)]);
    }

    [Fact]
    public void Bind_that_exhausts_its_budget_carries_on_through_the_auto_path()
    {
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        var fan = new SimulatedFan { Mac = FanMac, IgnoresBind = true };
        net.Fans.Add(fan);
        Ticks(hub, 1);
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Ticks(hub, PendingOpTickBudget + 2);
        Assert.False(hub.State.Fans[0].BoundToUs);

        fan.IgnoresBind = false;
        Ticks(hub, 1);
        Assert.True(CountBindFrames(tx) > 0);
        Ticks(hub, 1);
        Assert.True(hub.State.Fans[0].BoundToUs);
    }

    [Fact]
    public void Two_chains_reporting_one_slot_for_five_polls_move_the_higher_mac_to_a_free_slot()
    {
        var (hub, net, _, _, _) = CreateOwnershipHub();
        var low = Convert.FromHexString("A1A2A3A4A5A6");
        var high = Convert.FromHexString("B1B2B3B4B5B6");
        net.Fans.Add(new SimulatedFan { Mac = low, MasterMac = net.MasterMac, RxType = 3 });
        net.Fans.Add(new SimulatedFan { Mac = high, MasterMac = net.MasterMac, RxType = 3 });

        Ticks(hub, Established + Slv3Hub.SlotConflictPolls - 2);
        Assert.All(net.Fans, f => Assert.Equal(3, f.RxType));

        Ticks(hub, 2);

        Assert.Equal(3, net.Fans[0].RxType);
        Assert.Equal(1, net.Fans[1].RxType);
        Assert.Equal(1, hub.State.Fans[1].Slot);
    }

    [Fact]
    public void Slot_conflict_prefers_moving_the_chain_whose_owned_slot_differs()
    {
        var low = Convert.FromHexString("A1A2A3A4A5A6");
        var high = Convert.FromHexString("B1B2B3B4B5B6");
        var (hub, net, _, _, _) = CreateOwnershipHub(new()
        {
            [Convert.ToHexString(low)] = 5,
            [Convert.ToHexString(high)] = 3,
        });
        net.Fans.Add(new SimulatedFan { Mac = low, MasterMac = net.MasterMac, RxType = 3 });
        net.Fans.Add(new SimulatedFan { Mac = high, MasterMac = net.MasterMac, RxType = 3 });

        Ticks(hub, Established + Slv3Hub.SlotConflictPolls + 1);

        Assert.NotEqual(3, net.Fans[0].RxType);
        Assert.Equal(3, net.Fans[1].RxType);
    }

    [Fact]
    public void Phantom_zeroed_copy_of_a_real_strimer_is_never_listed_owned_or_moved()
    {
        var real = Convert.FromHexString("64F271E566E1");
        var phantom = Convert.FromHexString("6400000066E1");
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var saved = new Dictionary<string, int>();
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        hub.OwnedDevicesSave = owned =>
        {
            saved.Clear();
            foreach (var (mac, slot) in owned) saved[mac] = slot;
        };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = real, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Ticks(hub, Established);
        net.Fans.Add(new SimulatedFan { Mac = phantom, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        tx.SentFrames.Clear();

        Ticks(hub, Established);

        Assert.Single(hub.State.Fans);
        Assert.Equal(new Dictionary<string, int> { [Convert.ToHexString(real)] = 1 }, saved);
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void A_young_chain_is_not_seeded_as_owned_and_cannot_trigger_a_slot_move()
    {
        var real = Convert.FromHexString("64F271E566E1");
        var young = Convert.FromHexString("B1B2B3B4B5B6");
        var (hub, net, tx, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = real, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Ticks(hub, Established);
        net.Fans.Add(new SimulatedFan { Mac = young, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        tx.SentFrames.Clear();

        Ticks(hub, Established - 1);

        Assert.DoesNotContain(Convert.ToHexString(young), saved.Keys);
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void A_new_chain_needs_three_sightings_to_be_listed()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Ticks(hub, 2);
        Assert.Empty(hub.State.Fans);
        Ticks(hub, 1);
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void Suspend_sends_three_savecfg_frames_200ms_apart()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var sleeps = new List<int>();
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx) { ConfirmNewChains = false, SleepMs = sleeps.Add };
        Assert.True(hub.EnsureConnected());
        tx.SentFrames.Clear();

        hub.OnSystemSuspending();

        Assert.Equal(3, tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[5] == Slv3Protocol.RfSaveCfg).Count);
        Assert.Equal(new[] { 200, 200 }, sleeps);
    }

    [Fact]
    public void Suspend_does_nothing_with_the_link_down()
    {
        var sleeps = new List<int>();
        var hub = new Slv3Hub(new RoleDiscovery(), _ => new SilentTransport(Slv3DongleRole.Tx)) { SleepMs = sleeps.Add };

        hub.OnSystemSuspending();

        Assert.Empty(sleeps);
    }

    [Fact]
    public void ResetChain_sends_one_reboot_frame_and_stops_once_the_chain_echoes_the_seq()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.ResetChain(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        var rebootFrames = tx.SentFrames.FindAll(f =>
            f.Length >= 12 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRebootChain);
        var frame = Assert.Single(rebootFrames);
        Assert.Equal(FanMac, frame.AsSpan(6, 6).ToArray());
        Assert.Equal(2, frame[18]);      // target rx = the chain's slot
        Assert.Equal(0, frame[20]);
        Assert.Equal(1, frame[21]);      // cmd seq 0 -> 1
    }

    [Fact]
    public void Back_to_back_commands_issue_monotonic_seqs_before_the_chain_echoes()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        net.EchoCmdSeq = false;
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));
        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));
        Assert.True(hub.ResetChain(Convert.ToHexString(FanMac)));

        var seqs = tx.SentFrames
            .FindAll(f => f.Length >= 22 && f[1] == 0 && (f[5] == Slv3Protocol.RfSelect || f[5] == Slv3Protocol.RfRebootChain))
            .ConvertAll(f => (int)f[21]);
        Assert.Equal(new[] { 1, 2, 3 }, seqs);
    }

    [Fact]
    public void Pending_command_spends_its_budget_while_the_chain_is_absent()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 };
        net.Fans.Add(fan);
        net.EchoCmdSeq = false;
        Assert.True(hub.DriveTick());
        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));

        // The chain drops off the air (merged list keeps it; no record to send to).
        net.Fans.Clear();
        for (var i = 0; i < 40; i++)
        {
            Assert.True(hub.PollTick());
        }
        tx.SentFrames.Clear();

        net.Fans.Add(fan);
        Assert.True(hub.PollTick());
        Assert.True(hub.PollTick());
        Assert.DoesNotContain(tx.SentFrames, f => f.Length >= 12 && f[1] == 0 && f[5] == Slv3Protocol.RfSelect);
    }

    [Fact]
    public void Strimer_never_gets_a_pwm_sync_frame_even_with_a_port_duty_set()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(0, CountBindFrames(tx));

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.DriveTick());
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void HydroShift_never_gets_a_pwm_sync_frame_even_with_a_port_duty_set()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(0, CountBindFrames(tx));

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 3, 50));
        Assert.True(hub.DriveTick());
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void HydroShift_unbind_frame_never_carries_a_port_duty()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 10, FanCount = 0 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 3, 50));

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));

        var frame = LastBindFrame(tx, FanMac);
        Assert.All(frame.AsSpan(21, 4).ToArray(), b => Assert.Equal(Slv3Protocol.PwmFollowMotherboard, b));
    }

    [Fact]
    public void Driven_hydroshift_screen_follows_its_saved_settings()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 10, FanCount = 0 });
        hub.AioSensors = () => new Slv3AioSensors(55f, 12f, null, null);
        hub.AioScreens = () => new Dictionary<string, Slv3AioScreen>
        {
            [Convert.ToHexString(FanMac)] = Slv3Protocol.AioScreenFrom(30, 4, "#FF0000", "#00FF00", "#0000FF", cpuTemp: false, cpuLoad: true, gpuTemp: false, gpuLoad: false, fanSpeed: false),
        };
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 50));

        Assert.True(hub.DriveTick());

        var paramsFrame = Assert.Single(RfFrames(tx, Slv3Protocol.RfAioParams));
        Assert.Equal(new byte[] { 0, 12, 0, 0 }, paramsFrame[22..26]);
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, paramsFrame[30..34]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, paramsFrame[35..39]);
        Assert.Equal(30, paramsFrame[47]);
        Assert.Equal(4, paramsFrame[49]);
    }

    [Fact]
    public void Driven_hydroshift_pump_gets_the_switch_until_echoed_and_params_every_drive_tick()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 10, FanCount = 0 });
        hub.AioSensors = () => new Slv3AioSensors(55f, 12f, null, null);
        Assert.True(hub.DriveTick());
        Assert.Empty(RfFrames(tx, Slv3Protocol.RfAioParams));

        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 50));
        Assert.True(hub.DriveTick());

        var switchFrame = Assert.Single(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless));
        Assert.Equal(1, switchFrame[20]); // [16] slot: the only bound chain
        var paramsFrame = Assert.Single(RfFrames(tx, Slv3Protocol.RfAioParams));
        // 50% of the LCD-C's 1600..2500 rpm span is 2050 rpm, timer 740.
        Assert.Equal(740, (paramsFrame[50] << 8) | paramsFrame[51]);
        Assert.Equal(new byte[] { 55, 12, 0, 0 }, paramsFrame[22..26]);
        Assert.Equal(new byte[] { 1, 1, 0, 0 }, paramsFrame[30..34]);

        Assert.True(hub.DriveTick());
        Assert.Single(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless));
        Assert.Equal(2, RfFrames(tx, Slv3Protocol.RfAioParams).Count);
        Assert.Equal(0, CountBindFrames(tx));
    }

    [Fact]
    public void Hydroshift_reports_each_acknowledged_switch_and_takes_it_again_on_request()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        var switched = new List<string>();
        hub.AioSwitched += switched.Add;
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 50));
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(new[] { Convert.ToHexString(FanMac) }, switched);
        Assert.Single(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless));

        hub.ResendAioSwitch(Convert.ToHexString(FanMac).ToLowerInvariant());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Equal(2, RfFrames(tx, Slv3Protocol.RfAioSwitchWireless).Count);
        Assert.Equal(2, switched.Count);
    }

    [Fact]
    public void Hydroshift_switch_keeps_being_sent_while_the_aio_never_echoes()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        net.EchoCmdSeq = false;
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 100));

        for (var i = 0; i < 2 * Slv3Hub.SequencedCommandBudget; i++)
        {
            Assert.True(hub.DriveTick());
        }

        Assert.True(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless).Count > Slv3Hub.SequencedCommandBudget);
        // 100% of the LCD-S span is 3200 rpm, timer 0.
        Assert.All(RfFrames(tx, Slv3Protocol.RfAioParams), f => Assert.Equal(0, (f[50] << 8) | f[51]));
    }

    [Fact]
    public void Undriven_hydroshift_with_saved_screen_settings_gets_them_at_the_default_pump_speed()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        hub.AioScreens = () => new Dictionary<string, Slv3AioScreen>
        {
            [Convert.ToHexString(FanMac)] = Slv3Protocol.AioScreenFrom(30, 4, "#FFFFFF", "#FFFFFF", "#FFFFFF", true, false, false, false, false, loopInterval: 7),
        };
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Null(hub.GetPumpDuty(Convert.ToHexString(FanMac)));
        Assert.NotEmpty(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless));
        var paramsFrame = RfFrames(tx, Slv3Protocol.RfAioParams)[^1];
        Assert.Equal(4, paramsFrame[49]);
        Assert.Equal(7, paramsFrame[28]);
        Assert.Equal(Slv3Protocol.HydroShiftPumpTimer(Slv3Protocol.HydroShiftDefaultPumpRpm, 11), (paramsFrame[50] << 8) | paramsFrame[51]);
    }

    [Fact]
    public void Released_hydroshift_pump_with_saved_screen_settings_keeps_its_screen_at_the_default_pump_speed()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        hub.AioScreens = () => new Dictionary<string, Slv3AioScreen>
        {
            [Convert.ToHexString(FanMac)] = Slv3Protocol.AioScreenFrom(30, 4, "#FFFFFF", "#FFFFFF", "#FFFFFF", true, false, false, false, false),
        };
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 100));
        Assert.True(hub.DriveTick());
        Assert.Equal(0, (RfFrames(tx, Slv3Protocol.RfAioParams)[^1][50] << 8) | RfFrames(tx, Slv3Protocol.RfAioParams)[^1][51]);

        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), null));
        tx.SentFrames.Clear();
        Assert.True(hub.DriveTick());

        Assert.Null(hub.GetPumpDuty(Convert.ToHexString(FanMac)));
        var paramsFrame = Assert.Single(RfFrames(tx, Slv3Protocol.RfAioParams));
        Assert.Equal(Slv3Protocol.HydroShiftPumpTimer(Slv3Protocol.HydroShiftDefaultPumpRpm, 11), (paramsFrame[50] << 8) | paramsFrame[51]);
    }

    [Fact]
    public void Only_the_hydroshift_on_the_usb_link_is_marked_usb_connected()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        var other = Convert.FromHexString("102030405060");
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 });
        net.Fans.Add(new SimulatedFan { Mac = other, MasterMac = net.MasterMac, RxType = 2, DevType = 11, FanCount = 0 });
        hub.UsbAioMac = () => Convert.ToHexString(FanMac).ToLowerInvariant();

        Assert.True(hub.DriveTick());

        Assert.True(hub.State.Fans.Single(f => f.Mac == Convert.ToHexString(FanMac)).UsbConnected);
        Assert.False(hub.State.Fans.Single(f => f.Mac == Convert.ToHexString(other)).UsbConnected);
    }

    [Fact]
    public void Screen_held_hydroshift_is_switched_again_after_an_unbind_and_rebind()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var aio = new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0 };
        net.Fans.Add(aio);
        hub.AioScreens = () => new Dictionary<string, Slv3AioScreen>
        {
            [Convert.ToHexString(FanMac)] = Slv3Protocol.AioScreenFrom(30, 4, "#FFFFFF", "#FFFFFF", "#FFFFFF", true, false, false, false, false),
        };
        for (var i = 0; i < 4; i++)
        {
            Assert.True(hub.DriveTick());
        }
        var switches = RfFrames(tx, Slv3Protocol.RfAioSwitchWireless).Count;

        aio.MasterMac = new byte[6];
        Assert.True(hub.DriveTick());
        aio.MasterMac = net.MasterMac;
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.True(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless).Count > switches);
    }

    [Fact]
    public void Released_hydroshift_pump_gets_no_more_params()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 10, FanCount = 0 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), 30));
        Assert.True(hub.DriveTick());
        Assert.Equal(30, hub.GetPumpDuty(Convert.ToHexString(FanMac)));

        Assert.True(hub.SetPumpDuty(Convert.ToHexString(FanMac), null));
        tx.SentFrames.Clear();
        Assert.True(hub.DriveTick());

        Assert.Null(hub.GetPumpDuty(Convert.ToHexString(FanMac)));
        Assert.Empty(RfFrames(tx, Slv3Protocol.RfAioParams));
    }

    [Fact]
    public void Pump_duty_is_refused_for_a_fan_chain()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3 });
        Assert.True(hub.DriveTick());

        Assert.False(hub.SetPumpDuty(Convert.ToHexString(FanMac), 50));
        Assert.True(hub.DriveTick());
        Assert.Empty(RfFrames(tx, Slv3Protocol.RfAioParams));
        Assert.Empty(RfFrames(tx, Slv3Protocol.RfAioSwitchWireless));
    }

    [Fact]
    public void Zero_fan_chain_is_left_on_mobo_sync_until_a_port_duty_is_set()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 0 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(0, CountBindFrames(tx));

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.DriveTick());
        Assert.Equal(1, CountBindFrames(tx));
        Assert.Equal(127, LastBindFrame(tx, FanMac)[21]);
    }

    [Fact]
    public void Sequenced_command_is_re_sent_each_poll_until_the_echo_and_gives_up_after_ten()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        net.EchoCmdSeq = false;
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));
        for (var i = 0; i < 15; i++)
        {
            Assert.True(hub.PollTick());
        }

        var selectFrames = tx.SentFrames.FindAll(f => f.Length >= 12 && f[1] == 0 && f[5] == Slv3Protocol.RfSelect);
        Assert.Equal(Slv3Hub.SequencedCommandBudget, selectFrames.Count);
        Assert.All(selectFrames, f => Assert.Equal(1, f[21]));
    }

    [Fact]
    public void ResetChain_fails_for_unknown_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.ResetChain(Convert.ToHexString(FanMac)));
    }

    [Fact]
    public void Identify_sends_rf_select_frame_addressed_to_the_fan()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 2 });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Identify(Convert.ToHexString(FanMac)));

        var selectFrame = Assert.Single(tx.SentFrames, f => f.Length >= 12 && f[5] == Slv3Protocol.RfSelect);
        Assert.Equal(FanMac, selectFrame.AsSpan(6, 6).ToArray());
    }

    [Fact]
    public void SendRgbFrame_sends_header_four_times_then_data_parts()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());

        var leds = new RgbColor[40];
        for (var i = 0; i < leds.Length; i++)
        {
            leds[i] = new RgbColor((byte)i, (byte)(i * 2), (byte)(i * 3));
        }

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), leds, 100, 100, out var effectIndexHex);

        Assert.True(sent);
        Assert.Equal(8, effectIndexHex.Length);

        var rgbFrames = tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);
        // Header packet (part 0) is sent 4 times; at least one more data part follows.
        Assert.True(rgbFrames.Count >= 5, $"expected at least 5 chunk-0 RF_RgbSync frames, got {rgbFrames.Count}");
        foreach (var frame in rgbFrames)
        {
            Assert.Equal(FanMac, frame.AsSpan(6, 6).ToArray());
        }
    }

    [Fact]
    public async Task SendRgbWindowAsync_sends_back_to_back_headers_and_every_data_part_four_times()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        var frames = new byte[30 * 40 * 3];
        new Random(7).NextBytes(frames);
        Assert.True(await hub.SendRgbWindowAsync(Convert.ToHexString(FanMac), frames, 40, 30, 53, 100));

        // Chunk 0 of each RF payload carries the packet index at [22] and the count at [23].
        var parts = tx.SentFrames.FindAll(f => f.Length >= 24 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);
        Assert.Equal(8, parts.Count(f => f[22] == 0));
        var total = parts[0][23];
        Assert.True(total >= 2);
        for (var p = 1; p < total; p++)
        {
            Assert.Equal(4, parts.Count(f => f[22] == p));
        }
        Assert.All(parts, f => Assert.True(f.AsSpan(18, 4).SequenceEqual(parts[0].AsSpan(18, 4))));
        Assert.Equal(3, parts[0][18]);
    }

    [Fact]
    public async Task SendRgbWindowAsync_skips_late_passes_that_would_exceed_the_air_budget()
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        // Incompressible 120-LED window: ~50 data parts, so two late passes would add ~100 payloads to the second.
        var frames = new byte[30 * 120 * 3];
        new Random(7).NextBytes(frames);
        Assert.True(await hub.SendRgbWindowAsync(Convert.ToHexString(FanMac), frames, 120, 30, 53, 100));

        var parts = tx.SentFrames.FindAll(f => f.Length >= 24 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);
        var total = parts[0][23];
        Assert.True(total > 40);
        for (var p = 1; p < total; p++)
        {
            Assert.Equal(2, parts.Count(f => f[22] == p));
        }
    }

    [Fact]
    public async Task SendRgbWindowAsync_never_repeats_the_previous_effect_index()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 3 });
        Assert.True(hub.DriveTick());
        var frames = new byte[30 * 40 * 3];

        tx.SentFrames.Clear();
        Assert.True(await hub.SendRgbWindowAsync(Convert.ToHexString(FanMac), frames, 40, 30, 53, 100));
        var first = HeaderIndex(tx);
        tx.SentFrames.Clear();
        Assert.True(await hub.SendRgbWindowAsync(Convert.ToHexString(FanMac), frames, 40, 30, 53, 100));
        var second = HeaderIndex(tx);

        Assert.NotEqual(first, second);
        Assert.Equal(first[..6], second[..6]);

        static string HeaderIndex(FakeTxTransport tx) => Convert.ToHexString(tx.SentFrames.Find(
            f => f.Length >= 24 && f[1] == 0 && f[5] == Slv3Protocol.RfRgbSync && f[22] == 0)!.AsSpan(18, 4));
    }

    [Fact]
    public void SendRgbFrame_fails_for_unbound_fan()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, out var effectIndexHex);

        Assert.False(sent);
        Assert.Equal("", effectIndexHex);
    }

    [Fact]
    public void SendRgbFrame_fails_for_unknown_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        var sent = hub.SendRgbFrame(Convert.ToHexString(FanMac), new RgbColor[40], 100, 100, out var effectIndexHex);
        Assert.False(sent);
        Assert.Equal("", effectIndexHex);
    }

    [Fact]
    public void SetChannel_rejects_even_non_default_channel()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.SetChannel(10));
    }

    [Fact]
    public void SetChannel_accepts_default_and_odd_values()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.True(hub.SetChannel(Slv3Protocol.DefaultChannel));
        Assert.True(hub.SetChannel(15));
        Assert.Equal(15, hub.State.Channel);
    }

    // ── SetPortDuty / bind-frame PWM tuple (Phase 3) ──

    [Fact]
    public void DriveTick_defaults_every_occupied_port_to_mobo_sync()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3 });

        Assert.True(hub.DriveTick());

        var bind = LastBindFrame(tx, FanMac);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[21]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[22]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[23]);
        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, bind[24]); // port 3 is beyond FanCount=3: mirrors its neighbour
    }

    [Fact]
    public void SetPortDuty_writes_a_floored_manual_duty_into_the_next_bind_frame()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 2 });
        Assert.True(hub.DriveTick());

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 1, 5)); // floors to 14

        Assert.True(hub.DriveTick());

        var bind = LastBindFrame(tx, FanMac);
        Assert.Equal(127, bind[21]);   // 50 % on the 0..255 scale
        Assert.Equal(35, bind[22]);    // 14 % floor
        Assert.Equal(35, bind[23]);    // unoccupied ports mirror the last occupied one
        Assert.Equal(35, bind[24]);
    }

    [Fact]
    public void SetPortDuty_null_restores_mobo_sync()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 80));
        Assert.True(hub.DriveTick());
        Assert.Equal(204, LastBindFrame(tx, FanMac)[21]);

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, null));
        Assert.True(hub.DriveTick());

        Assert.Equal(Slv3Protocol.PwmFollowMotherboard, LastBindFrame(tx, FanMac)[21]);
    }

    [Fact]
    public void SetPortDuty_rejects_out_of_range_port_and_malformed_mac()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        Assert.False(hub.SetPortDuty(Convert.ToHexString(FanMac), -1, 50));
        Assert.False(hub.SetPortDuty(Convert.ToHexString(FanMac), 4, 50));
        Assert.False(hub.SetPortDuty("not-a-mac", 0, 50));
    }

    // ── Chain persistence / device-list merge (link-health) ──

    [Fact]
    public void Chain_persists_through_empty_polls_until_expiry()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);

        // Chain drops off the RF network but the poll keeps succeeding
        // (empty reply); a merge-based list must not evict it immediately.
        net.Fans.Clear();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(hub.DriveTick());
        }
        var fan = Assert.Single(hub.State.Fans);
        Assert.False(fan.Stale);

        clock.AdvanceMs(4_000); // past ChainStaleMs (3500 ms)
        Assert.True(hub.DriveTick());
        fan = Assert.Single(hub.State.Fans);
        Assert.True(fan.Stale);

        clock.AdvanceMs(31_000); // cumulative unseen time now past ChainExpiryMs (30000 ms)
        Assert.True(hub.DriveTick());
        Assert.Empty(hub.State.Fans);
    }

    [Fact]
    public void Chain_seen_every_poll_never_goes_stale_despite_slow_clock_advance()
    {
        var clock = new ManualClock();
        var (hub, net, _, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].Stale);

        clock.AdvanceMs(4_000); // would exceed ChainStaleMs if unseen, but the fan is re-reported below
        Assert.True(hub.DriveTick());

        Assert.False(hub.State.Fans[0].Stale);
    }

    // ── GetDev failure escalation / RX reset ──

    [Fact]
    public void Five_consecutive_getdev_failures_reset_the_rx_via_the_tx_and_keep_the_list()
    {
        var (hub, net, tx, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // A healthy handle that keeps returning an unreadable reply (wedged
        // RX MCU) drives the 5-consecutive-failure reset path.
        rx.FailReads = true;
        for (var i = 0; i < 5; i++)
        {
            Assert.True(hub.DriveTick());
        }
        rx.FailReads = false;

        var resetFrame = Assert.Single(tx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Equal(0x15, resetFrame[0]);
        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);

        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void Getdev_failures_never_fail_the_tick_and_resets_back_off_5_10_20_seconds()
    {
        var clock = new ManualClock();
        var (hub, net, tx, rx) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        int Resets() => tx.SentFrames.FindAll(f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother).Count;

        rx.FailReads = true;
        for (var i = 0; i < 40; i++)
        {
            Assert.True(hub.DriveTick());
        }
        Assert.Equal(1, Resets());
        Assert.Single(hub.State.Fans);

        clock.AdvanceMs(5_000);
        for (var i = 0; i < 6; i++) Assert.True(hub.DriveTick());
        Assert.Equal(2, Resets());

        clock.AdvanceMs(9_000);
        for (var i = 0; i < 6; i++) Assert.True(hub.DriveTick());
        Assert.Equal(2, Resets());
        clock.AdvanceMs(1_000);
        for (var i = 0; i < 6; i++) Assert.True(hub.DriveTick());
        Assert.Equal(3, Resets());
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_wedged_rx_is_reset_through_the_tx_then_reopened_in_place_without_clearing_the_list()
    {
        var (hub, net, tx, rx, rxOpens) = CreateConnectedHubCountingRxOpens();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        rx.FailReads = true;
        for (var i = 0; i < 5; i++) Assert.True(hub.DriveTick());
        Assert.Single(tx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Equal(Slv3LinkStatus.Recovering, hub.State.LinkStatus);
        Assert.Equal(1, rxOpens());

        rx.FailReads = false;
        Assert.True(hub.DriveTick());

        Assert.Equal(2, rxOpens());
        Assert.Single(hub.State.Fans);
        Assert.True(hub.DriveTick());
        Assert.Equal(Slv3LinkStatus.Ok, hub.State.LinkStatus);
    }

    [Fact]
    public void An_rx_that_has_not_enumerated_yet_is_retried_every_tick()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var discovery = new ToggleDiscovery();
        var hub = new Slv3Hub(discovery, port => port.Role == Slv3DongleRole.Tx ? tx : rx) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        rx.FailReads = true;
        for (var i = 0; i < 5; i++) Assert.True(hub.DriveTick());

        discovery.RxPresent = false;
        for (var i = 0; i < 3; i++) Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);

        rx.FailReads = false;
        discovery.RxPresent = true;
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].Stale);
    }

    [Fact]
    public void A_tx_that_keeps_failing_to_reopen_is_reset_through_the_rx_after_five_attempts()
    {
        var net = new FakeSlv3Network();
        var rx = new FakeRxTransport(net);
        var failTx = true;
        var opened = 0;
        FakeTxTransport? firstTx = null;
        var hub = new Slv3Hub(new FakeDiscovery(), port =>
        {
            if (port.Role != Slv3DongleRole.Tx) return rx;
            opened++;
            var tx = new FakeTxTransport(net) { MasterChannel = failTx && opened > 1 ? (byte)99 : Slv3Protocol.DefaultChannel };
            firstTx ??= tx;
            return tx;
        }) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        firstTx!.Dead = true;

        for (var i = 0; i < 4; i++) Assert.True(hub.PollTick());
        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.True(hub.PollTick());
        Assert.Single(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Single(hub.State.Fans);

        failTx = false;
        Assert.True(hub.PollTick());
        Assert.True(hub.PollTick());
        Assert.True(hub.State.IsConnected);
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_good_reply_between_failure_streaks_clears_the_reset_backoff()
    {
        var (hub, net, tx, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        // Four streaks, each followed by a good reply: more resets than one
        // connection's budget, yet none of them fails the tick.
        for (var streak = 0; streak < 4; streak++)
        {
            rx.FailReads = true;
            for (var i = 0; i < 5; i++)
            {
                Assert.True(hub.DriveTick());
            }
            rx.FailReads = false;
            Assert.True(hub.DriveTick());
        }

        Assert.Equal(4, tx.SentFrames.FindAll(f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother).Count);
    }

    [Fact]
    public void Busy_replies_keep_the_device_list_and_never_reset_a_responsive_rx()
    {
        var (hub, net, _, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        rx.BusyReads = true;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(hub.DriveTick());
        }

        Assert.Single(hub.State.Fans);
        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
    }

    [Fact]
    public void A_busy_spell_that_never_ends_reopens_the_rx_in_place_without_resetting_the_tx()
    {
        var (hub, net, _, rx, rxOpens) = CreateConnectedHubCountingRxOpens();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        rx.BusyReads = true;
        for (var i = 0; i < 60; i++)
        {
            Assert.True(hub.PollTick());
        }

        Assert.Equal(2, rxOpens());
        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Single(hub.State.Fans);
        Assert.True(hub.IsConnected);

        rx.BusyReads = false;
        Assert.True(hub.PollTick());
        Assert.False(Assert.Single(hub.State.Fans).Stale);
    }

    [Fact]
    public void A_busy_rx_is_reopened_with_backoff_and_never_fails_the_poll()
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var rxOpens = 0;
        var hub = new Slv3Hub(new FakeDiscovery(), port =>
        {
            if (port.Role == Slv3DongleRole.Tx) return tx;
            rxOpens++;
            return rx;
        }, clock.NowMs) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        rx.BusyReads = true;
        for (var i = 0; i < 4 * 60; i++)
        {
            Assert.True(hub.PollTick());
        }
        Assert.Equal(2, rxOpens);

        clock.AdvanceMs(5_000);
        for (var i = 0; i < 60; i++) Assert.True(hub.PollTick());
        Assert.Equal(3, rxOpens);
        Assert.DoesNotContain(rx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_good_reply_between_busy_spells_refills_the_reopen_budget()
    {
        var (hub, net, _, rx, rxOpens) = CreateConnectedHubCountingRxOpens();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        for (var spell = 0; spell < 4; spell++)
        {
            rx.BusyReads = true;
            for (var i = 0; i < 60; i++)
            {
                Assert.True(hub.PollTick());
            }
            rx.BusyReads = false;
            Assert.True(hub.PollTick());
        }

        Assert.Equal(1 + 4, rxOpens());
    }

    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, FakeRxTransport Rx, Func<int> RxOpens) CreateConnectedHubCountingRxOpens()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var rxOpens = 0;
        var hub = new Slv3Hub(new FakeDiscovery(), port =>
        {
            if (port.Role == Slv3DongleRole.Tx)
            {
                return tx;
            }
            rxOpens++;
            return rx;
        }) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        return (hub, net, tx, rx, () => rxOpens);
    }

    [Fact]
    public void An_rx_alternating_between_no_reply_and_busy_still_gets_reset()
    {
        var (hub, net, tx, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        for (var i = 0; i < 10; i++)
        {
            rx.FailReads = i % 2 == 0;
            rx.BusyReads = i % 2 == 1;
            Assert.True(hub.PollTick());
        }

        Assert.Single(tx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
    }

    [Fact]
    public void A_chain_unseen_through_a_busy_spell_is_reported_stale()
    {
        var clock = new ManualClock();
        var (hub, net, _, rx) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].Stale);

        rx.BusyReads = true;
        clock.AdvanceMs(4_000);
        Assert.True(hub.PollTick());

        Assert.True(Assert.Single(hub.State.Fans).Stale);
    }

    [Fact]
    public void Getdev_send_failures_count_toward_the_rx_reset_streak_without_failing_the_tick()
    {
        var (hub, net, tx, rx) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        rx.FailSend = true;
        for (var i = 0; i < 4; i++) Assert.True(hub.DriveTick());
        Assert.DoesNotContain(tx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.True(hub.DriveTick());

        Assert.Single(tx.SentFrames, f => f.Length >= 1 && f[0] == Slv3Protocol.UsbResetAnother);
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_tx_that_went_away_is_reopened_in_place_and_the_device_list_survives()
    {
        var net = new FakeSlv3Network();
        var rx = new FakeRxTransport(net);
        var txs = new List<FakeTxTransport>();
        var hub = new Slv3Hub(new FakeDiscovery(), port =>
        {
            if (port.Role != Slv3DongleRole.Tx) return rx;
            var tx = new FakeTxTransport(net);
            txs.Add(tx);
            return tx;
        }) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        txs[0].Dead = true;

        Assert.True(hub.IsConnected);
        Assert.True(hub.PollTick());
        Assert.Equal(2, txs.Count);
        Assert.Single(hub.State.Fans);
        Assert.True(hub.State.IsConnected);
    }

    [Fact]
    public void Chains_go_stale_then_the_link_drops_only_after_both_dongles_are_absent_for_ten_seconds()
    {
        var clock = new ManualClock();
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var discovery = new ToggleDiscovery();
        var hub = new Slv3Hub(discovery, port => port.Role == Slv3DongleRole.Tx ? tx : rx, clock.NowMs) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        tx.Dead = true;
        rx.Dead = true;
        discovery.TxPresent = false;
        discovery.RxPresent = false;
        Assert.True(hub.PollTick());
        clock.AdvanceMs(9_000);
        Assert.True(hub.PollTick());
        Assert.True(Assert.Single(hub.State.Fans).Stale);

        discovery.TxPresent = true;
        clock.AdvanceMs(5_000);
        Assert.True(hub.PollTick());
        discovery.TxPresent = false;
        Assert.True(hub.PollTick());
        clock.AdvanceMs(9_999);
        Assert.True(hub.PollTick());
        clock.AdvanceMs(1);
        Assert.False(hub.PollTick());
    }

    private sealed class ToggleDiscovery : ISlv3Discovery
    {
        public bool TxPresent { get; set; } = true;
        public bool RxPresent { get; set; } = true;

        public IReadOnlyList<Slv3PortInfo> Discover()
        {
            var ports = new List<Slv3PortInfo>();
            if (TxPresent) ports.Add(new Slv3PortInfo { PortName = "fake-tx", Role = Slv3DongleRole.Tx });
            if (RxPresent) ports.Add(new Slv3PortInfo { PortName = "fake-rx", Role = Slv3DongleRole.Rx });
            return ports;
        }
    }


    // ── Channel scan (MasterInitLocked) ──

    [Fact]
    public void EnsureConnected_scans_channels_when_the_master_answers_off_default()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net) { MasterChannel = 15 };
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx) { ConfirmNewChains = false };

        // One attempt probes a bounded slice of the scan order (each dead
        // channel costs a full read timeout under the hub lock); the cursor
        // resumes across attempts, mirroring the worker's connect retries.
        var connected = false;
        for (var attempt = 0; attempt < 6 && !connected; attempt++)
        {
            connected = hub.EnsureConnected();
        }

        Assert.True(connected);
        Assert.Equal(15, hub.State.Channel);
    }

    [Fact]
    public void EnsureConnected_skips_the_scan_when_the_master_answers_on_the_default_channel()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net); // MasterChannel defaults to Slv3Protocol.DefaultChannel
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx) { ConfirmNewChains = false };

        Assert.True(hub.EnsureConnected());

        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Channel);
        Assert.Single(tx.SentFrames, f => f.Length >= 2 && f[0] == Slv3Protocol.UsbGetMac);
    }

    // ── RF_SaveCfg after a confirmed bind/unbind ──

    [Fact]
    public void Confirmed_bind_broadcasts_a_savecfg_burst_of_ten()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        tx.SentFrames.Clear();

        // One SaveCfg per poll for the 5 s after the bind lands (L-Connect
        // SaveConfig(1) on every loop pass while lastBindTime < 5 s).
        for (var i = 0; i < 14; i++)
        {
            Assert.True(hub.DriveTick());
        }

        var saveCfgFrames = SaveCfgFrames(tx);
        Assert.Equal(10, saveCfgFrames.Count);
        foreach (var frame in saveCfgFrames)
        {
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, frame.AsSpan(6, 6).ToArray());
            Assert.Equal(net.MasterMac, frame.AsSpan(12, 6).ToArray());
            Assert.Equal(0xFF, frame[3]);   // broadcast pipe
        }
    }

    [Fact]
    public void Binding_change_schedules_one_throttled_savecfg_after_ten_quiet_seconds()
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.False(hub.State.Fans[0].BoundToUs);
        tx.SentFrames.Clear();

        // An unbind has no burst; the throttled save fires 10 s after the change.
        clock.AdvanceMs(9_000);
        Assert.True(hub.DriveTick());
        Assert.Empty(SaveCfgFrames(tx));
        clock.AdvanceMs(1_500);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));
        clock.AdvanceMs(20_000);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));
    }

    private static List<byte[]> SaveCfgFrames(FakeTxTransport tx) => tx.SentFrames.FindAll(f =>
        f.Length >= 18 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0
        && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfSaveCfg);

    // ── Pending bind/unbind drop after PendingOpTickBudget ──

    [Fact]
    public void Pending_bind_dropped_after_the_tick_budget_is_resumed_by_the_auto_path()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());

        net.Fans.Clear(); // fan unreachable: neither the immediate nor the re-send frame ever applies
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));

        for (var i = 0; i < PendingOpTickBudget + 1; i++)
        {
            Assert.True(hub.DriveTick());
        }

        tx.SentFrames.Clear();
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.True(hub.State.Fans[0].BoundToUs);
    }

    // ── EnsureVideoMode ──

    [Fact]
    public void EnsureVideoMode_arms_once_then_rearms_only_after_reconnect()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.EnsureVideoMode());

        Assert.Equal(2, tx.SentFrames.Count); // 1 video-start + 1 prep frame (one known chain)
        Assert.Equal(Slv3Protocol.UsbGetMac, tx.SentFrames[0][0]);
        Assert.Equal(0x01, tx.SentFrames[0][1]);
        Assert.Equal(Slv3Protocol.UsbSendRf, tx.SentFrames[1][0]);
        Assert.Equal(0xFF, tx.SentFrames[1][3]);

        tx.SentFrames.Clear();
        Assert.True(hub.EnsureVideoMode()); // idempotent until disconnect
        Assert.Empty(tx.SentFrames);

        hub.Disconnect();
        Assert.True(hub.EnsureConnected());
        tx.SentFrames.Clear();

        Assert.True(hub.EnsureVideoMode());
        Assert.NotEmpty(tx.SentFrames.FindAll(f => f.Length >= 2 && f[0] == Slv3Protocol.UsbGetMac && f[1] == 0x01));
    }

    // Finds the most recent RF_Bind USB frame (chunk 0) addressed to fanMac,
    // whose bytes [21..25) carry the 4-port PWM tuple (RF-payload [17..21),
    // shifted by the 4-byte USB-frame header).
    private static byte[] LastBindFrame(FakeTxTransport tx, byte[] fanMac)
    {
        byte[]? found = null;
        foreach (var frame in tx.SentFrames)
        {
            if (frame.Length < 25 || frame[0] != Slv3Protocol.UsbSendRf || frame[1] != 0
                || frame[4] != Slv3Protocol.RfFrameType || frame[5] != Slv3Protocol.RfBind)
            {
                continue;
            }
            if (!Slv3Protocol.MacEquals(frame.AsSpan(6, 6), fanMac)) continue;
            found = frame;
        }
        Assert.NotNull(found);
        return found!;
    }

    [Fact]
    public void DriveTick_stops_re_binding_a_converged_chain()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);

        // Nothing about the chain's duty has changed, so the keepalive has
        // nothing to say. Re-binding it every tick reboots the real chain
        // controller and drops the LCD screens wired behind it.
        var settled = CountBindFrames(tx);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(settled, CountBindFrames(tx));
    }

    [Fact]
    public void DriveTick_re_sends_the_pwm_frame_while_the_reported_duty_drifts()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());

        // The chain stops echoing the tuple (L-Connect NeedSyncPwm: a port
        // more than 5 off its target is re-sent every second until it is not).
        net.EchoPwm = false;
        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        var settled = CountBindFrames(tx);
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Equal(settled + 3, CountBindFrames(tx));
    }

    [Fact]
    public void Unbind_frame_clears_the_master_mac_and_the_chain_reports_no_master()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.Unbind(Convert.ToHexString(FanMac)));

        var frame = LastBindFrame(tx, FanMac);
        Assert.Equal(new byte[6], frame.AsSpan(12, 6).ToArray());
        Assert.Equal(0, frame[18]);
        Assert.Equal(0, frame[20]);
        Assert.Equal(1, frame[3]);   // steered at the chain's current slot
        Assert.True(hub.DriveTick());
        Assert.Equal("", hub.State.Fans[0].MasterMac);
    }

    [Fact]
    public void Bind_frame_carries_the_ordinal_among_bound_chains_not_the_slot()
    {
        var first = Convert.FromHexString("111111111111");
        var second = Convert.FromHexString("222222222222");
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = first, MasterMac = net.MasterMac, RxType = 3 });
        net.Fans.Add(new SimulatedFan { Mac = second });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.Bind(Convert.ToHexString(second)));

        var frame = LastBindFrame(tx, second);
        Assert.Equal(1, frame[18]);   // first free slot
        Assert.Equal(2, frame[20]);   // second bound chain in MAC order
        Assert.True(hub.DriveTick());
        Assert.Equal(1, hub.State.Fans[1].Slot);
    }

    [Fact]
    public void DriveTick_sends_the_lconnect_clock_heartbeat_and_a_getmac_every_second()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        tx.SentFrames.Clear();

        Assert.True(hub.DriveTick());
        Assert.True(hub.PollTick());
        Assert.True(hub.DriveTick());

        var clocks = tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfClockSync);
        Assert.Equal(2, clocks.Count);
        var clock = clocks[0];
        Assert.Equal(0xFF, clock[3]);                                   // broadcast pipe
        Assert.Equal(new byte[6], clock.AsSpan(6, 6).ToArray());        // fan MAC zero
        Assert.Equal(net.MasterMac, clock.AsSpan(12, 6).ToArray());
        Assert.All(clock.AsSpan(18, 32).ToArray(), b => Assert.Equal(0x14, b));
        Assert.Equal(2, tx.SentFrames.FindAll(f => f.Length >= 2 && f[0] == Slv3Protocol.UsbGetMac).Count);
    }

    [Fact]
    public void DriveTick_re_binds_once_a_port_duty_changes()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(Convert.ToHexString(FanMac)));
        Assert.True(hub.DriveTick());
        var settled = CountBindFrames(tx);

        Assert.True(hub.SetPortDuty(Convert.ToHexString(FanMac), 0, 50));
        Assert.True(hub.DriveTick());
        Assert.True(CountBindFrames(tx) > settled);
    }

    // Bind/PWM frames on the wire: a chunkSeq-0 USB frame carrying RF_Bind.
    // First USB chunk of every RF payload with this command; payload byte k sits at frame [4 + k].
    private static List<byte[]> RfFrames(FakeTxTransport tx, byte rfCmd) =>
        tx.SentFrames.FindAll(f => f.Length >= 6 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0
            && f[4] == Slv3Protocol.RfFrameType && f[5] == rfCmd);

    private static int CountBindFrames(FakeTxTransport tx)
    {
        var count = 0;
        foreach (var frame in tx.SentFrames)
        {
            if (frame.Length >= 6 && frame[0] == Slv3Protocol.UsbSendRf && frame[1] == 0
                && frame[4] == Slv3Protocol.RfFrameType && frame[5] == Slv3Protocol.RfBind)
            {
                count++;
            }
        }
        return count;
    }

    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, FakeRxTransport Rx) CreateConnectedHub(Func<long>? nowMs = null)
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx, nowMs) { ConfirmNewChains = false };
        Assert.True(hub.EnsureConnected());
        return (hub, net, tx, rx);
    }

    // Injectable monotonic clock for chain last-seen/expiry tests.
    private sealed class ManualClock
    {
        private long _nowMs = 1_000_000;
        public long NowMs() => _nowMs;
        public void AdvanceMs(long delta) => _nowMs += delta;
    }

    private sealed class FakeDiscovery : ISlv3Discovery
    {
        public IReadOnlyList<Slv3PortInfo> Discover() => new[]
        {
            new Slv3PortInfo { PortName = "fake-tx", Role = Slv3DongleRole.Tx },
            new Slv3PortInfo { PortName = "fake-rx", Role = Slv3DongleRole.Rx },
        };
    }

    // Discovery of an arbitrary subset of the module's two WinUSB devices.
    private sealed class RoleDiscovery : ISlv3Discovery
    {
        private readonly Slv3DongleRole[] _roles;

        public RoleDiscovery(params Slv3DongleRole[] roles) => _roles = roles;

        public IReadOnlyList<Slv3PortInfo> Discover() =>
            Array.ConvertAll(_roles, role => new Slv3PortInfo { PortName = $"fake-{role}", Role = role });
    }

    // Opens, then answers nothing: the GetMac probe runs its channel slice dry.
    private sealed class SilentTransport : ISlv3Transport
    {
        public SilentTransport(Slv3DongleRole role) => Role = role;

        public bool IsOpen => true;
        public Slv3DongleRole Role { get; }
        public string PortName => "fake-silent";

        public bool RfSend(ReadOnlySpan<byte> frame) => true;
        public byte[] RfRead(int expectedLen) => Array.Empty<byte>();
        public void Dispose() { }
    }

    private sealed class SimulatedFan
    {
        public required byte[] Mac { get; init; }
        /// <summary>A fan that never applies a bind frame.</summary>
        public bool IgnoresBind { get; set; }
        public byte[] MasterMac { get; set; } = new byte[6];
        public byte Channel { get; set; } = Slv3Protocol.DefaultChannel;
        public byte RxType { get; set; }
        public byte DevType { get; set; } = 25;
        public byte FanCount { get; set; } = 1;
        // The chain echoes the last bind-frame duty tuple (fans_pwm) and the
        // last sequenced command's seq (cmd_seq), as the Y70 firmware does.
        public byte[] Pwm { get; set; } = new byte[4];
        public byte CmdSeq { get; set; }
    }

    private sealed class FakeSlv3Network
    {
        public byte[] MasterMac { get; } = Convert.FromHexString("AABBCCDDEEFF");
        public List<SimulatedFan> Fans { get; } = new();
        // Off = a chain that never echoes (drift / lost-command tests).
        public bool EchoPwm { get; set; } = true;
        public bool EchoCmdSeq { get; set; } = true;
    }

    // Models the TX dongle: records every USB frame sent, and applies an
    // RF_Bind frame's target slot/master/channel to the addressed fan
    // immediately (no RF latency simulated), so the next GetDev report
    // reflects it.
    private sealed class FakeTxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;
        private byte _lastGetMacChannel;

        public FakeTxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        /// <summary>The device behind the handle is gone (it re-enumerated).</summary>
        public bool Dead { get; set; }

        public bool IsOpen => !Dead;
        public Slv3DongleRole Role => Slv3DongleRole.Tx;
        public string PortName => "fake-tx";
        public List<byte[]> SentFrames { get; } = new();

        // Channel the fake master answers GetMac on. Defaults to the protocol
        // default so a hub that never scans still connects on the first probe.
        public byte MasterChannel { get; set; } = Slv3Protocol.DefaultChannel;

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (copy.Length >= 2 && copy[0] == Slv3Protocol.UsbGetMac)
            {
                // GetMac and video-start share USB_CMD 0x11; byte [1] is the
                // requested channel for GetMac and a fixed 0x01 for video-start.
                // Only MasterInitLocked's probe/scan reads the reply that follows,
                // so a video-start's byte [1] never gets mistaken for a channel.
                _lastGetMacChannel = copy[1];
            }
            if (copy.Length >= 25 && copy[0] == Slv3Protocol.UsbSendRf && copy[1] == 0 && copy[5] == Slv3Protocol.RfBind)
            {
                var fanMac = copy.AsSpan(6, 6).ToArray();
                var masterMac = copy.AsSpan(12, 6).ToArray();
                var targetRx = copy[18];
                var targetChannel = copy[19];
                foreach (var fan in _net.Fans)
                {
                    if (!Slv3Protocol.MacEquals(fan.Mac, fanMac))
                    {
                        continue;
                    }
                    if (fan.IgnoresBind)
                    {
                        break;
                    }
                    fan.RxType = targetRx;
                    fan.Channel = targetChannel;
                    fan.MasterMac = masterMac;
                    if (_net.EchoPwm)
                    {
                        fan.Pwm = copy.AsSpan(21, 4).ToArray();
                    }
                    break;
                }
            }
            if (copy.Length >= 22 && copy[0] == Slv3Protocol.UsbSendRf && copy[1] == 0
                && (copy[5] == Slv3Protocol.RfSelect || copy[5] == Slv3Protocol.RfRebootChain || copy[5] == Slv3Protocol.RfAioSwitchWireless)
                && _net.EchoCmdSeq)
            {
                var fanMac = copy.AsSpan(6, 6).ToArray();
                foreach (var fan in _net.Fans)
                {
                    if (Slv3Protocol.MacEquals(fan.Mac, fanMac))
                    {
                        fan.CmdSeq = copy[21];
                        break;
                    }
                }
            }
            return true;
        }

        public byte[] RfRead(int expectedLen)
        {
            var reply = new byte[64];
            reply[0] = Slv3Protocol.UsbGetMac;
            if (_lastGetMacChannel == MasterChannel)
            {
                _net.MasterMac.CopyTo(reply, 1);
            }
            return reply;
        }

        public void Dispose()
        {
        }
    }

    // Models the RX dongle: GetDev replies are generated live from the fake
    // network's current fan states, so a bind/unbind the TX fake just applied
    // is visible on the following RfRead.
    private sealed class FakeRxTransport : ISlv3Transport
    {
        private readonly FakeSlv3Network _net;

        public FakeRxTransport(FakeSlv3Network net)
        {
            _net = net;
        }

        public bool Dead { get; set; }

        public bool IsOpen => !Dead;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";
        public List<byte[]> SentFrames { get; } = new();

        // Simulates a wedged RX MCU: the GetDev write succeeds but the reply
        // carries no valid echo, driving the 5-consecutive-failure reset path.
        public bool FailReads { get; set; }

        // Simulates a dead USB handle: the GetDev write itself fails, which
        // DriveTick treats as fatal (no reset streak, immediate false).
        public bool FailSend { get; set; }

        public bool RfSend(ReadOnlySpan<byte> frame)
        {
            var copy = frame.ToArray();
            SentFrames.Add(copy);
            if (FailSend && copy.Length >= 1 && copy[0] == Slv3Protocol.UsbSendRf)
            {
                return false;
            }
            return true;
        }

        // Simulates the RX answering "no device list this cycle": one frame
        // opening with 0, which the transport hands back as-is.
        public bool BusyReads { get; set; }

        public byte[] RfRead(int expectedLen)
        {
            if (FailReads)
            {
                return Array.Empty<byte>();
            }
            if (BusyReads)
            {
                var busy = new byte[Slv3Protocol.UsbPacketSize];
                busy[1] = 0x03;
                return busy;
            }
            var buf = new byte[Slv3Protocol.RecordHeaderLength + _net.Fans.Count * Slv3Protocol.RecordLength];
            buf[0] = Slv3Protocol.UsbSendRf;
            buf[1] = (byte)_net.Fans.Count;
            for (var i = 0; i < _net.Fans.Count; i++)
            {
                var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
                var rec = buf.AsSpan(offset);
                var fan = _net.Fans[i];
                fan.Mac.CopyTo(rec);
                fan.MasterMac.CopyTo(rec.Slice(6));
                rec[12] = fan.Channel;
                rec[13] = fan.RxType;
                rec[18] = fan.DevType;
                rec[19] = fan.FanCount;
                fan.Pwm.CopyTo(rec.Slice(36, 4));
                rec[40] = fan.CmdSeq;
                rec[41] = Slv3Protocol.RecordValidator;
            }
            // Firmware sends only the requested pages: the header still reports the
            // true device count, but records past PageLength * pageCount bytes are
            // truncated. A one-page poll of >10 fans therefore drops the overflow.
            return buf.Length <= expectedLen ? buf : buf[..expectedLen];
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_new_chain_is_listed_only_once_the_third_poll_confirms_it()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac });

        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Empty(hub.State.Fans);

        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_chain_missed_by_one_poll_under_rf_load_is_still_listed()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        net.Fans.Remove(fan);
        Assert.True(hub.DriveTick());
        net.Fans.Add(fan);

        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());

        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_sighting_outside_the_confirm_window_starts_over()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        var fan = new SimulatedFan { Mac = FanMac };
        net.Fans.Add(fan);
        Assert.True(hub.DriveTick());
        net.Fans.Remove(fan);
        for (var i = 0; i < Slv3Hub.ChainConfirmWindowPolls; i++) Assert.True(hub.DriveTick());
        net.Fans.Add(fan);

        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.Empty(hub.State.Fans);

        Assert.True(hub.DriveTick());
        Assert.Single(hub.State.Fans);
    }

    [Fact]
    public void A_mac_read_in_one_poll_only_never_appears()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var rx = new FakeRxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : rx);
        Assert.True(hub.EnsureConnected());
        var phantom = new SimulatedFan { Mac = Convert.FromHexString("64F2710066E1"), DevType = 2 };
        net.Fans.Add(phantom);
        Assert.True(hub.DriveTick());
        net.Fans.Remove(phantom);

        for (var i = 0; i < 3; i++) Assert.True(hub.DriveTick());

        Assert.Empty(hub.State.Fans);
    }
}
