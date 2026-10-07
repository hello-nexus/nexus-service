using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.LianLi;

/// <summary>
/// Drives one writer tick against a transport spy and pins the per-family wire
/// sequence, which no hardware on the bench can.
/// </summary>
public class LianLiLightingFrameWriterTests
{
    private readonly LianLiHub _hub = new();
    private readonly HubTransportSpy _spy = new();
    private readonly InMemoryConfigStore _store = new();
    private readonly LightingEngine _engine = new();
    private readonly LianLiLightingFrameWriter _writer;

    public LianLiLightingFrameWriterTests()
    {
        _writer = new LianLiLightingFrameWriter(_engine, _hub, _store, new Np50IdentifyTracker());
    }

    private static LianLiFanProfile Profile(int pid)
    {
        Assert.True(LianLiFanProfiles.TryGet(pid, out var p));
        return p;
    }

    private void Attach(int pid, int port, int fans)
    {
        _hub.Attach(_spy, Profile(pid));
        _store.Update(s =>
        {
            for (var p = 0; p < LianLiProtocol.PortCount; p++) s.Devices.LianLi.SetFans(p, p == port ? fans : 0);
        });
        // The writer only runs once the engine has a device set.
        _engine.UpdateDevices(new[] { new DeviceFrame(0, "lianli:port" + port, 16, 0, 0, 1, 1, 0) });
    }

    private void SetMode(string mode) => _store.Update(s => s.Devices.LianLiLighting.Mode = mode);

    private List<HubTransportSpy.Call> Calls => _spy.Calls;

    [Fact]
    public void Sl_v1_first_tick_clears_merge_and_sets_every_port_quantity_once()
    {
        Attach(0xA100, port: 2, fans: 3);
        SetMode("rainbowWave");

        _writer.Tick();
        var firstTick = Calls.Count;
        _writer.Tick();

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x00, 0x00, 0x00, 0x00 }, Calls[1].Bytes); // port 0, 0 fans
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x10, 0x00, 0x00, 0x00 }, Calls[2].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x23, 0x00, 0x00, 0x00 }, Calls[3].Bytes); // port 2, 3 fans
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x32, 0x30, 0x00, 0x00, 0x00 }, Calls[4].Bytes);
        Assert.All(Calls.Take(5), c => Assert.Equal(HubTransportSpy.CallKind.Feature, c.Kind));
        // The second tick re-sends nothing: same sig, hub already initialised.
        Assert.Equal(firstTick, Calls.Count);
    }

    [Fact]
    public void Sl_v1_firmware_mode_commits_one_channel_per_port_without_a_per_frame_start()
    {
        Attach(0xA100, port: 2, fans: 3);
        SetMode("rainbowWave");

        _writer.Tick();
        var calls = Calls.Skip(5).ToList(); // after merge-off + 4 quantities

        // colour (interrupt-OUT) -> commit (feature) -> frame sync (feature)
        Assert.Equal(3, calls.Count);
        Assert.Equal(HubTransportSpy.CallKind.Write, calls[0].Kind);
        Assert.Equal(0x32, calls[0].Bytes[1]); // 0x30 | channel 2 == port 2
        Assert.Equal(HubTransportSpy.CallKind.Feature, calls[1].Kind);
        Assert.Equal(0x12, calls[1].Bytes[1]); // 0x10 | channel 2
        Assert.Equal(0x05, calls[1].Bytes[2]); // rainbow
        Assert.Equal(new byte[] { 0xE0, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00 }, calls[2].Bytes);
    }

    [Fact]
    public void Sl_v1_static_fills_each_fan_ring_with_its_colour_cycling_the_list()
    {
        Attach(0xA100, port: 0, fans: 3);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "static";
            s.Devices.LianLiLighting.Colors = new List<string> { "#FF0000", "#00FF00" };
        });

        _writer.Tick();

        var colour = Calls.Single(c => c.Kind == HubTransportSpy.CallKind.Write).Bytes;
        // wire R,B,G per LED; fan 0 red, fan 1 green, fan 2 red again, 16 LEDs each
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[2..5]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[(2 + 15 * 3)..(2 + 16 * 3)]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, colour[(2 + 16 * 3)..(2 + 17 * 3)]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[(2 + 32 * 3)..(2 + 33 * 3)]);
        Assert.Equal(0x00, colour[2 + 48 * 3]); // fan 3 absent
    }

    [Fact]
    public void Sl_infinity_firmware_mode_commits_inner_and_outer_channels_per_port()
    {
        Attach(0xA102, port: 1, fans: 2);
        SetMode("rainbowWave");

        _writer.Tick();

        // merge-off + two channels x (start, colour, commit) + one frame sync
        Assert.Equal(8, Calls.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x02, 0x00, 0x00 }, Calls[1].Bytes);
        Assert.Equal(HubTransportSpy.CallKind.OutputReport, Calls[2].Kind);
        Assert.Equal(0x32, Calls[2].Bytes[1]); // 0x30 | inner channel 2
        Assert.Equal(0x33, Calls[5].Bytes[1]); // 0x30 | outer channel 3
        Assert.Equal(0x13, Calls[6].Bytes[1]); // 0x10 | outer channel 3
        Assert.Equal(0x60, Calls[7].Bytes[1]);
    }

    [Fact]
    public void Al_whole_fan_mode_commits_the_inner_channel_only()
    {
        Attach(0xA101, port: 1, fans: 2);
        SetMode("taichi");

        _writer.Tick();

        var commits = Calls.Where(c => c.IsSetFeature && (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] == 0x2C).ToList();
        Assert.Single(commits);
        Assert.Equal(0x12, commits[0].Bytes[1]); // inner channel 2 of port 1
        Assert.DoesNotContain(Calls, c => c.Kind == HubTransportSpy.CallKind.OutputReport && c.Bytes[1] == 0x33);
    }

    [Fact]
    public void Al_split_ring_mode_commits_both_channels()
    {
        Attach(0xA101, port: 0, fans: 2);
        SetMode("runway");

        _writer.Tick();

        var commits = Calls.Where(c => c.IsSetFeature && c.Bytes[2] == 0x1A && (c.Bytes[1] & 0xF0) == 0x10).Select(c => c.Bytes[1]).ToArray();
        Assert.Equal(new byte[] { 0x10, 0x11 }, commits);
    }

    [Fact]
    public void Sl_v2_commits_one_channel_per_port_and_latches_on_four()
    {
        Attach(0xA103, port: 2, fans: 6);
        SetMode("tide");

        _writer.Tick();

        Assert.Contains(Calls, c => c.IsSetFeature && c.Bytes[2] == 0x60 && c.Bytes[3] == 0x26);
        Assert.Contains(Calls, c => c.IsSetFeature && c.Bytes[1] == 0x12 && c.Bytes[2] == 0x1A);
        Assert.DoesNotContain(Calls, c => c.IsSetFeature && c.Bytes[1] is 0x14 or 0x15);
        Assert.Equal(new byte[] { 0xE0, 0x60, 0x00, 0x04, 0x00, 0x00, 0x00 }, Calls[^1].Bytes);
    }

    [Fact]
    public void An_empty_palette_falls_back_to_the_mode_defaults()
    {
        Attach(0xA102, port: 0, fans: 1);
        _store.Update(s => { s.Devices.LianLiLighting.Mode = "runway"; s.Devices.LianLiLighting.Colors.Clear(); });

        _writer.Tick();

        var colour = Calls.First(c => c.Kind == HubTransportSpy.CallKind.OutputReport && c.Bytes[1] == 0x30).Bytes;
        // Default runway palette red then blue, on the R,B,G wire.
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour[2..5]);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, colour[5..8]);
    }

    [Fact]
    public void A_system_resume_recommits_an_unchanged_firmware_mode()
    {
        Attach(0xA102, port: 0, fans: 2);
        SetMode("rainbowWave");
        _writer.Tick();
        var afterFirst = Calls.Count;
        _writer.Tick();
        Assert.Equal(afterFirst, Calls.Count);

        _hub.OnSystemResumed();
        _writer.Tick();

        Assert.True(Calls.Count > afterFirst);
        Assert.Equal(0x60, Calls[^1].Bytes[1]);
    }

    [Fact]
    public void Sl_v1_falls_back_to_static_for_a_persisted_sl_infinity_only_mode()
    {
        Attach(0xA100, port: 0, fans: 1);
        SetMode("voice");

        _writer.Tick();

        var commit = Calls.Single(c => c.Kind == HubTransportSpy.CallKind.Feature && (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] is not (0x32 or 0x34));
        Assert.Equal(0x01, commit.Bytes[2]); // static, not 0x26
    }

    [Fact]
    public void Sl_v1_custom_mode_streams_port_3_on_channel_3()
    {
        Attach(0xA100, port: 3, fans: 4);
        SetMode("custom");

        _writer.Tick();
        var calls = Calls.Skip(5).ToList();

        Assert.Equal(3, calls.Count);
        Assert.Equal(HubTransportSpy.CallKind.Write, calls[0].Kind);
        Assert.Equal(0x33, calls[0].Bytes[1]); // 0x30 | channel 3, not channel 1
        Assert.Equal(LianLiProtocol.OutputReportSize, calls[0].Bytes.Length);
        Assert.Equal(0x13, calls[1].Bytes[1]);
        Assert.Equal(0x01, calls[1].Bytes[2]); // static latches the streamed frame
        Assert.Equal(0x60, calls[2].Bytes[1]);
    }

    [Fact]
    public void Sl_infinity_custom_mode_sends_merge_off_once_per_run()
    {
        Attach(0xA102, port: 1, fans: 2);
        SetMode("custom");

        _writer.Tick();
        Assert.Single(Calls, c => c.Bytes[1] == 0x10 && c.Bytes[2] == 0x34);

        _spy.Calls.Clear();
        _writer.Tick();
        Assert.DoesNotContain(Calls, c => c.Bytes[1] == 0x10 && c.Bytes[2] == 0x34);
    }

    // ── Merge (one animation across every port) ──

    private void AttachMerged(int pid, string mode)
    {
        _hub.Attach(_spy, Profile(pid));
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 3);
            s.Devices.LianLi.SetFans(1, 3);
            s.Devices.LianLi.SetFans(2, 2);
            s.Devices.LianLi.SetFans(3, 0);
            s.Devices.LianLiLighting.Mode = mode;
            s.Devices.LianLiLighting.Merge = true;
            s.Devices.LianLiLighting.Colors = new List<string> { "#FF0000", "#0000FF" };
        });
        _engine.UpdateDevices(new[] { new DeviceFrame(0, "lianli:port0", 16, 0, 0, 1, 1, 0) });
    }

    [Fact]
    public void Sl_infinity_merged_runway_parks_channels_7_to_1_then_runs_the_merge_on_channel_0()
    {
        AttachMerged(0xA102, "runway");

        _writer.Tick();

        Assert.Equal(14, Calls.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x63, 0x00, 0x01, 0x02, 0x03, 0x08 }, Calls[0].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x01, 0x03, 0x00, 0x00 }, Calls[1].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x03, 0x00, 0x00 }, Calls[2].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x03, 0x02, 0x00, 0x00 }, Calls[3].Bytes);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x04, 0x00, 0x00, 0x00 }, Calls[4].Bytes);
        for (var i = 0; i < 7; i++)
        {
            // idle effect at brightness off, channel 7 first
            Assert.Equal(new byte[] { 0xE0, (byte)(0x17 - i), 0x32, 0x00, 0x00, 0x08, 0x00 }, Calls[5 + i].Bytes);
        }
        var colour = Calls[12];
        Assert.Equal(HubTransportSpy.CallKind.OutputReport, colour.Kind);
        Assert.Equal(0x30, colour.Bytes[1]);
        // wire R,B,G: fan 0 slot 0 red, slot 1 blue, slots 2-3 black; fan 1 repeats
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0, 0, 0, 0, 0, 0 }, colour.Bytes[2..14]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, colour.Bytes[14..17]);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x46, 0x00, 0x00, 0x00, 0x00 }, Calls[13].Bytes);
        Assert.DoesNotContain(Calls, c => c.Bytes[1] == 0x60);
    }

    [Fact]
    public void Merged_animation_follows_the_saved_port_order()
    {
        AttachMerged(0xA102, "runway");
        _store.Update(s => s.Devices.LianLiLighting.MergeOrder = new List<int> { 3, 2, 1, 0 });

        _writer.Tick();

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x63, 0x03, 0x02, 0x01, 0x00, 0x08 }, Calls[0].Bytes);
    }

    // ── Per-port and per-ring looks ──

    private static LianLiEffectSettings Effect(string mode, params string[] colors) =>
        new() { Mode = mode, Colors = colors.ToList() };

    // Effect byte committed on each channel, in commit order.
    private Dictionary<int, byte> CommittedEffects() => Calls
        .Where(c => c.IsSetFeature && (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] is not (0x34 or 0x60 or 0x63))
        .ToDictionary(c => c.Bytes[1] & 0x0F, c => c.Bytes[2]);

    private void AttachPorts(int pid, params int[] fansPerPort)
    {
        _hub.Attach(_spy, Profile(pid));
        _store.Update(s =>
        {
            for (var p = 0; p < LianLiProtocol.PortCount; p++) s.Devices.LianLi.SetFans(p, p < fansPerPort.Length ? fansPerPort[p] : 0);
        });
        _engine.UpdateDevices(new[] { new DeviceFrame(0, "lianli:port0", 16, 0, 0, 1, 1, 0) });
    }

    [Fact]
    public void Split_rings_commit_each_ring_its_own_effect()
    {
        AttachPorts(0xA102, 2);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "rainbowWave";
            s.Devices.LianLiLighting.InnerRing = Effect("taichi");
            s.Devices.LianLiLighting.OuterRing = Effect("reflect");
        });

        _writer.Tick();

        Assert.Equal(new Dictionary<int, byte> { [0] = 0x1C, [1] = 0x30 }, CommittedEffects());
    }

    [Fact]
    public void A_port_with_its_own_look_commits_it_and_the_others_keep_the_hubs()
    {
        AttachPorts(0xA102, 2, 2);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "rainbowWave";
            s.Devices.LianLiLighting.Ports = new List<LianLiPortLighting?>
            {
                null,
                new() { Whole = Effect("static"), InnerRing = Effect("meteor"), OuterRing = Effect("static") },
            };
        });

        _writer.Tick();

        Assert.Equal(new Dictionary<int, byte> { [0] = 0x05, [1] = 0x05, [2] = 0x19, [3] = 0x01 }, CommittedEffects());
    }

    [Fact]
    public void Split_rings_on_a_family_without_ring_effects_play_the_whole_fan_mode()
    {
        AttachPorts(0x7750, 1);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "rainbowWave";
            s.Devices.LianLiLighting.InnerRing = Effect("taichi");
            s.Devices.LianLiLighting.OuterRing = Effect("reflect");
        });

        _writer.Tick();

        Assert.All(CommittedEffects().Values, b => Assert.Equal(0x05, b));
    }

    [Fact]
    public void Split_rings_switch_off_merge()
    {
        AttachMerged(0xA102, "runway");
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.InnerRing = Effect("static");
            s.Devices.LianLiLighting.OuterRing = Effect("static");
        });

        _writer.Tick();

        Assert.DoesNotContain(Calls, c => c.Bytes[2] == 0x63);
    }

    [Fact]
    public void A_corner_palette_colours_each_side_of_the_outer_ring()
    {
        AttachPorts(0xA101, 1);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "static";
            s.Devices.LianLiLighting.InnerRing = Effect("static");
            s.Devices.LianLiLighting.OuterRing = Effect("staticColorful", "#FF0000", "#0000FF", "#00FF00", "#800000");
        });

        _writer.Tick();

        var outer = Calls.First(c => c.Kind == HubTransportSpy.CallKind.OutputReport && c.Bytes[1] == 0x31).Bytes;
        // wire R,B,G: each side of the ring holds one palette colour
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, outer[2..5]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, outer[(2 + 2 * 3)..(2 + 3 * 3)]);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x00 }, outer[(2 + 3 * 3)..(2 + 4 * 3)]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, outer[(2 + 6 * 3)..(2 + 7 * 3)]);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00 }, outer[(2 + 11 * 3)..(2 + 12 * 3)]);
    }

    [Fact]
    public void A_merged_animation_ignores_port_looks_and_does_not_restart_for_them()
    {
        AttachMerged(0xA102, "runway");
        _store.Update(s => s.Devices.LianLiLighting.Ports = new List<LianLiPortLighting?> { new() { Whole = Effect("static") } });
        _writer.Tick();
        Assert.Contains(Calls, c => c.Bytes[2] == 0x63);
        Assert.DoesNotContain(CommittedEffects(), e => e.Value == 0x01);
        Calls.Clear();

        _store.Update(s => s.Devices.LianLiLighting.Ports = new List<LianLiPortLighting?> { new() { Whole = Effect("breathing") } });
        _writer.Tick();

        Assert.Empty(Calls);
    }

    [Fact]
    public void Changing_a_ring_recommits()
    {
        AttachPorts(0xA102, 1);
        _store.Update(s =>
        {
            s.Devices.LianLiLighting.Mode = "static";
            s.Devices.LianLiLighting.InnerRing = Effect("static");
            s.Devices.LianLiLighting.OuterRing = Effect("static");
        });
        _writer.Tick();
        Calls.Clear();

        _store.Update(s => s.Devices.LianLiLighting.OuterRing!.Speed = 4);
        _writer.Tick();

        Assert.NotEmpty(CommittedEffects());
    }

    [Fact]
    public void Merge_on_a_mode_without_a_merged_variant_commits_per_port()
    {
        AttachMerged(0xA102, "rainbowWave");

        _writer.Tick();

        Assert.DoesNotContain(Calls, c => c.Bytes[2] == 0x63);
        Assert.Contains(Calls, c => c.Bytes[1] == 0x60); // frame sync of the per-port path
    }

    [Fact]
    public void Sl_v1_ignores_merge_and_commits_the_per_port_runway()
    {
        AttachMerged(0xA100, "runway");

        _writer.Tick();

        Assert.DoesNotContain(Calls, c => c.Bytes[2] is 0x63 or 0x46);
        Assert.Contains(Calls, c => (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] == 0x1C);
    }

    [Fact]
    public void Turning_merge_off_recommits_every_port()
    {
        AttachMerged(0xA102, "runway");
        _writer.Tick();
        _spy.Calls.Clear();

        _store.Update(s => s.Devices.LianLiLighting.Merge = false);
        _writer.Tick();

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x34, 0x00, 0x00, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Contains(Calls, c => (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] == 0x1A); // SL-Infinity runway
        Assert.Equal(0x60, Calls[^1].Bytes[1]);
    }

    [Fact]
    public void Merge_is_reported_blocked_only_while_a_port_is_disabled()
    {
        AttachMerged(0xA102, "runway");
        var s = _store.Load();
        var composed = LianLiZoneSupport.Compose(_hub.DeviceId, _hub.Profile, LianLiZoneSupport.ReadComposition(s, _hub.DeviceId), s.Devices.LianLi);
        Assert.False(LianLiLightingFrameWriter.AnyDeviceExcluded(composed, s));

        _store.Update(x => x.Devices.DisabledLightingDevices.Add("lianli:port1"));

        Assert.True(LianLiLightingFrameWriter.AnyDeviceExcluded(composed, _store.Load()));
    }

    [Fact]
    public void A_disabled_port_keeps_the_per_port_commit()
    {
        AttachMerged(0xA102, "runway");
        _store.Update(s => s.Devices.DisabledLightingDevices.Add("lianli:port1"));

        _writer.Tick();

        Assert.DoesNotContain(Calls, c => c.Bytes[2] == 0x63);
        Assert.Contains(Calls, c => c.Bytes[1] == 0x60);
    }

    // ── Rejected-write retry (a commit the hub drops must not latch) ──

    /// <summary>Drives the writer's backoff clock without sleeping.</summary>
    private long _now;

    private void UseFakeClock() => _writer.NowMs = () => _now;

    private void Advance(long ms) => _now += ms;

    [Fact]
    public void Firmware_commit_rejected_by_the_hub_is_retried_after_the_backoff()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectWrites = true;

        _writer.Tick();
        var afterFirst = Calls.Count;
        Assert.True(afterFirst > 0, "the first attempt must reach the hub");

        // Inside the 1s window the writer stays off the hub entirely.
        _writer.Tick();
        Assert.Equal(afterFirst, Calls.Count);

        Advance(1000);
        _writer.Tick();
        Assert.True(Calls.Count > afterFirst, "the commit must be retried once the window elapses");
    }

    [Fact]
    public void Firmware_commit_stops_retrying_once_the_hub_accepts_it()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectWrites = true;

        _writer.Tick();
        Advance(1000);
        _spy.RejectWrites = false;
        _writer.Tick();
        var afterAccepted = Calls.Count;

        // Latched now: no further writes, however long we wait.
        Advance(60_000);
        _writer.Tick();
        _writer.Tick();
        Assert.Equal(afterAccepted, Calls.Count);
    }

    [Fact]
    public void Firmware_commit_backoff_widens_and_caps_at_thirty_seconds()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectWrites = true;

        // 1s, 2s, 4s, 8s, 16s, then pinned at 30s.
        foreach (var expected in new long[] { 1000, 2000, 4000, 8000, 16000, 30000, 30000 })
        {
            var before = Calls.Count;
            _writer.Tick();
            Assert.True(Calls.Count > before, "attempt must reach the hub");

            // One tick short of the window sends nothing.
            Advance(expected - 1);
            var beforeEarly = Calls.Count;
            _writer.Tick();
            Assert.Equal(beforeEarly, Calls.Count);
            Advance(1);
        }
    }

    [Fact]
    public void A_settings_change_cancels_the_backoff_and_applies_at_once()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectWrites = true;

        _writer.Tick();
        var afterFirst = Calls.Count;
        _writer.Tick();
        Assert.Equal(afterFirst, Calls.Count); // backing off

        // The user picks another mode: their change must not wait out the window.
        SetMode("breathing");
        _writer.Tick();
        Assert.True(Calls.Count > afterFirst, "a settings change must retry immediately");
    }

    [Fact]
    public void Hub_detach_clears_the_backoff_so_a_reconnect_commits_at_once()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectWrites = true;

        _writer.Tick();
        var afterFirst = Calls.Count;

        _hub.Detach();
        _writer.Tick(); // observes the detach, resets state
        _spy.RejectWrites = false;
        _hub.Attach(_spy, Profile(0xA102));

        _writer.Tick();
        Assert.True(Calls.Count > afterFirst, "a reconnected hub must commit without serving out the old backoff");
    }

    [Fact]
    public void A_rejected_write_abandons_the_rest_of_the_commit()
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");

        _writer.Tick();
        var fullCommit = Calls.Count;
        Assert.Equal(8, fullCommit); // merge-off + 2 channels x (start + colour + commit) + frame sync

        // Same hub, a mode change to force a fresh commit, every write refused:
        // it must stop at the first rejection instead of walking all 8 channels.
        _spy.Calls.Clear();
        _spy.RejectWrites = true;
        SetMode("breathing");
        _writer.Tick();

        Assert.Single(Calls);
    }

    [Theory]
    [InlineData(5)] // mid-sequence: refused on the second channel's colour write
    [InlineData(7)] // only the trailing frame sync refused
    public void A_commit_refused_partway_is_not_latched_and_re_sends_every_channel(int acceptedWrites)
    {
        UseFakeClock();
        Attach(0xA102, port: 0, fans: 4);
        SetMode("rainbowWave");
        _spy.RejectAfter = acceptedWrites;

        _writer.Tick();
        Assert.Equal(acceptedWrites + 1, Calls.Count); // stops at the refused write

        // Unlatched, so the retry re-sends the whole sequence, not the remainder.
        _spy.Calls.Clear();
        _spy.RejectAfter = -1;
        Advance(1000);
        _writer.Tick();
        Assert.Equal(8, Calls.Count);
    }

    [Fact]
    public void Sl_infinity_custom_mode_paces_frames_to_the_hub_commit_rate()
    {
        UseFakeClock();
        Attach(0xA102, port: 1, fans: 3);
        SetMode("custom");

        _writer.Tick();
        Assert.Contains(Calls, c => c.Bytes[1] == 0x32);
        _spy.Calls.Clear();
        Advance(50);
        _writer.Tick();
        Assert.DoesNotContain(Calls, c => c.Bytes[1] == 0x32);
        Advance(40);
        _writer.Tick();
        Assert.Contains(Calls, c => c.Bytes[1] == 0x32);
    }

    [Fact]
    public void Sl_infinity_custom_mode_gives_two_ports_a_longer_frame_interval_than_one()
    {
        UseFakeClock();
        _hub.Attach(_spy, Profile(0xA102));
        _store.Update(s =>
        {
            s.Devices.LianLi.SetFans(0, 1);
            s.Devices.LianLi.SetFans(1, 2);
            s.Devices.LianLi.SetFans(2, 0);
            s.Devices.LianLi.SetFans(3, 0);
        });
        _engine.UpdateDevices(new[]
        {
            new DeviceFrame(0, "lianli:port0", 16, 0, 0, 1, 1, 0),
            new DeviceFrame(1, "lianli:port1", 32, 0, 0, 1, 1, 0),
        });
        SetMode("custom");

        _writer.Tick();
        Assert.Contains(Calls, c => c.Bytes[1] == 0x30);
        Assert.Contains(Calls, c => c.Bytes[1] == 0x32);
        _spy.Calls.Clear();
        Advance(90);
        _writer.Tick();
        Assert.DoesNotContain(Calls, c => c.Bytes[1] == 0x30);
        Advance(10);
        _writer.Tick();
        Assert.Contains(Calls, c => c.Bytes[1] == 0x30);
    }

    [Fact]
    public void A_rejected_hub_init_does_not_freeze_custom_mode_streaming()
    {
        UseFakeClock();
        // SL v1 is the family whose init actually writes (merge-off + quantities).
        Attach(0xA100, port: 2, fans: 3);
        SetMode("custom");
        _spy.RejectWrites = true;

        // The tick that attempts the init and has it refused must still stream:
        // SL v1 colour data goes out on interrupt-OUT, so a Write call proves it
        // got past the init block rather than aborting the tick there.
        _writer.Tick();
        Assert.Contains(Calls, c => c.Kind == HubTransportSpy.CallKind.Write);

        // And the next frame, while the init backoff is still running.
        _spy.Calls.Clear();
        _spy.RejectWrites = false;
        Advance(100);
        _writer.Tick();
        Assert.Contains(Calls, c => c.Kind == HubTransportSpy.CallKind.Write);
    }

    private static bool IsArgbSync(HubTransportSpy.Call c, byte on) =>
        c.IsSetFeature && c.Bytes.Length >= 4 && c.Bytes[1] == 0x10 && c.Bytes[2] == 0x61 && c.Bytes[3] == on;

    [Theory]
    [InlineData(0xA101, 0x41)]
    [InlineData(0xA100, 0x30)]
    [InlineData(0xA103, 0x61)]
    public void Argb_sync_switches_every_family_on_its_own_register(int pid, int register)
    {
        UseFakeClock();
        Attach(pid, port: 0, fans: 2);
        SetMode("static");
        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = true);

        _writer.Tick();

        Assert.Single(Calls, c => c.IsSetFeature && c.Bytes[1] == 0x10 && c.Bytes[2] == register && c.Bytes[3] == 1);
        Assert.DoesNotContain(Calls, c => c.Bytes.Length > 1 && (c.Bytes[1] & 0xF0) == 0x30);
    }

    [Fact]
    public void Argb_sync_switches_the_hub_once_and_stops_streaming()
    {
        UseFakeClock();
        Attach(0xA102, port: 1, fans: 3);
        SetMode("custom");
        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = true);

        _writer.Tick();
        Assert.Single(Calls, c => IsArgbSync(c, 1));
        Assert.DoesNotContain(Calls, c => c.Bytes.Length > 1 && (c.Bytes[1] & 0xF0) == 0x30);

        _spy.Calls.Clear();
        Advance(1000);
        _writer.Tick();
        Assert.Empty(Calls);
    }

    [Fact]
    public void Leaving_argb_sync_switches_the_hub_back_and_recommits_the_firmware_mode()
    {
        UseFakeClock();
        Attach(0xA102, port: 1, fans: 3);
        SetMode("static");
        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = true);
        _writer.Tick();

        _spy.Calls.Clear();
        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = false);
        Advance(100);
        _writer.Tick();

        Assert.Single(Calls, c => IsArgbSync(c, 0));
        Assert.Contains(Calls, c => c.Bytes.Length > 2 && c.Bytes[1] == 0x12);
        AssertReleasedAfterOff();
    }

    // The off alone leaves the hub on its ARGB input: every channel must take
    // a colour report and a commit, then the frame latch.
    private void AssertReleasedAfterOff()
    {
        var calls = Calls.ToList();
        var off = calls.FindIndex(c => IsArgbSync(c, 0));
        Assert.True(off >= 0);
        var after = calls.Skip(off + 1).ToList();
        for (var ch = 0; ch < 8; ch++)
        {
            Assert.Contains(after, c => !c.IsSetFeature && c.Bytes.Length > 2 && c.Bytes[1] == (0x30 | ch));
            Assert.Contains(after, c => c.IsSetFeature && c.Bytes.Length > 2 && c.Bytes[1] == (0x10 | ch) && c.Bytes[2] == 0x01);
        }
        Assert.Contains(after, c => c.IsSetFeature && c.Bytes.Length > 3 && c.Bytes[1] == 0x60 && c.Bytes[2] == 0x00 && c.Bytes[3] == 0x01);
    }

    [Fact]
    public void A_hub_never_put_on_argb_sync_gets_no_sync_write_even_with_a_source_saved()
    {
        Attach(0xA102, port: 1, fans: 3);
        SetMode("static");
        _store.Update(s => s.Devices.LianLiLighting.ArgbSyncSource = "openrgb-s-1-1");

        _writer.Tick();

        Assert.DoesNotContain(Calls, c => c.IsSetFeature && c.Bytes.Length >= 3 && c.Bytes[1] == 0x10 && c.Bytes[2] == 0x61);
    }

    [Fact]
    public void Leaving_sync_holds_output_then_recommits_the_mode_three_times()
    {
        UseFakeClock();
        Attach(0xA102, port: 1, fans: 3);
        SetMode("static");
        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = true);
        _writer.Tick();

        _store.Update(s => s.Devices.LianLiLighting.ArgbSync = false);
        _writer.Tick();
        _spy.Calls.Clear();
        Advance(300);
        _writer.Tick();
        Assert.Empty(Calls);

        var commits = 0;
        foreach (var step in new[] { 400, 1000, 1600 })
        {
            Advance(step);
            _spy.Calls.Clear();
            _writer.Tick();
            if (Calls.Any(c => c.IsSetFeature && c.Bytes.Length > 2 && c.Bytes[1] == 0x12)) commits++;
        }
        Assert.Equal(3, commits);
    }

}
