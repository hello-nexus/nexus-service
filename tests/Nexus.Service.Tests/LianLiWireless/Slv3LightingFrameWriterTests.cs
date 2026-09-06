using System;
using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingFrameWriterTests
{
    private static readonly byte[] FanMac = Convert.FromHexString("112233445566");

    private static (Slv3Hub Hub, Slv3TestHub.FakeSlv3Network Net, Slv3TestHub.FakeTxTransport Tx,
        Slv3LightingDeviceProvider Provider, LightingEngine Engine, Slv3LightingFrameWriter Writer, List<DeviceFrame> Frames)
        CreateBoundSetup(Func<long>? clock = null)
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frames = new List<DeviceFrame>(provider.BuildFrames(0));

        var engine = new LightingEngine();
        engine.UpdateDevices(frames.ToArray());

        // Default clock advances 200 ms per tick (> the writer's
        // MinPushIntervalMs) so each Tick() is a fresh push - the RGB rate-limit
        // isn't what most tests exercise. Tick_rate_limits_* passes its own clock.
        var autoClock = 0L;
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider,
            clock ?? (() => { autoClock += 200 * TimeSpan.TicksPerMillisecond; return autoClock; }));
        return (hub, net, tx, provider, engine, writer, frames);
    }

    private static void FillAll(IReadOnlyList<DeviceFrame> frames, byte r, byte g, byte b)
    {
        foreach (var frame in frames)
        {
            frame.Fill(r, g, b);
            // The engine publishes at the end of its tick; the writer reads the
            // published frame, so a test that paints has to publish too.
            frame.Publish();
        }
    }

    private static List<byte[]> RgbSyncFrames(Slv3TestHub.FakeTxTransport tx) =>
        tx.SentFrames.FindAll(f => f.Length >= 6 && f[1] == 0 && f[4] == Slv3Protocol.RfFrameType && f[5] == Slv3Protocol.RfRgbSync);

    [Fact]
    public void Tick_streams_composed_colors_to_a_bound_fan_chain()
    {
        var (_, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);

        writer.Tick();

        // Header packet sent 4x plus at least one data packet.
        Assert.True(RgbSyncFrames(tx).Count >= 5);
    }

    [Fact]
    public void Tick_does_not_resend_once_content_is_unchanged_and_confirmed()
    {
        var (hub, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);

        writer.Tick();
        Assert.True(hub.DriveTick()); // RX now echoes the effect_index we just sent

        var countAfterFirst = tx.SentFrames.Count;
        writer.Tick();

        Assert.Equal(countAfterFirst, tx.SentFrames.Count);
    }

    [Fact]
    public void Tick_resends_when_composed_content_changes()
    {
        var (hub, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        Assert.True(hub.DriveTick());
        var countAfterFirst = tx.SentFrames.Count;

        FillAll(frames, 200, 50, 5);
        writer.Tick();

        Assert.True(tx.SentFrames.Count > countAfterFirst);
    }

    [Fact]
    public void Tick_rate_limits_rapid_pushes_on_the_shared_rf_link()
    {
        var now = 0L;
        var (_, _, tx, _, _, writer, frames) = CreateBoundSetup(() => now);
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        var countAfterFirst = tx.SentFrames.Count;
        Assert.True(countAfterFirst > 0);

        // Content changes but less than the per-chain floor (100 ms for one
        // bound chain) elapsed: suppress the push so the fan's telemetry
        // beacon keeps RF air time (else the device list reads zero fans and
        // the controller looks "messed up").
        FillAll(frames, 200, 50, 5);
        now += 60 * TimeSpan.TicksPerMillisecond;
        writer.Tick();
        Assert.Equal(countAfterFirst, tx.SentFrames.Count);

        // Once the floor elapses, the latest frame goes out.
        now += 40 * TimeSpan.TicksPerMillisecond;
        writer.Tick();
        Assert.True(tx.SentFrames.Count > countAfterFirst);
    }

    [Fact]
    public void Tick_does_not_resend_unchanged_content_before_the_echo_had_time_to_arrive()
    {
        var now = 0L;
        var (_, net, tx, _, _, writer, frames) = CreateBoundSetup(() => now);
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        var countAfterFirst = tx.SentFrames.Count;

        // The RX has not polled yet (the fake fan still reports the old
        // effect_index): inside the confirm window that is not drift.
        net.Fans[0].EffectIndex = new byte[4];
        now += 1000 * TimeSpan.TicksPerMillisecond;
        writer.Tick();
        Assert.Equal(countAfterFirst, tx.SentFrames.Count);
    }

    [Fact]
    public void Tick_resends_unchanged_content_once_the_echo_window_passes_without_a_match()
    {
        var now = 0L;
        var (hub, net, tx, _, _, writer, frames) = CreateBoundSetup(() => now);
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        var countAfterFirst = tx.SentFrames.Count;

        // The chain reset (effect_index back to 0) and the device list said so;
        // past the confirm window the unchanged picture is pushed again.
        net.Fans[0].EffectIndex = new byte[4];
        Assert.True(hub.DriveTick());
        now += 2000 * TimeSpan.TicksPerMillisecond;
        var countBefore = tx.SentFrames.Count;
        writer.Tick();
        Assert.True(tx.SentFrames.Count > countBefore);
    }

    [Fact]
    public void Tick_does_nothing_when_engine_has_no_devices()
    {
        var (_, _, tx, _, engine, writer, _) = CreateBoundSetup();
        engine.UpdateDevices(Array.Empty<DeviceFrame>());

        writer.Tick();

        // The prior DriveTick() in CreateBoundSetup already sent bind/heartbeat
        // frames over the same fake TX; only RF_RgbSync frames are this writer's concern.
        Assert.Empty(RgbSyncFrames(tx));
    }

    [Fact]
    public void Tick_clears_cache_on_disconnect_so_reconnect_resends_unchanged_content()
    {
        var (hub, net, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 10, 20, 30);
        writer.Tick();
        Assert.True(hub.DriveTick());
        var countAfterFirst = tx.SentFrames.Count;

        hub.Disconnect();
        writer.Tick(); // hub disconnected: no-op, but clears the writer's cache
        Assert.Equal(countAfterFirst, tx.SentFrames.Count);

        Assert.True(hub.EnsureConnected());
        net.Fans.Clear();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        Assert.True(hub.DriveTick());

        writer.Tick();
        Assert.True(tx.SentFrames.Count > countAfterFirst);
    }
}
