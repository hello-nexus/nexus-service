using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

// Coordinates derive from the engine's canvas size (LightingEngine's
// CanvasBuffer ctor) and the logical frame space (CW/CH in
// SampleDevicesFromCanvas): at 160x90 over 1000x600, the RAM-stick frame
// below (300,100 40x250 rot 90) maps to canvas rect x=48 y=15 w=6.4 h=37.5
// with 10 LEDs pitched 3.75 rows apart down the x~51 centerline.
public class LightingEngineSamplingTests
{
    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(DeviceFrame device, Action<CanvasBuffer> paint, bool footprintSampling = true)
    {
        using var engine = new LightingEngine();
        engine.FootprintSamplingEnabled = footprintSampling;
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(paint));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    private static (byte r, byte g, byte b) Led(byte[] leds, int i) => (leds[i * 3], leds[i * 3 + 1], leds[i * 3 + 2]);

    private static DeviceFrame MakeRam() => new(0, "ram", 10, x: 300, y: 100, w: 40, h: 250, rotation: 90);

    [Fact]
    public async Task LinearStrip_OffCenterContent_LightsAllLeds()
    {
        // Column at canvas x=49: inside the frame, off the x=51 centerline the
        // old point sampler read exclusively.
        var leds = await RenderOnce(MakeRam(), c =>
        {
            c.Clear();
            for (int y = 16; y <= 51; y++) { c.SetPixel(49, y, 255, 0, 180); }
        });
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(((byte)255, (byte)0, (byte)180), Led(leds, i));
        }
    }

    [Fact]
    public async Task LinearStrip_ThinBarBetweenSamplePoints_LightsAdjacentLeds()
    {
        // Rows 37-38 fall between the point-sample rows 35 and 40, so the old
        // sampler saw nothing; the bar lands in LED 5's and LED 6's cells.
        var leds = await RenderOnce(MakeRam(), c =>
        {
            c.Clear();
            for (int x = 0; x < 160; x++) { c.SetPixel(x, 37, 0, 255, 80); c.SetPixel(x, 38, 0, 255, 80); }
        });
        for (int i = 0; i < 10; i++)
        {
            var expected = i is 5 or 6 ? ((byte)0, (byte)255, (byte)80) : ((byte)0, (byte)0, (byte)0);
            Assert.Equal(expected, Led(leds, i));
        }
    }

    [Fact]
    public async Task Sampling_PrioritizesBrightPixelOverDimFloor()
    {
        var leds = await RenderOnce(MakeRam(), c =>
        {
            c.Fill(30, 30, 30);
            c.SetPixel(50, 31, 255, 0, 0);
        });
        // LED 4's cell contains the red pixel: luminance-squared weighting makes
        // it dominate the dim floor instead of averaging away.
        var (r, g, b) = Led(leds, 4);
        Assert.True(r >= 150, $"red channel dominates (r={r})");
        Assert.True(g <= 30 && b <= 30, $"floor stays minor (g={g} b={b})");
        Assert.Equal(((byte)30, (byte)30, (byte)30), Led(leds, 0));
    }

    [Fact]
    public async Task FootprintSamplingOff_RevertsToPointSampling()
    {
        // Same off-center column that lights 10/10 with footprint sampling:
        // point sampling reads only the x=51 centerline pixels and misses it.
        var leds = await RenderOnce(MakeRam(), c =>
        {
            c.Clear();
            for (int y = 16; y <= 51; y++) { c.SetPixel(49, y, 255, 0, 180); }
        }, footprintSampling: false);
        Assert.All(leds, v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task AllDarkCanvas_AllLedsBlack()
    {
        var leds = await RenderOnce(MakeRam(), c => c.Clear());
        Assert.All(leds, v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task DisabledLed_StaysBlack_WhenItsCellIsLit()
    {
        var device = MakeRam();
        device.LedDisabled = new bool[10];
        device.LedDisabled[3] = true;
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 16; y <= 51; y++) { c.SetPixel(49, y, 255, 0, 180); }
        });
        Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, 3));
        Assert.Equal(((byte)255, (byte)0, (byte)180), Led(leds, 2));
    }

    [Fact]
    public async Task UvLayout_SamplesCellAroundEachLed()
    {
        // 6x4 UV grid on a frame mapping to canvas rect x=16 y=45 w=48 h=18.
        var device = new DeviceFrame(0, "kbd", 24, x: 100, y: 300, w: 300, h: 120);
        var u = new float[24];
        var v = new float[24];
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                u[row * 6 + col] = (col + 0.5f) / 6f;
                v[row * 6 + col] = (row + 0.5f) / 4f;
            }
        }
        device.LedU = u;
        device.LedV = v;

        // Pixel at (37,52): 1px off LED 8's exact point (36,51), inside its
        // cell and no other LED's.
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            c.SetPixel(37, 52, 0, 255, 0);
        });
        Assert.Equal(((byte)0, (byte)255, (byte)0), Led(leds, 8));
        for (int i = 0; i < 24; i++)
        {
            if (i == 8) continue;
            Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, i));
        }
    }

    [Fact]
    public async Task SingleLed_IntegratesWholeFrame()
    {
        // 1-LED frame maps to canvas rect x=96 y=30 w=16 h=15; a single lit
        // pixel near the corner is far from the centre point the old sampler
        // read, and full-coverage cell reads must still catch it.
        var device = new DeviceFrame(0, "one", 1, x: 600, y: 200, w: 100, h: 100);
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            c.SetPixel(97, 31, 255, 128, 0);
        });
        Assert.Equal(((byte)255, (byte)128, (byte)0), Led(leds, 0));
    }
}
