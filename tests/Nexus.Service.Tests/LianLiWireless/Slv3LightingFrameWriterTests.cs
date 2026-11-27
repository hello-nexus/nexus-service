using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LightingFrameWriterTests
{
    [Fact]
    public void TryFitClock_recovers_the_rf_clock_line_and_rejects_implausible_slopes()
    {
        var samples = new List<(double Utc, double Rf)>();
        for (var i = 0; i < 8; i++)
        {
            var utc = 1_000_000.0 + i * 250;
            samples.Add((utc, 5_000 + 1.6002 * utc));
        }

        Assert.True(Slv3LightingFrameWriter.TryFitClock(samples, out var a, out var b));
        Assert.Equal(1.6002, b, 6);
        Assert.Equal(5_000, a, 3);

        Assert.False(Slv3LightingFrameWriter.TryFitClock(samples.GetRange(0, 3), out _, out _));
        var fast = samples.ConvertAll(x => (x.Utc, x.Rf * 2));
        Assert.False(Slv3LightingFrameWriter.TryFitClock(fast, out _, out _));
    }

    [Fact]
    public void WindowTickTimes_render_each_slot_at_the_middle_of_the_frame_it_plays()
    {
        const double A = 12_345, B = 1.6003;
        const long FirstFrame = 987_654;
        var ticks = Slv3LightingFrameWriter.WindowTickTimes(A, B, FirstFrame);

        Assert.Equal(Slv3LightingFrameWriter.WindowFrames, ticks.Length);
        var slots = new HashSet<int>();
        for (var k = 0; k < ticks.Length; k++)
        {
            var frame = (long)Math.Floor((A + B * ticks[k]) / Slv3LightingFrameWriter.WindowIntervalTicks);
            Assert.Equal(FirstFrame + k, frame);
            slots.Add(Slv3LightingFrameWriter.WindowSlot(frame));
        }
        Assert.Equal(Slv3LightingFrameWriter.WindowFrames, slots.Count);
    }

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

    // An engine with an effect set holds a chain whose frame is unrendered. The loop renders its first tick and then
    // sleeps for the long period, so frames the test swaps in stay exactly as the rebuild left them.
    private static async Task StartActiveEngine(LightingEngine engine)
    {
        engine.FrameIntervalMs = 600_000;
        var first = new TaskCompletionSource<bool>();
        engine.FramePublished += () => first.TrySetResult(true);
        engine.SetEffect(new FlatColorEffect(0, 0, 0));
        await Task.WhenAny(first.Task, Task.Delay(5000));
        Assert.True(first.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Tick_does_not_upload_black_for_a_chain_with_no_engine_frame()
    {
        var (_, _, tx, _, engine, writer, frames) = CreateBoundSetup();
        try
        {
            await StartActiveEngine(engine);
            // The engine's device list lacks this chain's frame (a rebuild is pending).
            engine.UpdateDevices(new[] { new DeviceFrame(0, "other-device", 4) });
            frames[0].Fill(159, 0, 113);
            frames[0].Publish();

            writer.Tick();

            Assert.Empty(RgbSyncFrames(tx));
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public async Task Tick_holds_a_chain_whose_rebuilt_frame_lands_before_the_engine_paints_it()
    {
        var (_, _, tx, _, engine, writer, frames) = CreateBoundSetup();
        try
        {
            await StartActiveEngine(engine);
            FillAll(frames, 159, 0, 113);
            writer.Tick();
            var sent = RgbSyncFrames(tx).Count;
            Assert.True(sent > 0);

            // A neighbor left and rejoined: the chain's frame is rebuilt zeroed and unpublished.
            var fresh = frames.ConvertAll(f => new DeviceFrame(f.Index, f.Id, f.LedCount));
            engine.UpdateDevices(fresh.ToArray());
            writer.Tick();
            writer.Tick();
            Assert.Equal(sent, RgbSyncFrames(tx).Count);

            // Once the engine paints it, the new color streams.
            FillAll(fresh, 0, 255, 0);
            writer.Tick();
            Assert.True(RgbSyncFrames(tx).Count > sent);
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void Tick_streams_black_to_a_new_chain_while_lighting_is_off()
    {
        var (_, _, tx, _, engine, writer, frames) = CreateBoundSetup();
        // No effect (lighting Off): a chain that binds now gets black, as before.
        engine.UpdateDevices(frames.ConvertAll(f => new DeviceFrame(f.Index, f.Id, f.LedCount)).ToArray());

        writer.Tick();

        Assert.True(RgbSyncFrames(tx).Count > 0);
    }

    [Fact]
    public void Tick_still_sends_a_published_black_frame()
    {
        var (_, _, tx, _, _, writer, frames) = CreateBoundSetup();
        FillAll(frames, 159, 0, 113);
        writer.Tick();
        var sent = RgbSyncFrames(tx).Count;

        // The engine's suspend fade and a user-chosen black both publish zeros.
        FillAll(frames, 0, 0, 0);
        writer.Tick();

        Assert.True(RgbSyncFrames(tx).Count > sent);
    }

    [Fact]
    public void Tick_streams_a_bound_strimer_as_one_cable_in_wire_order()
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frame = Assert.Single(provider.BuildFrames(0));
        Assert.Equal(6 * 22, frame.LedCount);
        var engine = new LightingEngine();
        engine.UpdateDevices(new[] { frame });
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => 200 * TimeSpan.TicksPerMillisecond);
        // LED n painted red = n so the wire order is checkable after decode.
        for (var n = 0; n < frame.LedCount; n++)
        {
            frame.SetLed(n, (byte)n, 0, 0);
        }
        frame.Publish();

        writer.Tick();

        // Header packet (RF payload index 0 in chunk-seq-0 USB frame): RF [18]
        // packet index at USB [22], RF [27] LED count at USB [31].
        var sync = RgbSyncFrames(tx);
        var header = sync.Find(f => f[22] == 0);
        Assert.NotNull(header);
        Assert.Equal(6 * 22, header![31]);

        // Reassemble the data parts and decode: LED n at n * 3, R,G,B
        // interleaved, through BuildFrameBuffer's full-brightness scale.
        var compressedLen = (header[24] << 24) | (header[25] << 16) | (header[26] << 8) | header[27];
        var payloads = ReassembleRfPayloads(tx);
        var compressed = new List<byte>();
        foreach (var payload in payloads)
        {
            if (payload[1] != Slv3Protocol.RfRgbSync || payload[18] == 0) continue;
            compressed.AddRange(payload.AsSpan(Slv3RgbFrame.DataPacketOffset, Slv3RgbFrame.DataPacketChunk).ToArray());
        }
        var (_, raw) = TinyUz.Decompress(compressed.ToArray().AsSpan(0, compressedLen));
        Assert.Equal(6 * 22 * 3, raw.Length);
        for (var n = 0; n < 6 * 22; n++)
        {
            Assert.Equal((byte)((n * 255) >> 8), raw[n * 3]);
            Assert.Equal(0, raw[n * 3 + 1]);
        }
    }

    [Fact]
    public void Tick_interleaves_uneven_rings_fan_by_fan_on_the_wire()
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 63 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frames = provider.BuildFrames(0).ToArray();
        const int inner = 4, outer = 5, fans = 3;
        Assert.Equal(2, frames.Length);
        Assert.Equal(fans * inner, frames[0].LedCount);
        Assert.Equal(fans * outer, frames[1].LedCount);
        var engine = new LightingEngine();
        engine.UpdateDevices(frames);
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => 200 * TimeSpan.TicksPerMillisecond);
        // Red numbers every ring LED: the inner zone's LEDs first, then the outer's.
        for (var n = 0; n < frames[0].LedCount; n++) frames[0].SetLed(n, (byte)n, 0, 0);
        for (var n = 0; n < frames[1].LedCount; n++) frames[1].SetLed(n, (byte)(fans * inner + n), 0, 0);
        frames[0].Publish();
        frames[1].Publish();

        writer.Tick();

        var sync = RgbSyncFrames(tx);
        var header = sync.Find(f => f[22] == 0);
        Assert.NotNull(header);
        var compressedLen = (header![24] << 24) | (header[25] << 16) | (header[26] << 8) | header[27];
        var compressed = new List<byte>();
        foreach (var payload in ReassembleRfPayloads(tx))
        {
            if (payload[1] != Slv3Protocol.RfRgbSync || payload[18] == 0) continue;
            compressed.AddRange(payload.AsSpan(Slv3RgbFrame.DataPacketOffset, Slv3RgbFrame.DataPacketChunk).ToArray());
        }
        var (_, raw) = TinyUz.Decompress(compressed.ToArray().AsSpan(0, compressedLen));
        Assert.Equal(fans * (inner + outer) * 3, raw.Length);
        for (var f = 0; f < fans; f++)
        {
            for (var i = 0; i < inner; i++)
            {
                Assert.Equal((byte)(((f * inner + i) * 255) >> 8), raw[(f * (inner + outer) + i) * 3]);
            }
            for (var i = 0; i < outer; i++)
            {
                var source = fans * inner + f * outer + i;
                Assert.Equal((byte)((source * 255) >> 8), raw[(f * (inner + outer) + inner + i) * 3]);
            }
        }
    }

    [Fact]
    public void Tick_streams_a_bound_hydroshift_pump_ring_as_one_24_led_frame()
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 11, FanCount = 0, CoolantTempC = 29 });
        Assert.True(hub.DriveTick());

        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frame = Assert.Single(provider.BuildFrames(0));
        Assert.Equal(24, frame.LedCount);
        var engine = new LightingEngine();
        engine.UpdateDevices(new[] { frame });
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => 200 * TimeSpan.TicksPerMillisecond);
        FillAll(new[] { frame }, 10, 20, 30);

        writer.Tick();

        var header = RgbSyncFrames(tx).Find(f => f[22] == 0);
        Assert.NotNull(header);
        Assert.Equal(24, header![31]);
    }

    private static (Slv3Hub Hub, Slv3TestHub.FakeTxTransport Tx, InMemoryConfigStore Store, LightingEngine Engine, Slv3LightingFrameWriter Writer)
        CreateStrimerSetup(Func<long> clock) => CreateStrimerSetup(clock, new Np50IdentifyTracker());

    private static (Slv3Hub Hub, Slv3TestHub.FakeTxTransport Tx, InMemoryConfigStore Store, LightingEngine Engine, Slv3LightingFrameWriter Writer)
        CreateStrimerSetup(Func<long> clock, Np50IdentifyTracker identify)
    {
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, DevType = 2, FanCount = 0 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        var frames = provider.BuildFrames(0).ToArray();
        // The engine has rendered a first (black) frame by the time the writer streams from it.
        foreach (var frame in frames) frame.Publish();
        engine.UpdateDevices(frames);
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, clock);
        return (hub, tx, store, engine, writer);
    }

    private static void SetStrimer(InMemoryConfigStore store, Action<LianLiWirelessChainLighting> edit) => store.Update(s =>
    {
        var key = Convert.ToHexString(FanMac);
        if (!s.Devices.LianLiWireless.Chains.TryGetValue(key, out var ls))
        {
            ls = new LianLiWirelessChainLighting();
            s.Devices.LianLiWireless.Chains[key] = ls;
        }
        edit(ls);
    });

    // RF header of the latest RgbSync upload: [25..26] frame count, [27] LED count.
    private static byte[] LastRgbHeader(Slv3TestHub.FakeTxTransport tx) =>
        ReassembleRfPayloads(tx).FindLast(p => p[1] == Slv3Protocol.RfRgbSync && p[18] == 0)!;

    [Fact]
    public void Tick_uploads_a_strimer_preset_as_one_multi_frame_loop()
    {
        var now = 0L;
        var (_, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");

        writer.Tick();

        var header = LastRgbHeader(tx);
        Assert.True(((header[25] << 8) | header[26]) > 1);
        Assert.Equal(6 * 22, header[27]);
    }

    [Fact]
    public void Tick_does_not_reupload_an_unchanged_confirmed_preset()
    {
        var now = 0L;
        var (hub, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        writer.Tick();
        Assert.True(hub.DriveTick());
        var count = tx.SentFrames.Count;

        now += 5_000 * TimeSpan.TicksPerMillisecond;
        writer.Tick();

        Assert.Equal(count, tx.SentFrames.Count);
    }

    [Fact]
    public void Tick_reuploads_a_preset_when_its_settings_change()
    {
        var now = 0L;
        var (hub, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        writer.Tick();
        Assert.True(hub.DriveTick());
        var count = tx.SentFrames.Count;

        SetStrimer(store, ls => ls.Speed = 4);
        now += 1_000 * TimeSpan.TicksPerMillisecond;
        writer.Tick();

        Assert.True(tx.SentFrames.Count > count);
    }

    [Fact]
    public void Tick_holds_back_a_preset_reupload_during_rapid_edits()
    {
        var now = 0L;
        var (hub, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        writer.Tick();
        Assert.True(hub.DriveTick());
        var count = tx.SentFrames.Count;

        SetStrimer(store, ls => ls.Brightness = 2);
        now += 100 * TimeSpan.TicksPerMillisecond;
        writer.Tick();

        Assert.Equal(count, tx.SentFrames.Count);
    }

    [Fact]
    public void Tick_blacks_out_a_preset_as_one_black_frame()
    {
        var now = 0L;
        var (_, tx, store, engine, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        engine.SetBlackout(true);

        writer.Tick();

        var header = LastRgbHeader(tx);
        Assert.Equal(1, (header[25] << 8) | header[26]);
        var compressedLen = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        var compressed = new List<byte>();
        foreach (var payload in ReassembleRfPayloads(tx))
        {
            if (payload[1] != Slv3Protocol.RfRgbSync || payload[18] == 0) continue;
            compressed.AddRange(payload.AsSpan(Slv3RgbFrame.DataPacketOffset, Slv3RgbFrame.DataPacketChunk).ToArray());
        }
        var (_, raw) = TinyUz.Decompress(compressed.ToArray().AsSpan(0, compressedLen));
        Assert.All(raw, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Tick_streams_a_preset_strimer_while_it_is_identifying()
    {
        var now = DateTime.UtcNow.Ticks;
        var identify = new Np50IdentifyTracker();
        var (hub, tx, store, _, writer) = CreateStrimerSetup(() => now, identify);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        var zoneId = new Slv3LightingDeviceProvider(hub, store, identify).GetAll().Devices[0].Id;
        identify.Schedule(zoneId, 5_000);

        writer.Tick();

        var header = LastRgbHeader(tx);
        Assert.Equal(1, (header[25] << 8) | header[26]);
    }

    [Fact]
    public void Tick_retries_a_failed_preset_upload_no_faster_than_the_preset_floor()
    {
        var now = 0L;
        var (_, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        tx.FailSends = true;

        writer.Tick();
        var afterFirstAttempt = RgbSyncFrames(tx).Count;
        Assert.True(afterFirstAttempt > 0);

        now += 100 * TimeSpan.TicksPerMillisecond;
        writer.Tick();
        Assert.Equal(afterFirstAttempt, RgbSyncFrames(tx).Count);

        now += 500 * TimeSpan.TicksPerMillisecond;
        writer.Tick();
        Assert.True(RgbSyncFrames(tx).Count > afterFirstAttempt);
    }

    [Fact]
    public void Tick_uploads_a_fan_chain_preset_sized_to_its_fans()
    {
        var now = 0L;
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 24 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");

        writer.Tick();

        var header = LastRgbHeader(tx);
        Assert.True(((header[25] << 8) | header[26]) > 1);
        Assert.Equal(3 * Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Lcd), header[27]);
    }

    [Fact]
    public void Tick_reuploads_a_fan_chain_preset_when_its_fan_count_changes()
    {
        var now = 0L;
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = FanMac, MasterMac = net.MasterMac, RxType = 1, FanCount = 3, FansType = 24 });
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var engine = new LightingEngine();
        engine.UpdateDevices(provider.BuildFrames(0).ToArray());
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => now);
        SetStrimer(store, ls => ls.Mode = "rainbow");
        writer.Tick();
        Assert.True(hub.DriveTick());

        net.Fans[0].FanCount = 2;
        Assert.True(hub.DriveTick());
        now += 1_000 * TimeSpan.TicksPerMillisecond;
        writer.Tick();

        Assert.Equal(2 * Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Lcd), LastRgbHeader(tx)[27]);
    }

    [Fact]
    public void Tick_streams_engine_frames_to_a_strimer_in_custom_mode()
    {
        var now = 0L;
        var (_, tx, store, _, writer) = CreateStrimerSetup(() => now);
        SetStrimer(store, ls => ls.Mode = LianLiWirelessChainLighting.ModeCustom);

        writer.Tick();

        var header = LastRgbHeader(tx);
        Assert.Equal(1, (header[25] << 8) | header[26]);
    }

    // Joins the 60-byte USB chunks (chunk seq at [1], RF bytes at [4..]) back
    // into the 240-byte RF payloads the hub sent, in send order.
    private static List<byte[]> ReassembleRfPayloads(Slv3TestHub.FakeTxTransport tx)
    {
        var payloads = new List<byte[]>();
        byte[]? current = null;
        foreach (var frame in tx.SentFrames)
        {
            if (frame.Length < 64 || frame[0] != Slv3Protocol.UsbSendRf) continue;
            if (frame[1] == 0)
            {
                current = new byte[Slv3Protocol.RfPayloadSize];
                payloads.Add(current);
            }
            if (current is null) continue;
            var offset = frame[1] * 60;
            var len = Math.Min(60, Slv3Protocol.RfPayloadSize - offset);
            if (len > 0) frame.AsSpan(4, len).CopyTo(current.AsSpan(offset));
        }
        return payloads;
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

    [Theory]
    [InlineData(1, 132, 1, 700, 30)]
    [InlineData(1, 142, 1, 747, 30)]
    [InlineData(1, 143, 2, 1424, 60)]
    [InlineData(1, 174, 2, 1716, 60)]
    [InlineData(2, 264, 3, 2642, 60)]
    [InlineData(4, 528, 4, 5283, 60)]
    [InlineData(5, 702, 5, 6999, 60)]
    [InlineData(6, 600, 5, 6120, 60)]
    [InlineData(7, 720, 5, 7328, 60)]
    [InlineData(8, 798, 6, 8141, 60)]
    public void WindowPlan_sizes_the_round_to_half_the_air_budget(int chains, int leds, int stretch, int cycleMs, int frames)
    {
        var plan = Slv3LightingFrameWriter.WindowPlan(chains, leds);
        Assert.Equal(stretch, plan.Stretch);
        Assert.Equal(cycleMs, plan.CycleMs, 0);
        Assert.Equal(frames, plan.Frames);
    }

    [Theory]
    [InlineData(1, 132)]
    [InlineData(1, 60)]
    public void WindowPlan_keeps_short_windows_when_the_frame_interval_is_not_stretched(int chains, int leds)
    {
        var plan = Slv3LightingFrameWriter.WindowPlan(chains, leds);
        Assert.Equal(1, plan.Stretch);
        Assert.Equal(Slv3LightingFrameWriter.WindowFrames, plan.Frames);
    }

    [Theory]
    [InlineData(2, 264)]
    [InlineData(4, 528)]
    [InlineData(7, 720)]
    [InlineData(8, 798)]
    [InlineData(1, 174)]
    public void WindowPlan_long_windows_halve_uploads_without_a_longer_frame_interval(int chains, int leds)
    {
        var shortStretch = (int)Math.Ceiling(Math.Max(Math.Max(450, (8 * chains + 0.47 * leds) * 10), 150 * chains) / 750);
        var plan = Slv3LightingFrameWriter.WindowPlan(chains, leds);
        Assert.Equal(Slv3LightingFrameWriter.LongWindowFrames, plan.Frames);
        // The margin guard may add one step over the short plan.
        Assert.True(plan.Stretch <= Math.Min(6, shortStretch + 1));
        // A window holds more than the round it must outlast, whatever the chain count.
        var windowMs = plan.Frames * Slv3LightingFrameWriter.WindowIntervalTicks / 1.6 * plan.Stretch;
        Assert.True(windowMs > plan.CycleMs * 4 / 3);
    }

    [Theory]
    [InlineData(1, 143)]
    [InlineData(1, 174)]
    [InlineData(2, 264)]
    [InlineData(4, 528)]
    [InlineData(5, 702)]
    [InlineData(6, 600)]
    [InlineData(7, 720)]
    [InlineData(8, 798)]
    public void WindowPlan_long_windows_pick_the_smallest_stretch_that_keeps_average_air_at_half_the_budget_and_a_margin(int chains, int leds)
    {
        var plan = Slv3LightingFrameWriter.WindowPlan(chains, leds);
        Assert.Equal(Slv3LightingFrameWriter.LongWindowFrames, plan.Frames);
        // Payloads per round (headers plus both data passes) over the round: half the per-second budget.
        var payloads = 8 * chains + 0.47 * 2 * leds;
        var averageAir = payloads / (plan.CycleMs / 1000);
        Assert.True(averageAir <= 100.5, $"average air {averageAir:F0}/s");
        // The margin a window keeps after its replacement lands.
        Assert.True(Slv3LightingFrameWriter.WindowMarginMs(plan.Stretch, plan.Frames, plan.CycleMs, 200) >= 1000);
        // One step lower would not fit the round into 75 percent of the window, or would leave less margin.
        if (plan.Stretch > 1)
        {
            var lower = plan.Stretch - 1;
            var fits = plan.CycleMs <= 750 * 2 * lower
                && Slv3LightingFrameWriter.WindowMarginMs(lower, plan.Frames, plan.CycleMs, 200) >= 1000;
            Assert.False(fits);
        }
    }

    [Fact]
    public void WindowPlan_for_the_zaldlum_setup_is_the_first_long_plan_again()
    {
        var plan = Slv3LightingFrameWriter.WindowPlan(8, 798);
        Assert.Equal(6, plan.Stretch);
        Assert.Equal(60, plan.Frames);
    }

    [Fact]
    public void WindowMargin_is_the_window_less_wrap_safety_round_and_upload()
    {
        // Stretch 1: 26 usable frames of 33.125 ms, two safety frames and two wrap frames already taken off.
        var margin = Slv3LightingFrameWriter.WindowMarginMs(1, 30, 700, 200);
        Assert.Equal(26 * 33.125 - 900, margin, 3);
    }

    [Theory]
    [InlineData(2, 60, 2642, 390, 744)]
    [InlineData(4, 60, 5283, 780, 1490)]
    [InlineData(5, 60, 7328, 780, 1333)]
    [InlineData(6, 60, 8141, 780, 2408)]
    public void WindowMargin_stays_positive_at_the_planned_cycle_with_a_slow_upload(int stretch, int frames, double cycleMs, double uploadMs, int marginMs)
    {
        Assert.Equal(marginMs, Slv3LightingFrameWriter.WindowMarginMs(stretch, frames, cycleMs, uploadMs), 0);
    }

    [Fact]
    public void ReduceWindow_keeps_the_loop_slots_of_a_long_window()
    {
        const int Leds = 2;
        const long First = 47;
        var full = new byte[Slv3LightingFrameWriter.LongWindowFrames * Leds * 3];
        for (var j = 0; j < Slv3LightingFrameWriter.LongWindowFrames; j++)
        {
            Array.Fill(full, (byte)j, Slv3LightingFrameWriter.WindowSlot(First + j, Slv3LightingFrameWriter.LongWindowFrames) * Leds * 3, Leds * 3);
        }
        var reduced = Slv3LightingFrameWriter.ReduceWindow(full, Leds, First, 2, Slv3LightingFrameWriter.LongWindowFrames);
        Assert.Equal(Slv3LightingFrameWriter.LongWindowFrames / 2 * Leds * 3, reduced.Length);
        // Loop slot = (frame / divisor) mod count: each group holds the frame at its centre.
        for (var group = (First + 1) / 2; group < (First + 1) / 2 + 30; group++)
        {
            var expected = (byte)Math.Clamp(group * 2 - First, 0, 59);
            Assert.Equal(expected, reduced[(int)(group % 30) * Leds * 3]);
        }
    }

    [Theory]
    [InlineData(30, 3)]
    [InlineData(60, 6)]
    public void Divisor_ladder_reaches_the_shortest_loop_a_short_window_falls_back_to(int frames, int maxDivisor)
    {
        Assert.Equal(maxDivisor, Slv3LightingFrameWriter.MaxDivisorFor(frames));
        Assert.Equal(10, frames / Slv3LightingFrameWriter.MaxDivisorFor(frames));
        for (var d = 1; d <= maxDivisor; d++)
        {
            Assert.Equal(0, frames % d);
        }
    }

    [Fact]
    public void LatestLoopStart_covers_every_divisor_of_a_long_window()
    {
        const long First = 101;
        var latest = Slv3LightingFrameWriter.LatestLoopStart(First, Slv3LightingFrameWriter.LongWindowFrames);
        for (var d = 1; d <= 6; d++)
        {
            Assert.True((First + d - 1) / d * d <= latest);
        }
    }

    [Fact]
    public void WindowTickTimes_cover_the_requested_frame_count()
    {
        var ticks = Slv3LightingFrameWriter.WindowTickTimes(0, 1.6, 100, 53 * 3, Slv3LightingFrameWriter.LongWindowFrames);
        Assert.Equal(60, ticks.Length);
        Assert.InRange(ticks[1] - ticks[0], 99, 100);
    }

    [Theory]
    [InlineData(1, 3, 2)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 1, 1)]
    public void Window_lead_and_safety_keep_their_time_as_the_interval_stretches(int stretch, int lead, int safety)
    {
        Assert.Equal(lead, Slv3LightingFrameWriter.LeadFramesFor(stretch));
        Assert.Equal(safety, Slv3LightingFrameWriter.SafetyFramesFor(stretch));
    }

    [Fact]
    public void Tick_pushes_one_live_chain_per_tick_and_serves_the_longest_waiting_next()
    {
        var now = 0L;
        var (hub, net, tx) = Slv3TestHub.CreateConnected();
        var macs = new[] { "AA0000000001", "AA0000000002", "AA0000000003" };
        foreach (var mac in macs)
        {
            net.Fans.Add(new Slv3TestHub.SimulatedFan { Mac = Convert.FromHexString(mac), MasterMac = net.MasterMac, RxType = 1, FanCount = 1 });
        }
        Assert.True(hub.DriveTick());
        var store = new InMemoryConfigStore();
        var identify = new Np50IdentifyTracker();
        var provider = new Slv3LightingDeviceProvider(hub, store, identify);
        var frames = new List<DeviceFrame>(provider.BuildFrames(0));
        var engine = new LightingEngine();
        engine.UpdateDevices(frames.ToArray());
        var writer = new Slv3LightingFrameWriter(engine, hub, store, identify, provider, () => now);

        var counts = new List<int>();
        for (var tick = 0; tick < 3; tick++)
        {
            FillAll(frames, (byte)(10 + tick), 20, 30);
            now += 1000 * TimeSpan.TicksPerMillisecond;
            writer.Tick();
            counts.Add(RgbSyncFrames(tx).Count);
        }
        var perPush = counts[0];
        Assert.True(perPush > 0);
        Assert.Equal(new[] { perPush, 2 * perPush, 3 * perPush }, counts);
        var payloads = ReassembleRfPayloads(tx);
        foreach (var mac in macs)
        {
            var bytes = Convert.FromHexString(mac);
            Assert.Contains(payloads, p => p.AsSpan().IndexOf(bytes) >= 0);
        }
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

    // Smooth gradients with noise: the shape of a plasma window, which barely compresses.
    private static byte[] NoisyGradientWindow(int leds)
    {
        var rng = new Random(11);
        var data = new byte[Slv3LightingFrameWriter.WindowFrames * leds * 3];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(((i / 3) * 5 + i / (leds * 3) * 17 + rng.Next(0, 160)) & 0xFF);
        }
        return data;
    }

    [Fact]
    public void ReduceWindow_keeps_the_loop_span_and_places_each_group_in_its_slot()
    {
        const int Leds = 2;
        const long First = 1001;
        var full = new byte[Slv3LightingFrameWriter.WindowFrames * Leds * 3];
        for (var j = 0; j < Slv3LightingFrameWriter.WindowFrames; j++)
        {
            Array.Fill(full, (byte)j, Slv3LightingFrameWriter.WindowSlot(First + j) * Leds * 3, Leds * 3);
        }

        var reduced = Slv3LightingFrameWriter.ReduceWindow(full, Leds, First, 2);

        Assert.Equal(15 * Leds * 3, reduced.Length);
        for (var group = 501L; group < 501 + 15; group++)
        {
            var sourceFrame = group * 2;
            var expected = (byte)(sourceFrame - First);
            Assert.Equal(expected, reduced[(int)(group % 15) * Leds * 3]);
        }
    }

    [Fact]
    public async Task A_plasma_like_174_led_window_steps_down_uploads_and_is_remembered()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var full = NoisyGradientWindow(174);
        Assert.Throws<InvalidOperationException>(() => TinyUz.Compress(full));

        var st = new Slv3LightingFrameWriter.ChainRolling();
        st.Upload = writer.UploadWindowAsync(mac, st, full, 174, 1001, 1, 53);
        Assert.True(await st.Upload);
        Assert.Equal(Slv3Hub.WindowUploadResult.Sent, st.UploadResult);
        Assert.Equal(2, st.UploadDivisor);

        st.UploadStretch = 1;
        st.UploadFirstFrame = 1001;
        writer.CollectUpload(mac, st);
        Assert.Equal(1002, st.WindowFirstFrame);
        Assert.False(st.Failing);
    }

    [Fact]
    public async Task A_small_chain_window_uploads_unchanged()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var st = new Slv3LightingFrameWriter.ChainRolling();
        Assert.True(await writer.UploadWindowAsync(Convert.ToHexString(FanMac), st, new byte[30 * 40 * 3], 40, 1001, 1, 53));
        Assert.Equal(1, st.UploadDivisor);
    }

    [Fact]
    public async Task A_size_refusal_at_the_floor_is_not_a_write_failure()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var chainKey = Slv3LightingDeviceProvider.DeviceIdFor(mac);
        LightingWriteHealth.Shared.Forget(chainKey);
        var full = new byte[Slv3LightingFrameWriter.WindowFrames * 800 * 3];
        new Random(3).NextBytes(full);

        var st = new Slv3LightingFrameWriter.ChainRolling();
        st.Upload = writer.UploadWindowAsync(mac, st, full, 800, 1001, 1, 53);
        Assert.False(await st.Upload);
        Assert.Equal(Slv3Hub.WindowUploadResult.TooLarge, st.UploadResult);
        writer.CollectUpload(mac, st);

        Assert.False(st.Failing);
        Assert.Equal(long.MinValue, st.WindowFirstFrame);
        Assert.False(LightingWriteHealth.Shared.IsFailing(chainKey));
    }

    [Fact]
    public void ReduceWindow_by_three_samples_the_group_midpoint_and_clamps_at_the_window_ends()
    {
        const long First = 1000;
        var full = new byte[Slv3LightingFrameWriter.WindowFrames * 3];
        for (var j = 0; j < Slv3LightingFrameWriter.WindowFrames; j++)
        {
            Array.Fill(full, (byte)j, Slv3LightingFrameWriter.WindowSlot(First + j) * 3, 3);
        }

        var reduced = Slv3LightingFrameWriter.ReduceWindow(full, 1, First, 3);

        Assert.Equal(10 * 3, reduced.Length);
        // The first group samples its midpoint frame; the last group runs past the window and clamps to its final frame.
        Assert.Equal(3, reduced[(int)(334 % 10) * 3]);
        Assert.Equal(29, reduced[(int)(343 % 10) * 3]);
    }

    private static Slv3LightingFrameWriter.ChainRolling Joinable(double lastUploadMs, double retryAtMs = 0) =>
        new() { Joinable = true, LastUploadStartMs = lastUploadMs, RetryAtMs = retryAtMs };

    [Fact]
    public void An_oversized_chain_waiting_out_its_retry_never_holds_the_upload_turn()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var oversized = Joinable(0, retryAtMs: 10_000);
        var rolling = new[] { oversized, Joinable(100), Joinable(200), Joinable(300) };
        for (var i = 0; i < rolling.Length; i++) writer._rolling["m" + i] = rolling[i];

        // The three normal chains take the turn in order of waiting even though the oversized one waited longest.
        Assert.True(writer.IsNextToUpload(rolling[1], 1000));
        Assert.False(writer.IsNextToUpload(rolling[2], 1000));
        rolling[1].LastUploadStartMs = 1000;
        Assert.True(writer.IsNextToUpload(rolling[2], 1000));
        rolling[2].LastUploadStartMs = 1100;
        Assert.True(writer.IsNextToUpload(rolling[3], 1000));
        rolling[3].LastUploadStartMs = 1200;
        Assert.True(writer.IsNextToUpload(rolling[1], 1300));

        // Once the retry time passes it competes again, as the longest waiting.
        Assert.True(writer.IsNextToUpload(oversized, 20_000));
        Assert.False(writer.IsNextToUpload(rolling[1], 20_000));
    }

    [Fact]
    public void A_divisor_learned_under_a_longer_plan_is_not_kept_after_the_plan_drops()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var st = new Slv3LightingFrameWriter.ChainRolling
        {
            UploadFrames = Slv3LightingFrameWriter.LongWindowFrames,
            UploadStretch = 1,
            UploadDivisor = 5,
            UploadResult = Slv3Hub.WindowUploadResult.Sent,
            Upload = Task.FromResult(true),
        };
        // The writer plans short windows, so an upload that settled at a long window's divisor must not teach it.
        writer.CollectUpload(mac, st);
        Assert.Equal(1, writer.StartDivisor(mac, st, 0));
    }

    [Fact]
    public async Task The_learned_divisor_resets_on_a_content_change_and_probes_one_step_lower_periodically()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var full = NoisyGradientWindow(174);
        var st = new Slv3LightingFrameWriter.ChainRolling();
        st.Upload = writer.UploadWindowAsync(mac, st, full, 174, 1001, 1, 53);
        await st.Upload;
        st.UploadStretch = 1;
        writer.CollectUpload(mac, st);
        // This content sits near the cap, which the size check would call hopeless; it is covered on its own.
        st.LastCompressedBytes = 0;
        Assert.Equal(2, writer.StartDivisor(mac, st, 0));

        // Within the probe period the remembered divisor is used; once due, one step lower is tried once.
        var probeAt = st.ProbeAtMs;
        Assert.Equal(2, writer.StartDivisor(mac, st, probeAt - 1));
        Assert.Equal(1, writer.StartDivisor(mac, st, probeAt));
        Assert.Equal(2, writer.StartDivisor(mac, st, probeAt + 1));

        writer.ResetWindowDivisor(mac, st);
        Assert.Equal(1, writer.StartDivisor(mac, st, probeAt + 2));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(1002)]
    [InlineData(1003)]
    [InlineData(1004)]
    [InlineData(1005)]
    public void The_safety_margin_start_covers_every_divisor_an_upload_may_land_on(long first)
    {
        var latest = Slv3LightingFrameWriter.LatestLoopStart(first);
        for (var d = 1; d <= Slv3LightingFrameWriter.MaxWindowDivisor; d++)
        {
            Assert.True(latest >= (first + d - 1) / d * d);
        }
    }

    [Fact]
    public async Task A_refused_probe_steps_back_up_to_the_remembered_divisor_within_the_same_call()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var full = NoisyGradientWindow(174);
        var st = new Slv3LightingFrameWriter.ChainRolling();
        st.Upload = writer.UploadWindowAsync(mac, st, full, 174, 1001, 1, 53);
        await st.Upload;
        st.UploadStretch = 1;
        writer.CollectUpload(mac, st);
        st.LastCompressedBytes = 0;

        var probeStart = writer.StartDivisor(mac, st, st.ProbeAtMs);
        Assert.Equal(1, probeStart);
        st.Upload = writer.UploadWindowAsync(mac, st, full, 174, 1001, probeStart, 53);
        Assert.True(await st.Upload);
        Assert.Equal(2, st.UploadDivisor);
        writer.CollectUpload(mac, st);
        Assert.Equal(2, writer.StartDivisor(mac, st, 0));
    }

    private async Task<(Slv3LightingFrameWriter Writer, string Mac, Slv3LightingFrameWriter.ChainRolling St)> ChainReducedToTwo()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var mac = Convert.ToHexString(FanMac);
        var st = new Slv3LightingFrameWriter.ChainRolling();
        st.Upload = writer.UploadWindowAsync(mac, st, NoisyGradientWindow(174), 174, 1001, 1, 53);
        await st.Upload;
        st.UploadStretch = 1;
        writer.CollectUpload(mac, st);
        Assert.Equal(2, st.UploadDivisor);
        return (writer, mac, st);
    }

    [Fact]
    public async Task A_probe_that_cannot_fit_is_skipped_without_compressing_a_window()
    {
        var (writer, mac, st) = await ChainReducedToTwo();
        // The last fit sat at the cap, so one divisor lower holds twice the bytes.
        st.LastCompressedBytes = TinyUz.MaxCompressedLength;
        var probeAt = st.ProbeAtMs;
        Assert.Equal(2, writer.StartDivisor(mac, st, probeAt));
        Assert.False(st.Probing);
        Assert.True(st.ProbeAtMs > probeAt);
        Assert.True(Slv3LightingFrameWriter.ProbeCannotFit(st, 2));
    }

    [Fact]
    public async Task A_probe_with_room_under_the_cap_is_tried()
    {
        var (writer, mac, st) = await ChainReducedToTwo();
        st.LastCompressedBytes = 4000;
        Assert.Equal(1, writer.StartDivisor(mac, st, st.ProbeAtMs));
        Assert.True(st.Probing);
    }

    [Fact]
    public async Task A_probe_that_falls_back_doubles_the_wait_at_once_and_one_that_lands_lower_clears_it()
    {
        var (writer, mac, st) = await ChainReducedToTwo();
        st.LastCompressedBytes = 4000;
        double start = st.ProbeAtMs;
        Assert.Equal(1, writer.StartDivisor(mac, st, start));
        var period = st.ProbeAtMs - start;

        // The probe came back at the remembered divisor: the very next wait is already doubled.
        st.Upload = Task.FromResult(true);
        st.UploadResult = Slv3Hub.WindowUploadResult.Sent;
        st.UploadStretch = 1;
        st.UploadDivisor = 2;
        writer.CollectUpload(mac, st);
        Assert.Equal(1, st.ProbeFailures);
        Assert.Equal(2 * period, st.ProbeAtMs - start);

        start = st.ProbeAtMs;
        st.LastCompressedBytes = 4000;
        Assert.Equal(1, writer.StartDivisor(mac, st, start));
        st.Upload = Task.FromResult(true);
        st.UploadResult = Slv3Hub.WindowUploadResult.Sent;
        st.UploadStretch = 1;
        st.UploadDivisor = 2;
        writer.CollectUpload(mac, st);
        Assert.Equal(4 * period, st.ProbeAtMs - start);

        // This probe fit one divisor lower.
        start = st.ProbeAtMs;
        st.LastCompressedBytes = 4000;
        Assert.Equal(1, writer.StartDivisor(mac, st, start));
        st.Upload = Task.FromResult(true);
        st.UploadResult = Slv3Hub.WindowUploadResult.Sent;
        st.UploadStretch = 1;
        st.UploadDivisor = 1;
        writer.CollectUpload(mac, st);
        Assert.Equal(0, st.ProbeFailures);
    }

    [Fact]
    public void The_busy_defer_deadline_leaves_a_render_wait_an_upload_a_turn_and_the_plan_margin()
    {
        // The 8-chain plan's turn, the render lead and the assumed upload.
        var deadline = Slv3LightingFrameWriter.BusyDeferDeadlineMs(727, 150, 727);
        Assert.Equal(727 + 100 + 200 + 727 + 1000, deadline);
        // A slower observed upload raises it.
        Assert.Equal(deadline + 300, Slv3LightingFrameWriter.BusyDeferDeadlineMs(727, 500, 727));
        // At the 8-chain plan a turn arrives with more window left than the deadline, so a deferral has room and still leaves a retry.
        var plan = Slv3LightingFrameWriter.WindowPlan(8, 798);
        var turnWindowRemainingMs = Slv3LightingFrameWriter.WindowMarginMs(plan.Stretch, plan.Frames, plan.CycleMs, 200) + 400;
        Assert.True(turnWindowRemainingMs > deadline);
        // A released chain keeps a full retry: the deadline is at least what one retry takes.
        Assert.True(deadline >= 727 + 100 + 200 + 727);
    }

    [Fact]
    public void Per_chain_periodic_work_spreads_over_stable_slots()
    {
        var macs = new[] { "04EF71E566E1", "4ED271E566E1", "D716CCE566E1", "1150D8E566E1", "7F9987E566E1", "884713506CE1", "E300CCE566E1", "F7C806F517E1" };
        var slots = new HashSet<int>();
        foreach (var mac in macs)
        {
            var slot = Slv3LightingFrameWriter.StaggerSlot(mac, 8);
            Assert.InRange(slot, 0, 7);
            Assert.Equal(slot, Slv3LightingFrameWriter.StaggerSlot(mac, 8));
            slots.Add(slot);
        }
        Assert.True(slots.Count >= 4, $"only {slots.Count} distinct slots");
    }

    [Theory]
    // A long window just landed: plenty left, so the turn can wait.
    [InlineData(1000, 1000, 60, 4, true)]
    // About 2 s left at the stretch 4 step (15 frames): still beyond the deadline.
    [InlineData(1000, 1043, 60, 4, true)]
    // Under the deadline: the turn is due.
    [InlineData(1000, 1051, 60, 4, false)]
    // A window that is already past its wrap margin is never deferred.
    [InlineData(1000, 1058, 60, 4, false)]
    // A short window never holds more than the deadline.
    [InlineData(1000, 1000, 30, 1, false)]
    public void A_busy_rx_defers_a_turn_only_while_the_window_has_time_beyond_the_upload_deadline(long first, long current, int frames, int stretch, bool defer)
    {
        Assert.Equal(defer, Slv3LightingFrameWriter.WindowOutlastsBusyDeadline(first, current, frames, stretch, 1200));
    }

    [Fact]
    public void A_deferring_chain_does_not_hold_the_upload_turn()
    {
        var (_, _, _, _, _, writer, _) = CreateBoundSetup();
        var deferring = Joinable(0);
        deferring.BusyDeferred = true;
        var waiting = Joinable(100);
        writer._rolling["a"] = deferring;
        writer._rolling["b"] = waiting;

        Assert.True(writer.IsNextToUpload(waiting, 1000));
        deferring.BusyDeferred = false;
        Assert.False(writer.IsNextToUpload(waiting, 1000));
    }

    [Fact]
    public void A_landed_window_keeps_the_chain_covered_with_no_device_echo_consulted()
    {
        // During an RX busy spell the dongle reports no echoes; the writer takes only the upload outcome, so a sent window rolls on.
        var (hub, _, _, _, _, writer, _) = CreateBoundSetup();
        var st = new Slv3LightingFrameWriter.ChainRolling { UploadFirstFrame = 500, UploadStretch = 1, UploadFrames = Slv3LightingFrameWriter.WindowFrames, UploadDivisor = 1, Failing = true };
        st.Upload = Task.FromResult(true);
        st.UploadResult = Slv3Hub.WindowUploadResult.Sent;

        writer.CollectUpload(Convert.ToHexString(FanMac), st);

        Assert.False(st.Failing);
        Assert.Equal(500, st.WindowFirstFrame);
        Assert.False(hub.TxThrottled);
    }
}
