using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// A shader that has to compile must not freeze, blank or poll-starve the output.
/// The GL render is replaced by ShaderEffect.TestRender, which reports when the
/// program lands; no GL context is involved.
/// </summary>
public class AsyncCompileSwitchTests
{
    private static GpuContext Context()
    {
        var gpu = new GpuContext(16, 9, null, () => { });
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(5)));
        gpu.SetParallelCompileForTest(true);
        return gpu;
    }

    /// <summary>A shader whose program lands after a number of frames, or, with a gate, when the gate opens.</summary>
    private static ShaderEffect Shader(GpuContext gpu, string name, byte r, byte g, byte b, int landsAfterCalls, Action? onCall = null,
        ManualResetEventSlim? gate = null)
    {
        var calls = 0;
        return new ShaderEffect(name, gpu, "")
        {
            TestRender = (canvas, _) =>
            {
                calls++;
                onCall?.Invoke();
                if (gate is not null ? !gate.IsSet : calls <= landsAfterCalls)
                {
                    return false;
                }
                for (var y = 0; y < canvas.Height; y++)
                {
                    for (var x = 0; x < canvas.Width; x++)
                    {
                        canvas.SetPixel(x, y, r, g, b);
                    }
                }
                return true;
            },
        };
    }

    [Fact]
    public void The_outgoing_effect_keeps_drawing_until_the_new_program_lands_then_is_disposed_once()
    {
        using var gpu = Context();
        var old = Shader(gpu, "old", 255, 0, 0, 0);
        var incoming = Shader(gpu, "new", 0, 255, 0, 3);
        var canvas = new CanvasBuffer(16, 9);
        old.RenderFrame(canvas, 0);

        Assert.True(incoming.AdoptPredecessor(old));

        // The first frames of a fresh effect used to run the generation reset and drop the predecessor.
        for (var i = 0; i < 3; i++)
        {
            incoming.RenderFrame(canvas, i);
            Assert.True(incoming.IsRenderPending);
            Assert.True(incoming.HasPredecessor);
            Assert.Equal((255, 0, 0), canvas.GetPixel(0, 0));
            Assert.Equal(0, old.DisposeCount);
        }

        incoming.RenderFrame(canvas, 3);
        Assert.False(incoming.IsRenderPending);
        Assert.Equal((0, 255, 0), canvas.GetPixel(0, 0));

        incoming.RenderFrame(canvas, 4);
        incoming.Dispose();
        Assert.Equal(1, old.DisposeCount);
        Assert.False(incoming.HasPredecessor);
    }

    [Fact]
    public void A_disposed_effect_refuses_a_predecessor_and_leaves_it_to_the_caller()
    {
        using var gpu = Context();
        var old = Shader(gpu, "old", 255, 0, 0, 0);
        var incoming = Shader(gpu, "new", 0, 255, 0, 3);
        old.RenderFrame(new CanvasBuffer(16, 9), 0);
        incoming.Dispose();

        Assert.False(incoming.AdoptPredecessor(old));

        Assert.False(incoming.HasPredecessor);
        Assert.Equal(0, old.DisposeCount);
    }

    [Fact]
    public void A_switch_during_a_switch_keeps_the_last_effect_that_had_a_program()
    {
        using var gpu = Context();
        var first = Shader(gpu, "a", 255, 0, 0, 0);
        var second = Shader(gpu, "b", 0, 255, 0, 100);
        var third = Shader(gpu, "c", 0, 0, 255, 100);
        var canvas = new CanvasBuffer(16, 9);
        first.RenderFrame(canvas, 0);

        Assert.True(second.AdoptPredecessor(first));
        Assert.True(third.AdoptPredecessor(second));
        third.RenderFrame(canvas, 0);

        Assert.Equal((255, 0, 0), canvas.GetPixel(0, 0));
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(0, first.DisposeCount);
    }

    private static async Task<bool> Eventually(Func<bool> cond)
    {
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (cond())
            {
                return true;
            }
            await Task.Delay(5);
        }
        return cond();
    }

    private static async Task Frames(LightingEngine engine, int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        void Handler()
        {
            if (Interlocked.Increment(ref seen) >= count)
            {
                done.TrySetResult();
            }
        }
        engine.FramePublished += Handler;
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            engine.FramePublished -= Handler;
        }
    }

    [Fact]
    public async Task A_frozen_shader_that_is_still_compiling_is_polled_until_it_renders()
    {
        using var gpu = Context();
        var calls = 0;
        using var gate = new ManualResetEventSlim(false);
        var shader = Shader(gpu, "static", 0, 255, 0, 0, () => Interlocked.Increment(ref calls), gate);
        using var engine = new LightingEngine { FrameIntervalMs = 10 };
        var dev = new DeviceFrame(0, "dev-a", 4, x: 100, y: 100, w: 100, h: 100);
        engine.UpdateDevices(new[] { dev });

        engine.SetEffect(shader);
        engine.SetFrozen(true);

        // Polled on every tick while it compiles, not asked once.
        Assert.True(await Eventually(() => Volatile.Read(ref calls) >= 5));
        gate.Set();
        Assert.True(await Eventually(() => !shader.IsRenderPending));
        await Frames(engine, 5);
        var settled = Volatile.Read(ref calls);
        await Frames(engine, 5);
        // Rendered once it landed: a frozen effect then stops rendering.
        Assert.Equal(settled, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_pin_still_compiling_leaves_its_device_on_the_global_canvas()
    {
        using var gpu = Context();
        var pins = new DevicePinTracker();
        pins.Set("dev-a", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "pinned" });
        using var gate = new ManualResetEventSlim(false);
        var pinShader = Shader(gpu, "pinned", 255, 0, 0, 0, null, gate);
        using var engine = new LightingEngine
        {
            Pins = pins,
            PinEffectFactory = (_, _) => pinShader,
            PinLookTag = _ => "t",
            FrameIntervalMs = 10,
        };
        var dev = new DeviceFrame(0, "dev-a", 5, x: 300, y: 100, w: 40, h: 250, rotation: 90);
        engine.UpdateDevices(new[] { dev });
        engine.SetEffect(new SolidEffect(0, 255, 0));

        // The pin cannot land until the gate opens, so the device can only show the global green meanwhile.
        Assert.True(await Eventually(() => dev.LedBytes.ToArray() is { Length: > 1 } l && l[1] == 255));
        await Frames(engine, 5);
        var during = dev.LedBytes.ToArray();
        Assert.Equal(255, during[1]);
        Assert.Equal(0, during[0]);

        gate.Set();
        Assert.True(await Eventually(() => dev.LedBytes.ToArray()[0] == 255));
    }

    [Fact]
    public async Task Switching_shaders_while_the_previous_one_lands_never_blanks_or_double_disposes()
    {
        for (var round = 0; round < 10; round++)
        {
            using var gpu = Context();
            using var gateB = new ManualResetEventSlim(false);
            using var gateC = new ManualResetEventSlim(false);
            var aDraws = 0;
            var a = Shader(gpu, "a", 255, 0, 0, 0, () => Interlocked.Increment(ref aDraws));
            var b = Shader(gpu, "b", 0, 255, 0, 0, null, gateB);
            var c = Shader(gpu, "c", 0, 0, 255, 0, null, gateC);
            using var engine = new LightingEngine { FrameIntervalMs = 5 };
            var dev = new DeviceFrame(0, "dev-a", 4, x: 100, y: 100, w: 100, h: 100);
            engine.UpdateDevices(new[] { dev });
            var blank = 0;
            engine.FramePublished += () =>
            {
                var l = dev.LedBytes.ToArray();
                if (l.Length > 2 && l[0] == 0 && l[1] == 0 && l[2] == 0 && Volatile.Read(ref aDraws) > 0)
                {
                    Interlocked.Increment(ref blank);
                }
            };

            engine.SetEffect(a);
            Assert.True(await Eventually(() => dev.LedBytes.ToArray()[0] == 255));

            engine.SetEffect(b);
            var seen = Volatile.Read(ref aDraws);
            Assert.True(await Eventually(() => Volatile.Read(ref aDraws) > seen + 3));

            // B lands while C is switched in from another thread.
            var race = Task.Run(() => engine.SetEffect(c));
            gateB.Set();
            await race;
            Assert.True(await Eventually(() => dev.LedBytes.ToArray()[0] != 0 || dev.LedBytes.ToArray()[1] != 0));

            gateC.Set();
            Assert.True(await Eventually(() => dev.LedBytes.ToArray()[2] == 255));
            Assert.True(await Eventually(() => a.DisposeCount == 1 && b.DisposeCount == 1));
            await Frames(engine, 3);

            Assert.Equal(1, a.DisposeCount);
            Assert.Equal(1, b.DisposeCount);
            Assert.Equal(0, c.DisposeCount);
            Assert.Equal(0, Volatile.Read(ref blank));
        }
    }

    private sealed class SolidEffect : IEffect
    {
        private readonly byte _r, _g, _b;
        public SolidEffect(byte r, byte g, byte b) { _r = r; _g = g; _b = b; }
        public string Name => "solid";
        public void RenderFrame(CanvasBuffer canvas, double tickMs)
        {
            for (var y = 0; y < canvas.Height; y++)
            {
                for (var x = 0; x < canvas.Width; x++)
                {
                    canvas.SetPixel(x, y, _r, _g, _b);
                }
            }
        }
        public void Dispose() { }
    }
}
