using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Lighting.Bench;

/// <summary>Pixels one device reads from the canvas, as flat tap lists in LED order.</summary>
internal sealed class LedTapLayout
{
    public int Leds { get; init; }
    public int[] TapStart { get; init; } = Array.Empty<int>();
    public int[] TapCount { get; init; } = Array.Empty<int>();

    /// <summary>Canvas column of each tap, top-left origin.</summary>
    public int[] TapX { get; init; } = Array.Empty<int>();

    /// <summary>Canvas row of each tap, top-left origin.</summary>
    public int[] TapY { get; init; } = Array.Empty<int>();

    public int TotalTaps => TapX.Length;
}

/// <summary>
/// Pure pieces of the LED-space probe: which canvas pixels an LED reads, how those reads reduce to
/// one color, how a fragment source is rewritten to evaluate at chosen pixel centers, and which
/// source constructs make an effect depend on neighboring pixels. Probe only; the live engine keeps
/// its own copy of the sampling rules.
/// </summary>
internal static class LedSpaceCore
{
    public const int StrimerColumns = 22;
    public const int StrimerRows = 6;

    /// <summary>The 24-pin cable's map: rows of columns, centered in each grid cell, rounded as the shipped data rounds them.</summary>
    public static (float[] U, float[] V) StrimerMap()
    {
        var n = StrimerColumns * StrimerRows;
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = (float)Math.Round((i % StrimerColumns + 0.5) / StrimerColumns, 5);
            v[i] = (float)Math.Round((i / StrimerColumns + 0.5) / StrimerRows, 5);
        }
        return (u, v);
    }

    /// <summary>The shader clock for a tick, as the render path pushes it to u_time.</summary>
    public static float UTime(double tickMs) => (float)(tickMs % 86_400_000 / 1000.0);

    /// <summary>Per-LED tap lists for one placement, from the same rule the canvas sampler and the look-ahead share.</summary>
    public static LedTapLayout BuildTaps(DeviceFrame dev, int cw, int ch)
    {
        var start = new int[dev.LedCount];
        var count = new int[dev.LedCount];
        var xs = new List<int>(dev.LedCount * 9);
        var ys = new List<int>(dev.LedCount * 9);
        var ok = LedTaps.VisitSpotRects(dev, cw, ch, (led, x0, y0, x1, y1) =>
        {
            start[led] = xs.Count;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
            count[led] = xs.Count - start[led];
        });
        if (!ok)
        {
            throw new ArgumentException("device carries an editor preview");
        }
        return new LedTapLayout { Leds = dev.LedCount, TapStart = start, TapCount = count, TapX = xs.ToArray(), TapY = ys.ToArray() };
    }

    /// <summary>One LED color per LED from per-tap RGB bytes: the integer mean of each LED's taps, black when it has none.</summary>
    public static void Resolve(LedTapLayout layout, ReadOnlySpan<byte> tapRgb, Span<byte> ledRgb)
    {
        for (var i = 0; i < layout.Leds; i++)
        {
            var n = layout.TapCount[i];
            if (n == 0)
            {
                ledRgb[i * 3] = 0;
                ledRgb[i * 3 + 1] = 0;
                ledRgb[i * 3 + 2] = 0;
                continue;
            }
            long r = 0, g = 0, b = 0;
            var o = layout.TapStart[i] * 3;
            for (var k = 0; k < n; k++)
            {
                r += tapRgb[o];
                g += tapRgb[o + 1];
                b += tapRgb[o + 2];
                o += 3;
            }
            ledRgb[i * 3] = (byte)(r / n);
            ledRgb[i * 3 + 1] = (byte)(g / n);
            ledRgb[i * 3 + 2] = (byte)(b / n);
        }
    }

    /// <summary>Expected tap bytes straight from a rendered canvas.</summary>
    public static void TapsFromCanvas(LedTapLayout layout, CanvasBuffer canvas, Span<byte> tapRgb)
    {
        var px = canvas.Pixels;
        for (var t = 0; t < layout.TotalTaps; t++)
        {
            var o = (layout.TapY[t] * canvas.Width + layout.TapX[t]) * 3;
            tapRgb[t * 3] = px[o];
            tapRgb[t * 3 + 1] = px[o + 1];
            tapRgb[t * 3 + 2] = px[o + 2];
        }
    }

    public static string? Rewrite(string source, int windowFrames) => LedWindowShader.Rewrite(source, windowFrames);

    public static List<string> CanvasDependencyFlags(string source) => LedWindowShader.CanvasDependencyFlags(source);
}
