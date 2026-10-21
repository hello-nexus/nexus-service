using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class AreaSamplingTests
{
    private static float[][] Cells(float[]? u, float[]? v, bool[]? off, int count, float w = 100f, float h = 100f) =>
        AreaCells.Compute(u, v, off, count, 0, count, w, h);

    private static double Area(float[] p)
    {
        double a = 0;
        for (var i = 0; i < p.Length / 2; i++)
        {
            var j = (i + 1) % (p.Length / 2);
            a += (double)p[i * 2] * p[j * 2 + 1] - (double)p[j * 2] * p[i * 2 + 1];
        }
        return Math.Abs(a) * 0.5;
    }

    private static bool Contains(float[] p, double x, double y)
    {
        var n = p.Length / 2;
        if (n < 3) return false;
        bool pos = false, neg = false;
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var cr = (p[j * 2] - p[i * 2]) * (y - p[i * 2 + 1]) - (p[j * 2 + 1] - p[i * 2 + 1]) * (x - p[i * 2]);
            if (cr > 1e-9) pos = true;
            else if (cr < -1e-9) neg = true;
        }
        return !(pos && neg);
    }

    private static (float min, float max) Range(float[] p, int axis) =>
        (Enumerable.Range(0, p.Length / 2).Min(i => p[i * 2 + axis]), Enumerable.Range(0, p.Length / 2).Max(i => p[i * 2 + axis]));

    // KM7: 24 LEDs, frame pixels x 853..1426, y 397..703.
    private static (float[] u, float[] v) Km7()
    {
        var pts = new List<(float x, float y)>();
        foreach (var x in new[] { 1369, 1311, 1254, 1196, 1139, 1082, 1025, 967, 910 }) pts.Add((x, 703));
        foreach (var y in new[] { 642, 580, 519, 458 }) pts.Add((853, y));
        foreach (var x in new[] { 910, 967, 1025, 1082, 1139, 1196, 1254 }) pts.Add((x, 397));
        pts.Add((1311, 458));
        pts.Add((1369, 458));
        pts.Add((1426, 580));
        pts.Add((1426, 703));
        return (pts.Select(p => (p.x - 853f) / 573f).ToArray(), pts.Select(p => (p.y - 397f) / 306f).ToArray());
    }

    [Fact]
    public void Km7_CellsCoverTheWholeFrameWithoutOverlap_AndEachLedLiesInItsOwnCell()
    {
        var (u, v) = Km7();
        Assert.Equal(24, u.Length);
        var cells = Cells(u, v, null, 24, 573f, 306f);
        Assert.Equal(1.0, cells.Sum(Area), 4);
        for (var i = 0; i < 24; i++)
        {
            Assert.True(Contains(cells[i], u[i], v[i]), $"led {i} outside its cell");
        }
        // every sample point sits in the cell of its nearest LED and in no cell whose LED is farther
        for (var gy = 0; gy < 41; gy++)
        {
            for (var gx = 0; gx < 41; gx++)
            {
                double x = (gx + 0.37) / 41.74, y = (gy + 0.29) / 41.58;
                var owners = Enumerable.Range(0, 24).Where(i => Contains(cells[i], x, y)).ToArray();
                Assert.Single(owners);
                var nearest = Enumerable.Range(0, 24).OrderBy(i => Math.Pow((u[i] - x) * 573, 2) + Math.Pow((v[i] - y) * 306, 2)).First();
                Assert.Equal(nearest, owners[0]);
            }
        }
    }

    [Fact]
    public void TwoEdgeRows_SplitAtTheMidline()
    {
        var u = new[] { 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.8f };
        var v = new[] { 0.1f, 0.1f, 0.1f, 0.9f, 0.9f, 0.9f };
        var c = Cells(u, v, null, 6);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0.5f, Range(c[i], 1).max, 4);
            Assert.Equal(0f, Range(c[i], 1).min, 4);
            Assert.Equal(0.5f, Range(c[i + 3], 1).min, 4);
            Assert.Equal(1f, Range(c[i + 3], 1).max, 4);
        }
        Assert.Equal(0.35f, Range(c[1], 0).min, 4);
        Assert.Equal(0.65f, Range(c[1], 0).max, 4);
        Assert.Equal(1.0, c.Sum(Area), 4);
    }

    [Fact]
    public void LinearStripWithoutUv_IsFullHeightSlices()
    {
        var c = Cells(null, null, null, 5);
        foreach (var cell in c)
        {
            Assert.Equal(0f, Range(cell, 1).min, 4);
            Assert.Equal(1f, Range(cell, 1).max, 4);
        }
        Assert.Equal(0.375f, Range(c[2], 0).min, 4);
        Assert.Equal(0.625f, Range(c[2], 0).max, 4);
        Assert.Equal(0f, Range(c[0], 0).min, 4);
        Assert.Equal(0.125f, Range(c[0], 0).max, 4);
    }

    [Fact]
    public void Grid_GivesPerKeyCells()
    {
        var u = new List<float>();
        var v = new List<float>();
        for (var r = 0; r < 3; r++)
        {
            for (var col = 0; col < 3; col++)
            {
                u.Add(col * 0.4f + 0.1f);
                v.Add(r * 0.4f + 0.1f);
            }
        }
        var c = Cells(u.ToArray(), v.ToArray(), null, 9);
        Assert.Equal(0.16, Area(c[4]), 4);
        Assert.Equal(0.3f, Range(c[4], 0).min, 4);
        Assert.Equal(0.7f, Range(c[4], 0).max, 4);
        Assert.Equal(1.0, c.Sum(Area), 4);
    }

    [Fact]
    public void DisabledLed_OwnsNothing_AndItsNeighboursTakeTheSpace()
    {
        var c = Cells(null, null, new[] { false, true, false }, 3);
        Assert.Empty(c[1]);
        Assert.Equal(0.5f, Range(c[0], 0).max, 4);
        Assert.Equal(0.5f, Range(c[2], 0).min, 4);
        Assert.Equal(1.0, c.Sum(Area), 4);
    }

    [Fact]
    public void SingleLed_IsTheWholeFrame()
    {
        Assert.Equal(1.0, Area(Cells(new[] { 0.3f }, new[] { 0.7f }, null, 1)[0]), 4);
    }

    [Fact]
    public void Aspect_ChangesThePartition()
    {
        var u = new[] { 0.2f, 0.6f };
        var v = new[] { 0.3f, 0.8f };
        var square = Cells(u, v, null, 2, 100f, 100f)[0];
        var wide = Cells(u, v, null, 2, 400f, 100f)[0];
        Assert.NotEqual(Area(square), Area(wide), 3);
    }

    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(DeviceFrame device, Action<CanvasBuffer> paint, bool area, bool fullFrame = false)
    {
        using var engine = new LightingEngine();
        engine.FullFrameSampling = fullFrame;
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

    private static DeviceFrame EdgeKeyboard() => new(0, "kb", 6, x: 200, y: 150, w: 600, h: 300)
    {
        LedU = new[] { 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.8f },
        LedV = new[] { 0.05f, 0.05f, 0.05f, 0.95f, 0.95f, 0.95f },
    };

    // A bright bar across the frame's vertical middle, between the two LED rows (frame spans canvas y 22..67).
    private static void MiddleBar(CanvasBuffer c)
    {
        c.Clear();
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
    public async Task LinearLightMean_OfHalfBlackHalfWhiteCell_IsAbout188()
    {
        var dev = new DeviceFrame(0, "one", 1, x: 0, y: 0, w: 1000, h: 600);
        var leds = await RenderOnce(dev, c =>
        {
            c.Clear();
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = 80; x < c.Width; x++)
                {
                    c.SetPixel(x, y, 255, 255, 255);
                }
            }
        }, area: true);
        Assert.InRange(leds[0], 187, 189);
    }

    [Fact]
    public async Task SweepFullFrameRead_AlsoUsesCells()
    {
        var dev = new DeviceFrame(0, "one", 1, x: 400, y: 250, w: 100, h: 100);
        var plain = await RenderOnce(dev, c => { c.Clear(); c.SetPixel(10, 10, 255, 255, 255); }, area: false, fullFrame: true);
        var dev2 = new DeviceFrame(0, "one", 1, x: 400, y: 250, w: 100, h: 100);
        var area = await RenderOnce(dev2, c => { c.Clear(); c.SetPixel(10, 10, 255, 255, 255); }, area: true, fullFrame: true);
        // one lit pixel of the whole canvas: the cell is the whole canvas, so the linear mean is tiny but not above the plain box read
        Assert.True(area[0] <= 30);
        Assert.NotNull(plain);
    }

    private static byte[] Scene(DeviceFrame dev, bool area, Action<LightingEngine>? mutate = null)
    {
        static void Paint(CanvasBuffer c)
        {
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = 0; x < c.Width; x++)
                {
                    c.SetPixel(x, y, (byte)(x * 255 / (c.Width - 1)), (byte)(y * 255 / (c.Height - 1)), 40);
                }
            }
        }
        using var engine = new LightingEngine();
        if (area)
        {
            engine.SetAreaSampling(new[] { dev.Id });
        }
        mutate?.Invoke(engine);
        engine.UpdateDevices(new[] { dev });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(dev.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(Paint));
        Assert.True(tcs.Task.Wait(3000));
        return tcs.Task.Result;
    }

    [Fact]
    public void AreaSamplingOff_MatchesAnEngineSamplingAnotherCard()
    {
        var plain = Scene(EdgeKeyboard(), false);
        var other = Scene(EdgeKeyboard(), false, e => e.SetAreaSampling(new[] { "someone-else" }));
        Assert.Equal(plain, other);
    }

    [Fact]
    public void TurnedOnThenOff_MatchesAnUntouchedEngine()
    {
        var untouched = Scene(EdgeKeyboard(), false);
        var toggled = Scene(EdgeKeyboard(), false, e =>
        {
            e.SetAreaSampling(new[] { "kb" });
            e.SetAreaSampling(Array.Empty<string>());
        });
        Assert.Equal(untouched, toggled);
    }

    [Fact]
    public void OwnerMap_OwnsEveryFramePixelOnce_AndGivesEveryEnabledLedAPixel()
    {
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "kb" });
        var dev = EdgeKeyboard();
        var map = engine.AreaMapFor(dev, 160, 90)!;
        Assert.Equal(6, map.LedCount);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(map.Start[i + 1] > map.Start[i]);
        }
        // frame covers canvas x 32..128, y 22.5..67.5 : 96 x 45 pixels
        Assert.Equal(map.Pix.Distinct().Count(), map.Pix.Length);
        Assert.InRange(map.Pix.Length, 96 * 45 - 200, 96 * 45 + 200);
    }

    [Fact]
    public void OwnerMap_DenseStrip_EveryLedKeepsAtLeastItsOwnPixel()
    {
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "dense" });
        var dev = new DeviceFrame(0, "dense", 200, 300, 290, 250, 20);
        var map = engine.AreaMapFor(dev, 160, 90)!;
        for (var i = 0; i < 200; i++)
        {
            Assert.True(map.Start[i + 1] > map.Start[i], $"led {i}");
        }
    }

    [Fact]
    public void LedTapSet_WithOwnerMap_CoversTheOwnedPixels()
    {
        using var engine = new LightingEngine();
        engine.SetAreaSampling(new[] { "kb" });
        var dev = EdgeKeyboard();
        var spot = new LedTapSet(160, 90);
        Assert.True(spot.AddDevice(dev, 160, 90));
        var area = new LedTapSet(160, 90);
        Assert.True(area.AddDevice(dev, 160, 90, engine.AreaMapFor(dev, 160, 90)));
        Assert.True(area.Count > spot.Count * 20);
        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < area.Count; i++) { seen.Add((area.X[i], area.Y[i])); }
        Assert.Contains((80, 44), seen);
        Assert.Contains((80, 46), seen);
    }

    [Fact]
    public void Card_CarriesAreaSamplingAndCellsFromTheEngineFrame()
    {
        using var engine = new LightingEngine();
        var frame = new DeviceFrame(0, "card", 3, 0, 0, 100, 20) { LedDisabled = new[] { false, true, false } };
        engine.UpdateDevices(new[] { frame });
        var cards = new List<LightingDevice> { new() { Id = "card", LedCount = 3 }, new() { Id = "plain", LedCount = 3 } };
        var settings = new NexusSettings();
        settings.Devices.AreaSamplingDevices.Add("card");

        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);

        Assert.True(cards[0].AreaSampling);
        Assert.Equal(3, cards[0].AreaCells!.Length);
        Assert.Empty(cards[0].AreaCells![1]);
        Assert.Equal(AreaCells.Compute(null, null, frame.LedDisabled, 3, 0, 3, 100, 20)[0], cards[0].AreaCells![0]);
        Assert.False(cards[1].AreaSampling);
        Assert.Null(cards[1].AreaCells);
    }

    [Fact]
    public void Card_CellsKeepOneEntryPerLed_UnderAShorterPreviewCountAndAMismatchedFrame()
    {
        using var engine = new LightingEngine();
        var frame = new DeviceFrame(0, "card", 6, 0, 0, 100, 20) { PreviewLedCount = 4 };
        var settings = new NexusSettings();
        settings.Devices.AreaSamplingDevices.AddRange(new[] { "card", "short", "noframe" });
        engine.UpdateDevices(new[] { frame, new DeviceFrame(1, "short", 8, 0, 0, 100, 20) });
        var cards = new List<LightingDevice>
        {
            new() { Id = "card", LedCount = 6 }, new() { Id = "short", LedCount = 5 }, new() { Id = "noframe", LedCount = 2 },
        };

        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);

        Assert.Equal(6, cards[0].AreaCells!.Length);
        Assert.NotEmpty(cards[0].AreaCells![3]);
        Assert.Empty(cards[0].AreaCells![4]);
        Assert.Empty(cards[0].AreaCells![5]);
        Assert.Equal(5, cards[1].AreaCells!.Length);
        Assert.All(cards[1].AreaCells!, c => Assert.NotEmpty(c));
        Assert.Equal(2, cards[2].AreaCells!.Length);
        Assert.All(cards[2].AreaCells!, c => Assert.Empty(c));
    }

    [Fact]
    public void Card_Json_UsesCamelCaseAndNestedPolygonArrays()
    {
        var card = new LightingDevice { Id = "c", LedCount = 2, AreaSampling = true, AreaCells = new[] { new[] { 0f, 0f, 1f, 0f, 1f, 1f }, Array.Empty<float>() } };
        var json = System.Text.Json.JsonSerializer.Serialize(card, Nexus.Service.Serialization.AppJsonContext.Default.LightingDevice);
        Assert.Contains("\"areaSampling\":true", json);
        Assert.Contains("\"areaCells\":[[0,0,1,0,1,1],[]]", json);
    }
}
