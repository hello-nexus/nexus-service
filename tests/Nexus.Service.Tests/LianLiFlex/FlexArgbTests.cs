using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexArgbTests
{
    private const string Mac = "6D1007F517E1";

    private sealed class Rig
    {
        public required FlexHub Hub { get; init; }
        public required FakeFlexTransport Tx { get; init; }
        public required FlexClock Clock { get; init; }
        public required InMemoryConfigStore Store { get; init; }
        public required FlexLightingDeviceProvider Provider { get; init; }
        public required FlexLightingFrameWriter Writer { get; init; }

        public void Run(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                Clock.Now += 100;
                Writer.Tick();
                Hub.Tick();
            }
        }

        public void SetLook(Action<LianLiFlexUnitSettings> edit) => Store.Update(s =>
        {
            s.Devices.FlexUnits.TryGetValue(Mac, out var old);
            var next = (old ?? new LianLiFlexUnitSettings { Mode = "rainbow" }).Clone();
            edit(next);
            s.Devices.FlexUnits = new Dictionary<string, LianLiFlexUnitSettings>(s.Devices.FlexUnits) { [Mac] = next };
        });
    }

    private static Rig CreateRig(byte rxType = FlexProtocol.UnboundRxType, bool cable = true)
    {
        var tx = new FakeFlexTransport(Mac, rxType: rxType);
        if (cable)
        {
            tx.Info[29] = 0x80;
        }
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func, runUpload: work => Task.FromResult(work()));
        return new Rig { Hub = hub, Tx = tx, Clock = clock, Store = store, Provider = provider, Writer = writer };
    }

    [Fact]
    public void The_info_flags_report_the_cable_and_the_board_input()
    {
        var info = FlexProtocol.ParseInfo(FakeFlexTransport.InfoFrame(Mac, 0x33, FlexProtocol.UnboundRxType, 100, 0))!;
        Assert.False(info.ArgbCableConnected);
        var frame = FakeFlexTransport.InfoFrame(Mac, 0x33, FlexProtocol.UnboundRxType, 100, 0);
        frame[29] = 0xC0;
        info = FlexProtocol.ParseInfo(frame)!;
        Assert.True(info.ArgbCableConnected);
        Assert.True(info.PlayingMotherboardArgb);
        Assert.Equal(new byte[] { 0x14, 1 }, FlexProtocol.BuildSyncBoard(true).Take(2).ToArray());
        Assert.Equal(0, FlexProtocol.BuildSyncBoard(false)[1]);
    }

    [Fact]
    public void The_board_command_is_never_sent_to_an_rf_bound_unit()
    {
        var r = CreateRig(rxType: 3);
        Assert.False(r.Hub.SetMotherboardArgb(Mac, true));
        Assert.Empty(r.Tx.Of(FlexProtocol.CmdSyncBoard));
    }

    [Fact]
    public void The_saved_choice_is_sent_until_the_unit_reports_it_and_uploads_stop_meanwhile()
    {
        var r = CreateRig();
        r.SetLook(l => l.MotherboardArgb = true);
        r.Run(2000);
        Assert.Single(r.Tx.Of(FlexProtocol.CmdSyncBoard));
        Assert.Equal(1, r.Tx.Of(FlexProtocol.CmdSyncBoard).First()[1]);
        Assert.True(r.Hub.Find(Mac)!.Info.PlayingMotherboardArgb);
        Assert.Empty(r.Tx.Of(FlexProtocol.CmdPackage));
        Assert.Empty(r.Tx.Of(FlexProtocol.CmdLive));
        r.Run(10_000);
        Assert.Empty(r.Tx.Of(FlexProtocol.CmdPackage));
        Assert.Single(r.Tx.Of(FlexProtocol.CmdSyncBoard));
    }

    [Fact]
    public void A_unit_that_never_follows_gets_three_tries_five_seconds_apart()
    {
        var r = CreateRig();
        r.Tx.EchoSyncBoard = false;
        r.SetLook(l => l.MotherboardArgb = true);
        r.Run(30_000);
        Assert.Equal(3, r.Tx.Of(FlexProtocol.CmdSyncBoard).Count());
    }

    [Fact]
    public void Returning_to_usb_sends_zero_and_the_stored_look_is_uploaded_again()
    {
        var r = CreateRig();
        r.SetLook(l => l.MotherboardArgb = true);
        r.Run(2000);
        r.SetLook(l => l.MotherboardArgb = false);
        r.Run(3000);
        Assert.Equal(0, r.Tx.Of(FlexProtocol.CmdSyncBoard).Last()[1]);
        Assert.NotEmpty(r.Tx.Of(FlexProtocol.CmdPackage));
    }

    [Fact]
    public void A_unit_on_the_board_input_is_a_card_the_engine_never_drives_and_leaves_merge_groups()
    {
        var r = CreateRig();
        r.SetLook(l => { l.MotherboardArgb = true; l.Merge = true; l.Mode = "wave"; });
        var cards = r.Provider.GetAll().Devices;
        Assert.NotEmpty(cards);
        Assert.All(cards, c => Assert.Equal(LightingDevice.ReasonArgbInput, c.DevicePageReason));
        Assert.Empty(r.Provider.GetStructures());
        Assert.Empty(r.Provider.BuildFrames(0));
        Assert.Null(FlexStoredLook.FindMergeGroup(r.Hub.Units, r.Store.Load().Devices.FlexUnits));
        r.SetLook(l => l.MotherboardArgb = false);
        Assert.NotEmpty(r.Provider.GetStructures());
    }

    [Fact]
    public void The_lighting_route_stores_the_choice_and_reports_the_flags()
    {
        var r = CreateRig();
        var next = LianLiFlexRoutes.Patch(new LianLiFlexUnitSettings(), new LianLiFlexChainLightingRequest { MotherboardArgb = true }, FlexFamily.TlFlex);
        Assert.True(next.MotherboardArgb);
        r.Store.Update(s => s.Devices.FlexUnits[Mac] = next);
        var chain = LianLiFlexRoutes.BuildLighting(r.Hub, r.Store.Load()).Chains.Single();
        Assert.True(chain.MotherboardArgb);
        Assert.True(chain.ArgbCableConnected);
    }
}
