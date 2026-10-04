using System.Numerics;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Tests.Lighting.Scene;

// Canvas is the engine's 160x90 over the 1000x600 logical space; the paint is a
// left-to-right red ramp, so an LED's red channel reads back where it sampled.
public class SceneSamplingTests
{
    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static void Ramp(CanvasBuffer c)
    {
        for (var x = 0; x < c.Width; x++)
        {
            for (var y = 0; y < c.Height; y++)
            {
                c.SetPixel(x, y, (byte)(x * 255 / (c.Width - 1)), (byte)(y * 255 / (c.Height - 1)), 0);
            }
        }
    }

    private static async Task<byte[]> RenderOnce(DeviceFrame device, SceneProjection? scene, bool footprint = false, bool fullFrame = false)
    {
        using var engine = new LightingEngine();
        engine.FootprintSamplingEnabled = footprint;
        engine.FullFrameSampling = fullFrame;
        engine.UpdateDevices(new[] { device });
        engine.SetScene(scene);
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(Ramp));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    private static byte Red(byte[] leds, int i) => leds[i * 3];
    private static byte Green(byte[] leds, int i) => leds[i * 3 + 1];

    // Looking straight down -Z at the origin from a metre away.
    private static SceneCameraBasis FrontCamera() =>
        new(new Vector3(0, 0, 1000), Vector3.Zero, 40, 1000, 600);

    // A 400 mm wide strip facing the camera, u running to +X.
    private static SceneQuad Strip(float centerX = 0) =>
        new(new Vector3(centerX, 0, 0), new Vector3(400, 0, 0), new Vector3(0, -40, 0));

    private static SceneProjection Placed(string id, params SceneQuad[] quads) =>
        new(FrontCamera(), new Dictionary<string, SceneQuad[]> { [id] = quads });

    [Fact]
    public async Task Linear_device_reads_the_canvas_where_the_camera_sees_each_led()
    {
        var cam = FrontCamera();
        var quad = Strip();
        var leds = await RenderOnce(new DeviceFrame(0, "strip", 10, x: 0, y: 0, w: 50, h: 10), Placed("strip", quad));

        for (var i = 0; i < 10; i++)
        {
            Assert.True(cam.Project(quad.At(i / 9f, 0.5f), out var cx, out var cy));
            var px = (int)(cx * 160 / 1000f);
            var py = (int)(cy * 90 / 600f);
            Assert.Equal((byte)(px * 255 / 159), Red(leds, i));
            Assert.Equal((byte)(py * 255 / 89), Green(leds, i));
        }
        Assert.True(Red(leds, 0) < Red(leds, 9));
    }

    [Fact]
    public async Task Unplaced_device_keeps_sampling_its_2d_frame()
    {
        // Frame on the far left of the canvas; the scene places only another device.
        var frame = new DeviceFrame(0, "ram", 4, x: 0, y: 280, w: 40, h: 40);
        var leds = await RenderOnce(frame, Placed("other", Strip()));
        var baseline = await RenderOnce(new DeviceFrame(0, "ram", 4, x: 0, y: 280, w: 40, h: 40), null);
        Assert.Equal(baseline, leds);
    }

    [Fact]
    public async Task Half_turn_binding_reverses_the_led_order_across_the_view()
    {
        var obj = new SceneObject { Id = "desk", Position = [0, 0, 0] };
        var anchor = new SceneAnchor { Id = "a", Center = [0, 0, 0], Right = [1, 0, 0], Up = [0, 1, 0], Width = 400, Height = 40 };
        var upright = await RenderOnce(new DeviceFrame(0, "s", 6), Placed("s", SceneMath.AnchorQuad(obj, anchor, 0, false)));
        var turned = await RenderOnce(new DeviceFrame(0, "s", 6), Placed("s", SceneMath.AnchorQuad(obj, anchor, 180, false)));
        var flipped = await RenderOnce(new DeviceFrame(0, "s", 6), Placed("s", SceneMath.AnchorQuad(obj, anchor, 0, true)));

        Assert.True(Red(upright, 0) < Red(upright, 5));
        Assert.True(Red(turned, 0) > Red(turned, 5));
        Assert.True(Red(flipped, 0) > Red(flipped, 5));
    }

    [Fact]
    public async Task Several_targets_split_the_leds_into_runs_per_surface()
    {
        // 8 LEDs over two strips well apart: the first run lands on the left one.
        var leds = await RenderOnce(new DeviceFrame(0, "chain", 8), Placed("chain", Strip(-250), Strip(250)));
        var leftMax = Enumerable.Range(0, 4).Max(i => Red(leds, i));
        var rightMin = Enumerable.Range(4, 4).Min(i => Red(leds, i));
        Assert.True(leftMax < rightMin);
    }

    [Fact]
    public async Task Map_uvs_of_a_split_run_are_stretched_over_its_own_surface()
    {
        // Two rings drawn side by side in one 2D map: each half covers u 0..0.5 or 0.5..1.
        var frame = new DeviceFrame(0, "pair", 4)
        {
            LedU = [0f, 0.5f, 0.5f, 1f],
            LedV = [0.5f, 0.5f, 0.5f, 0.5f],
        };
        var cam = FrontCamera();
        var left = Strip(-250);
        var right = Strip(250);
        var leds = await RenderOnce(frame, Placed("pair", left, right));

        Assert.True(cam.Project(left.At(0, 0.5f), out var l0, out _));
        Assert.True(cam.Project(right.At(1, 0.5f), out var r1, out _));
        Assert.Equal((byte)((int)(l0 * 160 / 1000f) * 255 / 159), Red(leds, 0));
        Assert.Equal((byte)((int)(r1 * 160 / 1000f) * 255 / 159), Red(leds, 3));
    }

    [Fact]
    public async Task Leds_behind_the_camera_go_dark()
    {
        var behind = new SceneQuad(new Vector3(0, 0, 1500), new Vector3(400, 0, 0), new Vector3(0, -40, 0));
        var leds = await RenderOnce(new DeviceFrame(0, "s", 3), Placed("s", behind));
        Assert.All(leds, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Placed_devices_follow_the_scene_even_under_full_frame_sampling()
    {
        // Sweep effects turn full-frame sampling on; a placed device still reads where the camera sees it.
        var cam = FrontCamera();
        var quad = Strip(300);
        var placed = await RenderOnce(new DeviceFrame(0, "s", 5, x: 100, y: 100, w: 200, h: 40), Placed("s", quad), fullFrame: true);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(cam.Project(quad.At(i / 4f, 0.5f), out var cx, out _));
            Assert.Equal((byte)((int)(cx * 160 / 1000f) * 255 / 159), Red(placed, i));
        }

        // An unplaced device keeps full-frame sampling.
        var unplaced = await RenderOnce(new DeviceFrame(0, "u", 5, x: 100, y: 100, w: 200, h: 40), Placed("s", quad), fullFrame: true);
        var baseline = await RenderOnce(new DeviceFrame(0, "u", 5, x: 100, y: 100, w: 200, h: 40), null, fullFrame: true);
        Assert.Equal(baseline, unplaced);
    }

    [Fact]
    public async Task Footprint_sampling_reads_a_cell_around_each_projected_led()
    {
        var leds = await RenderOnce(new DeviceFrame(0, "s", 10), Placed("s", Strip()), footprint: true);
        for (var i = 1; i < 10; i++)
        {
            Assert.True(Red(leds, i) >= Red(leds, i - 1));
        }
        Assert.True(Red(leds, 0) < Red(leds, 9));
    }
}
