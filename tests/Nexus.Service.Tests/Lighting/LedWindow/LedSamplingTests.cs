using System;
using Nexus.Service.Lighting.Engine;
using Xunit;

namespace Nexus.Service.Tests.Lighting.LedWindow;

public class LedSamplingTests
{
    private const float CW = 1000f;
    private const float CH = 600f;
    private const float Spot = 3f / 160f;
    private const float Strip = 0.08f;

    private static CanvasBuffer Noise(int seed)
    {
        var c = new CanvasBuffer(160, 90);
        var rng = new Random(seed);
        for (var y = 0; y < 90; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                c.SetPixel(x, y, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            }
        }
        return c;
    }

    private static DeviceFrame RandomDevice(Random rng, int index)
    {
        var leds = rng.Next(1, 220);
        var dev = new DeviceFrame(
            index, "d" + index, leds, rng.Next(0, 900), rng.Next(0, 560), rng.Next(4, 700), rng.Next(4, 400),
            rng.Next(0, 4) == 0 ? 90 * rng.Next(0, 4) : rng.Next(-400, 400));
        if (rng.Next(3) != 0)
        {
            var u = new float[leds];
            var v = new float[leds];
            for (var i = 0; i < leds; i++)
            {
                u[i] = (float)rng.NextDouble();
                v[i] = (float)rng.NextDouble();
            }
            dev.LedU = u;
            dev.LedV = v;
        }
        if (rng.Next(4) == 0)
        {
            var dis = new bool[leds];
            for (var i = 0; i < leds; i++)
            {
                dis[i] = rng.Next(5) == 0;
            }
            dev.LedDisabled = dis;
        }
        return dev;
    }

    // The sampler as it was written before the geometry moved into LedSampling.cs.
    private static byte[] Reference(DeviceFrame dev, CanvasBuffer canvas, bool fullFrame)
    {
        var cw = canvas.Width;
        var ch = canvas.Height;
        var ledCount = dev.LedCount;
        var rectX = fullFrame ? 0f : dev.X;
        var rectY = fullFrame ? 0f : dev.Y;
        var rectW = fullFrame ? CW : dev.W;
        var rectH = fullFrame ? CH : dev.H;
        var frameCx = rectX + rectW * 0.5f;
        var frameCy = rectY + rectH * 0.5f;
        var kx = cw / CW;
        var ky = ch / CH;
        var rad = fullFrame ? 0f : (((dev.Rotation % 360) + 360) % 360) * (MathF.PI / 180f);
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var absCos = MathF.Abs(cos);
        var absSin = MathF.Abs(sin);
        var out_ = new byte[ledCount * 3];
        void Set(int i, (byte r, byte g, byte b) c)
        {
            out_[i * 3] = c.r;
            out_[i * 3 + 1] = c.g;
            out_[i * 3 + 2] = c.b;
        }
        if (dev.LedU is not null && dev.LedV is not null && dev.LedU.Length == ledCount && dev.LedV.Length == ledCount)
        {
            var aspect = rectH > 0.001f ? rectW / rectH : 1f;
            var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ledCount * aspect)));
            var rows = Math.Max(1, (ledCount + cols - 1) / cols);
            var cellHalfW = Math.Max(1f / kx, rectW / cols) * 0.5f;
            var cellHalfH = Math.Max(1f / ky, rectH / rows) * 0.5f;
            if (fullFrame)
            {
                cellHalfW = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
                cellHalfH = Math.Max(1f / ky, rectH * Strip) * 0.5f;
            }
            var uvHalfW = (cellHalfW * absCos + cellHalfH * absSin) * kx;
            var uvHalfH = (cellHalfW * absSin + cellHalfH * absCos) * ky;
            for (var i = 0; i < ledCount; i++)
            {
                if (dev.LedDisabled is not null && i < dev.LedDisabled.Length && dev.LedDisabled[i])
                {
                    continue;
                }
                var lx = rectX + dev.LedU[i] * rectW - frameCx;
                var ly = rectY + dev.LedV[i] * rectH - frameCy;
                var sx = (frameCx + lx * cos - ly * sin) * kx;
                var sy = (frameCy + lx * sin + ly * cos) * ky;
                Set(i, fullFrame ? Box(canvas, sx - uvHalfW, sy - uvHalfH, sx + uvHalfW, sy + uvHalfH) : SpotRef(canvas, sx, sy, uvHalfW, uvHalfH));
            }
            return out_;
        }
        var pitchHalf = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
        var breadth = fullFrame ? rectH * Strip : rectH;
        var breadthHalf = Math.Max(1f / ky, breadth) * 0.5f;
        var linHalfW = (pitchHalf * absCos + breadthHalf * absSin) * kx;
        var linHalfH = (pitchHalf * absSin + breadthHalf * absCos) * ky;
        var midY = rectY + rectH * 0.5f - frameCy;
        var denom = ledCount > 1 ? 1f / (ledCount - 1) : 0f;
        for (var i = 0; i < ledCount; i++)
        {
            if (dev.LedDisabled is not null && i < dev.LedDisabled.Length && dev.LedDisabled[i])
            {
                continue;
            }
            var t = ledCount > 1 ? i * denom - 0.5f : 0f;
            var lx = rectX + rectW * 0.5f + t * rectW - frameCx;
            var sx = (frameCx + lx * cos - midY * sin) * kx;
            var sy = (frameCy + lx * sin + midY * cos) * ky;
            Set(i, fullFrame ? Box(canvas, sx - linHalfW, sy - linHalfH, sx + linHalfW, sy + linHalfH) : SpotRef(canvas, sx, sy, linHalfW, linHalfH));
        }
        return out_;
    }

    private static (byte, byte, byte) SpotRef(CanvasBuffer c, float sx, float sy, float cellHalfW, float cellHalfH)
    {
        var spotHalf = Math.Max(0.5f, c.Width * Spot * 0.5f);
        var hx = Math.Min(cellHalfW, spotHalf);
        var hy = Math.Min(cellHalfH, spotHalf);
        var cx = MathF.Floor(sx) + 0.5f;
        var cy = MathF.Floor(sy) + 0.5f;
        var ix0 = Math.Clamp((int)MathF.Floor(cx - hx), 0, c.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(cy - hy), 0, c.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(cx + hx) - 1, ix0, c.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(cy + hy) - 1, iy0, c.Height - 1);
        return Mean(c, ix0, iy0, ix1, iy1);
    }

    private static (byte, byte, byte) Box(CanvasBuffer c, float x0, float y0, float x1, float y1)
    {
        var ix0 = Math.Clamp((int)MathF.Floor(x0), 0, c.Width - 1);
        var iy0 = Math.Clamp((int)MathF.Floor(y0), 0, c.Height - 1);
        var ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, c.Width - 1);
        var iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, c.Height - 1);
        return Mean(c, ix0, iy0, ix1, iy1);
    }

    private static (byte, byte, byte) Mean(CanvasBuffer c, int ix0, int iy0, int ix1, int iy1)
    {
        long n = 0, r = 0, g = 0, b = 0;
        for (var y = iy0; y <= iy1; y++)
        {
            for (var x = ix0; x <= ix1; x++)
            {
                var (pr, pg, pb) = c.GetPixel(x, y);
                r += pr;
                g += pg;
                b += pb;
                n++;
            }
        }
        return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sampler_matches_the_reference_for_random_devices(bool fullFrame)
    {
        var rng = new Random(fullFrame ? 11 : 7);
        using var engine = new LightingEngine { FullFrameSampling = fullFrame };
        for (var n = 0; n < 300; n++)
        {
            var dev = RandomDevice(rng, n);
            var canvas = Noise(n);
            engine.SampleForBench(new[] { dev }, canvas);
            dev.Publish();
            Assert.Equal(Reference(dev, canvas, fullFrame), dev.LedBytes.ToArray());
        }
    }

    [Fact]
    public void Tap_set_alone_reproduces_the_canvas_sample()
    {
        var rng = new Random(23);
        using var engine = new LightingEngine();
        for (var n = 0; n < 300; n++)
        {
            var dev = RandomDevice(rng, n);
            var canvas = Noise(1000 + n);
            var taps = new LedTapSet(160, 90);
            Assert.True(taps.AddDevice(dev, 160, 90));
            // Poison everything the device does not read: a missing tap would change the sample.
            var sparse = new CanvasBuffer(160, 90);
            sparse.Fill(255, 0, 255);
            for (var t = 0; t < taps.Count; t++)
            {
                var (r, g, b) = canvas.GetPixel(taps.X[t], taps.Y[t]);
                sparse.SetPixel(taps.X[t], taps.Y[t], r, g, b);
            }
            engine.SampleForBench(new[] { dev }, canvas);
            dev.Publish();
            var want = dev.LedBytes.ToArray();
            engine.SampleForBench(new[] { dev }, sparse);
            dev.Publish();
            Assert.Equal(want, dev.LedBytes.ToArray());
        }
    }

    [Fact]
    public void Tap_set_refuses_a_device_with_an_editor_preview()
    {
        var dev = new DeviceFrame(0, "p", 4) { PreviewLedCount = 2 };
        Assert.False(new LedTapSet(160, 90).AddDevice(dev, 160, 90));
        dev = new DeviceFrame(0, "p", 4) { PreviewLayout = new[] { new PreviewLedPosition { Index = 0, U = 0.5f, V = 0.5f } } };
        Assert.False(new LedTapSet(160, 90).AddDevice(dev, 160, 90));
    }

    [Fact]
    public void Tap_set_lists_each_pixel_once()
    {
        var set = new LedTapSet(160, 90);
        set.Add(3, 4);
        set.Add(3, 4);
        set.Add(4, 4);
        Assert.Equal(2, set.Count);
    }
}
