using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>Pins the L-Connect bind, reconcile, lock and SaveCfg behavior of <see cref="Slv3Hub"/>.</summary>
public partial class Slv3HubTests
{
    private static string FanKey => Convert.ToHexString(FanMac);
    private static readonly byte[] FarMaster = Convert.FromHexString("0102030405FF");

    private static (Slv3Hub Hub, FakeSlv3Network Net, FakeTxTransport Tx, SimulatedFan Fan, Dictionary<string, int> Saved, ManualClock Clock) Stranded(
        byte[] master, byte rx, byte channel, bool ownedFan = true, bool lockOn = true)
    {
        var (hub, net, tx, saved, clock) = CreateOwnershipHub(ownedFan ? new() { [FanKey] = 1 } : null);
        hub.LockBoundDevices = () => lockOn;
        var fan = new SimulatedFan { Mac = FanMac, MasterMac = master, RxType = rx, Channel = channel, Pwm = new byte[] { 6, 6, 6, 6 } };
        net.Fans.Add(fan);
        return (hub, net, tx, fan, saved, clock);
    }

    private static byte[] Ours => Convert.FromHexString("AABBCCDDEEFF");

    private static List<byte[]> AllBindFrames(FakeTxTransport tx) => tx.SentFrames.FindAll(f =>
        f.Length >= 25 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType
        && f[5] == Slv3Protocol.RfBind);

    public static IEnumerable<object[]> StrandedStates() => new[]
    {
        new object[] { "zeroed master", new byte[6], (byte)0, (byte)8 },
        new object[] { "0xE5 fill", Convert.FromHexString("E5E5E5E5E5E5"), (byte)229, (byte)229 },
        new object[] { "bit-flipped master, ch 72", Convert.FromHexString("AABBCCDDEEFE"), (byte)1, (byte)72 },
        new object[] { "ours rx 0xFE", Convert.FromHexString("AABBCCDDEEFF"), (byte)254, (byte)8 },
        new object[] { "ours rx 0xFF", Convert.FromHexString("AABBCCDDEEFF"), (byte)255, (byte)8 },
        new object[] { "ours rx 69", Convert.FromHexString("AABBCCDDEEFF"), (byte)69, (byte)8 },
        new object[] { "ours ch 152", Convert.FromHexString("AABBCCDDEEFF"), (byte)1, (byte)152 },
        new object[] { "far foreign master", Convert.FromHexString("0102030405FF"), (byte)3, (byte)8 },
        new object[] { "ours rx 255 ch 0", Convert.FromHexString("AABBCCDDEEFF"), (byte)255, (byte)0 },
        new object[] { "zero master rx 254", new byte[6], (byte)254, (byte)8 },
    };

    [Theory]
    [MemberData(nameof(StrandedStates))]
    public void Lock_on_rebinds_every_stranded_state_with_one_bind_to_its_current_pipe(string state, byte[] master, byte rx, byte channel)
    {
        var (hub, _, tx, _, _, _) = Stranded(master, rx, channel);

        Ticks(hub, 1);

        var frames = AllBindFrames(tx);
        Assert.True(frames.Count == 1, $"{state}: {frames.Count} bind frames");
        Assert.Equal(channel, frames[0][2]);
        Assert.Equal(rx, frames[0][3]);
        Assert.Equal(Ours, frames[0].AsSpan(12, 6).ToArray());
        Assert.Equal(1, frames[0][18]);
        Assert.Equal(Slv3Protocol.DefaultChannel, frames[0][19]);

        Ticks(hub, 1);
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(1, hub.State.Fans[0].Slot);
        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Fans[0].Channel);
        tx.SentFrames.Clear();
        Ticks(hub, 3);
        Assert.Empty(AllBindFrames(tx));
    }

    [Theory]
    [MemberData(nameof(StrandedStates))]
    public void Lock_off_leaves_a_chain_alone_unless_it_reports_our_master_on_the_wrong_channel(string state, byte[] master, byte rx, byte channel)
    {
        var (hub, _, tx, _, _, _) = Stranded(master, rx, channel, lockOn: false);

        Ticks(hub, 3);

        var adoptedOffChannel = Slv3Protocol.MacEquals(master, Ours) && channel != Slv3Protocol.DefaultChannel;
        var frames = AllBindFrames(tx);
        if (adoptedOffChannel)
        {
            Assert.True(frames.Count >= 1, state);
            Assert.All(frames, f => { Assert.Equal(channel, f[2]); Assert.Equal(rx, f[3]); });
        }
        else
        {
            Assert.True(frames.Count == 0, $"{state}: {frames.Count} bind frames");
        }
    }

    [Fact]
    public void Toggling_the_lock_sends_no_rf_and_is_reported_in_the_state()
    {
        var (hub, net, tx, _, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        var lockOn = true;
        hub.LockBoundDevices = () => lockOn;
        Ticks(hub, 2);
        Assert.True(hub.State.LockBoundDevices);
        tx.SentFrames.Clear();
        var rxSent = net.Fans.Count;

        lockOn = false;
        Ticks(hub, 1);
        Assert.False(hub.State.LockBoundDevices);
        lockOn = true;
        Ticks(hub, 1);

        Assert.True(hub.State.LockBoundDevices);
        Assert.Empty(AllBindFrames(tx));
        Assert.Empty(SaveCfgFrames(tx));
        Assert.Equal(rxSent, net.Fans.Count);
    }

    [Fact]
    public void The_bind_frame_carries_our_master_the_target_slot_and_the_mobo_follow_duty()
    {
        var (hub, _, tx, _, _, _) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel);

        Ticks(hub, 1);

        var frame = AllBindFrames(tx).Single();
        Assert.Equal(FanMac, frame.AsSpan(6, 6).ToArray());
        Assert.Equal(Ours, frame.AsSpan(12, 6).ToArray());
        Assert.Equal(1, frame[18]);
        Assert.Equal(Slv3Protocol.DefaultChannel, frame[19]);
        Assert.Equal(1, frame[20]);
        Assert.Equal(new byte[] { 6, 6, 6, 6 }, frame.AsSpan(21, 4).ToArray());
    }

    [Fact]
    public void An_owned_chain_is_bound_on_its_first_sighting_even_before_it_is_listed()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : new FakeRxTransport(net));
        hub.OwnedDevicesLoad = () => new Dictionary<string, int> { [FanKey] = 2 };
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac, RxType = 0 });

        Assert.True(hub.DriveTick());

        Assert.Empty(hub.State.Fans);
        var frame = AllBindFrames(tx).Single();
        Assert.Equal(2, frame[18]);
    }

    [Fact]
    public void An_unlisted_chain_that_is_not_owned_is_never_bound()
    {
        var net = new FakeSlv3Network();
        var tx = new FakeTxTransport(net);
        var hub = new Slv3Hub(new FakeDiscovery(), port => port.Role == Slv3DongleRole.Tx ? tx : new FakeRxTransport(net));
        Assert.True(hub.EnsureConnected());
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = FarMaster, RxType = 3 });

        Ticks(hub, 2);

        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void An_unowned_chain_is_never_bound_with_the_lock_on()
    {
        var (hub, _, tx, _, _, _) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel, ownedFan: false);

        Ticks(hub, 5);

        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void The_reconcile_never_gives_up_on_a_chain_that_ignores_binds_and_converges_once_it_listens()
    {
        var (hub, _, tx, fan, _, _) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel);
        fan.IgnoresBind = true;

        Ticks(hub, 400);

        Assert.Equal(400, AllBindFrames(tx).Count);
        fan.IgnoresBind = false;
        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        tx.SentFrames.Clear();
        Ticks(hub, 3);
        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void An_owned_chain_that_expires_and_returns_stranded_is_bound_again_with_the_lock_on_only()
    {
        foreach (var lockOn in new[] { true, false })
        {
            var (hub, net, tx, fan, _, clock) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel, lockOn: lockOn);
            Ticks(hub, 2);
            net.Fans.Clear();
            clock.AdvanceMs(40_000);
            Ticks(hub, 1);
            Assert.Empty(hub.State.Fans);

            fan.MasterMac = new byte[6];
            fan.RxType = 0;
            net.Fans.Add(fan);
            tx.SentFrames.Clear();
            Ticks(hub, 1);

            Assert.Equal(lockOn ? 1 : 0, AllBindFrames(tx).Count);
        }
    }

    [Fact]
    public void A_chain_reporting_our_master_is_adopted_and_walked_back_after_it_loses_it_with_the_lock_off()
    {
        var (hub, _, tx, fan, _, _) = Stranded(Ours, 5, Slv3Protocol.DefaultChannel, ownedFan: false, lockOn: false);
        Ticks(hub, 2);
        Assert.Empty(AllBindFrames(tx));

        fan.MasterMac = FarMaster;
        Ticks(hub, 2);

        var frame = AllBindFrames(tx).First();
        Assert.Equal(5, frame[18]);
        Assert.True(hub.State.Fans[0].BoundToUs);
    }

    [Fact]
    public void Unbind_sends_a_zero_master_frame_every_poll_until_the_chain_reports_no_master()
    {
        var (hub, _, tx, fan, saved, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        fan.IgnoresBind = true;

        Assert.True(hub.Unbind(FanKey));
        tx.SentFrames.Clear();
        Ticks(hub, 100);

        var frames = AllBindFrames(tx);
        Assert.Equal(100, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.Equal(new byte[6], f.AsSpan(12, 6).ToArray());
            Assert.Equal(0, f[18]);
            Assert.Equal(0, f[20]);
        });
        Assert.Empty(saved);

        fan.IgnoresBind = false;
        Ticks(hub, 2);
        Assert.False(hub.State.Fans[0].BoundToUs);
        tx.SentFrames.Clear();
        Ticks(hub, 5);
        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void Unbind_clears_a_chain_held_by_a_master_that_is_not_live_and_it_is_not_retaken()
    {
        var (hub, _, tx, fan, _, _) = Stranded(FarMaster, 3, Slv3Protocol.DefaultChannel, ownedFan: false);
        Ticks(hub, 1);

        Assert.True(hub.Unbind(FanKey));
        Ticks(hub, 3);

        var frames = AllBindFrames(tx);
        Assert.NotEmpty(frames);
        Assert.All(frames, f => { Assert.Equal(3, f[3]); Assert.Equal(new byte[6], f.AsSpan(12, 6).ToArray()); });
        Assert.Equal(new byte[6], fan.MasterMac);
        tx.SentFrames.Clear();
        Ticks(hub, 3);
        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void A_chain_held_by_a_live_master_cannot_be_unbound_bound_or_tugged_until_that_master_goes_quiet()
    {
        var (hub, net, tx, fan, _, _) = Stranded(FarMaster, 3, Slv3Protocol.DefaultChannel);
        var master = new SimulatedFan { Mac = FarMaster, DevType = 0xFF };
        net.Fans.Add(master);

        Ticks(hub, 3);

        Assert.Empty(AllBindFrames(tx));
        Assert.False(hub.Unbind(FanKey));
        Assert.False(hub.Bind(FanKey));
        Assert.Empty(AllBindFrames(tx));

        net.Fans.Remove(master);
        Ticks(hub, Slv3Hub.LiveMasterPolls);
        Assert.NotEmpty(AllBindFrames(tx));
        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(Ours, fan.MasterMac);
    }

    [Fact]
    public void Bind_sends_the_first_frame_at_once_to_the_current_pipe_and_takes_the_next_slot_above_the_highest()
    {
        var other = Convert.FromHexString("A1A2A3A4A5A6");
        var (hub, net, tx, _) = CreateConnectedHub();
        net.Fans.Add(new SimulatedFan { Mac = other, MasterMac = net.MasterMac, RxType = 7 });
        net.Fans.Add(new SimulatedFan { Mac = FanMac, Channel = 33, RxType = 9 });
        Assert.True(hub.DriveTick());
        tx.SentFrames.Clear();

        Assert.True(hub.Bind(FanKey));

        var frame = AllBindFrames(tx).Single();
        Assert.Equal(33, frame[2]);
        Assert.Equal(9, frame[3]);
        Assert.Equal(8, frame[18]);
        Assert.True(hub.DriveTick());
        Assert.Equal(8, hub.State.Fans.Single(f => f.Mac == FanKey).Slot);
    }

    [Fact]
    public void Bind_wraps_to_a_free_low_slot_when_the_slots_above_the_highest_are_full()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        for (var i = 0; i < 6; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = new byte[] { 0x10, 0, 0, 0, 0, (byte)i }, MasterMac = net.MasterMac, RxType = (byte)(8 + i) });
        }
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Bind(FanKey));
        Assert.True(hub.DriveTick());

        Assert.Equal(1, hub.State.Fans.Single(f => f.Mac == FanKey).Slot);
    }

    [Fact]
    public void Bind_persists_ownership_at_request_time_and_a_user_unbind_removes_it()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Ticks(hub, 1);

        Assert.True(hub.Bind(FanKey));
        Assert.Equal(1, saved[FanKey]);
        Ticks(hub, 2);
        Assert.True(hub.Unbind(FanKey));
        Assert.Empty(saved);
    }

    [Fact]
    public void Two_chains_on_one_slot_with_the_lock_off_unbind_the_first_in_record_order_and_leave_it_unbound()
    {
        var first = Convert.FromHexString("111111111111");
        var second = Convert.FromHexString("222222222222");
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        hub.LockBoundDevices = () => false;
        net.Fans.Add(new SimulatedFan { Mac = first, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        net.Fans.Add(new SimulatedFan { Mac = second, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });

        Ticks(hub, Slv3Hub.SlotConflictLimit);
        Assert.Empty(AllBindFrames(tx));
        Ticks(hub, 3);

        Assert.False(hub.State.Fans.Single(f => f.Mac == "111111111111").BoundToUs);
        Assert.True(hub.State.Fans.Single(f => f.Mac == "222222222222").BoundToUs);
        tx.SentFrames.Clear();
        Ticks(hub, 5);
        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void A_conflict_that_clears_decays_so_a_later_one_starts_from_zero()
    {
        var first = Convert.FromHexString("111111111111");
        var second = Convert.FromHexString("222222222222");
        var (hub, net, tx, _, clock) = CreateOwnershipHub();
        hub.LockBoundDevices = () => false;
        net.Fans.Add(new SimulatedFan { Mac = first, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        var clash = new SimulatedFan { Mac = second, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } };
        net.Fans.Add(clash);
        Ticks(hub, Slv3Hub.SlotConflictLimit);
        net.Fans.Remove(clash);
        clock.AdvanceMs(40_000);
        Ticks(hub, 10);
        tx.SentFrames.Clear();

        net.Fans.Add(clash);
        Ticks(hub, Slv3Hub.SlotConflictLimit);

        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void A_user_bind_broadcasts_savecfg_every_poll_for_five_seconds_then_one_throttled_save_ten_seconds_after_it()
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.Bind(FanKey));
        tx.SentFrames.Clear();

        for (var i = 0; i < 14; i++)
        {
            clock.AdvanceMs(500);
            Assert.True(hub.DriveTick());
        }

        // Polls land at 0.5 s steps; the sends stop once 5 s have passed since the bind.
        Assert.Equal(9, SaveCfgFrames(tx).Count);
        foreach (var frame in SaveCfgFrames(tx))
        {
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, frame.AsSpan(6, 6).ToArray());
            Assert.Equal(0xFF, frame[3]);
        }
        tx.SentFrames.Clear();
        clock.AdvanceMs(3_500);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));
        clock.AdvanceMs(30_000);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));
    }

    [Fact]
    public void Unbind_and_automatic_rebinds_send_no_savecfg()
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        hub.OwnedDevicesLoad = () => new Dictionary<string, int> { [FanKey] = 1 };
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());
        Assert.True(hub.DriveTick());
        Assert.True(hub.State.Fans[0].BoundToUs);

        Assert.True(hub.Unbind(FanKey));
        for (var i = 0; i < 40; i++)
        {
            clock.AdvanceMs(500);
            Assert.True(hub.DriveTick());
        }

        Assert.Empty(SaveCfgFrames(tx));
    }

    [Fact]
    public void The_periodic_save_fires_after_one_hour_then_only_after_three_more_and_waits_out_an_effect_change()
    {
        var clock = new ManualClock();
        var (hub, net, tx, _) = CreateConnectedHub(clock.NowMs);
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1 });
        Assert.True(hub.DriveTick());

        clock.AdvanceMs(Slv3Hub.PeriodicFirstSaveMs - 1_000);
        Assert.True(hub.DriveTick());
        Assert.Empty(SaveCfgFrames(tx));
        clock.AdvanceMs(2_000);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));

        clock.AdvanceMs(Slv3Hub.PeriodicSaveMs - 1_000);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));

        Assert.True(hub.SendRgbFrame(FanKey, new[] { new Nexus.Service.Peripherals.Hyte.Np50.RgbColor(1, 2, 3) }, 100, 100, out _));
        clock.AdvanceMs(2_000);
        Assert.True(hub.DriveTick());
        Assert.Single(SaveCfgFrames(tx));
        clock.AdvanceMs(Slv3Hub.PeriodicEffectQuietMs);
        Assert.True(hub.DriveTick());
        Assert.Equal(2, SaveCfgFrames(tx).Count);
    }

    [Fact]
    public void Channel_changes_walk_every_kept_bound_chain_to_the_new_channel_through_its_current_pipe()
    {
        var (hub, _, tx, _, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        tx.SentFrames.Clear();

        Assert.True(hub.SetChannel(11));
        Ticks(hub, 2);

        var frame = AllBindFrames(tx).First();
        Assert.Equal(Slv3Protocol.DefaultChannel, frame[2]);
        Assert.Equal(11, frame[19]);
        Assert.Equal(11, hub.State.Fans[0].Channel);
    }

    private static (Slv3Hub Hub, FakeTxTransport Tx, SimulatedFan Fan) StuckOnGarbledPipe(
        int ownedSlot, byte[]? garbledMaster, byte channel, byte rx)
    {
        var (hub, net, tx, _, _) = CreateOwnershipHub(new() { [FanKey] = ownedSlot });
        var fan = new SimulatedFan
        {
            Mac = FanMac, MasterMac = net.MasterMac, RxType = (byte)ownedSlot, ListensOnItsPipeOnly = true, Pwm = new byte[] { 6, 6, 6, 6 },
        };
        net.Fans.Add(fan);
        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        fan.MasterMac = garbledMaster ?? net.MasterMac;
        fan.Channel = channel;
        fan.RxType = rx;
        tx.SentFrames.Clear();
        return (hub, tx, fan);
    }

    [Theory]
    [InlineData(61, 75)]
    [InlineData(62, 161)]
    [InlineData(64, 203)]
    [InlineData(65, 66)]
    [InlineData(66, 21)]
    [InlineData(69, 152)]
    [InlineData(255, 8)]
    [InlineData(255, 0)]
    public void A_chain_that_took_a_misread_bind_is_bound_again_through_its_own_garbled_pipe_only(int rx, int channel)
    {
        var (hub, tx, _) = StuckOnGarbledPipe(4, garbledMaster: null, (byte)channel, (byte)rx);

        Ticks(hub, 3);

        Assert.NotEmpty(BindFramesAt(tx, (byte)channel, (byte)rx));
        Assert.All(AllBindFrames(tx), f => { Assert.Equal(channel, f[2]); Assert.Equal(rx, f[3]); });
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(4, hub.State.Fans[0].Slot);
        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Fans[0].Channel);
    }

    [Fact]
    public void A_chain_on_a_garbled_foreign_master_at_slot_11_channel_46_is_retaken_through_its_garbled_pipe_only()
    {
        var garbled = new byte[] { 0xAA, 0xBB, 0xCC, 0x65, 0xE1, 0xFC };
        var (hub, tx, _) = StuckOnGarbledPipe(3, garbled, channel: 46, rx: 11);

        Ticks(hub, 3);

        Assert.NotEmpty(BindFramesAt(tx, 46, 11));
        Assert.All(AllBindFrames(tx), f => { Assert.Equal(46, f[2]); Assert.Equal(11, f[3]); });
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(3, hub.State.Fans[0].Slot);
    }

    [Fact]
    public void A_manual_bind_of_a_chain_on_a_garbled_pipe_reaches_it_through_that_pipe()
    {
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        var fan = new SimulatedFan { Mac = FanMac, MasterMac = FarMaster, Channel = 152, RxType = 69, ListensOnItsPipeOnly = true };
        net.Fans.Add(fan);
        Ticks(hub, 1);
        Assert.Empty(AllBindFrames(tx));

        Assert.True(hub.Bind(FanKey));
        Assert.NotEmpty(BindFramesAt(tx, 152, 69));
        Ticks(hub, 2);

        Assert.True(hub.State.Fans[0].BoundToUs);
    }

    [Fact]
    public void A_chain_on_a_non_live_foreign_master_is_retaken_every_time_with_the_lock_on_and_never_given_up_on()
    {
        var (hub, _, tx, fan, _, _) = Stranded(Convert.FromHexString("A27CD726C6D3"), 3, Slv3Protocol.DefaultChannel);

        for (var round = 0; round < 5; round++)
        {
            Ticks(hub, 3);
            Assert.True(hub.State.Fans[0].BoundToUs);
            fan.MasterMac = Convert.FromHexString("A27CD726C6D3");
            fan.RxType = 3;
        }
        tx.SentFrames.Clear();
        fan.IgnoresBind = true;
        Ticks(hub, 600);

        Assert.Equal(600, AllBindFrames(tx).Count);
    }

    [Fact]
    public void With_the_lock_on_two_chains_on_one_slot_are_steered_back_to_their_targets_not_unbound()
    {
        var first = Convert.FromHexString("111111111111");
        var second = Convert.FromHexString("222222222222");
        var (hub, net, _, _, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = first, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        net.Fans.Add(new SimulatedFan { Mac = second, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });

        Ticks(hub, Slv3Hub.SlotConflictLimit + 6);

        Assert.All(hub.State.Fans, f => Assert.True(f.BoundToUs));
    }

    [Fact]
    public void A_bind_attempts_all_four_chunks_even_when_every_write_fails()
    {
        var (hub, net, tx, _, _, _) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel);
        net.TxWedged = true;

        hub.DriveTick();

        var chunks = tx.SentFrames.Count(f => f.Length > 3 && f[0] == Slv3Protocol.UsbSendRf && f[2] == Slv3Protocol.DefaultChannel && f[3] == 0);
        Assert.Equal(4, chunks);
    }
}
