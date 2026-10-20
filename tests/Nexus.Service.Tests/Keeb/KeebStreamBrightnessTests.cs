using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
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
            var frame = new DeviceFrame(0, Hub.DeviceId + KeebLightingDeviceProvider.KeysSuffix, Hub.KeyMap.LedCount)
            {
                LedU = Enumerable.Repeat(0.5f, Hub.KeyMap.LedCount).ToArray(),
                LedV = Enumerable.Repeat(0.5f, Hub.KeyMap.LedCount).ToArray(),
            };
            Engine.UpdateDevices(new[] { frame });
            Writer = new KeebLightingFrameWriter(Engine, Hub, Store, new Np50IdentifyTracker(), Applier, new Nexus.Service.Lighting.KeyReactive.KeyReactiveOverlay());
        }

        public float Global => Store.Load().Lighting.GlobalBrightness;
        public int Level => Store.Load().Keeb.FirmwareLighting.Brightness;

        public void StartStream() => Engine.SetEffect(new White());

        /// <summary>Ticks until the writer has streamed a frame from the live engine canvas.</summary>
        public void Pump(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Hid.Device.ResetStream();
                Writer.Tick();
                Thread.Sleep(15);
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
}
