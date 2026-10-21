using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Lighting.LedWindow;

public class AheadFastWindowTests
{
    private static (byte R, byte G, byte B) Paint(int x, int y, double tick, int salt) =>
        ((byte)((x * 3 + (int)tick + salt) & 255), (byte)((y * 5 + (int)(tick / 7) + salt * 2) & 255), (byte)(((x ^ y) + (int)tick) & 255));

    // Stands for a shader whose window is proven exact: the canvas render and the LED window paint the same function of pixel and tick.
    private static ShaderEffect Shader(GpuContext gpu, string name, int salt, int windowError = 0)
    {
        var fx = new ShaderEffect(name, gpu, "unused")
        {
            TestRender = (canvas, tick) =>
            {
                for (var y = 0; y < canvas.Height; y++)
                {
                    for (var x = 0; x < canvas.Width; x++)
                    {
                        var (r, g, b) = Paint(x, y, tick, salt);
                        canvas.SetPixel(x, y, r, g, b);
                    }
                }
                return true;
            },
        };
        fx.TestLedWindow = (xs, ys, ticks, frames, rgba) =>
        {
            for (var f = 0; f < frames; f++)
            {
                for (var t = 0; t < xs.Length; t++)
                {
                    var (r, g, b) = Paint(xs[t], ys[t], ticks[f], salt);
                    var o = (f * xs.Length + t) * 4;
                    rgba[o] = (byte)(r + windowError);
                    rgba[o + 1] = g;
                    rgba[o + 2] = b;
                    rgba[o + 3] = 255;
                }
            }
            return true;
        };
        return fx;
    }

    private sealed class ClockPaint : IClockOnlyEffect
    {
        public string Name => "clockpaint";

        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
            {
                for (var x = 0; x < canvas.Width; x++)
                {
                    var (r, g, b) = Paint(x, y, tickMs, 1);
                    canvas.SetPixel(x, y, r, g, b);
                }
            }
        }

        public void Dispose() { }
    }

    private static DeviceFrame[] Devices()
    {
        var uv = new DeviceFrame(0, "d-0", 12, x: 60, y: 90, w: 300, h: 80, rotation: 0);
        var u = new float[12];
        var v = new float[12];
        for (var i = 0; i < 12; i++)
        {
            u[i] = (i % 6 + 0.5f) / 6f;
            v[i] = (i / 6 + 0.5f) / 2f;
        }
        uv.LedU = u;
        uv.LedV = v;
        return new[]
        {
            uv,
            new DeviceFrame(1, "d-1", 30, x: 400, y: 300, w: 380, h: 20, rotation: 37),
            new DeviceFrame(2, "d-2", 8, x: 700, y: 100, w: 30, h: 200, rotation: 90),
        };
    }

    private sealed class Rig : IDisposable
    {
        public readonly DevicePinTracker Pins = new();
        public readonly LightingEngine Engine;
        private readonly GpuContext _gpu = new(160, 90);

        public Rig(bool fast, int windowError = 0, bool shaderGlobal = true, bool pin = false)
        {
            Engine = new LightingEngine
            {
                Pins = Pins,
                FrameIntervalMs = 10,
                PinLookTag = _ => "t",
                PinEffectFactory = (key, _) => Shader(_gpu, key, 5),
                StaticPinEffectFactory = _ => new FlatColorEffect(0, 0, 255),
                AheadFastForTest = fast,
            };
            Engine.UpdateDevices(Devices());
            Engine.SetEffect(shaderGlobal ? Shader(_gpu, "global", 1, windowError) : new ClockPaint());
            if (!shaderGlobal)
            {
                Pins.Set("d-2", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "pinfx" });
            }
            if (pin)
            {
                Pins.Set("d-0", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "pinfx" });
            }
        }

        public async Task<byte[][][]> Windows(int count, long baseMs)
        {
            var ids = new[] { "d-0", "d-1", "d-2" };
            var end = Environment.TickCount64 + 5000;
            while (!Engine.CanRenderAhead(ids) && Environment.TickCount64 < end)
            {
                await Task.Delay(10);
            }
            Assert.True(Engine.CanRenderAhead(ids));
            var result = new byte[count][][];
            for (var w = 0; w < count; w++)
            {
                var ticks = Enumerable.Range(0, 30).Select(i => baseMs + w * 5000 + i * 33L + (i % 3)).ToArray();
                var req = Engine.RequestAhead(ids, ticks);
                Assert.True(req.Done.Wait(TimeSpan.FromSeconds(10)));
                Assert.NotNull(req.Frames);
                Assert.Equal(30, req.Frames!.Length);
                result[w] = req.Frames.Select(f => f.SelectMany(d => d.LedBytes.ToArray().Concat(new[] { (byte)d.Index })).ToArray()).ToArray();
            }
            return result;
        }

        public void Dispose()
        {
            Engine.Dispose();
            _gpu.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_from_the_led_path_equal_the_canvas_path(bool pin)
    {
        using var fast = new Rig(fast: true, pin: pin);
        using var slow = new Rig(fast: false, pin: pin);
        var a = await fast.Windows(4, 1_000_000);
        var b = await slow.Windows(4, 1_000_000);
        for (var w = 0; w < a.Length; w++)
        {
            for (var f = 0; f < a[w].Length; f++)
            {
                Assert.Equal(b[w][f], a[w][f]);
            }
        }
        Assert.Equal(3, fast.Engine.AheadFastWindows);
        Assert.Equal(1, fast.Engine.AheadSpotChecks);
        Assert.Equal(0, fast.Engine.AheadSpotMismatches);
        Assert.Equal(0, slow.Engine.AheadFastWindows);
    }

    [Fact]
    public async Task A_window_that_disagrees_is_caught_by_the_first_spot_check_and_still_delivers_canvas_bytes()
    {
        using var fast = new Rig(fast: true, windowError: 1);
        using var slow = new Rig(fast: false);
        var a = await fast.Windows(1, 2_000_000);
        var b = await slow.Windows(1, 2_000_000);
        for (var f = 0; f < a[0].Length; f++)
        {
            Assert.Equal(b[0][f], a[0][f]);
        }
        Assert.Equal(1, fast.Engine.AheadSpotMismatches);
        Assert.Equal(0, fast.Engine.AheadFastWindows);
    }

    [Fact]
    public async Task A_source_that_is_not_a_shader_takes_the_canvas_path()
    {
        using var rig = new Rig(fast: true, shaderGlobal: false);
        var w = await rig.Windows(2, 3_000_000);
        Assert.Equal(30, w[0].Length);
        Assert.Equal(0, rig.Engine.AheadFastWindows);
        Assert.Equal(0, rig.Engine.AheadSpotChecks);
    }

    [Fact]
    public async Task Full_frame_sampling_takes_the_canvas_path()
    {
        using var rig = new Rig(fast: true);
        rig.Engine.FullFrameSampling = true;
        await rig.Windows(2, 4_000_000);
        Assert.Equal(0, rig.Engine.AheadFastWindows);
    }
}
