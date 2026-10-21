using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class AreaSamplingTests
{
    private static float[] Boxes(float[]? u, float[]? v, bool[]? off, int count) => AreaBoxes.Compute(u, v, off, count, 0, count);

    private static void AssertBox(float[] boxes, int led, float u0, float v0, float u1, float v1)
    {
        Assert.Equal(u0, boxes[led * 4], 4);
        Assert.Equal(v0, boxes[led * 4 + 1], 4);
        Assert.Equal(u1, boxes[led * 4 + 2], 4);
        Assert.Equal(v1, boxes[led * 4 + 3], 4);
    }

    [Fact]
    public void TwoEdgeRows_BoxesMeetAtTheVerticalMidpoint()
    {
        // top row at v 0.1, bottom row at v 0.9, three LEDs each at u 0.2 / 0.5 / 0.8
        var u = new[] { 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.8f };
        var v = new[] { 0.1f, 0.1f, 0.1f, 0.9f, 0.9f, 0.9f };
        var b = Boxes(u, v, null, 6);
        AssertBox(b, 1, 0.35f, 0f, 0.65f, 0.5f);
        AssertBox(b, 4, 0.35f, 0.5f, 0.65f, 1f);
        AssertBox(b, 0, 0f, 0f, 0.35f, 0.5f);
        AssertBox(b, 5, 0.65f, 0.5f, 1f, 1f);
    }

    [Fact]
    public void LinearStripWithoutUv_IsFullHeightSlicesMeetingHalfwayBetweenLeds()
    {
        var b = Boxes(null, null, null, 5);
        AssertBox(b, 0, 0f, 0f, 0.125f, 1f);
        AssertBox(b, 2, 0.375f, 0f, 0.625f, 1f);
        AssertBox(b, 4, 0.875f, 0f, 1f, 1f);
    }

    [Fact]
    public void RegularGrid_InteriorBoxIsOneCellWide()
    {
        var u = new List<float>();
        var v = new List<float>();
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                u.Add(c * 0.4f + 0.1f);
                v.Add(r * 0.4f + 0.1f);
            }
        }
        var b = Boxes(u.ToArray(), v.ToArray(), null, 9);
        AssertBox(b, 4, 0.3f, 0.3f, 0.7f, 0.7f);
    }

    [Fact]
    public void DisabledLed_GetsNoBoxAndIsNotANeighbor()
    {
        var off = new[] { false, true, false };
        var b = Boxes(null, null, off, 3);
        AssertBox(b, 1, 0, 0, 0, 0);
        AssertBox(b, 0, 0f, 0f, 0.5f, 1f);
        AssertBox(b, 2, 0.5f, 0f, 1f, 1f);
    }

    [Fact]
    public void SingleLed_IsTheWholeFrame()
    {
        AssertBox(Boxes(null, null, null, 1), 0, 0f, 0f, 1f, 1f);
        AssertBox(Boxes(new[] { 0.3f }, new[] { 0.7f }, null, 1), 0, 0f, 0f, 1f, 1f);
    }

    [Fact]
    public void LinearLightMean_OfHalfBlackHalfWhite_IsAbout188()
    {
        var canvas = new CanvasBuffer(4, 2);
        canvas.Clear();
        for (var y = 0; y < 2; y++)
        {
            for (var x = 2; x < 4; x++)
            {
                canvas.SetPixel(x, y, 255, 255, 255);
            }
        }
        var sat = new AreaSat();
        sat.Build(canvas);
        var (r, g, b) = sat.Mean(0, 0, 3, 1);
        Assert.InRange(r, 187, 189);
        Assert.Equal(r, g);
        Assert.Equal(r, b);
        Assert.Equal(((byte)255, (byte)255, (byte)255), sat.Mean(2, 0, 3, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0), sat.Mean(0, 0, 1, 1));
    }

    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(DeviceFrame device, Action<CanvasBuffer> paint, bool area)
    {
        using var engine = new LightingEngine();
        if (area)
        {
            engine.SetAreaSampling(new[] { device.Id });
        }
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(paint));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    // Two edge rows of three LEDs: top at v 0.05, bottom at v 0.95, on a frame covering the canvas middle.
    private static DeviceFrame EdgeKeyboard() => new(0, "kb", 6, x: 200, y: 150, w: 600, h: 300)
    {
        LedU = new[] { 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.8f },
        LedV = new[] { 0.05f, 0.05f, 0.05f, 0.95f, 0.95f, 0.95f },
    };

    // A bright bar across the frame's vertical middle, between the two LED rows.
    private static void MiddleBar(CanvasBuffer c)
    {
        c.Clear();
        // frame spans canvas x 32..128, y 22..67; middle rows
        for (var y = 41; y <= 48; y++)
        {
            for (var x = 0; x < c.Width; x++)
            {
                c.SetPixel(x, y, 255, 255, 255);
            }
        }
    }

    [Fact]
    public async Task BarBetweenTheRows_IsInvisibleWithoutAreaSamplingAndVisibleWith()
    {
        var off = await RenderOnce(EdgeKeyboard(), MiddleBar, area: false);
        var on = await RenderOnce(EdgeKeyboard(), MiddleBar, area: true);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(0, off[i * 3]);
            Assert.True(on[i * 3] > 0, $"led {i} stayed dark with area sampling");
        }
    }

    [Fact]
    public async Task AreaSamplingOff_MatchesTheSpotOutputOfAnUntouchedEngine()
    {
        static void Scene(CanvasBuffer c)
        {
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = 0; x < c.Width; x++)
                {
                    c.SetPixel(x, y, (byte)(x * 255 / (c.Width - 1)), (byte)(y * 255 / (c.Height - 1)), 40);
                }
            }
        }
        var plain = await RenderOnce(EdgeKeyboard(), Scene, area: false);
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "someone-else" });
        var dev = EdgeKeyboard();
        engine.UpdateDevices(new[] { dev });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(dev.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(Scene));
        await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Equal(plain, await tcs.Task);
    }

    [Fact]
    public void Card_CarriesAreaSamplingAndBoxesFromTheEngineFrame()
    {
        using var engine = new LightingEngine();
        var frame = new DeviceFrame(0, "card", 3, 0, 0, 100, 20) { LedDisabled = new[] { false, true, false } };
        engine.UpdateDevices(new[] { frame });
        var cards = new List<LightingDevice> { new() { Id = "card", LedCount = 3 }, new() { Id = "plain", LedCount = 3 } };
        var settings = new NexusSettings();
        settings.Devices.AreaSamplingDevices.Add("card");

        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);

        Assert.True(cards[0].AreaSampling);
        Assert.Equal(AreaBoxes.Compute(null, null, frame.LedDisabled, 3, 0, 3), cards[0].AreaBoxes);
        Assert.False(cards[1].AreaSampling);
        Assert.Null(cards[1].AreaBoxes);
    }

    [Fact]
    public void Card_BoxesKeepFourPerLed_UnderAShorterPreviewCountAndAMismatchedFrame()
    {
        using var engine = new LightingEngine();
        var frame = new DeviceFrame(0, "card", 6, 0, 0, 100, 20) { PreviewLedCount = 4 };
        engine.UpdateDevices(new[] { frame });
        var settings = new NexusSettings();
        settings.Devices.AreaSamplingDevices.AddRange(new[] { "card", "short", "noframe" });
        engine.UpdateDevices(new[] { frame, new DeviceFrame(1, "short", 8, 0, 0, 100, 20) });
        var cards = new List<LightingDevice>
        {
            new() { Id = "card", LedCount = 6 }, new() { Id = "short", LedCount = 5 }, new() { Id = "noframe", LedCount = 2 },
        };

        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);

        Assert.Equal(24, cards[0].AreaBoxes!.Length);
        Assert.Equal(new float[] { 0, 0, 0, 0, 0, 0, 0, 0 }, cards[0].AreaBoxes![16..]);
        Assert.NotEqual(0f, cards[0].AreaBoxes![2]);
        Assert.Equal(20, cards[1].AreaBoxes!.Length);
        Assert.Equal(8, cards[2].AreaBoxes!.Length);
        Assert.All(cards[2].AreaBoxes!, x => Assert.Equal(0f, x));
    }

    [Fact]
    public async Task TurnedOnThenOff_MatchesAnUntouchedEngine()
    {
        static void Scene(CanvasBuffer c)
        {
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = 0; x < c.Width; x++)
                {
                    c.SetPixel(x, y, (byte)(x * 255 / (c.Width - 1)), (byte)(y * 255 / (c.Height - 1)), 40);
                }
            }
        }
        var untouched = await RenderOnce(EdgeKeyboard(), Scene, area: false);
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "kb" });
        Assert.NotNull(engine.AreaBoxesFor(EdgeKeyboard()));
        engine.SetAreaSampling(Array.Empty<string>());
        var dev = EdgeKeyboard();
        engine.UpdateDevices(new[] { dev });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(dev.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(Scene));
        await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Equal(untouched, await tcs.Task);
    }

    [Fact]
    public void Cache_KeepsWholeFrameAndRunBoxesApart()
    {
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "x" });
        var whole = engine.AreaBoxesFor("x", 8, null, null, null, 0, 8);
        var run = engine.AreaBoxesFor("x", 8, null, null, null, 0, 4);
        Assert.Equal(32, whole!.Length);
        Assert.Equal(16, run!.Length);
        Assert.Same(whole, engine.AreaBoxesFor("x", 8, null, null, null, 0, 8));
    }

    [Fact]
    public void SummedAreaTable_DoesNotOverflowOnALargeWhiteCanvas()
    {
        var canvas = new CanvasBuffer(400, 400);
        canvas.Fill(255, 255, 255);
        var sat = new AreaSat();
        sat.Build(canvas);
        Assert.Equal(((byte)255, (byte)255, (byte)255), sat.Mean(0, 0, 399, 399));
    }

    [Fact]
    public void Card_Json_UsesCamelCaseAndFlatBoxArray()
    {
        var card = new LightingDevice { Id = "c", LedCount = 1, AreaSampling = true, AreaBoxes = new[] { 0f, 0f, 1f, 1f } };
        var json = System.Text.Json.JsonSerializer.Serialize(card, Nexus.Service.Serialization.AppJsonContext.Default.LightingDevice);
        Assert.Contains("\"areaSampling\":true", json);
        Assert.Contains("\"areaBoxes\":[0,0,1,1]", json);
    }

    [Fact]
    public void LedTapSet_WithAreaBoxes_CoversEveryPixelOfEachBox()
    {
        var dev = EdgeKeyboard();
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { dev.Id });
        var boxes = engine.AreaBoxesFor(dev);
        Assert.NotNull(boxes);
        var spot = new LedTapSet(160, 90);
        Assert.True(spot.AddDevice(dev, 160, 90));
        var area = new LedTapSet(160, 90);
        Assert.True(area.AddDevice(dev, 160, 90, boxes));
        Assert.True(area.Count > spot.Count * 20);
        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < area.Count; i++) { seen.Add((area.X[i], area.Y[i])); }
        // frame centre row (canvas y 45) sits inside the top-row and bottom-row boxes of the middle column
        Assert.Contains((80, 44), seen);
        Assert.Contains((80, 46), seen);
    }
}
