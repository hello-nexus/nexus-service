using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

/// <summary>Where one device's LEDs land on the canvas and how wide each LED's read is; the single source for the canvas sampler and the look-ahead tap builder.</summary>
internal readonly struct LedSpotGeometry
{
    private readonly bool _uv;
    private readonly float[]? _u;
    private readonly float[]? _v;
    private readonly float _rectX;
    private readonly float _rectY;
    private readonly float _rectW;
    private readonly float _rectH;
    private readonly float _frameCx;
    private readonly float _frameCy;
    private readonly float _kx;
    private readonly float _ky;
    private readonly float _cos;
    private readonly float _sin;
    private readonly float _midY;
    private readonly float _denom;
    private readonly int _ledCount;

    public float HalfW { get; }
    public float HalfH { get; }

    private LedSpotGeometry(
        bool uv, float[]? u, float[]? v, float rectX, float rectY, float rectW, float rectH, float frameCx, float frameCy,
        float kx, float ky, float cos, float sin, float midY, float denom, int ledCount, float halfW, float halfH)
    {
        _uv = uv;
        _u = u;
        _v = v;
        _rectX = rectX;
        _rectY = rectY;
        _rectW = rectW;
        _rectH = rectH;
        _frameCx = frameCx;
        _frameCy = frameCy;
        _kx = kx;
        _ky = ky;
        _cos = cos;
        _sin = sin;
        _midY = midY;
        _denom = denom;
        _ledCount = ledCount;
        HalfW = halfW;
        HalfH = halfH;
    }

    /// <summary>The canvas position (pixels, fractional) of LED <paramref name="i"/>.</summary>
    public void Center(int i, out float sx, out float sy)
    {
        if (_uv)
        {
            var lx = _rectX + _u![i] * _rectW - _frameCx;
            var ly = _rectY + _v![i] * _rectH - _frameCy;
            sx = (_frameCx + lx * _cos - ly * _sin) * _kx;
            sy = (_frameCy + lx * _sin + ly * _cos) * _ky;
            return;
        }
        var t = _ledCount > 1 ? i * _denom - 0.5f : 0f;
        var llx = _rectX + _rectW * 0.5f + t * _rectW - _frameCx;
        sx = (_frameCx + llx * _cos - _midY * _sin) * _kx;
        sy = (_frameCy + llx * _sin + _midY * _cos) * _ky;
    }

    /// <summary>The canvas-pixel axis-aligned bounding rect of a box given in the frame's normalized unrotated space, laid out like LED positions are (frame rect, rotation, canvas scale); at a rotation that is not a right-angle multiple it reads the rect's bounding box, slightly more than the turned box.</summary>
    public void MapBox(float u0, float v0, float u1, float v1, out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = float.MaxValue;
        x1 = y1 = float.MinValue;
        for (var c = 0; c < 4; c++)
        {
            var lx = _rectX + ((c & 1) == 0 ? u0 : u1) * _rectW - _frameCx;
            var ly = _rectY + ((c & 2) == 0 ? v0 : v1) * _rectH - _frameCy;
            var sx = (_frameCx + lx * _cos - ly * _sin) * _kx;
            var sy = (_frameCy + lx * _sin + ly * _cos) * _ky;
            x0 = MathF.Min(x0, sx);
            x1 = MathF.Max(x1, sx);
            y0 = MathF.Min(y0, sy);
            y1 = MathF.Max(y1, sy);
        }
    }

    /// <summary>Geometry for a device laid out in canvas units. <paramref name="fullFrame"/> is the sweep read, which ignores orientation and widens to the whole canvas.</summary>
    public static LedSpotGeometry Create(
        float rectX, float rectY, float rectW, float rectH, float frameCx, float frameCy, int rotation,
        int cw, int ch, int ledCount, float[]? ledU, float[]? ledV, bool fullFrame, float fullFrameStripBreadth)
    {
        const float CW = LightingEngine.CanvasUnitsW, CH = LightingEngine.CanvasUnitsH;
        var kx = cw / CW;
        var ky = ch / CH;
        var rad = fullFrame ? 0f : (((rotation % 360) + 360) % 360) * (MathF.PI / 180f);
        var cos = MathF.Cos(rad);
        var sin = MathF.Sin(rad);
        var absCos = MathF.Abs(cos);
        var absSin = MathF.Abs(sin);
        if (ledU is not null && ledV is not null && ledU.Length == ledCount && ledV.Length == ledCount)
        {
            var aspect = rectH > 0.001f ? rectW / rectH : 1f;
            var cols = Math.Max(1, (int)MathF.Round(MathF.Sqrt(ledCount * aspect)));
            var rows = Math.Max(1, (ledCount + cols - 1) / cols);
            var cellHalfW = Math.Max(1f / kx, rectW / cols) * 0.5f;
            var cellHalfH = Math.Max(1f / ky, rectH / rows) * 0.5f;
            if (fullFrame)
            {
                cellHalfW = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
                cellHalfH = Math.Max(1f / ky, rectH * fullFrameStripBreadth) * 0.5f;
            }
            return new LedSpotGeometry(
                true, ledU, ledV, rectX, rectY, rectW, rectH, frameCx, frameCy, kx, ky, cos, sin, 0f, 0f, ledCount,
                (cellHalfW * absCos + cellHalfH * absSin) * kx, (cellHalfW * absSin + cellHalfH * absCos) * ky);
        }
        var pitchHalf = Math.Max(1f / kx, rectW / ledCount) * 0.5f;
        var breadth = fullFrame ? rectH * fullFrameStripBreadth : rectH;
        var breadthHalf = Math.Max(1f / ky, breadth) * 0.5f;
        return new LedSpotGeometry(
            false, null, null, rectX, rectY, rectW, rectH, frameCx, frameCy, kx, ky, cos, sin,
            rectY + rectH * 0.5f - frameCy, ledCount > 1 ? 1f / (ledCount - 1) : 0f, ledCount,
            (pitchHalf * absCos + breadthHalf * absSin) * kx, (pitchHalf * absSin + breadthHalf * absCos) * ky);
    }

    /// <summary>The inclusive pixel box an LED's spot read covers: a spot of <paramref name="spotSideOfWidth"/> of the canvas width centered on the pixel holding the position, shrunk to the LED's own cell, clamped to the canvas.</summary>
    public static void SpotRect(
        float sx, float sy, float cellHalfW, float cellHalfH, int cw, int ch, float spotSideOfWidth,
        out int ix0, out int iy0, out int ix1, out int iy1)
    {
        var spotHalf = Math.Max(0.5f, cw * spotSideOfWidth * 0.5f);
        var hx = Math.Min(cellHalfW, spotHalf);
        var hy = Math.Min(cellHalfH, spotHalf);
        var cx = MathF.Floor(sx) + 0.5f;
        var cy = MathF.Floor(sy) + 0.5f;
        ix0 = Math.Clamp((int)MathF.Floor(cx - hx), 0, cw - 1);
        iy0 = Math.Clamp((int)MathF.Floor(cy - hy), 0, ch - 1);
        ix1 = Math.Clamp((int)MathF.Ceiling(cx + hx) - 1, ix0, cw - 1);
        iy1 = Math.Clamp((int)MathF.Ceiling(cy + hy) - 1, iy0, ch - 1);
    }
}
