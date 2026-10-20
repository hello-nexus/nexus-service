using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// The keeb stream is min(global, zone) x the keeb's own firmware level. The slider moves
/// the level only; the knob moves global only; neither feeds the other, including across
/// a stream stop and start. Real writer, applier and hub over a fake device that stores the
/// settings brightness byte like the firmware does.
/// </summary>
public class KeebStreamBrightnessTests
{
    private sealed class FakeKeeb : IHidDevice
    {
        private readonly object _sync = new();
        private byte[] _lastFeature = Array.Empty<byte>();
        private readonly Queue<byte[]> _input = new();
        private int _maxKeyByte;

        /// <summary>The brightness byte the "device" holds; the knob and settings writes both set it.</summary>
        public volatile int BrightByte = 255;

        /// <summary>When set, the next settings read parks inside Read until <see cref="ReleaseRead"/>.</summary>
        public volatile bool HoldNextSettingsRead;
        public readonly ManualResetEventSlim ReadEntered = new(false);
        public readonly ManualResetEventSlim ReleaseRead = new(false);

        public int VendorId => KeebProtocol.VendorId;
        public int ProductId => KeebProtocol.ProductId;
        public string Path => "fake-keeb";
        public string? Serial => "TESTKEEB";
        public int UsagePage => KeebProtocol.VendorUsagePage;
        public int Usage => KeebProtocol.VendorUsage;

        /// <summary>Strongest channel byte in the key pages written since the last <see cref="ResetStream"/>.</summary>
        public int MaxKeyByte { get { lock (_sync) { return _maxKeyByte; } } }

        public void ResetStream() { lock (_sync) { _maxKeyByte = 0; } }

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            lock (_sync)
            {
                _lastFeature = report.ToArray();
                if (_lastFeature.AsSpan().SequenceEqual(KeebProtocol.SettingsReadFeature))
                {
                    var page = new byte[KeebLayout.PageSize];
                    page[(OperatingSystem.IsWindows() ? 3 : 2) + 1] = (byte)BrightByte;
                    _input.Enqueue(page);
                }
            }
            return true;
        }

        public bool Write(ReadOnlySpan<byte> report)
        {
            lock (_sync)
            {
                if (_lastFeature.AsSpan().SequenceEqual(KeebProtocol.SettingsWriteFeature))
                {
                    BrightByte = report[4];
                }
                else if (_lastFeature.AsSpan().SequenceEqual(KeebProtocol.KeyboardStreamFeature))
                {
                    for (var i = 1; i < report.Length; i++) _maxKeyByte = Math.Max(_maxKeyByte, report[i]);
                }
            }
            return true;
        }

        public bool GetFeature(Span<byte> buffer) => true;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            // Park only the real response read (a reply is queued), not the drain that precedes the request.
            bool replyQueued;
            lock (_sync) { replyQueued = _input.Count > 0; }
            if (HoldNextSettingsRead && replyQueued)
            {
                HoldNextSettingsRead = false;
                ReadEntered.Set();
                ReleaseRead.Wait(5000);
            }
            lock (_sync)
            {
                if (_input.Count == 0) return 0;
                var page = _input.Dequeue();
                page.CopyTo(buffer);
                return page.Length;
            }
        }

        public void Dispose() { }
    }

    private sealed class FakeHid : IHidEnumerator
    {
        public FakeKeeb Device { get; } = new();

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => new[]
        {
            new HidDeviceInfo
            {
                Path = "fake-keeb", VendorId = vendorId, ProductId = productId, Serial = "TESTKEEB",
                UsagePage = KeebProtocol.VendorUsagePage, Usage = KeebProtocol.VendorUsage,
                FeatureReportByteLength = KeebProtocol.FeatureReportSize, OutputReportByteLength = KeebLayout.PageSize,
            },
        };

        public IReadOnlyList<HidDeviceInfo> FindAll() => Find(KeebProtocol.VendorId, KeebProtocol.ProductId);
        public IHidDevice? Open(string path, bool forInput = false) => Device;
    }

    private sealed class White : IEffect
    {
        public string Name => "white";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(255, 255, 255);
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeHid Hid = new();
        public readonly InMemoryConfigStore Store = new();
        public readonly KeebHub Hub;
        public readonly KeebSettingsApplier Applier;
        public readonly LightingEngine Engine = new() { FrameIntervalMs = 10 };
        public readonly KeebLightingFrameWriter Writer;
        public readonly KeyReactiveOverlay Overlay = new();
        public readonly string KeysId;
        private readonly object _frameSync = new();
        private int _frames;

        public Rig(int level, float global)
        {
            Store.Update(s =>
            {
                s.Keeb.RotaryRight = "BrightnessAdjustment";
                s.Keeb.FirmwareLighting.Brightness = level;
                s.Lighting.GlobalBrightness = global;
            });
            Hid.Device.BrightByte = (int)Math.Round(level / 100.0 * 255);
            Hub = new KeebHub(Hid);
            Assert.True(Hub.EnsureConnected());
            Applier = new KeebSettingsApplier(Hub, Store, new Nexus.Service.Sockets.MultiplexHub(), Hid);
            var leds = Hub.KeyMap.LedCount;
            KeysId = Hub.DeviceId + KeebLightingDeviceProvider.KeysSuffix;
            var frame = new DeviceFrame(0, KeysId, leds)
            {
                Archetype = "keyboard",
                DeviceId = Hub.DeviceId,
                LedU = Enumerable.Range(0, leds).Select(i => i / (float)(leds - 1)).ToArray(),
                LedV = Enumerable.Repeat(0.5f, leds).ToArray(),
            };
            Engine.UpdateDevices(new[] { frame });
            Engine.OnFrame += _ =>
            {
                lock (_frameSync)
                {
                    _frames++;
                    Monitor.PulseAll(_frameSync);
                }
            };
            Writer = new KeebLightingFrameWriter(Engine, Hub, Store, new Np50IdentifyTracker(), Applier, Overlay) { KnobReadEveryTicks = 2 };
        }

        public float Global => Store.Load().Lighting.GlobalBrightness;
        public int Level => Store.Load().Keeb.FirmwareLighting.Brightness;

        public void StartStream() => Engine.SetEffect(new White());

        /// <summary>Streams with key reactions only: no engine effect, just an enabled reaction on the keeb's card.</summary>
        public void StartReactionsOnlyStream()
        {
            Overlay.Set(Hub.DeviceId, KeyReactionCatalog.Sanitize(new KeyReaction
            {
                Enabled = true, Effect = KeyReactionCatalog.Fade, Color = "#ffffff", Background = KeyReactionCatalog.BackgroundDark,
            }));
        }

        /// <summary>Waits for the next engine frame, or a short timeout when the engine is stopped.</summary>
        private void WaitForFrame()
        {
            lock (_frameSync)
            {
                var seen = _frames;
                var end = Environment.TickCount64 + 100;
                while (_frames == seen && Environment.TickCount64 < end) Monitor.Wait(_frameSync, 20);
            }
        }

        /// <summary>Runs writer ticks, each paced by an engine frame (or a short wait when the engine is idle).</summary>
        public void Pump(int ticks, bool press = false)
        {
            // MaxKeyByte then covers every tick of this call: a reaction fades between presses.
            Hid.Device.ResetStream();
            for (var i = 0; i < ticks; i++)
            {
                WaitForFrame();
                if (press) Overlay.PressOn(KeysId, key: null, led: 10);
                Writer.Tick();
            }
        }

        public void Dispose()
        {
            Engine.Dispose();
            Hub.Dispose();
        }
    }

    private static int Expected(float global, int level) => (int)Math.Round(255 * global * level / 100.0);

    [Theory]
    [InlineData(100, 1.0f)]
    [InlineData(30, 1.0f)]
    [InlineData(30, 0.8f)]
    [InlineData(0, 1.0f)]
    public void The_stream_is_global_times_the_keeb_level(int level, float global)
    {
        using var rig = new Rig(level, global);
        rig.StartStream();
        rig.Pump(20);
        Assert.InRange(rig.Hid.Device.MaxKeyByte, Expected(global, level) - 2, Expected(global, level) + 2);
        Assert.Equal(global, rig.Global);
        Assert.Equal(level, rig.Level);
    }

    [Fact]
    public void A_knob_detent_while_streaming_moves_global_and_leaves_the_level_alone()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(10);

        Assert.True(rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: false));
        rig.Hid.Device.BrightByte -= (int)Math.Round(0.05 * 255);   // the firmware steps its byte with the same detent
        rig.Pump(10);

        Assert.Equal(0.75f, rig.Global, 3);
        Assert.Equal(50, rig.Level);
        // Global x level, not level squared.
        Assert.InRange(rig.Hid.Device.MaxKeyByte, Expected(0.75f, 50) - 2, Expected(0.75f, 50) + 2);
    }

    [Fact]
    public void A_knob_turn_the_device_reports_while_streaming_moves_global_by_the_same_amount_once()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(8);                       // first read after the start records the baseline
        Assert.Equal(0.8f, rig.Global, 3);

        rig.Hid.Device.BrightByte += (int)Math.Round(0.10 * 255);   // firmware moved the byte up about 10 points
        rig.Pump(8);

        Assert.Equal(0.9f, rig.Global, 2);
        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void A_detent_the_input_event_already_applied_is_not_counted_again_by_the_byte_poll()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(8);

        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);
        rig.Hid.Device.BrightByte += (int)Math.Round(0.05 * 255);
        rig.Pump(8);

        Assert.Equal(0.85f, rig.Global, 2);
    }

    [Fact]
    public void Stopping_and_restarting_the_stream_never_turns_the_level_into_global()
    {
        using var rig = new Rig(level: 30, global: 0.8f);
        rig.StartStream();
        rig.Pump(10);

        rig.Engine.Stop();
        rig.Pump(4);                       // the writer hands the firmware animation back (Apply writes the level byte)
        rig.StartStream();
        rig.Pump(14);                      // past the first knob read of the new stream

        Assert.Equal(0.8f, rig.Global, 3);
        Assert.Equal(30, rig.Level);
        Assert.InRange(rig.Hid.Device.MaxKeyByte, Expected(0.8f, 30) - 2, Expected(0.8f, 30) + 2);
    }

    [Fact]
    public void A_knob_turn_made_while_not_streaming_changes_nothing_at_the_next_stream_start()
    {
        using var rig = new Rig(level: 30, global: 0.8f);
        // Firmware mode: no software effect, so a detent is the device's business.
        Assert.True(rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true));
        Assert.Equal(0.8f, rig.Global, 3);
        Assert.Equal(30, rig.Level);

        rig.Hid.Device.BrightByte = (int)Math.Round(0.60 * 255);   // the device moved its own byte meanwhile
        rig.StartStream();
        rig.Pump(14);

        Assert.Equal(0.8f, rig.Global, 3);
    }

    [Fact]
    public void A_detent_on_an_encoder_assigned_to_something_else_is_ignored()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.Store.Update(s => s.Keeb.RotaryRight = "VolumeAdjustment");
        rig.StartStream();
        Assert.False(rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true));
        Assert.Equal(0.8f, rig.Global, 3);
    }

    private static void MoveDeviceByte(Rig rig, double points) =>
        rig.Hid.Device.BrightByte = Math.Clamp(rig.Hid.Device.BrightByte + (int)Math.Round(points / 100.0 * 255), 0, 255);

    [Fact]
    public void A_knob_turned_in_one_stream_is_not_counted_again_after_stop_and_restart()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);                                  // baseline recorded
        MoveDeviceByte(rig, +10);                     // a turn during stream 1, seen by the poll
        rig.Pump(6);
        var afterTurn = rig.Global;
        Assert.Equal(0.9f, afterTurn, 2);

        rig.Engine.Stop();
        rig.Pump(4);                                  // stop: Apply writes the level back into the device byte
        rig.StartStream();
        rig.Pump(10);

        Assert.Equal(afterTurn, rig.Global, 3);
        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void A_settings_rewrite_during_a_stream_is_not_read_as_a_knob_turn()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);                                  // baseline recorded at the level
        MoveDeviceByte(rig, +10);                     // the user turns the knob; the poll counts it
        rig.Pump(6);
        Assert.Equal(0.9f, rig.Global, 2);

        rig.Applier.Apply();                          // SetRotary, SetGameMode, a reconnect: puts the level back in the byte
        rig.Pump(8);

        Assert.Equal(0.9f, rig.Global, 2);
        Assert.Equal(50, rig.Level);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_detent_steps_global_once_whether_or_not_the_firmware_moves_its_byte(bool firmwareMovesByte)
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);

        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);
        if (firmwareMovesByte) MoveDeviceByte(rig, +5);
        rig.Pump(10);

        Assert.Equal(0.85f, rig.Global, 2);
        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void Repeated_detents_at_the_limit_do_not_ratchet_global()
    {
        using var rig = new Rig(level: 50, global: 1.0f);
        rig.StartStream();
        rig.Pump(6);

        for (var i = 0; i < 4; i++)
        {
            rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);   // the byte does not move
            rig.Pump(4);
        }
        Assert.Equal(1.0f, rig.Global, 3);
        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: false);
        rig.Pump(6);
        Assert.Equal(0.95f, rig.Global, 2);
    }

    [Fact]
    public async Task A_poll_whose_read_straddles_a_settings_rewrite_discards_its_stale_byte()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.Applier.SuppressFirmwareAnimation();
        rig.Applier.ResetKnobBaseline();
        rig.Applier.PollKnobToGlobal(true);           // baseline recorded at the level, 50
        MoveDeviceByte(rig, +10);
        rig.Applier.PollKnobToGlobal(true);           // the turn is counted: baseline 60, global 0.9
        Assert.Equal(0.9f, rig.Global, 2);

        rig.Hid.Device.HoldNextSettingsRead = true;
        var poll = Task.Run(() => rig.Applier.PollKnobToGlobal(true));
        Assert.True(rig.Hid.Device.ReadEntered.Wait(5000));
        rig.Applier.Apply();                          // rewrites the byte to the level (50) and re-baselines
        rig.Hid.Device.ReleaseRead.Set();             // the parked read returns the pre-rewrite byte, 60
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Applier.PollKnobToGlobal(true);           // records 50 as the baseline
        rig.Applier.PollKnobToGlobal(true);

        Assert.Equal(0.9f, rig.Global, 2);
    }

    [Fact]
    public void A_poll_that_reads_the_moved_byte_before_the_detent_event_counts_the_turn_once()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);

        MoveDeviceByte(rig, +5);                      // the firmware moves its byte
        rig.Pump(6);                                  // the poll reads it first: +5
        Assert.Equal(0.85f, rig.Global, 2);

        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);   // then the event arrives
        rig.Pump(6);
        Assert.Equal(0.85f, rig.Global, 2);

        // From now on events alone drive global; the byte poll stands down.
        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);
        MoveDeviceByte(rig, +5);
        rig.Pump(10);
        Assert.Equal(0.9f, rig.Global, 2);
        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void Without_detent_events_the_byte_poll_still_follows_the_knob()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);

        MoveDeviceByte(rig, +5);
        rig.Pump(6);
        MoveDeviceByte(rig, +5);
        rig.Pump(6);
        MoveDeviceByte(rig, -10);
        rig.Pump(6);

        Assert.Equal(0.8f, rig.Global, 2);
        MoveDeviceByte(rig, +10);
        rig.Pump(6);
        Assert.Equal(0.9f, rig.Global, 2);
    }

    [Fact]
    public void A_reconnect_makes_the_service_learn_again_whether_the_firmware_reports_detents()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(6);
        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);   // events seen: the poll stands down
        rig.Pump(4);
        Assert.Equal(0.85f, rig.Global, 2);
        MoveDeviceByte(rig, +10);
        rig.Pump(6);
        Assert.Equal(0.85f, rig.Global, 2);

        rig.Applier.SeedBaselines();                  // what the connection worker does on a reconnect
        rig.Applier.ResetKnobBaseline();
        rig.Pump(6);                                  // baseline recorded
        MoveDeviceByte(rig, +5);
        rig.Pump(6);

        Assert.Equal(0.9f, rig.Global, 2);
    }

    [Fact]
    public async Task Ending_a_stream_never_lets_the_knob_moved_byte_become_the_level()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(4);
        using var stop = new CancellationTokenSource();
        var syncing = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) rig.Applier.SyncFromDevice();
        });
        try
        {
            for (var i = 0; i < 40; i++)
            {
                MoveDeviceByte(rig, i % 2 == 0 ? +10 : -10);   // a knob turn made while streaming
                rig.Applier.EndStream();
                Assert.Equal(50, rig.Level);
                rig.Applier.SuppressFirmwareAnimation();
            }
        }
        finally
        {
            stop.Cancel();
            await syncing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void In_firmware_mode_the_device_byte_is_adopted_as_the_level_and_global_is_untouched()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        Assert.False(rig.Applier.IsStreaming);

        rig.Hid.Device.BrightByte = (int)Math.Round(0.70 * 255);
        Assert.True(rig.Applier.SyncFromDevice());

        Assert.Equal(70, rig.Level);
        Assert.Equal(0.8f, rig.Global, 3);
    }

    [Fact]
    public void While_streaming_the_device_byte_is_not_adopted_as_the_level()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartStream();
        rig.Pump(4);
        Assert.True(rig.Applier.IsStreaming);

        rig.Hid.Device.BrightByte = (int)Math.Round(0.90 * 255);
        Assert.False(rig.Applier.SyncFromDevice());

        Assert.Equal(50, rig.Level);
    }

    [Fact]
    public void A_key_reactions_only_stream_scales_by_the_level_and_the_slider_never_moves_global()
    {
        using var rig = new Rig(level: 100, global: 0.8f);
        rig.StartReactionsOnlyStream();
        Assert.Equal("none", rig.Engine.CurrentEffectName);
        rig.Pump(6, press: true);
        Assert.True(rig.Applier.IsStreaming);

        var provider = new Nexus.Service.Peripherals.Keeb.RealKeebProvider(rig.Store, rig.Hub, rig.Applier);
        var deviceByteBefore = rig.Hid.Device.BrightByte;
        provider.SetFirmwareLighting(new Nexus.Service.Models.Peripherals.Keeb.SetFirmwareLightingBody
        {
            AnimationMode = "Static", Speed = "Standard", Direction = "LeftToRight", Brightness = 30,
        });
        rig.Pump(12, press: true);

        Assert.Equal(30, rig.Level);
        Assert.Equal(0.8f, rig.Global, 3);
        // Streaming: the slider does not write the device byte, so the poll has nothing to import.
        Assert.Equal(deviceByteBefore, rig.Hid.Device.BrightByte);
        Assert.InRange(rig.Hid.Device.MaxKeyByte, Expected(0.8f, 30) - 3, Expected(0.8f, 30) + 3);
    }

    [Fact]
    public void A_key_reactions_only_stream_follows_the_knob_through_global_without_squaring()
    {
        using var rig = new Rig(level: 50, global: 0.8f);
        rig.StartReactionsOnlyStream();
        rig.Pump(6, press: true);

        rig.Applier.HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true);
        MoveDeviceByte(rig, +5);
        // The connection worker and the input worker both call this on their own cadence.
        Assert.False(rig.Applier.SyncFromDevice());
        rig.Pump(12, press: true);

        Assert.Equal(0.85f, rig.Global, 2);
        Assert.Equal(50, rig.Level);
        Assert.InRange(rig.Hid.Device.MaxKeyByte, Expected(0.85f, 50) - 3, Expected(0.85f, 50) + 3);
    }
}
