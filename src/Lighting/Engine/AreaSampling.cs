using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

/// <summary>sRGB to linear light and back, as lookup tables.</summary>
internal static class AreaLight
{
    public const int LinMax = 65535;
    public static readonly ushort[] ToLinear = BuildToLinear();
    public static readonly byte[] ToSrgb = BuildToSrgb();

    private static ushort[] BuildToLinear()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            var lin = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            t[i] = (ushort)Math.Round(lin * LinMax);
        }
        return t;
    }

    private static byte[] BuildToSrgb()
    {
        var t = new byte[LinMax + 1];
        for (var i = 0; i <= LinMax; i++)
        {
            var lin = i / (double)LinMax;
            var c = lin <= 0.0031308 ? lin * 12.92 : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;
            t[i] = (byte)Math.Clamp((int)Math.Round(c * 255.0), 0, 255);
        }
        return t;
    }
}

/// <summary>
/// Nearest-LED cells (a Voronoi partition) of a device frame. Every point of the frame belongs to its nearest enabled LED, with
/// distance measured in the frame's aspect space (u scaled by the frame width, v by its height), so the cells cover the frame
/// completely and never overlap.
/// </summary>
internal static class AreaCells
{
    /// <summary>The LED positions of [lo, hi) in the frame's normalized space and whether each is enabled; no usable UVs lay the LEDs on a line at v one half.</summary>
    public static void Positions(float[]? u, float[]? v, bool[]? disabled, int ledCount, int lo, int hi, out float[] pu, out float[] pv, out bool[] on)
    {
        var count = Math.Max(0, hi - lo);
        var hasUv = u is not null && v is not null && u.Length == ledCount && v.Length == ledCount;
        pu = new float[count];
        pv = new float[count];
        on = new bool[count];
        for (var k = 0; k < count; k++)
        {
            var i = lo + k;
            on[k] = !(disabled is not null && i < disabled.Length && disabled[i]);
            if (hasUv)
            {
                pu[k] = u![i];
                pv[k] = v![i];
            }
            else
            {
                pu[k] = count > 1 ? (float)k / (count - 1) : 0.5f;
                pv[k] = 0.5f;
            }
        }
    }

    /// <summary>
    /// One polygon per LED of [lo, hi): the unit frame clipped by the perpendicular bisector toward every other enabled LED,
    /// vertices <c>[u,v,u,v,...]</c> in order in the normalized unrotated frame space. A disabled LED gets an empty array.
    /// </summary>
    public static float[][] Compute(float[]? u, float[]? v, bool[]? disabled, int ledCount, int lo, int hi, float aspectW, float aspectH)
    {
        Positions(u, v, disabled, ledCount, lo, hi, out var pu, out var pv, out var on);
        var count = pu.Length;
        var cells = new float[count][];
        var w = aspectW > 1e-6f ? (double)aspectW : 1.0;
        var h = aspectH > 1e-6f ? (double)aspectH : 1.0;
        var poly = new List<(double X, double Y)>(16);
        var next = new List<(double X, double Y)>(16);
        var order = new List<(int B, double D)>(count);
        for (var a = 0; a < count; a++)
        {
            if (!on[a])
            {
                cells[a] = Array.Empty<float>();
                continue;
            }
            poly.Clear();
            poly.Add((0, 0));
            poly.Add((1, 0));
            poly.Add((1, 1));
            poly.Add((0, 1));
            double ax = pu[a] * w, ay = pv[a] * h;
            // Nearest first: once a LED is farther than twice the cell's reach its bisector cannot touch the cell, nor can any later one.
            order.Clear();
            for (var b = 0; b < count; b++)
            {
                if (b != a && on[b])
                {
                    {
                        double ex = pu[b] * w - ax, ey = pv[b] * h - ay;
                        order.Add((b, ex * ex + ey * ey));
                    }
                }
            }
            order.Sort((p, q) => p.D.CompareTo(q.D));
            var reach2 = double.MaxValue;
            foreach (var (b, d2) in order)
            {
                if (poly.Count == 0 || d2 > reach2)
                {
                    break;
                }
                double bx = pu[b] * w, by = pv[b] * h;
                // keep points p (aspect space) with (b - a) . p <= (|b|^2 - |a|^2) / 2; in normalized x,y that is nx*x + ny*y <= c
                double nx = (bx - ax) * w, ny = (by - ay) * h;
                var c = (bx * bx + by * by - ax * ax - ay * ay) * 0.5;
                if (nx == 0 && ny == 0)
                {
                    // coincident LEDs: the lower index keeps the cell
                    if (b < a)
                    {
                        poly.Clear();
                    }
                    continue;
                }
                Clip(poly, next, nx, ny, c);
                (poly, next) = (next, poly);
                var far = 0.0;
                foreach (var q in poly)
                {
                    {
                        double ex = q.X * w - ax, ey = q.Y * h - ay;
                        far = Math.Max(far, ex * ex + ey * ey);
                    }
                }
                reach2 = 4 * far;
            }
            var arr = new float[poly.Count * 2];
            for (var k = 0; k < poly.Count; k++)
            {
                arr[k * 2] = (float)Math.Clamp(poly[k].X, 0, 1);
                arr[k * 2 + 1] = (float)Math.Clamp(poly[k].Y, 0, 1);
            }
            cells[a] = arr;
        }
        return cells;
    }

    private static void Clip(List<(double X, double Y)> src, List<(double X, double Y)> dst, double nx, double ny, double c)
    {
        dst.Clear();
        var n = src.Count;
        for (var i = 0; i < n; i++)
        {
            var p = src[i];
            var q = src[(i + 1) % n];
            var dp = nx * p.X + ny * p.Y - c;
            var dq = nx * q.X + ny * q.Y - c;
            var pin = dp <= 0;
            var qin = dq <= 0;
            if (pin)
            {
                dst.Add(p);
            }
            if (pin != qin)
            {
                var t = dp / (dp - dq);
                dst.Add((p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t));
            }
        }
    }
}

/// <summary>
/// Which canvas pixels each LED owns, as one flat list grouped per LED, so a frame costs one pass over the owned pixels:
/// <see cref="Start"/>[i] to <see cref="Start"/>[i + 1] index <see cref="Pix"/>, whose entries are byte offsets into the RGB canvas.
/// Built when a layout, toggle, aspect or camera changes, never per frame.
/// </summary>
internal sealed class AreaOwnerMap
{
    public int Cw { get; private set; }
    public int Ch { get; private set; }
    public int[] Start { get; private set; } = Array.Empty<int>();
    public int[] Pix { get; private set; } = Array.Empty<int>();
    public int LedCount => Math.Max(0, Start.Length - 1);

    /// <summary>Linear-light mean, back in sRGB, of an LED's owned pixels; black for an LED that owns none.</summary>
    public void Sample(ReadOnlySpan<byte> px, int led, out byte r, out byte g, out byte b)
    {
        int s = Start[led], e = Start[led + 1];
        if (e <= s)
        {
            r = g = b = 0;
            return;
        }
        var lin = AreaLight.ToLinear;
        var pix = Pix;
        long sr = 0, sg = 0, sb = 0;
        for (var k = s; k < e; k++)
        {
            var o = pix[k];
            sr += lin[px[o]];
            sg += lin[px[o + 1]];
            sb += lin[px[o + 2]];
        }
        long n = e - s, half = n / 2;
        var srgb = AreaLight.ToSrgb;
        r = srgb[(int)Math.Min((sr + half) / n, AreaLight.LinMax)];
        g = srgb[(int)Math.Min((sg + half) / n, AreaLight.LinMax)];
        b = srgb[(int)Math.Min((sb + half) / n, AreaLight.LinMax)];
    }

    private static AreaOwnerMap Pack(int cw, int ch, int ledCount, int[] owner, int[] fallbackPix)
    {
        var start = new int[ledCount + 1];
        for (var i = 0; i < owner.Length; i++)
        {
            if (owner[i] >= 0)
            {
                start[owner[i] + 1]++;
            }
        }
        for (var l = 0; l < ledCount; l++)
        {
            if (start[l + 1] == 0 && fallbackPix[l] >= 0)
            {
                start[l + 1] = 1;
            }
        }
        for (var l = 0; l < ledCount; l++)
        {
            start[l + 1] += start[l];
        }
        var pix = new int[start[ledCount]];
        var fill = new int[ledCount];
        for (var i = 0; i < owner.Length; i++)
        {
            var l = owner[i];
            if (l >= 0)
            {
                pix[start[l] + fill[l]++] = i * 3;
            }
        }
        for (var l = 0; l < ledCount; l++)
        {
            if (start[l + 1] - start[l] == 1 && fill[l] == 0 && fallbackPix[l] >= 0)
            {
                pix[start[l]] = fallbackPix[l] * 3;
            }
        }
        return new AreaOwnerMap { Cw = cw, Ch = ch, Start = start, Pix = pix };
    }

    /// <summary>
    /// Owner map of a flat frame. Each pixel centre is taken back through the canvas scale and the frame's turn into the frame's
    /// normalized space; inside the rect it belongs to the nearest enabled LED in aspect space (lowest index on a tie). An LED
    /// owning no pixel keeps the pixel at its own position (<paramref name="geo"/>).
    /// </summary>
    public static AreaOwnerMap BuildFrame(
        float[] pu, float[] pv, bool[] on, LedSpotGeometry geo, float rectX, float rectY, float rectW, float rectH,
        float frameCx, float frameCy, float cos, float sin, int cw, int ch)
    {
        var count = pu.Length;
        var kx = cw / LightingEngine.CanvasUnitsW;
        var ky = ch / LightingEngine.CanvasUnitsH;
        // enabled LEDs only, compacted, in aspect space
        var idx = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (on[i]) idx.Add(i);
        }
        var m = idx.Count;
        var ax = new float[m];
        var ay = new float[m];
        for (var j = 0; j < m; j++)
        {
            ax[j] = pu[idx[j]] * rectW;
            ay[j] = pv[idx[j]] * rectH;
        }
        var owner = new int[cw * ch];
        Array.Fill(owner, -1);
        // pixel rows/cols that can fall inside the turned rect
        var minX = float.MaxValue; var maxX = float.MinValue; var minY = float.MaxValue; var maxY = float.MinValue;
        for (var c = 0; c < 4; c++)
        {
            var lx = rectX + ((c & 1) == 0 ? 0 : rectW) - frameCx;
            var ly = rectY + ((c & 2) == 0 ? 0 : rectH) - frameCy;
            var sx = (frameCx + lx * cos - ly * sin) * kx;
            var sy = (frameCy + lx * sin + ly * cos) * ky;
            minX = MathF.Min(minX, sx); maxX = MathF.Max(maxX, sx);
            minY = MathF.Min(minY, sy); maxY = MathF.Max(maxY, sy);
        }
        var x0 = Math.Clamp((int)MathF.Floor(minX) - 1, 0, cw - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(maxX) + 1, 0, cw - 1);
        var y0 = Math.Clamp((int)MathF.Floor(minY) - 1, 0, ch - 1);
        var y1 = Math.Clamp((int)MathF.Ceiling(maxY) + 1, 0, ch - 1);
        if (m > 0 && rectW > 1e-6f && rectH > 1e-6f)
        {
            for (var py = y0; py <= y1; py++)
            {
                for (var px = x0; px <= x1; px++)
                {
                    var dx = (px + 0.5f) / kx - frameCx;
                    var dy = (py + 0.5f) / ky - frameCy;
                    var lx = dx * cos + dy * sin;
                    var ly = -dx * sin + dy * cos;
                    var u = (frameCx + lx - rectX) / rectW;
                    var v = (frameCy + ly - rectY) / rectH;
                    if (u < 0f || u > 1f || v < 0f || v > 1f)
                    {
                        continue;
                    }
                    var fx = u * rectW;
                    var fy = v * rectH;
                    var best = 0;
                    var bestD = float.MaxValue;
                    for (var j = 0; j < m; j++)
                    {
                        var ex = ax[j] - fx;
                        var ey = ay[j] - fy;
                        var d = ex * ex + ey * ey;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = j;
                        }
                    }
                    owner[py * cw + px] = idx[best];
                }
            }
        }
        var fallback = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (!on[i])
            {
                fallback[i] = -1;
                continue;
            }
            geo.Center(i, out var sx, out var sy);
            fallback[i] = Math.Clamp((int)MathF.Floor(sy), 0, ch - 1) * cw + Math.Clamp((int)MathF.Floor(sx), 0, cw - 1);
        }
        return Pack(cw, ch, count, owner, fallback);
    }

    /// <summary>
    /// Owner map of a surface in the 3D scene: the pixels inside the surface's projected quad belong to the nearest projected LED
    /// centre in canvas pixels. This is a screen-space approximation; it ignores perspective foreshortening along the surface.
    /// An LED that does not project owns nothing; one owning no pixel keeps the pixel it projects to.
    /// </summary>
    public static AreaOwnerMap BuildScene(float[] sx, float[] sy, bool[] ok, (float X, float Y)[] quad, bool quadOk, int cw, int ch)
    {
        var count = sx.Length;
        var owner = new int[cw * ch];
        Array.Fill(owner, -1);
        var idx = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (ok[i]) idx.Add(i);
        }
        var m = idx.Count;
        if (quadOk && m > 0)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var q in quad)
            {
                minX = MathF.Min(minX, q.X); maxX = MathF.Max(maxX, q.X);
                minY = MathF.Min(minY, q.Y); maxY = MathF.Max(maxY, q.Y);
            }
            var x0 = Math.Clamp((int)MathF.Floor(minX), 0, cw - 1);
            var x1 = Math.Clamp((int)MathF.Ceiling(maxX), 0, cw - 1);
            var y0 = Math.Clamp((int)MathF.Floor(minY), 0, ch - 1);
            var y1 = Math.Clamp((int)MathF.Ceiling(maxY), 0, ch - 1);
            // quad corners come in (u,v) order 00,10,01,11; walk them as a loop 00,10,11,01
            var loop = new[] { quad[0], quad[1], quad[3], quad[2] };
            for (var py = y0; py <= y1; py++)
            {
                for (var px = x0; px <= x1; px++)
                {
                    float cx = px + 0.5f, cy = py + 0.5f;
                    if (!Inside(loop, cx, cy))
                    {
                        continue;
                    }
                    var best = 0;
                    var bestD = float.MaxValue;
                    for (var j = 0; j < m; j++)
                    {
                        var ex = sx[idx[j]] - cx;
                        var ey = sy[idx[j]] - cy;
                        var d = ex * ex + ey * ey;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = j;
                        }
                    }
                    owner[py * cw + px] = idx[best];
                }
            }
        }
        var fallback = new int[count];
        for (var i = 0; i < count; i++)
        {
            fallback[i] = ok[i]
                ? Math.Clamp((int)MathF.Floor(sy[i]), 0, ch - 1) * cw + Math.Clamp((int)MathF.Floor(sx[i]), 0, cw - 1)
                : -1;
        }
        return Pack(cw, ch, count, owner, fallback);
    }

    private static bool Inside((float X, float Y)[] p, float x, float y)
    {
        var pos = false;
        var neg = false;
        for (var i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            var cr = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cr > 0) pos = true;
            else if (cr < 0) neg = true;
        }
        return !(pos && neg);
    }
}
