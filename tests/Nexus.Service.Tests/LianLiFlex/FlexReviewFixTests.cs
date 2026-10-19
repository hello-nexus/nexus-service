using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexReviewFixTests
{
    private const string Mac = "6D1007F517E1";
    private const string InfMac = "C5D212506CE1";

    private sealed class Rig
    {
        public required FlexHub Hub { get; init; }
        public required FakeFlexTransport Tx { get; init; }
        public required FlexClock Clock { get; init; }
        public required InMemoryConfigStore Store { get; init; }
        public required FlexLightingDeviceProvider Provider { get; init; }
        public required LightingEngine Engine { get; init; }
        public required FlexLightingFrameWriter Writer { get; init; }
        public required List<DeviceFrame> Frames { get; init; }

        public void Run(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                Clock.Now += 100;
                Writer.Tick();
                Hub.Tick();
            }
        }

        public void SetLook(LianLiFlexUnitSettings look) => Store.Update(s => s.Devices.FlexUnits[Mac] = look);
    }

    private static Rig CreateRig()
    {
        var tx = new FakeFlexTransport(Mac);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var frames = new List<DeviceFrame>(provider.BuildFrames(0));
        var engine = new LightingEngine();
        engine.UpdateDevices(frames.ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func,
            runUpload: work => Task.FromResult(work()));
        return new Rig { Hub = hub, Tx = tx, Clock = clock, Store = store, Provider = provider, Engine = engine, Writer = writer, Frames = frames };
    }

    // ----- live frames then the stored look -----

    [Fact]
    public void A_stored_look_is_uploaded_again_after_live_frames_replaced_it()
    {
        var r = CreateRig();
        r.SetLook(new LianLiFlexUnitSettings { Mode = "rainbow" });
        r.Run(1000);
        Assert.Single(r.Tx.Of(FlexProtocol.CmdEnable));

        r.SetLook(new LianLiFlexUnitSettings { Mode = "custom" });
        r.Frames[0].Fill(9, 9, 9);
        r.Frames[0].Publish();
        r.Run(600);
        Assert.NotEmpty(r.Tx.Of(FlexProtocol.CmdLive));

        r.SetLook(new LianLiFlexUnitSettings { Mode = "rainbow" });
        r.Run(1000);
        Assert.Equal(2, r.Tx.Of(FlexProtocol.CmdEnable).Count());
    }

    [Fact]
    public void The_persisted_package_shortcut_is_not_taken_when_live_frames_went_out_in_between()
    {
        var r = CreateRig();
        r.SetLook(new LianLiFlexUnitSettings { Mode = "rainbow" });
        r.Run(1000);
        var look = r.Store.Load().Devices.FlexUnits[Mac];
        r.Tx.Info[21] = Convert.FromHexString(look.PackageEffectIndex!)[0];
        r.Tx.Info[22] = Convert.FromHexString(look.PackageEffectIndex!)[1];
        r.Tx.Info[23] = Convert.FromHexString(look.PackageEffectIndex!)[2];
        r.Tx.Info[24] = Convert.FromHexString(look.PackageEffectIndex!)[3];
        // a fresh writer (a restart) streams live first, then the user picks the saved look
        var restarted = new FlexLightingFrameWriter(r.Engine, r.Hub, r.Store, new Np50IdentifyTracker(), r.Provider, r.Clock.Func,
            runUpload: work => Task.FromResult(work()));
        r.SetLook(new LianLiFlexUnitSettings { Mode = "custom", PackageSignature = look.PackageSignature, PackageEffectIndex = look.PackageEffectIndex });
        r.Frames[0].Fill(5, 5, 5);
        r.Frames[0].Publish();
        for (var i = 0; i < 10; i++)
        {
            r.Clock.Now += 100;
            restarted.Tick();
            r.Hub.Tick();
        }
        var before = r.Tx.Of(FlexProtocol.CmdEnable).Count();
        r.SetLook(new LianLiFlexUnitSettings { Mode = "rainbow", PackageSignature = look.PackageSignature, PackageEffectIndex = look.PackageEffectIndex });
        for (var i = 0; i < 20; i++)
        {
            r.Clock.Now += 100;
            restarted.Tick();
            r.Hub.Tick();
        }
        Assert.Equal(before + 1, r.Tx.Of(FlexProtocol.CmdEnable).Count());
    }

    [Fact]
    public void After_identify_the_stored_look_comes_back_without_a_flash_save()
    {
        var r = CreateRig();
        r.SetLook(new LianLiFlexUnitSettings { Mode = "rainbow" });
        r.Run(1000);
        r.Run(FlexHub.SaveSettleMs + 500);
        Assert.Single(r.Tx.Of(FlexProtocol.CmdSave));
        r.Writer.Identify(Mac);
        r.Run(4000);
        r.Run(FlexHub.SaveSettleMs + 500);
        Assert.Equal(2, r.Tx.Of(FlexProtocol.CmdEnable).Count());
        Assert.Single(r.Tx.Of(FlexProtocol.CmdSave));
    }

    // ----- merged defaults and default colors -----

    private static (FlexHub Hub, InMemoryConfigStore Store) TwoUnits()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("tl"));
        discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl"
            ? new FakeFlexTransport(Mac)
            : new FakeFlexTransport(InfMac, fansType: 0x2B), new FlexClock().Func, null, _ => { });
        hub.Refresh();
        return (hub, new InMemoryConfigStore());
    }

    [Theory]
    [InlineData(FlexFamily.TlFlex)]
    [InlineData(FlexFamily.SlInfFlex)]
    public void Merged_effects_with_no_colors_are_not_black_for_every_mergeable_key(FlexFamily leaderFamily)
    {
        var (hub, store) = TwoUnits();
        var leaderMac = leaderFamily == FlexFamily.TlFlex ? Mac : InfMac;
        foreach (var key in FlexMergeEffects.MergeKeys(leaderFamily).Where(k => !FlexMergeEffects.IsPerReceiver(k)))
        {
            store.Update(s =>
            {
                s.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { Mode = key, Merge = true, SortIndex = leaderMac == Mac ? 0 : 1 };
                s.Devices.FlexUnits[InfMac] = new LianLiFlexUnitSettings { Mode = key, Merge = true, SortIndex = leaderMac == InfMac ? 0 : 1 };
            });
            var group = FlexStoredLook.FindMergeGroup(hub.Units, store.Load().Devices.FlexUnits);
            Assert.NotNull(group);
            Assert.Equal(leaderFamily, group!.LeaderFamily);
            var slices = FlexMergeEffects.Render(key, group.Participants, 3, 0, 4, FlexStoredLook.MergeColors(group));
            Assert.All(slices, sl => Assert.Contains(sl.Frames, b => b != 0));
        }
    }

    [Fact]
    public void A_merge_group_resolves_one_palette_from_the_leader_for_every_member()
    {
        var (hub, store) = TwoUnits();
        store.Update(s =>
        {
            s.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { Mode = "wave", Merge = true, SortIndex = 1, Colors = new() { "#010203" } };
            s.Devices.FlexUnits[InfMac] = new LianLiFlexUnitSettings { Mode = "wave", Merge = true, SortIndex = 0, Colors = new() { "#FF0000" } };
        });
        var group = FlexStoredLook.FindMergeGroup(hub.Units, store.Load().Devices.FlexUnits)!;
        Assert.Equal(InfMac, group.LeaderMac);
        var colors = FlexStoredLook.MergeColors(group);
        Assert.Equal(new RgbColor(255, 0, 0), colors[0]);
        var units = hub.Units.ToDictionary(u => u.Mac);
        var a = FlexStoredLook.Signature(units[Mac], store.Load().Devices.FlexUnits[Mac], group);
        var b = FlexStoredLook.Signature(units[InfMac], store.Load().Devices.FlexUnits[InfMac], group);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(FlexFamily.TlFlex)]
    [InlineData(FlexFamily.SlInfFlex)]
    public void Default_colors_match_what_an_effect_renders_when_no_colors_are_set(FlexFamily family)
    {
        foreach (var mode in LianLiFlexRoutes.ModesOf(family))
        {
            var defaults = mode.DefaultColors.Select(h => ColorOf(h)).ToList();
            byte[] Render(IReadOnlyList<RgbColor> explicitColors)
            {
                var zones = new Dictionary<string, FlexZoneLook>
                {
                    ["all"] = new FlexZoneLook(mode.Key, 3, 0, 4, explicitColors),
                };
                return FlexAnimationComposer.Compose(family, 1, zones, false).Raw;
            }
            var stored = FlexStoredLook.ZoneLooks(family, new LianLiFlexUnitSettings { Mode = mode.Key })["all"];
            var noColors = FlexAnimationComposer.Compose(family, 1, new Dictionary<string, FlexZoneLook> { ["all"] = stored }, false).Raw;
            if (defaults.Count == 0)
            {
                // the effect takes no palette: any colors give the same frames
                var other = new List<RgbColor> { new(1, 2, 3), new(4, 5, 6), new(7, 8, 9), new(10, 11, 12) };
                Assert.True(Render(other).SequenceEqual(noColors), $"{family}/{mode.Key} reads colors but lists none");
                Assert.Equal(0, mode.ColorsMax);
            }
            else if (!FlexDefaultPalettes.RendersFromTable(family, mode.Key))
            {
                Assert.True(Render(defaults).SequenceEqual(noColors), $"{family}/{mode.Key} default colors differ from what it renders");
                Assert.True(mode.ColorsMax >= defaults.Count || mode.ColorsMax == 4);
            }
        }
    }

    private static RgbColor ColorOf(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ----- hub fixes -----

    [Fact]
    public void Concurrent_ticks_after_the_settle_time_send_one_flash_save()
    {
        for (var round = 0; round < 25; round++)
        {
            var tx = new FakeFlexTransport(Mac);
            var discovery = new FakeFlexDiscovery();
            discovery.Ports.Add(FakeFlexDiscovery.Tl());
            var clock = new FlexClock();
            var hub = new FlexHub(discovery, _ => tx, clock.Func, null, _ => { });
            hub.Refresh();
            hub.UploadPackage(Mac, new byte[100], 13, 26, 44);
            clock.Now += FlexHub.SaveSettleMs + 10;
            using var gate = new Barrier(4);
            var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                gate.SignalAndWait();
                hub.Tick();
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.Single(tx.Of(FlexProtocol.CmdSave));
        }
    }

    [Fact]
    public void An_rf_bound_receiver_gets_no_clock_sync_and_a_master_mac_alone_counts_as_bound()
    {
        var tx = new FakeFlexTransport(Mac, rxType: 3);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, null, _ => { });
        hub.Refresh();
        clock.Now += 10_000;
        hub.Tick();
        Assert.Empty(tx.Of(FlexProtocol.CmdSyncClock));

        var frame = FakeFlexTransport.InfoFrame(Mac, 0x33, FlexProtocol.UnboundRxType, 100, 0);
        frame[7] = 0x11;
        Assert.True(FlexProtocol.ParseInfo(frame)!.RfBound);
        Assert.False(FlexProtocol.ParseInfo(FakeFlexTransport.InfoFrame(Mac, 0x33, FlexProtocol.UnboundRxType, 100, 0))!.RfBound);
    }

    [Fact]
    public async Task Opening_a_receiver_does_not_hold_the_lock_the_readers_use()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hub = new FlexHub(discovery, _ =>
        {
            entered.Set();
            release.Wait(5000);
            return new FakeFlexTransport(Mac);
        }, new FlexClock().Func, null, _ => { });
        var opener = Task.Run(hub.Refresh);
        Assert.True(entered.Wait(5000));
        var reader = Task.Run(() => hub.SetDuty(Mac, 40));
        Assert.Same(reader, await Task.WhenAny(reader, Task.Delay(2000)));
        release.Set();
        await opener.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(hub.Units);
    }

    // ----- sort index -----

    [Fact]
    public void Moving_a_unit_renumbers_every_stored_unit_including_absent_ones()
    {
        var units = new Dictionary<string, LianLiFlexUnitSettings>
        {
            ["A"] = new() { SortIndex = 0 },
            ["B"] = new() { SortIndex = 1 },
            ["C"] = new() { SortIndex = 2 },
            ["D"] = new() { SortIndex = 3 },
        };
        FlexStoredLook.MoveSortIndex(units, "D", 1);
        Assert.Equal(new[] { "A", "D", "B", "C" }, units.OrderBy(u => u.Value.SortIndex).Select(u => u.Key).ToArray());
        FlexStoredLook.MoveSortIndex(units, "A", 99);
        Assert.Equal(new[] { "D", "B", "C", "A" }, units.OrderBy(u => u.Value.SortIndex).Select(u => u.Key).ToArray());
        Assert.Equal(4, units.Values.Select(u => u.SortIndex).Distinct().Count());
    }

    [Fact]
    public void A_negative_sort_index_is_refused()
    {
        Assert.NotNull(LianLiFlexRoutes.ValidateLighting(new LianLiFlexChainLightingRequest { SortIndex = -1 }, FlexFamily.TlFlex));
        Assert.Null(LianLiFlexRoutes.ValidateLighting(new LianLiFlexChainLightingRequest { SortIndex = 0 }, FlexFamily.TlFlex));
    }

    // ----- restart replay -----

    private sealed class FakeLcd : ISlv3LcdTransport
    {
        public List<string> Calls { get; } = new();
        public bool IsOpen => true;
        public string PortName => "p";
        public bool PushImage(byte[] jpeg) { Calls.Add("push"); return true; }
        public bool SetBrightness(byte value) { Calls.Add("bright"); return true; }
        public bool SetRotation(byte value) { Calls.Add("rot"); return true; }
        public bool TryGetPosition(out byte groupIndex) { groupIndex = 0; return false; }
        public void Dispose() { }
    }

    private sealed class Ports : ISlv3LcdDiscovery
    {
        public IReadOnlyList<Slv3LcdPortInfo> Discover() => new[] { new Slv3LcdPortInfo { PortName = "p", Serial = "5C00C251854D2B79" } };
    }

    [Fact]
    public void A_restarted_screen_stays_hidden_until_it_was_seen_gone_and_back_and_the_unit_flag_follows()
    {
        var fake = new FakeLcd();
        var lcd = new FlexLcdHub(new Slv3LcdHub(new Ports(), _ => fake));
        var screen = new Slv3LcdScreenInfo { Serial = "5C00C251854D2B79" };
        lcd.Hub.UpdateScreens(new[] { screen });
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => new FakeFlexTransport(Mac), clock.Func, null, _ => { });
        hub.Refresh();
        var screens = new FlexScreens(lcd, hub, clock.Func);
        Assert.True(screens.Restart(Mac));
        Assert.True(hub.Units[0].LcdRestarting);

        clock.Now += 1000;
        Assert.Empty(screens.AttachedSerials());
        lcd.Hub.UpdateScreens(Array.Empty<Slv3LcdScreenInfo>());
        Assert.Empty(screens.AttachedSerials());
        clock.Now += 500;
        lcd.Hub.UpdateScreens(new[] { screen });
        Assert.Single(screens.AttachedSerials());
        Assert.False(hub.Units[0].LcdRestarting);
        Assert.True(screens.SetRotation("5C00C251854D2B79", 1));
        Assert.Contains("rot", fake.Calls);
    }

    [Fact]
    public void A_screen_that_never_returns_is_released_after_the_give_up_time()
    {
        var lcd = new FlexLcdHub(new Slv3LcdHub(new Ports(), _ => new FakeLcd()));
        lcd.Hub.UpdateScreens(Array.Empty<Slv3LcdScreenInfo>());
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => new FakeFlexTransport(Mac), clock.Func, null, _ => { });
        hub.Refresh();
        var screens = new FlexScreens(lcd, hub, clock.Func);
        Assert.True(screens.Restart(Mac));
        clock.Now += FlexScreens.RestartGiveUpMs + 1;
        Assert.Empty(screens.AttachedSerials());
        Assert.False(hub.Units[0].LcdRestarting);
    }

    // ----- catalog -----

    [Theory]
    [InlineData("0x43A8", "0x0102")]
    [InlineData("0x43A8", "0x0104")]
    [InlineData("0x1CBE", "0xA018")]
    [InlineData("0x1CBE", "0xA019")]
    public void The_supported_devices_catalog_lists_the_flex_hardware(string vid, string pid)
    {
        Assert.Contains(LightingDevicesCatalog.All, d =>
            d.Source == "nexus" && d.VendorId == vid && d.ProductId == pid && d.Vendor == "Lian Li");
    }
}
