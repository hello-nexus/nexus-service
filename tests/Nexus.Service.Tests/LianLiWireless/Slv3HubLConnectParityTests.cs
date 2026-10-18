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

    // Every byte non-zero, so the zeroed-copy phantom filter never takes one for a copy of another.
    private static byte[] ChainMac(int n) => new byte[] { 0x71, 0x72, 0x73, 0x74, 0x75, (byte)(0x80 + n) };

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

        lockOn = false;
        Ticks(hub, 1);
        Assert.False(hub.State.LockBoundDevices);
        lockOn = true;
        Ticks(hub, 1);

        Assert.True(hub.State.LockBoundDevices);
        Assert.Empty(AllBindFrames(tx));
        Assert.Empty(SaveCfgFrames(tx));
        Assert.Equal(Ours, net.Fans[0].MasterMac);
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
    public void A_live_master_blocks_a_manual_bind_and_unbind_but_never_the_reconcile_of_an_owned_chain()
    {
        var (hub, net, _, fan, _, _) = Stranded(FarMaster, 3, Slv3Protocol.DefaultChannel);
        net.Fans.Add(new SimulatedFan { Mac = FarMaster, DevType = 0xFF });
        Ticks(hub, 1);

        Assert.False(hub.Bind(FanKey, out var bindError));
        Assert.Equal("held by a live master", bindError);
        Assert.False(hub.Unbind(FanKey, out var unbindError));
        Assert.Equal("held by a live master", unbindError);
        Ticks(hub, 3);

        Assert.True(hub.State.Fans.Single(f => f.Mac == FanKey).BoundToUs);
        Assert.Equal(Ours, fan.MasterMac);
    }

    [Fact]
    public void A_garbled_master_record_carrying_an_owned_fans_corrupted_master_does_not_stop_its_retake()
    {
        var corrupted = Convert.FromHexString("A27CD726C6D3");
        var (hub, net, _, fan, _, _) = Stranded(corrupted, 3, Slv3Protocol.DefaultChannel);
        net.Fans.Add(new SimulatedFan { Mac = corrupted, DevType = 0xFF });

        Ticks(hub, 3);

        Assert.True(hub.State.Fans.Single(f => f.Mac == FanKey).BoundToUs);
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
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(8 + i) });
        }
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Assert.True(hub.DriveTick());

        Assert.True(hub.Bind(FanKey));
        Assert.True(hub.DriveTick());

        Assert.Equal(1, hub.State.Fans.Single(f => f.Mac == FanKey).Slot);
    }

    [Fact]
    public void A_user_unbind_removes_the_owned_entry()
    {
        var (hub, net, _, saved, _) = CreateOwnershipHub();
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Ticks(hub, 1);

        Assert.True(hub.Bind(FanKey));
        Ticks(hub, 2);
        Assert.Equal(1, saved[FanKey]);
        Assert.True(hub.Unbind(FanKey));
        Assert.Empty(saved);
    }

    [Fact]
    public void Two_chains_on_one_slot_with_the_lock_off_unbind_the_first_in_record_order_and_leave_it_unbound()
    {
        var low = Convert.FromHexString("111111111111");
        var high = Convert.FromHexString("222222222222");
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        hub.LockBoundDevices = () => false;
        // The RX reports the higher MAC first, so record order and MAC order disagree.
        net.Fans.Add(new SimulatedFan { Mac = high, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        net.Fans.Add(new SimulatedFan { Mac = low, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });

        Ticks(hub, Slv3Hub.SlotConflictLimit);
        Assert.Empty(AllBindFrames(tx));
        Ticks(hub, 3);

        Assert.False(hub.State.Fans.Single(f => f.Mac == "222222222222").BoundToUs);
        Assert.True(hub.State.Fans.Single(f => f.Mac == "111111111111").BoundToUs);
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

        Ticks(hub, 3);
        Assert.NotEmpty(AllBindFrames(tx));
    }

    [Fact]
    public void A_user_bind_broadcasts_savecfg_every_poll_for_five_seconds_and_nothing_after()
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

        // The sends stop once the post-bind window has passed.
        Assert.Equal(9, SaveCfgFrames(tx).Count);
        foreach (var frame in SaveCfgFrames(tx))
        {
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, frame.AsSpan(6, 6).ToArray());
            Assert.Equal(0xFF, frame[3]);
        }
        tx.SentFrames.Clear();
        clock.AdvanceMs(3_500);
        Assert.True(hub.DriveTick());
        clock.AdvanceMs(30_000);
        Assert.True(hub.DriveTick());
        Assert.Empty(SaveCfgFrames(tx));
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
    public void A_bind_attempts_all_four_chunks_when_writes_fail_without_timing_out()
    {
        var (hub, net, tx, _, _, _) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel);
        net.TxFailFast = true;

        hub.DriveTick();

        var chunks = tx.SentFrames.Count(f => f.Length > 3 && f[0] == Slv3Protocol.UsbSendRf && f[2] == Slv3Protocol.DefaultChannel && f[3] == 0);
        Assert.Equal(4, chunks);
    }

    [Fact]
    public void A_stalled_tx_costs_one_write_timeout_per_poll_however_many_chains_need_a_bind()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var owned = new Dictionary<string, int>();
        for (var i = 0; i < 4; i++)
        {
            var mac = ChainMac(i);
            owned[Convert.ToHexString(mac)] = i + 1;
            net.Fans.Add(new SimulatedFan { Mac = mac, Pwm = new byte[] { 6, 6, 6, 6 } });
        }
        hub.OwnedDevicesLoad = () => owned;
        net.TxWedged = true;
        tx.SentFrames.Clear();

        hub.DriveTick();

        // One timed-out chunk ends the poll's sends; the GetMac probe is the only other write.
        Assert.True(tx.SentFrames.Count <= 2, $"{tx.SentFrames.Count} writes in one stalled poll");
    }

    private static void StreamWindow(Slv3Hub hub)
    {
        var frames = new byte[30 * 40 * 3];
        new Random(7).NextBytes(frames);
        hub.SendRgbWindowAsync(FanKey, frames, 40, 30, 53, 100).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(255, 0)]
    [InlineData(1, 152)]
    public void A_stranded_chain_is_sent_a_bind_every_poll_while_windows_stream_and_binds_once_it_listens(int rx, int channel)
    {
        var (hub, _, tx, fan, _, _) = Stranded(Ours, (byte)rx, (byte)channel);
        fan.IgnoresBind = true;

        for (var poll = 0; poll < 4; poll++)
        {
            StreamWindow(hub);
            tx.SentFrames.Clear();
            Ticks(hub, 1);
            Assert.Single(AllBindFrames(tx));
        }

        fan.IgnoresBind = false;
        StreamWindow(hub);
        Ticks(hub, 2);
        Assert.True(hub.State.Fans[0].BoundToUs);
        Assert.Equal(Slv3Protocol.DefaultChannel, hub.State.Fans[0].Channel);
    }

    [Fact]
    public void A_pwm_change_goes_out_within_one_poll_while_windows_stream_every_poll()
    {
        var (hub, _, tx, fan, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        StreamWindow(hub);
        tx.SentFrames.Clear();

        Assert.True(hub.SetPortDuty(FanKey, 0, 60));
        StreamWindow(hub);
        Ticks(hub, 1);

        Assert.NotEmpty(AllBindFrames(tx));
        Assert.Equal(Slv3Protocol.EncodeDuty(60), fan.Pwm[0]);
    }

    [Fact]
    public void A_chain_on_its_pipe_mid_upload_is_not_held_back_from_a_bind()
    {
        var (hub, _, tx, fan, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        StreamWindow(hub);
        fan.RxType = 5;
        tx.SentFrames.Clear();

        Ticks(hub, 1);

        Assert.NotEmpty(AllBindFrames(tx));
    }

    [Fact]
    public void A_failed_bind_leaves_a_user_unbound_chain_unbound()
    {
        var (hub, net, tx, saved, _) = CreateOwnershipHub();
        hub.LockBoundDevices = () => false;
        for (var i = 0; i < Slv3Hub.BindCapDevices; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 1), Pwm = new byte[] { 6, 6, 6, 6 } });
        }
        net.Fans.Add(new SimulatedFan { Mac = FanMac, MasterMac = FarMaster, RxType = 3, Pwm = new byte[] { 6, 6, 6, 6 } });
        Ticks(hub, 2);
        Assert.True(hub.Unbind(FanKey));
        Ticks(hub, 3);
        tx.SentFrames.Clear();

        Assert.False(hub.Bind(FanKey, out var error));

        Assert.Contains("limit", error);
        Assert.DoesNotContain(FanKey, saved.Keys);
        Ticks(hub, 3);
        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void A_failed_bind_leaves_an_existing_takeback_intent_running()
    {
        var (hub, net, tx, fan, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel, ownedFan: false, lockOn: false);
        Ticks(hub, 2);
        fan.IgnoresBind = true;
        fan.MasterMac = Convert.FromHexString("A27CD726C6D3");
        for (var i = 0; i < Slv3Hub.BindCapDevices; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 2), Pwm = new byte[] { 6, 6, 6, 6 } });
        }
        Ticks(hub, 2);
        tx.SentFrames.Clear();

        Assert.False(hub.Bind(FanKey, out _));
        Ticks(hub, 2);

        Assert.True(AllBindFrames(tx).Count >= 2);
    }

    [Fact]
    public void The_bind_caps_refuse_a_fourth_strimer_and_a_second_hydroshift_of_one_model()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        for (var i = 0; i < 3; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(0x20 + i), MasterMac = net.MasterMac, RxType = (byte)(i + 1), DevType = 2, FanCount = 0 });
        }
        net.Fans.Add(new SimulatedFan { Mac = ChainMac(0x31), MasterMac = net.MasterMac, RxType = 5, DevType = 10 });
        var fourthStrimer = ChainMac(0x29);
        var secondHydro = ChainMac(0x32);
        var otherModel = ChainMac(0x33);
        net.Fans.Add(new SimulatedFan { Mac = fourthStrimer, DevType = 2, FanCount = 0 });
        net.Fans.Add(new SimulatedFan { Mac = secondHydro, DevType = 10 });
        net.Fans.Add(new SimulatedFan { Mac = otherModel, DevType = 11 });
        Ticks(hub, 1);

        Assert.False(hub.Bind(Convert.ToHexString(fourthStrimer), out var strimerError));
        Assert.Contains("Strimers", strimerError);
        Assert.False(hub.Bind(Convert.ToHexString(secondHydro), out var hydroError));
        Assert.Contains("HydroShift", hydroError);
        Assert.True(hub.Bind(Convert.ToHexString(otherModel), out _));
    }

    [Fact]
    public void An_eleventh_device_is_refused_with_the_device_limit()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        for (var i = 0; i < Slv3Hub.BindCapDevices; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 1) });
        }
        net.Fans.Add(new SimulatedFan { Mac = FanMac });
        Ticks(hub, 2);

        Assert.False(hub.Bind(FanKey, out var error));
        Assert.Equal("limit of 10 bound devices reached", error);
    }

    [Fact]
    public void A_chain_beyond_the_adoption_cap_on_our_master_is_unbound()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        for (var i = 0; i < Slv3Hub.AdoptionCap + 1; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 1), Pwm = new byte[] { 6, 6, 6, 6 } });
        }

        Ticks(hub, 8);

        Assert.Equal(1, net.Fans.Count(f => !Slv3Protocol.MacEquals(f.MasterMac, net.MasterMac)));
        Assert.Equal(Slv3Hub.AdoptionCap, net.Fans.Count(f => Slv3Protocol.MacEquals(f.MasterMac, net.MasterMac)));
    }

    [Fact]
    public void A_chain_this_hub_unbound_is_unbound_again_if_it_reports_our_master_with_the_lock_off()
    {
        var low = Convert.FromHexString("111111111111");
        var high = Convert.FromHexString("222222222222");
        var (hub, net, _, _, _) = CreateOwnershipHub();
        hub.LockBoundDevices = () => false;
        var loser = new SimulatedFan { Mac = high, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } };
        net.Fans.Add(loser);
        net.Fans.Add(new SimulatedFan { Mac = low, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        Ticks(hub, Slv3Hub.SlotConflictLimit + 3);
        Assert.Equal(new byte[6], loser.MasterMac);

        loser.MasterMac = net.MasterMac;
        loser.RxType = 9;
        Ticks(hub, 3);

        Assert.Equal(new byte[6], loser.MasterMac);
    }

    [Fact]
    public void A_user_unbound_chain_that_expires_and_returns_stranded_is_not_retaken_with_the_lock_on()
    {
        var (hub, net, tx, fan, _, clock) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        Assert.True(hub.Unbind(FanKey));
        Ticks(hub, 3);
        net.Fans.Clear();
        clock.AdvanceMs(40_000);
        Ticks(hub, 1);

        fan.MasterMac = new byte[6];
        fan.RxType = 0;
        net.Fans.Add(fan);
        tx.SentFrames.Clear();
        Ticks(hub, 5);

        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void Turning_the_lock_off_mid_takeback_keeps_steering_the_chain_while_it_is_listed_but_not_after_it_returns()
    {
        var (hub, net, tx, fan, _, clock) = Stranded(new byte[6], 0, Slv3Protocol.DefaultChannel);
        var lockOn = true;
        hub.LockBoundDevices = () => lockOn;
        fan.IgnoresBind = true;
        Ticks(hub, 3);
        lockOn = false;
        tx.SentFrames.Clear();
        Ticks(hub, 3);
        Assert.Equal(3, AllBindFrames(tx).Count);

        net.Fans.Clear();
        clock.AdvanceMs(40_000);
        Ticks(hub, 1);
        fan.IgnoresBind = false;
        net.Fans.Add(fan);
        tx.SentFrames.Clear();
        Ticks(hub, 3);

        Assert.Empty(AllBindFrames(tx));
    }

    [Fact]
    public void The_worker_runs_the_drive_pass_once_per_group_of_polls()
    {
        var (hub, _, tx, _) = CreateConnectedHub();
        tx.SentFrames.Clear();
        var tick = 0;

        for (var i = 0; i < 3 * Slv3ConnectionWorker.DriveEveryTicks; i++)
        {
            Assert.True(Slv3ConnectionWorker.RunTick(hub, ref tick));
        }

        var clockSyncs = tx.SentFrames.Count(f => f.Length > 5 && f[0] == Slv3Protocol.UsbSendRf && f[1] == 0
            && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfClockSync);
        Assert.Equal(3, clockSyncs);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_lock_put_that_lands_between_a_ticks_read_and_apply_is_not_overwritten()
    {
        var (hub, _, _, _) = CreateConnectedHub();
        using var reading = new System.Threading.ManualResetEventSlim();
        using var proceed = new System.Threading.ManualResetEventSlim();
        var armed = false;
        hub.LockBoundDevices = () =>
        {
            if (armed)
            {
                reading.Set();
                proceed.Wait(TimeSpan.FromSeconds(10));
            }
            return true;
        };
        armed = true;
        var tick = System.Threading.Tasks.Task.Run(() => hub.PollTick());
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));

        hub.SetLockBoundDevices(false);
        proceed.Set();
        await tick;

        Assert.False(hub.State.LockBoundDevices);
    }

    [Fact]
    public void The_lock_loads_from_the_setting_when_it_is_assigned_not_on_the_first_tick()
    {
        var (hub, _, _, _) = CreateConnectedHub();

        hub.LockBoundDevices = () => false;

        Assert.False(hub.State.LockBoundDevices);
    }

    [Fact]
    public void A_bind_due_mid_upload_goes_out_at_once()
    {
        var (hub, _, tx, fan, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        fan.RxType = 7;
        var during = -1;
        tx.OnSend = f =>
        {
            if (during >= 0 || f.Length < 6 || f[4] != Slv3Protocol.RfFrameType || f[5] != Slv3Protocol.RfRgbSync)
            {
                return;
            }
            var before = AllBindFrames(tx).Count;
            hub.PollTick();
            during = AllBindFrames(tx).Count - before;
        };

        Assert.True(hub.SendRgbFrame(FanKey, new[] { new Nexus.Service.Peripherals.Hyte.Np50.RgbColor(1, 2, 3) }, 100, 100, out _));

        Assert.Equal(1, during);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_late_pass_and_the_suspend_save_still_send_after_a_poll_timed_out()
    {
        var (hub, net, tx, _, _, _) = Stranded(Ours, 1, Slv3Protocol.DefaultChannel);
        Ticks(hub, 2);
        net.TxWedged = true;
        hub.DriveTick();
        net.TxWedged = false;
        tx.SentFrames.Clear();

        var frames = new byte[30 * 40 * 3];
        new Random(7).NextBytes(frames);
        Assert.True(await hub.SendRgbWindowAsync(FanKey, frames, 40, 30, 53, 100));
        await hub.OnSystemSuspending();

        // Chunk 0 of each RF payload carries the packet index at [22] and the count at [23]; each data part goes out
        // in the upload's passes and again in the late passes, four times in all.
        var parts = tx.SentFrames.FindAll(f => f.Length >= 24 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);
        var total = parts[0][23];
        Assert.True(total >= 2);
        for (var p = 1; p < total; p++)
        {
            Assert.Equal(4, parts.Count(f => f[22] == p));
        }
        Assert.Equal(3, SaveCfgFrames(tx).Count);
    }

    [Fact]
    public void A_slow_tx_gets_each_chain_an_attempt_in_turn_not_just_the_first()
    {
        var (hub, net, tx, _) = CreateConnectedHub();
        var owned = new Dictionary<string, int>();
        for (var i = 0; i < 3; i++)
        {
            owned[Convert.ToHexString(ChainMac(i))] = i + 1;
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), Pwm = new byte[] { 6, 6, 6, 6 } });
        }
        hub.OwnedDevicesLoad = () => owned;
        Ticks(hub, 1);
        net.TxWedged = true;
        foreach (var fan in net.Fans)
        {
            fan.IgnoresBind = true;
            fan.MasterMac = new byte[6];
            fan.RxType = 0;
        }
        tx.SentFrames.Clear();

        Ticks(hub, 3);

        var tried = AllBindFrames(tx).Select(f => Convert.ToHexString(f.AsSpan(6, 6))).Distinct().Count();
        Assert.Equal(3, tried);
    }

    [Fact]
    public void With_the_lock_on_owned_chains_are_never_unbound_by_the_adoption_cap_whatever_their_mac_order()
    {
        var (hub, net, _, _, _) = CreateOwnershipHub(new() { [Convert.ToHexString(ChainMac(40))] = 13 });
        for (var i = 0; i < Slv3Hub.AdoptionCap; i++)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 1), Pwm = new byte[] { 6, 6, 6, 6 } });
        }
        net.Fans.Add(new SimulatedFan { Mac = ChainMac(40), MasterMac = net.MasterMac, RxType = 13, Pwm = new byte[] { 6, 6, 6, 6 } });

        Ticks(hub, 8);

        Assert.True(net.Fans.Single(f => f.Mac.SequenceEqual(ChainMac(40))).MasterMac.SequenceEqual(net.MasterMac));
    }

    [Fact]
    public void The_chain_past_the_adoption_cap_is_the_last_newcomer_in_record_order()
    {
        var (hub, net, _, _) = CreateConnectedHub();
        // Reported highest MAC first, so the last reported is the lowest MAC.
        for (var i = Slv3Hub.AdoptionCap; i >= 0; i--)
        {
            net.Fans.Add(new SimulatedFan { Mac = ChainMac(i), MasterMac = net.MasterMac, RxType = (byte)(i + 1), Pwm = new byte[] { 6, 6, 6, 6 } });
        }

        Ticks(hub, 8);

        var loser = net.Fans.Single(f => !f.MasterMac.SequenceEqual(net.MasterMac));
        Assert.Equal(ChainMac(0), loser.Mac);
    }

    [Fact]
    public void Locking_back_on_turns_a_slot_conflict_unbind_back_into_a_bind_but_not_a_user_unbind()
    {
        var low = Convert.FromHexString("111111111111");
        var high = Convert.FromHexString("222222222222");
        var (hub, net, tx, _, _) = CreateOwnershipHub();
        var lockOn = false;
        hub.LockBoundDevices = () => lockOn;
        var loser = new SimulatedFan { Mac = high, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } };
        net.Fans.Add(loser);
        net.Fans.Add(new SimulatedFan { Mac = low, MasterMac = net.MasterMac, RxType = 4, Pwm = new byte[] { 6, 6, 6, 6 } });
        loser.IgnoresBind = true;
        Ticks(hub, Slv3Hub.SlotConflictLimit + 3);
        Assert.Contains(AllBindFrames(tx), f => f.AsSpan(12, 6).SequenceEqual(new byte[6]));
        Assert.Equal(new byte[6], AllBindFrames(tx).Last().AsSpan(12, 6).ToArray());

        lockOn = true;
        tx.SentFrames.Clear();
        Ticks(hub, 2);
        Assert.NotEmpty(AllBindFrames(tx));
        Assert.All(AllBindFrames(tx), f => Assert.Equal(net.MasterMac, f.AsSpan(12, 6).ToArray()));

        net.Fans.Single(f => f.Mac.SequenceEqual(low)).IgnoresBind = true;
        Assert.True(hub.Unbind("111111111111"));
        tx.SentFrames.Clear();
        Ticks(hub, 2);
        Assert.Contains(AllBindFrames(tx), f => f.AsSpan(6, 6).SequenceEqual(low));
        Assert.All(AllBindFrames(tx).Where(f => f.AsSpan(6, 6).SequenceEqual(low)), f => Assert.Equal(new byte[6], f.AsSpan(12, 6).ToArray()));
    }
}
