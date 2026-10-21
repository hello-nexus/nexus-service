using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexHubTests
{
    private static (FlexHub Hub, FakeFlexTransport Tx, FlexClock Clock) Create(FakeFlexTransport? tx = null)
    {
        tx ??= new FakeFlexTransport();
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new System.DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        return (hub, tx, clock);
    }

    [Fact]
    public void Refresh_opens_the_receiver_and_publishes_a_unit_keyed_by_mac()
    {
        var (hub, _, _) = Create();
        var unit = Assert.Single(hub.Units);
        Assert.Equal("6D1007F517E1", unit.Mac);
        Assert.Equal(FlexFamily.TlFlex, unit.Family);
        Assert.Equal(22, unit.FirmwareVersion);
        Assert.Equal("5C00C251854D2B79", unit.ScreenSerial);
        Assert.True(unit.Controllable);
        Assert.Equal(26, unit.LedCount);
    }

    [Fact]
    public void An_rf_bound_receiver_is_not_controllable_and_takes_no_frames_or_duty()
    {
        var (hub, tx, clock) = Create(new FakeFlexTransport(rxType: 3));
        Assert.False(hub.Units[0].Controllable);
        Assert.False(hub.SendLive("6D1007F517E1", new byte[26 * 3]));
        hub.SetDuty("6D1007F517E1", 50);
        clock.Now += 1000;
        hub.Tick();
        Assert.Empty(tx.Of(FlexProtocol.CmdSetPwm));
        Assert.Empty(tx.Of(FlexProtocol.CmdLive));
    }

    [Fact]
    public void Duty_is_written_only_when_it_changes()
    {
        var (hub, tx, clock) = Create();
        hub.SetDuty("6D1007F517E1", 50);
        for (var i = 0; i < 5; i++)
        {
            clock.Now += 1000;
            hub.Tick();
        }
        var pwm = Assert.Single(tx.Of(FlexProtocol.CmdSetPwm));
        Assert.Equal(128, pwm[1]);
        hub.SetDuty("6D1007F517E1", null);
        clock.Now += 1000;
        hub.Tick();
        Assert.Equal(2, tx.Of(FlexProtocol.CmdSetPwm).Count());
        Assert.Equal(6, tx.Of(FlexProtocol.CmdSetPwm).Last()[1]);
    }

    [Fact]
    public void Poll_runs_at_the_receiver_cadence()
    {
        var (hub, tx, clock) = Create();
        var before = tx.Of(FlexProtocol.CmdGetInfo).Count();
        clock.Now += 100;
        hub.Tick();
        Assert.Equal(before, tx.Of(FlexProtocol.CmdGetInfo).Count());
        clock.Now += FlexHub.PollPeriodMs;
        hub.Tick();
        Assert.Equal(before + 1, tx.Of(FlexProtocol.CmdGetInfo).Count());
    }

    [Fact]
    public void Clock_sync_runs_every_three_seconds_and_pauses_while_streaming()
    {
        var (hub, tx, clock) = Create();
        clock.Now += 3100;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSyncClock));
        hub.SendLive("6D1007F517E1", new byte[26 * 3]);
        clock.Now += 3100;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSyncClock));
        clock.Now += 5000;
        hub.Tick();
        Assert.Equal(2, tx.Of(FlexProtocol.CmdSyncClock).Count());
    }

    [Fact]
    public void Live_frames_commit_on_the_last_packet_only()
    {
        var (hub, tx, _) = Create();
        Assert.True(hub.SendLive("6D1007F517E1", new byte[26 * 3]));
        var live = tx.Of(FlexProtocol.CmdLive).ToList();
        Assert.Equal(2, live.Count);
        Assert.Equal(0, live[0][3]);
        Assert.Equal(2, live[1][3]);
    }

    [Fact]
    public void An_upload_arms_one_flash_save_after_the_settle_time()
    {
        var (hub, tx, clock) = Create();
        Assert.True(hub.UploadPackage("6D1007F517E1", new byte[100], 13, 26, 44));
        Assert.Single(tx.Of(FlexProtocol.CmdEnable));
        Assert.Empty(tx.Of(FlexProtocol.CmdSave));
        clock.Now += FlexHub.SaveSettleMs - 1;
        hub.Tick();
        Assert.Empty(tx.Of(FlexProtocol.CmdSave));
        clock.Now += 2;
        hub.Tick();
        var save = Assert.Single(tx.Of(FlexProtocol.CmdSave));
        Assert.Equal(1, save[1]);
        clock.Now += 60_000;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void Repeated_uploads_push_the_save_out_and_save_once()
    {
        var (hub, tx, clock) = Create();
        for (var i = 0; i < 5; i++)
        {
            hub.UploadPackage("6D1007F517E1", new byte[100], 13, 26, 44);
            clock.Now += 4000;
            hub.Tick();
        }
        Assert.Empty(tx.Of(FlexProtocol.CmdSave));
        clock.Now += FlexHub.SaveSettleMs;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void Streaming_alone_never_saves()
    {
        var (hub, tx, clock) = Create();
        for (var i = 0; i < 20; i++)
        {
            hub.SendLive("6D1007F517E1", new byte[26 * 3]);
            clock.Now += 2000;
            hub.Tick();
        }
        Assert.Empty(tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void Nothing_off_the_allow_list_is_ever_written()
    {
        var (hub, tx, clock) = Create();
        hub.SetDuty("6D1007F517E1", 30);
        hub.SendLive("6D1007F517E1", new byte[26 * 3]);
        hub.UploadPackage("6D1007F517E1", new byte[200], 13, 26, 44);
        hub.RebootLcd("6D1007F517E1", 1000);
        clock.Now += 20_000;
        hub.Tick();
        Assert.All(tx.Written, f => Assert.True(FlexProtocol.IsAllowed(f)));
        Assert.DoesNotContain(tx.Written, f => f[0] == FlexProtocol.CmdSave && f[1] == 0);
    }

    [Fact]
    public void A_vanished_receiver_is_dropped_and_the_change_is_announced()
    {
        var tx = new FakeFlexTransport();
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => tx, new FlexClock().Func, null, _ => { });
        var changes = 0;
        hub.UnitsChanged += () => changes++;
        hub.Refresh();
        discovery.Ports.Clear();
        hub.Refresh();
        Assert.Empty(hub.Units);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_receiver_that_will_not_open_is_reported_busy_and_retried_later()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var opens = 0;
        var hub = new FlexHub(discovery, _ =>
        {
            opens++;
            throw new System.IO.IOException("busy");
        }, clock.Func, null, _ => { });
        hub.Refresh();
        Assert.Equal("busy", hub.LinkStatus);
        hub.Refresh();
        Assert.Equal(1, opens);
        clock.Now += 11_000;
        hub.Refresh();
        Assert.Equal(2, opens);
    }

    [Fact]
    public void Reboot_lcd_marks_the_unit_restarting_and_resends_duty_afterwards()
    {
        var (hub, tx, clock) = Create();
        hub.SetDuty("6D1007F517E1", 40);
        clock.Now += 1000;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSetPwm));
        Assert.True(hub.RebootLcd("6D1007F517E1", 5000));
        Assert.Single(tx.Of(FlexProtocol.CmdRebootLcd));
        Assert.True(hub.Units[0].LcdRestarting);
        clock.Now += 1000;
        hub.Tick();
        Assert.Equal(2, tx.Of(FlexProtocol.CmdSetPwm).Count());
        clock.Now += 6000;
        hub.Tick();
        Assert.False(hub.Units[0].LcdRestarting);
    }
}
