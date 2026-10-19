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
        var writer = new FlexLightingFrameWriter(engine, hub, store, identify, provider, clock.Func);
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
}
