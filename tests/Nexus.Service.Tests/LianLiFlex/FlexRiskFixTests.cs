using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexRiskFixTests
{
    private const string Mac = "6D1007F517E1";

    private static (FlexHub Hub, FakeFlexTransport Tx, FlexClock Clock, FakeFlexDiscovery Discovery) Hub(Action<int>? sleep = null)
    {
        var tx = new FakeFlexTransport(Mac);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), sleep ?? (_ => { }));
        hub.Refresh();
        return (hub, tx, clock, discovery);
    }

    private sealed class Rig
    {
        public required FlexHub Hub { get; init; }
        public required FakeFlexTransport Tx { get; init; }
        public required FlexClock Clock { get; init; }
        public required InMemoryConfigStore Store { get; init; }
        public required FlexLightingFrameWriter Writer { get; init; }
        public required FlexLightingDeviceProvider Provider { get; init; }

        public void Run(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                Clock.Now += 100;
                Writer.Tick();
                Hub.Tick();
            }
        }
    }

    private static Rig CreateRig()
    {
        var (hub, tx, clock, _) = Hub();
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func, runUpload: work => Task.FromResult(work()));
        return new Rig { Hub = hub, Tx = tx, Clock = clock, Store = store, Writer = writer, Provider = provider };
    }

    [Fact]
    public void An_upload_that_is_not_saved_does_not_cancel_a_pending_flash_save()
    {
        var (hub, tx, clock, _) = Hub();
        Assert.True(hub.UploadPackage(Mac, new byte[100], 13, 26, 44, saveToFlash: true));
        Assert.True(hub.UploadPackage(Mac, new byte[100], 13, 26, 44, saveToFlash: false));
        clock.Now += FlexHub.SaveSettleMs + 10;
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void Identify_sends_nothing_to_a_unit_on_the_board_input_and_the_route_refuses_it()
    {
        var r = CreateRig();
        r.Store.Update(s => s.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { Mode = "rainbow", MotherboardArgb = true });
        r.Run(1000);
        r.Writer.Identify(Mac);
        r.Run(3000);
        Assert.Empty(r.Tx.Of(FlexProtocol.CmdLive));
        var unit = r.Hub.Find(Mac)!;
        Assert.True(FlexLightingDeviceProvider.PlaysMotherboardArgb(unit, r.Store.Load().Devices.FlexUnits));
        Assert.Equal((409, "fan follows the motherboard ARGB input"), LianLiFlexRoutes.IdentifyRefusal(unit, r.Store.Load().Devices.FlexUnits));
        Assert.Equal((404, "fan not found"), LianLiFlexRoutes.IdentifyRefusal(null, r.Store.Load().Devices.FlexUnits));
        Assert.Null(LianLiFlexRoutes.IdentifyRefusal(unit, new Dictionary<string, LianLiFlexUnitSettings> { [Mac] = new LianLiFlexUnitSettings { MotherboardArgb = false } }));
    }

    [Fact]
    public void Reset_hands_a_unit_back_to_usb_and_keeps_its_list_position()
    {
        var look = LianLiFlexRoutes.ResetLook(new LianLiFlexUnitSettings { SortIndex = 3, MotherboardArgb = true, Mode = "wave" });
        Assert.False(look.MotherboardArgb);
        Assert.Equal(3, look.SortIndex);
        Assert.Equal("rainbow", look.Mode);
        Assert.False(LianLiFlexRoutes.ResetLook(null).MotherboardArgb);
    }

    [Fact]
    public void A_zone_color_order_changes_the_stored_look_signature()
    {
        var r = CreateRig();
        var unit = r.Hub.Find(Mac)!;
        var look = new LianLiFlexUnitSettings { Mode = "wave" };
        var zone = Nexus.Service.Lighting.FlexLightingDeviceProvider.ZonesOf(unit.Family)[0];
        var settings = new Nexus.Service.Persistence.NexusSettings();
        var plain = FlexStoredLook.Signature(unit, look);
        Assert.Equal(plain, FlexStoredLook.Signature(unit, look, null, settings));
        settings.Devices.LightingDevicePrefs[Nexus.Service.Lighting.FlexLightingDeviceProvider.ZoneIdFor(Mac, zone)] = new() { ColorOrder = "GRB" };
        Assert.NotEqual(plain, FlexStoredLook.Signature(unit, look, null, settings));
    }

    [Fact]
    public void Renumbering_or_a_merge_toggle_does_not_change_what_is_uploaded_for_a_single_unit()
    {
        var r = CreateRig();
        var unit = r.Hub.Find(Mac)!;
        var a = FlexStoredLook.Signature(unit, new LianLiFlexUnitSettings { Mode = "wave", SortIndex = 0, Merge = false });
        var b = FlexStoredLook.Signature(unit, new LianLiFlexUnitSettings { Mode = "wave", SortIndex = 5, Merge = true });
        Assert.Equal(a, b);
        Assert.NotEqual(a, FlexStoredLook.Signature(unit, new LianLiFlexUnitSettings { Mode = "wave", Speed = 4 }));
    }

    [Fact]
    public void A_unit_that_left_and_returns_gets_its_board_input_request_tried_again()
    {
        var tx = new FakeFlexTransport(Mac) { EchoSyncBoard = false };
        tx.Info[29] = 0x80;
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        store.Update(s => s.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { Mode = "rainbow", MotherboardArgb = true });
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var writer = new FlexLightingFrameWriter(new LightingEngine(), hub, store, identify, provider, clock.Func, runUpload: work => Task.FromResult(work()));
        void Run(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                clock.Now += 100;
                writer.Tick();
                hub.Tick();
            }
        }
        Run(30_000);
        Assert.Equal(3, tx.Of(FlexProtocol.CmdSyncBoard).Count());

        discovery.Ports.Clear();
        hub.Refresh();
        Assert.Empty(hub.Units);
        Run(200);
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        hub.Refresh();
        Run(1000);
        Assert.Equal(4, tx.Of(FlexProtocol.CmdSyncBoard).Count());
    }

    [Fact]
    public async Task A_duty_change_waits_for_a_running_upload_and_is_sent_after_it()
    {
        using var inSleep = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (hub, tx, _, _) = Hub(_ =>
        {
            inSleep.Set();
            release.Wait(5000);
        });
        var upload = Task.Run(() => hub.UploadPackage(Mac, new byte[100], 13, 26, 44));
        Assert.True(inSleep.Wait(5000));

        hub.SetDuty(Mac, 40);
        var tick = Task.Run(hub.Tick);
        Assert.Same(tick, await Task.WhenAny(tick, Task.Delay(2000)));
        Assert.Empty(tx.Of(FlexProtocol.CmdSetPwm));

        release.Set();
        Assert.True(await upload.WaitAsync(TimeSpan.FromSeconds(5)));
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSetPwm));
    }

    [Fact]
    public void A_receiver_that_stops_answering_is_dropped_and_comes_back_when_it_answers_again()
    {
        var (hub, tx, clock, _) = Hub();
        Assert.Single(hub.Units);
        tx.AnswerInfo = false;
        for (var i = 0; i < 14; i++)
        {
            clock.Now += FlexHub.PollPeriodMs + 10;
            hub.Tick();
        }
        Assert.Empty(hub.Units);

        tx.AnswerInfo = true;
        hub.Refresh();
        Assert.Single(hub.Units);
    }

    [Fact]
    public void A_wanted_duty_survives_a_replug_and_one_set_while_the_receiver_is_away_is_kept()
    {
        var (hub, tx, clock, discovery) = Hub();
        hub.SetDuty(Mac, 40);
        hub.Tick();
        Assert.Single(tx.Of(FlexProtocol.CmdSetPwm));

        discovery.Ports.Clear();
        hub.Refresh();
        Assert.Empty(hub.Units);
        hub.SetDuty(Mac, 60);
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        hub.Refresh();
        hub.Tick();

        var last = tx.Of(FlexProtocol.CmdSetPwm).Last();
        Assert.Equal(FlexProtocol.BuildSetPwm(FlexProtocol.EncodeDuty(FlexFamily.TlFlex, 60)), last);
    }

    [Fact]
    public void A_duty_set_before_the_receiver_first_appears_is_applied_when_it_does()
    {
        var discovery = new FakeFlexDiscovery();
        var tx = new FakeFlexTransport(Mac);
        var hub = new FlexHub(discovery, _ => tx, new FlexClock().Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        hub.SetDuty(Mac, 55);
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        hub.Refresh();
        hub.Tick();
        Assert.Equal(FlexProtocol.BuildSetPwm(FlexProtocol.EncodeDuty(FlexFamily.TlFlex, 55)), tx.Of(FlexProtocol.CmdSetPwm).Single());
    }

    [Fact]
    public void A_requested_mode_and_duty_are_reported_until_the_echo_catches_up()
    {
        var tx = new FakeFlexTransport(Mac, pwm: FlexProtocol.PwmFollowMotherboard) { EchoPwm = true };
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var cooling = new Nexus.Service.Cooling.FlexCoolingProvider(hub);
        Assert.Equal(Nexus.Service.Models.Cooling.FanModes.Auto, cooling.GetFanChannels().Single().Mode);

        hub.SetDuty(Mac, 40);
        var asked = cooling.GetFanChannels().Single();
        Assert.Equal(Nexus.Service.Models.Cooling.FanModes.Manual, asked.Mode);
        Assert.Equal(40, asked.DutyPercent);

        clock.Now += FlexHub.PollPeriodMs + 10;
        hub.Tick();
        Assert.Equal(40, hub.PendingDuty(Mac));
        clock.Now += FlexHub.PollPeriodMs + 10;
        hub.Tick();
        Assert.Null(hub.PendingDuty(Mac));
        var settled = cooling.GetFanChannels().Single();
        Assert.Equal(Nexus.Service.Models.Cooling.FanModes.Manual, settled.Mode);
        Assert.Equal(FlexProtocol.DecodeDuty(FlexProtocol.EncodeDuty(FlexFamily.TlFlex, 40)), settled.DutyPercent);
    }

    [Fact]
    public void A_request_the_receiver_never_confirms_is_reported_only_for_a_short_time()
    {
        var tx = new FakeFlexTransport(Mac, pwm: FlexProtocol.PwmFollowMotherboard);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        hub.SetDuty(Mac, 40);
        Assert.Equal(40, hub.PendingDuty(Mac));
        clock.Now += FlexHub.PendingDutyMs + 1;
        Assert.Null(hub.PendingDuty(Mac));
        hub.SetDuty(Mac, null);
        Assert.Null(hub.PendingDuty(Mac));
    }

    [Fact]
    public void Releasing_everything_also_covers_a_receiver_that_is_away_so_it_returns_on_the_header()
    {
        var (hub, tx, _, discovery) = Hub();
        hub.SetDuty(Mac, 60);
        discovery.Ports.Clear();
        hub.Refresh();
        Assert.Empty(hub.Units);

        hub.ReleaseAllDuty();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        hub.Refresh();
        hub.Tick();

        Assert.Equal(FlexProtocol.BuildFollowMotherboard(), tx.Of(FlexProtocol.CmdSetPwm).Last());
    }

    [Fact]
    public void Repeating_the_same_request_does_not_restart_the_pending_window()
    {
        var tx = new FakeFlexTransport(Mac, pwm: FlexProtocol.PwmFollowMotherboard);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        hub.SetDuty(Mac, 40);
        for (var i = 0; i < 5; i++)
        {
            clock.Now += 700;
            hub.SetDuty(Mac, 40);
        }
        Assert.Null(hub.PendingDuty(Mac));
        hub.SetDuty(Mac, 50);
        Assert.NotNull(hub.PendingDuty(Mac));
    }

    [Fact]
    public void The_pending_duty_is_the_percent_the_receiver_will_echo_not_the_raw_request()
    {
        var tx = new FakeFlexTransport(Mac, pwm: FlexProtocol.PwmFollowMotherboard);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => tx, new FlexClock().Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        hub.SetDuty(Mac, 5);
        Assert.Equal(FlexProtocol.DecodeDuty(FlexProtocol.EncodeDuty(FlexFamily.TlFlex, 5)), hub.PendingDuty(Mac));
        Assert.True(hub.PendingDuty(Mac) > 5);
    }
}
