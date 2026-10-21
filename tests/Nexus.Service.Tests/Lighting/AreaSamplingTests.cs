using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class AreaSamplingTests
{
    private static void Modes(LightingEngine engine, string id, string mode = "nearest") =>
        engine.SetSamplingModes(new Dictionary<string, string> { [id] = mode });

    private static float[][] Cells(float[]? u, float[]? v, bool[]? off, int count, float w = 100f, float h = 100f, SamplingMode mode = SamplingMode.Nearest) =>
        AreaCells.Compute(u, v, off, count, 0, count, w, h, mode);

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

    [Theory]
    [InlineData(SamplingMode.Nearest)]
    [InlineData(SamplingMode.Rows)]
    [InlineData(SamplingMode.Columns)]
    public void Km7_CellsCoverTheWholeFrameWithoutOverlap_AndEachLedLiesInItsOwnCell(SamplingMode mode)
    {
        var (u, v) = Km7();
        Assert.Equal(24, u.Length);
        AreaMetric.Weights(573f, 306f, mode, out var wu, out var wv);
        var cells = Cells(u, v, null, 24, 573f, 306f, mode);
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
                var nearest = Enumerable.Range(0, 24).OrderBy(i => Math.Pow((u[i] - x) * wu, 2) + Math.Pow((v[i] - y) * wv, 2)).First();
                Assert.Equal(nearest, owners[0]);
            }
        }
    }

    [Fact]
    public void Km7_ColumnsRunFullHeightToTheMidline()
    {
        var (u, v) = Km7();
        var cells = Cells(u, v, null, 24, 573f, 306f, SamplingMode.Columns);
        // bottom (1025,703) is led 6, top (1025,397) is led 15: same column, split at the midline
        Assert.Equal(0.5f, Range(cells[6], 1).min, 2);
        Assert.Equal(1f, Range(cells[6], 1).max, 2);
        Assert.Equal(0f, Range(cells[15], 1).min, 2);
        Assert.Equal(0.5f, Range(cells[15], 1).max, 2);
        Assert.Equal(Range(cells[6], 0).min, Range(cells[15], 0).min, 2);
        Assert.Equal(Range(cells[6], 0).max, Range(cells[15], 0).max, 2);
        // the column is about a key pitch wide, not stretched sideways
        Assert.InRange(Range(cells[6], 0).max - Range(cells[6], 0).min, 0.08f, 0.14f);
    }

    [Fact]
    public void Km7_NearestIsEuclidean_NotColumns()
    {
        var (u, v) = Km7();
        var near = Cells(u, v, null, 24, 573f, 306f, SamplingMode.Nearest);
        var cols = Cells(u, v, null, 24, 573f, 306f, SamplingMode.Columns);
        // the two metrics partition the frame differently
        Assert.Contains(Enumerable.Range(0, 24), i => Math.Abs(Area(near[i]) - Area(cols[i])) > 0.005);
    }

    [Fact]
    public void RowsMode_IsTheTransposeOfColumnsMode()
    {
        var (u, v) = Km7();
        var rows = Cells(v, u, null, 24, 306f, 573f, SamplingMode.Rows);
        var cols = Cells(u, v, null, 24, 573f, 306f, SamplingMode.Columns);
        // rows on the transposed layout == columns on the original, vertices swapped
        for (var i = 0; i < 24; i++)
        {
            Assert.Equal(Area(cols[i]), Area(rows[i]), 5);
        }
        Assert.Equal(1.0, rows.Sum(Area), 4);
    }

    [Fact]
    public void RowsMode_GivesHorizontalBands()
    {
        // LEDs down the left and right edges: rows mode splits the frame into bands at the midlines between rows
        var u = new[] { 0.1f, 0.1f, 0.1f, 0.9f, 0.9f, 0.9f };
        var v = new[] { 0.1f, 0.5f, 0.9f, 0.1f, 0.5f, 0.9f };
        var c = Cells(u, v, null, 6, mode: SamplingMode.Rows);
        Assert.Equal(0.5f, Range(c[0], 0).max, 2);
        Assert.Equal(0f, Range(c[0], 0).min, 2);
        Assert.Equal(0.3f, Range(c[0], 1).max, 2);
        Assert.Equal(0.3f, Range(c[1], 1).min, 2);
        Assert.Equal(0.7f, Range(c[1], 1).max, 2);
        Assert.Equal(1.0, c.Sum(Area), 4);
    }

    [Fact]
    public void FanRing_UnderNearest_IsPieSlices()
    {
        foreach (var radius in new[] { 0.42f, 0.5f })
        {
            var u = new float[16];
            var v = new float[16];
            for (var i = 0; i < 16; i++)
            {
                var a = i * Math.PI * 2 / 16;
                u[i] = 0.5f + radius * (float)Math.Sin(a);
                v[i] = 0.5f - radius * (float)Math.Cos(a);
            }
            var c = Cells(u, v, null, 16, 100f, 100f, SamplingMode.Nearest);
            Assert.Equal(1.0, c.Sum(Area), 4);
            for (var i = 0; i < 16; i++)
            {
                Assert.True(Contains(c[i], u[i], v[i]));
                // every wedge reaches the ring's centre and its own LED, and nothing of the opposite side
                Assert.True(Contains(c[i], 0.5 + (u[i] - 0.5) * 0.05, 0.5 + (v[i] - 0.5) * 0.05));
                Assert.False(Contains(c[i], 0.5 - (u[i] - 0.5) * 0.5, 0.5 - (v[i] - 0.5) * 0.5));
            }
        }
    }

    [Theory]
    [InlineData(SamplingMode.Nearest)]
    [InlineData(SamplingMode.Rows)]
    [InlineData(SamplingMode.Columns)]
    public void EveryMode_KeepsCoverageForAPerKeyGridAndTwoRows(SamplingMode mode)
    {
        var u = new List<float>(); var v = new List<float>();
        for (var r = 0; r < 5; r++) for (var col = 0; col < 15; col++) { u.Add((col + 0.5f) / 15); v.Add((r + 0.5f) / 5); }
        var c = Cells(u.ToArray(), v.ToArray(), null, 75, 450f, 150f, mode);
        Assert.Equal(1.0, c.Sum(Area), 4);
        for (var i = 0; i < 75; i++) Assert.True(Contains(c[i], u[i], v[i]));
    }

    [Fact]
    public void TwoEdgeRows_SplitAtTheMidline()
    {
        var u = new[] { 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.8f };
        var v = new[] { 0.1f, 0.1f, 0.1f, 0.9f, 0.9f, 0.9f };
        var c = Cells(u, v, null, 6, mode: SamplingMode.Columns);
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
    public void Grid_CoversTheFrameWithoutOverlap_EachLedInItsOwnCell()
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
        Assert.Equal(1.0, c.Sum(Area), 4);
        for (var i = 0; i < 9; i++)
        {
            Assert.True(Contains(c[i], u[i], v[i]));
        }
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
            Modes(engine, device.Id);
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
            Modes(engine, dev.Id);
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
        var other = Scene(EdgeKeyboard(), false, e => Modes(e, "someone-else"));
        Assert.Equal(plain, other);
    }

    [Fact]
    public void TurnedOnThenOff_MatchesAnUntouchedEngine()
    {
        var untouched = Scene(EdgeKeyboard(), false);
        var toggled = Scene(EdgeKeyboard(), false, e =>
        {
            Modes(e, "kb");
            e.SetSamplingModes(null);
        });
        Assert.Equal(untouched, toggled);
    }

    [Fact]
    public void OwnerMap_OwnsEveryFramePixelOnce_AndGivesEveryEnabledLedAPixel()
    {
        using var engine = new LightingEngine();
        Modes(engine, "kb");
        var dev = EdgeKeyboard();
        var map = engine.AreaMapFor(dev, 160, 90)!;
        Assert.Equal(6, map.LedCount);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(map.PixelCount(i) > 0);
        }
        // frame covers canvas x 32..128, y 22.5..67.5 : 96 x 45 pixels
        Assert.Equal(map.Pix.Distinct().Count(), map.Pix.Length);
        Assert.InRange(map.Pix.Length, 96 * 45 - 200, 96 * 45 + 200);
    }

    [Fact]
    public void OwnerMap_DenseStrip_EveryLedKeepsAtLeastItsOwnPixel()
    {
        using var engine = new LightingEngine();
        Modes(engine, "dense");
        var dev = new DeviceFrame(0, "dense", 200, 300, 290, 250, 20);
        var map = engine.AreaMapFor(dev, 160, 90)!;
        for (var i = 0; i < 200; i++)
        {
            Assert.True(map.PixelCount(i) > 0, $"led {i}");
        }
    }

    [Fact]
    public void LedTapSet_WithOwnerMap_CoversTheOwnedPixels()
    {
        using var engine = new LightingEngine();
        Modes(engine, "kb");
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
        settings.Devices.SamplingModes["card"] = "nearest";

        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);

        Assert.Equal("nearest", cards[0].Sampling);
        Assert.Equal(3, cards[0].AreaCells!.Length);
        Assert.Empty(cards[0].AreaCells![1]);
        Assert.Equal(AreaCells.Compute(null, null, frame.LedDisabled, 3, 0, 3, 100, 20, SamplingMode.Nearest)[0], cards[0].AreaCells![0]);
        Assert.Equal("led", cards[1].Sampling);
        Assert.Null(cards[1].AreaCells);
    }

    [Fact]
    public void Card_CellsKeepOneEntryPerLed_UnderAShorterPreviewCountAndAMismatchedFrame()
    {
        using var engine = new LightingEngine();
        var frame = new DeviceFrame(0, "card", 6, 0, 0, 100, 20) { PreviewLedCount = 4 };
        var settings = new NexusSettings();
        foreach (var id in new[] { "card", "short", "noframe" }) { settings.Devices.SamplingModes[id] = "nearest"; }
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
        var card = new LightingDevice { Id = "c", LedCount = 2, Sampling = "columns", AreaCells = new[] { new[] { 0f, 0f, 1f, 0f, 1f, 1f }, Array.Empty<float>() } };
        var json = System.Text.Json.JsonSerializer.Serialize(card, Nexus.Service.Serialization.AppJsonContext.Default.LightingDevice);
        Assert.Contains("\"sampling\":\"columns\"", json);
        Assert.Contains("\"areaCells\":[[0,0,1,0,1,1],[]]", json);
    }

    [Fact]
    public void SteadyStateSamplingPass_AllocatesNoMoreWithAreaSamplingThanWithout()
    {
        long Measure(bool area)
        {
            using var engine = new LightingEngine();
            if (area)
            {
                Modes(engine, "kb");
            }
            var dev = EdgeKeyboard();
            var devs = new[] { dev };
            engine.UpdateDevices(devs);
            for (var i = 0; i < 5; i++) { engine.SampleForTest(devs); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++) { engine.SampleForTest(devs); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var off = Measure(false);
        var on = Measure(true);
        Assert.True(on <= off, $"area on allocated {on} bytes over 200 passes, off {off}");
    }

    [Fact]
    public void Cache_EvictsEntriesOfRemovedDevices()
    {
        using var engine = new LightingEngine();
        Modes(engine, "kb");
        var dev = EdgeKeyboard();
        Assert.NotNull(engine.AreaMapFor(dev, 160, 90));
        Assert.NotEmpty(engine.AreaCellsForCard(dev, 6)!);
        engine.UpdateDevices(new[] { new DeviceFrame(1, "other", 2) });
        // rebuilt from scratch afterwards; the old object is not retained
        var first = engine.AreaMapFor(dev, 160, 90);
        engine.UpdateDevices(new[] { new DeviceFrame(1, "other", 2) });
        Assert.NotSame(first, engine.AreaMapFor(dev, 160, 90));
    }

    [Fact]
    public void Card_Cells_FollowTheStackSlotShare()
    {
        using var engine = new LightingEngine();
        Modes(engine, "a");
        engine.SetStackSlots(new List<Nexus.Service.Persistence.DeviceGroup>
        {
            new() { Id = "s", Layout = "parallel", Members = new List<string> { "a", "b" } },
        });
        var frame = new DeviceFrame(0, "a", 2, 0, 0, 100, 100);
        var cells = engine.AreaCellsForCard(frame, 2)!;
        // the first slot of a parallel stack is the top half of the frame
        Assert.All(cells, c => Assert.True(Range(c, 1).max <= 0.5f + 1e-4f));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(96)]
    [InlineData(400)]
    public void NearestGrid_MatchesBruteForce_LowestIndexOnTies(int n)
    {
        var rng = new Random(n);
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            // quantized so exact ties occur
            x[i] = rng.Next(0, 40) * 5f;
            y[i] = rng.Next(0, 20) * 5f;
        }
        var grid = new AreaNearest(x, y, 200f, 100f);
        for (var q = 0; q < 3000; q++)
        {
            float fx = rng.Next(0, 401) * 0.5f, fy = rng.Next(0, 201) * 0.5f;
            var best = 0;
            var bd = float.MaxValue;
            for (var j = 0; j < n; j++)
            {
                var d = (x[j] - fx) * (x[j] - fx) + (y[j] - fy) * (y[j] - fy);
                if (d < bd) { bd = d; best = j; }
            }
            Assert.Equal(best, grid.Query(fx, fy));
        }
    }

    [Fact]
    public void LegacyAreaSamplingList_MigratesToNearest_AndIsDropped()
    {
        var json = """{"devices":{"areaSamplingDevices":["a","b"],"samplingModes":{"b":"columns"}}}""";
        var doc = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(PinMigration.Apply(doc));
        Assert.Equal("nearest", doc.Devices.SamplingModes["a"]);
        Assert.Equal("columns", doc.Devices.SamplingModes["b"]);
        Assert.Null(doc.Devices.AreaSamplingDevices);
        var written = System.Text.Json.JsonSerializer.Serialize(doc, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        Assert.DoesNotContain("areaSamplingDevices", written);
        Assert.False(PinMigration.Apply(doc));
    }

    [Fact]
    public void Card_AlwaysCarriesASamplingName_AndCellsOnlyOutsideLedMode()
    {
        using var engine = new LightingEngine();
        engine.UpdateDevices(new[] { new DeviceFrame(0, "r", 2, 0, 0, 100, 20) });
        var settings = new NexusSettings();
        settings.Devices.SamplingModes["r"] = "rows";
        var cards = new List<LightingDevice> { new() { Id = "r", LedCount = 2 }, new() { Id = "x", LedCount = 2 } };
        CompositeLightingDeviceProvider.ApplyAreaSampling(engine, cards, settings);
        Assert.Equal("rows", cards[0].Sampling);
        Assert.NotNull(cards[0].AreaCells);
        Assert.Equal("led", cards[1].Sampling);
        Assert.Null(cards[1].AreaCells);
        var json = System.Text.Json.JsonSerializer.Serialize(cards[1], Nexus.Service.Serialization.AppJsonContext.Default.LightingDevice);
        Assert.Contains("\"sampling\":\"led\"", json);
        Assert.DoesNotContain("areaCells", json);
    }

    [Fact]
    public void CoincidentLeds_ShareOneCell_AndOnePixelRange()
    {
        // leds 0 and 1 sit at the same spot, led 2 elsewhere
        var u = new[] { 0.25f, 0.25f, 0.75f };
        var v = new[] { 0.5f, 0.5f, 0.5f };
        var cells = Cells(u, v, null, 3);
        Assert.Equal(cells[0], cells[1]);
        Assert.Equal(0.5, Area(cells[0]), 4);
        Assert.Equal(1.0, cells.Distinct().Sum(Area), 4);

        using var engine = new LightingEngine();
        Modes(engine, "g");
        var dev = new DeviceFrame(0, "g", 3, 200, 200, 600, 200) { LedU = u, LedV = v };
        var map = engine.AreaMapFor(dev, 160, 90)!;
        Assert.True(map.PixelCount(0) > 20);
        Assert.Equal(map.PixelCount(0), map.PixelCount(1));
        var px = new byte[160 * 90 * 3];
        for (var i = 0; i < px.Length; i += 3) { px[i] = (byte)(i / 3 % 160 * 255 / 159); }
        map.Sample(px, 0, out var r0, out _, out _);
        map.Sample(px, 1, out var r1, out _, out _);
        Assert.Equal(r0, r1);
        Assert.NotEqual(0, r0);
    }

    [Fact]
    public void SamplingMode_FollowsTheProduct_ThroughReorderSwapAndDelete()
    {
        const string dev = "openrgb-s-PORT";
        string Z(int i) => Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(dev, i);
        NexusSettings Fresh()
        {
            var s = new NexusSettings();
            s.Devices.SamplingModes[Z(0)] = "rows";
            s.Devices.SamplingModes[Z(1)] = "columns";
            return s;
        }
        void Reslot(NexusSettings s, List<int?> origins)
        {
            var carried = Nexus.Service.Routes.DevicesRoutes.CarriedChainState(s, dev, origins);
            Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(s, new[] { Z(0), Z(1), Z(2) });
            Nexus.Service.Routes.DevicesRoutes.RestoreChainState(s, carried);
        }

        var swap = Fresh();
        Reslot(swap, new List<int?> { 1, 0 });
        Assert.Equal("columns", swap.Devices.SamplingModes[Z(0)]);
        Assert.Equal("rows", swap.Devices.SamplingModes[Z(1)]);

        var reorder = Fresh();
        Reslot(reorder, new List<int?> { null, 0, 1 });
        Assert.False(reorder.Devices.SamplingModes.ContainsKey(Z(0)));
        Assert.Equal("rows", reorder.Devices.SamplingModes[Z(1)]);
        Assert.Equal("columns", reorder.Devices.SamplingModes[Z(2)]);

        var delete = Fresh();
        Reslot(delete, new List<int?> { 1 });
        Assert.Equal("columns", delete.Devices.SamplingModes[Z(0)]);
        Assert.False(delete.Devices.SamplingModes.ContainsKey(Z(1)));

        var cleared = Fresh();
        Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(cleared, new[] { Z(0), Z(1) });
        Assert.Empty(cleared.Devices.SamplingModes);
    }

    [Fact]
    public void Engine_AdoptsAReplacedSettingsDictionary_FromTheSource()
    {
        using var engine = new LightingEngine();
        var settings = new NexusSettings();
        engine.SamplingModeSource = () => settings.Devices.SamplingModes;
        engine.RefreshSamplingModes();
        Assert.False(engine.IsAreaSampled("z"));
        settings.Devices.SamplingModes = new Dictionary<string, string> { ["z"] = "rows" };
        engine.RefreshSamplingModes();
        Assert.Equal(SamplingMode.Rows, engine.ModeOf("z"));
        settings.Devices.SamplingModes = new Dictionary<string, string>();
        engine.RefreshSamplingModes();
        Assert.False(engine.IsAreaSampled("z"));
    }

    [Fact]
    public void StackSlot_WithCoincidentLeds_RemapsEachPolygonOnce()
    {
        var frame = new DeviceFrame(0, "a", 3, 0, 0, 100, 100)
        {
            LedU = new[] { 0.25f, 0.25f, 0.75f },
            LedV = new[] { 0.5f, 0.5f, 0.5f },
        };
        float[][] Run(bool slotted)
        {
            using var engine = new LightingEngine();
            Modes(engine, "a");
            if (slotted)
            {
                engine.SetStackSlots(new List<Nexus.Service.Persistence.DeviceGroup>
                {
                    new() { Id = "s", Layout = "parallel", Members = new List<string> { "b", "a" } },
                });
            }
            return engine.AreaCellsForCard(frame, 3)!;
        }
        var plain = Run(false);
        var slotted = Run(true);
        // slot 1 of 2 in a parallel stack is the bottom half: v -> 0.5 + 0.5 * v, u unchanged
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(plain[i].Length, slotted[i].Length);
            for (var k = 0; k < plain[i].Length; k += 2)
            {
                Assert.Equal(plain[i][k], slotted[i][k], 4);
                Assert.Equal(0.5f + 0.5f * plain[i][k + 1], slotted[i][k + 1], 4);
            }
        }
        Assert.Same(slotted[0], slotted[1]);
    }

    [Fact]
    public void SceneOwnerMap_FollowsTheMode()
    {
        // two projected LEDs; the canvas point (60, 30) is nearer the first in x, nearer the second in y
        var sx = new[] { 40f, 100f };
        var sy = new[] { 20f, 32f };
        var ok = new[] { true, true };
        var quad = new (float X, float Y)[] { (0f, 0f), (160f, 0f), (0f, 90f), (160f, 90f) };
        int OwnerOfPixel(SamplingMode mode, int px, int py)
        {
            var map = AreaOwnerMap.BuildScene(sx, sy, ok, quad, true, 160, 90, mode);
            var off = (py * 160 + px) * 3;
            for (var led = 0; led < 2; led++)
            {
                for (var k = map.Start[led]; k < map.Start[led + 1]; k++)
                {
                    if (map.Pix[k] == off) return led;
                }
            }
            return -1;
        }
        // (60,30): to led0 dx 19.5 dy 9.5; to led1 dx 39.5 dy 1.5
        Assert.Equal(0, OwnerOfPixel(SamplingMode.Nearest, 60, 30));
        Assert.Equal(0, OwnerOfPixel(SamplingMode.Columns, 60, 30));
        Assert.Equal(1, OwnerOfPixel(SamplingMode.Rows, 60, 30));
    }
}
