using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexLightingFrameWriterTests
{
    private const string Mac = "6D1007F517E1";

    private sealed class Setup
    {
        public required FlexHub Hub { get; init; }
        public required FakeFlexTransport Tx { get; init; }
        public required FlexClock Clock { get; init; }
        public required InMemoryConfigStore Store { get; init; }
        public required FlexLightingDeviceProvider Provider { get; init; }
        public required LightingEngine Engine { get; init; }
        public required FlexLightingFrameWriter Writer { get; init; }
        public required List<DeviceFrame> Frames { get; init; }
    }

    private static Setup Create(bool slInf = false)
    {
        var tx = slInf
            ? new FakeFlexTransport("C5D212506CE1", fansType: 0x2B)
            : new FakeFlexTransport(Mac);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(slInf
            ? new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId, ScreenSerial = "S" }
            : FakeFlexDiscovery.Tl());
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, _ => tx, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var frames = new List<DeviceFrame>(provider.BuildFrames(0));
        var engine = new LightingEngine();
        engine.UpdateDevices(frames.ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func, runUpload: work => System.Threading.Tasks.Task.FromResult(work()));
        return new Setup { Hub = hub, Tx = tx, Clock = clock, Store = store, Provider = provider, Engine = engine, Writer = writer, Frames = frames };
    }

    private static void Paint(DeviceFrame frame, byte r, byte g, byte b)
    {
        frame.Fill(r, g, b);
        frame.Publish();
    }

    private static byte[] Wire(FakeFlexTransport tx, int leds)
    {
        var wire = new byte[leds * 3];
        foreach (var p in tx.Of(FlexProtocol.CmdLive).Reverse().Take((leds + 19) / 20).Reverse())
        {
            var start = p[1];
            var n = p[2];
            Array.Copy(p, 4, wire, start * 3, n * 3);
        }
        return wire;
    }

    [Fact]
    public void A_tl_fan_has_up_and_down_zone_frames_of_thirteen_leds()
    {
        var s = Create();
        Assert.Equal(new[] { 13, 13 }, s.Frames.Select(f => f.LedCount).ToArray());
        Assert.Equal(new[] { $"lianli-flex:{Mac}:up", $"lianli-flex:{Mac}:down" }, s.Frames.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void An_sl_inf_fan_has_center_inner_and_outer_zone_frames()
    {
        var s = Create(slInf: true);
        Assert.Equal(new[] { 8, 20, 16 }, s.Frames.Select(f => f.LedCount).ToArray());
    }

    [Fact]
    public void Lighting_page_mode_streams_each_zone_to_its_wire_leds()
    {
        var s = Create();
        Paint(s.Frames[0], 255, 0, 0);
        Paint(s.Frames[1], 0, 0, 255);
        s.Writer.Tick();
        var wire = Wire(s.Tx, 26);
        Assert.Equal(255, wire[0]);
        Assert.Equal(255, wire[12 * 3]);
        Assert.Equal(0, wire[13 * 3]);
        Assert.Equal(255, wire[(13 * 3) + 2]);
        Assert.Equal(255, wire[(25 * 3) + 2]);
    }

    [Fact]
    public void Sl_inf_zones_land_on_their_split_wire_ranges()
    {
        var s = Create(slInf: true);
        Paint(s.Frames[0], 255, 0, 0);
        Paint(s.Frames[1], 0, 255, 0);
        Paint(s.Frames[2], 0, 0, 255);
        s.Writer.Tick();
        var wire = Wire(s.Tx, 44);
        Assert.Equal(255, wire[0]);
        Assert.Equal(255, wire[(8 * 3) + 1]);
        Assert.Equal(255, wire[(35 * 3) + 1]);
        Assert.Equal(255, wire[(18 * 3) + 2]);
        Assert.Equal(255, wire[(43 * 3) + 2]);
        Assert.Equal(0, wire[(26 * 3) + 2]);
    }

    [Fact]
    public void Up_down_swap_exchanges_the_halves_on_the_wire()
    {
        var s = Create();
        s.Store.Update(x => x.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { UpDownSwap = true });
        Paint(s.Frames[0], 255, 0, 0);
        Paint(s.Frames[1], 0, 0, 255);
        s.Writer.Tick();
        var wire = Wire(s.Tx, 26);
        Assert.Equal(255, wire[13 * 3]);
        Assert.Equal(255, wire[2]);
    }

    [Fact]
    public void An_unchanged_frame_is_resent_only_on_the_refresh_interval()
    {
        var s = Create();
        Paint(s.Frames[0], 10, 20, 30);
        s.Writer.Tick();
        var first = s.Tx.Of(FlexProtocol.CmdLive).Count();
        s.Clock.Now += 100;
        s.Writer.Tick();
        Assert.Equal(first, s.Tx.Of(FlexProtocol.CmdLive).Count());
        s.Clock.Now += 1000;
        s.Writer.Tick();
        Assert.Equal(first * 2, s.Tx.Of(FlexProtocol.CmdLive).Count());
    }

    [Fact]
    public void A_changed_frame_streams_at_once()
    {
        var s = Create();
        Paint(s.Frames[0], 10, 20, 30);
        s.Writer.Tick();
        var first = s.Tx.Of(FlexProtocol.CmdLive).Count();
        s.Clock.Now += 50;
        Paint(s.Frames[0], 90, 20, 30);
        s.Writer.Tick();
        Assert.True(s.Tx.Of(FlexProtocol.CmdLive).Count() > first);
    }

    [Fact]
    public void Streaming_never_saves_to_flash()
    {
        var s = Create();
        for (var i = 0; i < 30; i++)
        {
            Paint(s.Frames[0], (byte)(i * 8), 0, 0);
            s.Clock.Now += 500;
            s.Writer.Tick();
            s.Hub.Tick();
        }
        Assert.Empty(s.Tx.Of(FlexProtocol.CmdSave));
        Assert.Empty(s.Tx.Of(FlexProtocol.CmdPackage));
    }

    [Fact]
    public void Identify_flashes_the_unit_without_touching_flash()
    {
        var s = Create();
        s.Writer.Identify(Mac);
        s.Writer.Tick();
        s.Clock.Now += 400;
        s.Writer.Tick();
        Assert.NotEmpty(s.Tx.Of(FlexProtocol.CmdLive));
        Assert.Empty(s.Tx.Of(FlexProtocol.CmdSave));
    }

    private static void SetLook(Setup s, LianLiFlexUnitSettings look) => s.Store.Update(x => x.Devices.FlexUnits[Mac] = look);

    private static void Run(Setup s, int ms)
    {
        for (var t = 0; t < ms; t += 100)
        {
            s.Clock.Now += 100;
            s.Writer.Tick();
            s.Hub.Tick();
        }
    }

    [Fact]
    public void A_stored_effect_uploads_once_after_the_settings_settle_and_saves_once_later()
    {
        var s = Create();
        SetLook(s, new LianLiFlexUnitSettings { Mode = "rainbow" });
        Run(s, 1000);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdEnable));
        var header = s.Tx.Of(FlexProtocol.CmdPackage).First();
        Assert.Equal(26, header[29]);
        Assert.Equal(13, (header[27] << 8) | header[28]);
        Assert.Empty(s.Tx.Of(FlexProtocol.CmdLive));
        Assert.Empty(s.Tx.Of(FlexProtocol.CmdSave));
        Run(s, FlexHub.SaveSettleMs + 500);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdSave));
        Run(s, 20_000);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdEnable));
        Assert.Single(s.Tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void Quick_successive_edits_upload_the_settled_value_and_save_once()
    {
        var s = Create();
        for (var speed = 0; speed < 5; speed++)
        {
            SetLook(s, new LianLiFlexUnitSettings { Mode = "rainbow", Speed = speed });
            Run(s, 100);
        }
        Run(s, 500);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdEnable));
        Run(s, FlexHub.SaveSettleMs + 500);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdSave));
    }

    [Fact]
    public void A_look_already_stored_on_the_receiver_is_not_uploaded_again()
    {
        var s = Create();
        SetLook(s, new LianLiFlexUnitSettings { Mode = "rainbow" });
        Run(s, 1000);
        var look = s.Store.Load().Devices.FlexUnits[Mac];
        Assert.False(string.IsNullOrEmpty(look.PackageSignature));
        // the receiver echoes the index it was sent
        s.Tx.Info[21] = Convert.FromHexString(look.PackageEffectIndex!)[0];
        s.Tx.Info[22] = Convert.FromHexString(look.PackageEffectIndex!)[1];
        s.Tx.Info[23] = Convert.FromHexString(look.PackageEffectIndex!)[2];
        s.Tx.Info[24] = Convert.FromHexString(look.PackageEffectIndex!)[3];
        var restarted = new FlexLightingFrameWriter(s.Engine, s.Hub, s.Store, new Np50IdentifyTracker(), s.Provider, s.Clock.Func, runUpload: work => System.Threading.Tasks.Task.FromResult(work()));
        var before = s.Tx.Of(FlexProtocol.CmdEnable).Count();
        for (var i = 0; i < 20; i++)
        {
            s.Clock.Now += 100;
            s.Hub.Tick();
            restarted.Tick();
        }
        Assert.Equal(before, s.Tx.Of(FlexProtocol.CmdEnable).Count());
    }

    [Fact]
    public void A_receiver_that_never_echoes_the_index_is_not_rewritten_forever()
    {
        var s = Create();
        s.Tx.EchoEffectIndex = false;
        SetLook(s, new LianLiFlexUnitSettings { Mode = "rainbow" });
        Run(s, 60_000);
        Assert.InRange(s.Tx.Of(FlexProtocol.CmdEnable).Count(), 1, 3);
    }

    [Fact]
    public void Per_zone_looks_upload_one_package_for_the_whole_fan()
    {
        var s = Create();
        SetLook(s, new LianLiFlexUnitSettings
        {
            Mode = LianLiFlexUnitSettings.ModePerZone,
            Zones = new()
            {
                new LianLiFlexZoneSettings { Mode = "meteor" },
                new LianLiFlexZoneSettings { Mode = "static" },
            },
        });
        Run(s, 1000);
        Assert.Single(s.Tx.Of(FlexProtocol.CmdEnable));
        Assert.Equal(26, s.Tx.Of(FlexProtocol.CmdPackage).First()[29]);
    }

    [Fact]
    public void Returning_to_the_lighting_page_mode_streams_again_without_saving()
    {
        var s = Create();
        SetLook(s, new LianLiFlexUnitSettings { Mode = "rainbow" });
        Run(s, 1000);
        SetLook(s, new LianLiFlexUnitSettings { Mode = "custom" });
        Paint(s.Frames[0], 9, 9, 9);
        Run(s, 500);
        Assert.NotEmpty(s.Tx.Of(FlexProtocol.CmdLive));
    }

    [Fact]
    public void Merged_units_each_upload_their_slice_of_one_effect_at_the_shared_interval()
    {
        var tl = new FakeFlexTransport(Mac);
        var inf = new FakeFlexTransport("C5D212506CE1", fansType: 0x2B);
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl("tl"));
        discovery.Ports.Add(new FlexUnitPort { ReceiverPath = "inf", ReceiverPid = FlexProtocol.SlInfReceiverProductId });
        var clock = new FlexClock();
        var hub = new FlexHub(discovery, p => p.ReceiverPath == "tl" ? tl : inf, clock.Func, () => new DateTime(2026, 10, 19, 12, 0, 0), _ => { });
        hub.Refresh();
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new FlexLightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func, runUpload: work => System.Threading.Tasks.Task.FromResult(work()));
        store.Update(x =>
        {
            x.Devices.FlexUnits[Mac] = new LianLiFlexUnitSettings { Mode = "rainbow", Merge = true, SortIndex = 0 };
            x.Devices.FlexUnits["C5D212506CE1"] = new LianLiFlexUnitSettings { Mode = "rainbow", Merge = true, SortIndex = 1 };
        });
        for (var t = 0; t < 1000; t += 100)
        {
            clock.Now += 100;
            writer.Tick();
            hub.Tick();
        }
        var tlHeader = tl.Of(FlexProtocol.CmdPackage).First();
        var infHeader = inf.Of(FlexProtocol.CmdPackage).First();
        Assert.Equal(26, tlHeader[29]);
        Assert.Equal(44, infHeader[29]);
        Assert.Equal(24, (tlHeader[27] << 8) | tlHeader[28]);
        Assert.Equal(24, (infHeader[27] << 8) | infHeader[28]);
        Assert.Equal(48, (tlHeader[34] << 8) | tlHeader[35]);
        Assert.Equal(48, (infHeader[34] << 8) | infHeader[35]);
    }
}
