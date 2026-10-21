using System;

namespace Nexus.Service.Lighting.Engine;

/// <summary>
/// The box each LED reads under Area sampling, in the frame's normalized unrotated space (the space of
/// <see cref="DeviceFrame.LedU"/> and <see cref="DeviceFrame.LedV"/>). Single source for the card DTO and every sampler.
/// </summary>
internal static class AreaBoxes
{
    /// <summary>
    /// <c>[u0,v0,u1,v1]</c> per LED of the half-open range [<paramref name="lo"/>, <paramref name="hi"/>). Each enabled LED's edge
    /// toward a direction is the midpoint to its nearest enabled neighbor in that direction's cone (|cross| &lt;= along), or the frame
    /// edge when it has none. A disabled LED gets zeros. Without usable UVs the LEDs lie on a line at v of one half.
    /// </summary>
    public static float[] Compute(float[]? u, float[]? v, bool[]? disabled, int ledCount, int lo, int hi)
    {
        var count = Math.Max(0, hi - lo);
        var boxes = new float[count * 4];
        if (count == 0)
        {
            return boxes;
        }
        var hasUv = u is not null && v is not null && u.Length == ledCount && v.Length == ledCount;
        var pu = new float[count];
        var pv = new float[count];
        var on = new bool[count];
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
        for (var a = 0; a < count; a++)
        {
            if (!on[a])
            {
                continue;
            }
            float x0 = 0f, x1 = 1f, y0 = 0f, y1 = 1f;
            float dR = float.MaxValue, dL = float.MaxValue, dD = float.MaxValue, dU = float.MaxValue;
            for (var b = 0; b < count; b++)
            {
                if (b == a || !on[b])
                {
                    continue;
                }
                var dx = pu[b] - pu[a];
                var dy = pv[b] - pv[a];
                var d2 = dx * dx + dy * dy;
                var ax = MathF.Abs(dx);
                var ay = MathF.Abs(dy);
                if (dx > 0f && ay <= dx && d2 < dR)
                {
                    dR = d2;
                    x1 = (pu[a] + pu[b]) * 0.5f;
                }
                if (dx < 0f && ay <= ax && d2 < dL)
                {
                    dL = d2;
                    x0 = (pu[a] + pu[b]) * 0.5f;
                }
                if (dy > 0f && ax <= dy && d2 < dD)
                {
                    dD = d2;
                    y1 = (pv[a] + pv[b]) * 0.5f;
                }
                if (dy < 0f && ax <= ay && d2 < dU)
                {
                    dU = d2;
                    y0 = (pv[a] + pv[b]) * 0.5f;
                }
            }
            var o = a * 4;
            boxes[o] = Math.Clamp(x0, 0f, 1f);
            boxes[o + 1] = Math.Clamp(y0, 0f, 1f);
            boxes[o + 2] = Math.Clamp(x1, 0f, 1f);
            boxes[o + 3] = Math.Clamp(y1, 0f, 1f);
        }
        return boxes;
    }
}

/// <summary>Summed-area table of a canvas in linear light, so any box mean costs four reads whatever its size.</summary>
internal sealed class AreaSat
{
    private const int LinMax = 65535;
    private static readonly ushort[] ToLinear = BuildToLinear();
    private static readonly byte[] ToSrgb = BuildToSrgb();

    private int[] _sum = Array.Empty<int>();
    private int _w;
    private int _h;

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

    /// <summary>Rebuilds the table from <paramref name="canvas"/>'s current pixels.</summary>
    public void Build(CanvasBuffer canvas)
    {
        var w = canvas.Width;
        var h = canvas.Height;
        var stride = w + 1;
        var need = stride * (h + 1) * 3;
        if (_sum.Length != need)
        {
            _sum = new int[need];
        }
        _w = w;
        _h = h;
        var px = canvas.Pixels;
        var sum = _sum;
        for (var y = 0; y < h; y++)
        {
            int r = 0, g = 0, b = 0;
            var row = (y + 1) * stride;
            var above = y * stride;
            var src = y * w * 3;
            for (var x = 0; x < w; x++)
            {
                r += ToLinear[px[src]];
                g += ToLinear[px[src + 1]];
                b += ToLinear[px[src + 2]];
                src += 3;
                var o = (row + x + 1) * 3;
                var a = (above + x + 1) * 3;
                sum[o] = sum[a] + r;
                sum[o + 1] = sum[a + 1] + g;
                sum[o + 2] = sum[a + 2] + b;
            }
        }
    }

    /// <summary>Linear-light mean, back in sRGB, of the inclusive pixel box (clamped to the canvas, at least one pixel).</summary>
    public (byte r, byte g, byte b) Mean(int ix0, int iy0, int ix1, int iy1)
    {
        ix0 = Math.Clamp(ix0, 0, _w - 1);
        iy0 = Math.Clamp(iy0, 0, _h - 1);
        ix1 = Math.Clamp(ix1, ix0, _w - 1);
        iy1 = Math.Clamp(iy1, iy0, _h - 1);
        var stride = _w + 1;
        var tl = (iy0 * stride + ix0) * 3;
        var tr = (iy0 * stride + ix1 + 1) * 3;
        var bl = ((iy1 + 1) * stride + ix0) * 3;
        var br = ((iy1 + 1) * stride + ix1 + 1) * 3;
        long n = (long)(ix1 - ix0 + 1) * (iy1 - iy0 + 1);
        var half = n / 2;
        var s = _sum;
        var r = ((long)s[br] - s[bl] - s[tr] + s[tl] + half) / n;
        var g = ((long)s[br + 1] - s[bl + 1] - s[tr + 1] + s[tl + 1] + half) / n;
        var b = ((long)s[br + 2] - s[bl + 2] - s[tr + 2] + s[tl + 2] + half) / n;
        return (ToSrgb[(int)Math.Min(r, LinMax)], ToSrgb[(int)Math.Min(g, LinMax)], ToSrgb[(int)Math.Min(b, LinMax)]);
    }

    /// <summary>Inclusive pixel box covering a canvas-pixel rect, as the full-frame box read rounds it.</summary>
    public static void PixelBox(float x0, float y0, float x1, float y1, int cw, int ch, out int ix0, out int iy0, out int ix1, out int iy1)
    {
        ix0 = Math.Clamp((int)MathF.Floor(x0), 0, cw - 1);
        iy0 = Math.Clamp((int)MathF.Floor(y0), 0, ch - 1);
        ix1 = Math.Clamp((int)MathF.Ceiling(x1) - 1, ix0, cw - 1);
        iy1 = Math.Clamp((int)MathF.Ceiling(y1) - 1, iy0, ch - 1);
    }
}
