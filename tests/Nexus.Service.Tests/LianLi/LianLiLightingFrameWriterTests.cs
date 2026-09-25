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

        // two channels x (start, colour, commit) + one frame sync
        Assert.Equal(7, Calls.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x60, 0x02, 0x02, 0x00, 0x00 }, Calls[0].Bytes);
        Assert.Equal(HubTransportSpy.CallKind.OutputReport, Calls[1].Kind);
        Assert.Equal(0x32, Calls[1].Bytes[1]); // 0x30 | inner channel 2
        Assert.Equal(0x33, Calls[4].Bytes[1]); // 0x30 | outer channel 3
        Assert.Equal(0x13, Calls[5].Bytes[1]); // 0x10 | outer channel 3
        Assert.Equal(0x60, Calls[6].Bytes[1]);
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

        Assert.Contains(Calls, c => (c.Bytes[1] & 0xF0) == 0x10 && c.Bytes[2] == 0x1C);
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
        Assert.Equal(7, fullCommit); // 2 channels x (start + colour + commit) + frame sync

        // Same hub, a mode change to force a fresh commit, every write refused:
        // it must stop at the first rejection instead of walking all 8 channels.
        _spy.Calls.Clear();
        _spy.RejectWrites = true;
        SetMode("breathing");
        _writer.Tick();

        Assert.Single(Calls);
    }

    [Theory]
    [InlineData(3)] // mid-sequence: refused on the second channel's colour write
    [InlineData(6)] // only the trailing frame sync refused
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
        Assert.Equal(7, Calls.Count);
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

        // And the tick after, while the init backoff is still running.
        _spy.Calls.Clear();
        _spy.RejectWrites = false;
        _writer.Tick();
        Assert.Contains(Calls, c => c.Kind == HubTransportSpy.CallKind.Write);
    }
}
