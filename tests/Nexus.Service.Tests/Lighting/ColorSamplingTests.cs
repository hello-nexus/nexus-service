using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class ColorSamplingTests
{
    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static async Task<byte[]> Render(DeviceFrame device, Action<CanvasBuffer> paint, string color, string? area = null, bool fullFrame = false)
    {
        using var engine = new LightingEngine();
        engine.FullFrameSampling = fullFrame;
        engine.SetColorSamplingModes(new Dictionary<string, string> { [device.Id] = color });
        if (area is not null)
        {
            engine.SetSamplingModes(new Dictionary<string, string> { [device.Id] = area });
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

    private static DeviceFrame One() => new(0, "one", 1, x: 0, y: 0, w: 1000, h: 600);

    private static void HalfWhite(CanvasBuffer c)
    {
        c.Clear();
        for (var y = 0; y < c.Height; y++)
        {
            for (var x = c.Width / 2; x < c.Width; x++)
            {
                c.SetPixel(x, y, 255, 255, 255);
            }
        }
    }

    // A sparse grid of pure red pixels on black.
    private static void ElevenPercentRed(CanvasBuffer c)
    {
        c.Clear();
        for (var y = 0; y < c.Height; y++)
        {
            for (var x = 0; x < c.Width; x++)
            {
                if ((x + y * 3) % 9 == 0)
                {
                    c.SetPixel(x, y, 255, 0, 0);
                }
            }
        }
    }

    private static void NearBlack(CanvasBuffer c)
    {
        c.Clear();
        for (var y = 0; y < c.Height; y++)
        {
            for (var x = 0; x < c.Width; x++)
            {
                c.SetPixel(x, y, 8, 3, 0);
            }
        }
    }

    // The read paths: the 1-LED spot, the full-frame sweep box, and the area cell.
    public static TheoryData<string?, bool> Paths() => new()
    {
        { null, false },
        { null, true },
        { "nearest", false },
        { "nearest", true },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task IgnoreBlack_KeepsTheLitPixelsAtFullValue(string? area, bool fullFrame)
    {
        var leds = await Render(One(), ElevenPercentRed, ColorSamplings.IgnoreBlack, area, fullFrame);
        Assert.Equal(255, leds[0]);
        Assert.Equal(0, leds[1]);
        Assert.Equal(0, leds[2]);
        var plain = await Render(One(), ElevenPercentRed, ColorSamplings.Standard, area, fullFrame);
        Assert.InRange(plain[0], 1, 200);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task IgnoreBlack_FallsBackToThePlainMean_WhenEveryPixelIsNearBlack(string? area, bool fullFrame)
    {
        var leds = await Render(One(), NearBlack, ColorSamplings.IgnoreBlack, area, fullFrame);
        Assert.Equal((byte)8, leds[0]);
        Assert.Equal((byte)3, leds[1]);
        Assert.Equal((byte)0, leds[2]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("nearest", false)]
    [InlineData("nearest", true)]
    public async Task HalfBlackHalfWhite_IsStandard127_Vivid188(string? area, bool fullFrame)
    {
        var standard = await Render(One(), HalfWhite, ColorSamplings.Standard, area, fullFrame);
        var vivid = await Render(One(), HalfWhite, ColorSamplings.Vivid, area, fullFrame);
        Assert.Equal(127, standard[0]);
        Assert.Equal(188, vivid[0]);
    }

    [Fact]
    public async Task Spot_Vivid_ReadsBrighterThanStandard_OnAMixedSpot()
    {
        static void Columns(CanvasBuffer c)
        {
            c.Clear();
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = 0; x < c.Width; x += 2)
                {
                    c.SetPixel(x, y, 255, 255, 255);
                }
            }
        }
        var standard = await Render(One(), Columns, ColorSamplings.Standard);
        var vivid = await Render(One(), Columns, ColorSamplings.Vivid);
        Assert.InRange(standard[0], 1, 254);
        Assert.True(vivid[0] > standard[0]);
    }

    [Fact]
    public void Combiner_PinsEachMode()
    {
        var half = new ColorAccumulator(ColorSampling.Vivid);
        half.Add(0, 0, 0);
        half.Add(255, 255, 255);
        Assert.Equal(((byte)188, (byte)188, (byte)188), half.Result());

        var std = new ColorAccumulator(ColorSampling.Standard);
        std.Add(0, 0, 0);
        std.Add(255, 255, 255);
        Assert.Equal(((byte)127, (byte)127, (byte)127), std.Result());

        var ib = new ColorAccumulator(ColorSampling.IgnoreBlack);
        for (var i = 0; i < 8; i++)
        {
            ib.Add(0, 0, 0);
        }
        ib.Add(255, 0, 0);
        ib.Add(9, 0, 0);
        Assert.Equal(((byte)132, (byte)0, (byte)0), ib.Result());

        var edge = new ColorAccumulator(ColorSampling.IgnoreBlack);
        edge.Add(ColorSamplings.NearBlackMax, 0, 0);
        edge.Add(0, 0, 0);
        Assert.Equal(((byte)(ColorSamplings.NearBlackMax / 2), (byte)0, (byte)0), edge.Result());

        Assert.Equal(((byte)0, (byte)0, (byte)0), new ColorAccumulator(ColorSampling.Vivid).Result());
    }

    [Theory]
    [InlineData("standard", ColorSampling.Standard)]
    [InlineData("vivid", ColorSampling.Vivid)]
    [InlineData("ignore-black", ColorSampling.IgnoreBlack)]
    public void WireNames_RoundTrip(string name, ColorSampling mode)
    {
        Assert.True(ColorSamplings.TryParse(name, out var parsed));
        Assert.Equal(mode, parsed);
        Assert.Equal(name, ColorSamplings.Name(mode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sepia")]
    public void UnknownOrMissing_IsStandard(string? name)
    {
        Assert.False(ColorSamplings.TryParse(name, out var parsed));
        Assert.Equal(ColorSampling.Standard, parsed);
        Assert.Equal(ColorSampling.Standard, ColorSamplings.Of(new Dictionary<string, string> { ["a"] = name! }, "a"));
        Assert.Equal(ColorSampling.Standard, ColorSamplings.Of(null, "a"));
    }

    [Fact]
    public void ChainReorderAndZoneDrop_CarryTheModeLikeTheSamplingMode()
    {
        const string dev = "openrgb-s-PORT";
        string Z(int i) => Nexus.Service.Lighting.Zones.ZoneResolution.CustomZoneId(dev, i);
        var s = new NexusSettings();
        s.Devices.ColorSamplingModes[Z(0)] = "vivid";
        s.Devices.ColorSamplingModes[Z(1)] = "ignore-black";
        var carried = Nexus.Service.Routes.DevicesRoutes.CarriedChainState(s, dev, new List<int?> { 1, 0 });
        Nexus.Service.Lighting.Zones.ZoneStateDrop.Drop(s, new[] { Z(0), Z(1), Z(2) });
        Assert.Empty(s.Devices.ColorSamplingModes);
        Nexus.Service.Routes.DevicesRoutes.RestoreChainState(s, carried);
        Assert.Equal("ignore-black", s.Devices.ColorSamplingModes[Z(0)]);
        Assert.Equal("vivid", s.Devices.ColorSamplingModes[Z(1)]);
    }
}
