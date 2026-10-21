using System;

namespace Nexus.Service.Lighting.Engine;

/// <summary>The unique canvas pixels a set of devices reads, in the order first seen.</summary>
internal sealed class LedTapSet
{
    private readonly int _w;
    private readonly int _h;
    private readonly bool[] _seen;
    private int[] _x = new int[1024];
    private int[] _y = new int[1024];

    public LedTapSet(int canvasW, int canvasH)
    {
        _w = canvasW;
        _h = canvasH;
        _seen = new bool[canvasW * canvasH];
    }

    public int Count { get; private set; }
    public ReadOnlySpan<int> X => _x.AsSpan(0, Count);
    public ReadOnlySpan<int> Y => _y.AsSpan(0, Count);

    public void Add(int x, int y)
    {
        var i = y * _w + x;
        if (_seen[i])
        {
            return;
        }
        _seen[i] = true;
        if (Count == _x.Length)
        {
            Array.Resize(ref _x, Count * 2);
            Array.Resize(ref _y, Count * 2);
        }
        _x[Count] = x;
        _y[Count] = y;
        Count++;
    }

    /// <summary>Adds every pixel the device's spot reads touch. False for a device whose layout the canvas sampler resolves through the editor preview, which this does not model.</summary>
    public bool AddDevice(DeviceFrame dev, int canvasW, int canvasH, float[]? areaBoxes = null) =>
        LedTaps.VisitSpotRects(dev, canvasW, canvasH, areaBoxes, (_, x0, y0, x1, y1) =>
        {
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    Add(x, y);
                }
            }
        });
}

internal static class LedTaps
{
    /// <summary>
    /// Calls <paramref name="visit"/> with each enabled LED's inclusive pixel box exactly as the canvas sampler's spot read computes it, for a
    /// device that is not full-frame, not stacked and not in the 3D scene. False when the device carries an editor preview.
    /// </summary>
    public static bool VisitSpotRects(DeviceFrame dev, int cw, int ch, Action<int, int, int, int, int> visit) =>
        VisitSpotRects(dev, cw, ch, null, visit);

    /// <summary>As above, but with <paramref name="areaBoxes"/> each LED's rect is its Area sampling box instead of its spot.</summary>
    public static bool VisitSpotRects(DeviceFrame dev, int cw, int ch, float[]? areaBoxes, Action<int, int, int, int, int> visit)
    {
        if (dev.PreviewLedCount is not null || (dev.PreviewLayout is { Length: > 0 }))
        {
            return false;
        }
        var ledCount = dev.LedCount;
        if (ledCount <= 0)
        {
            return true;
        }
        var frameCx = dev.X + dev.W * 0.5f;
        var frameCy = dev.Y + dev.H * 0.5f;
        var geo = LedSpotGeometry.Create(
            dev.X, dev.Y, dev.W, dev.H, frameCx, frameCy, dev.Rotation, cw, ch, ledCount, dev.LedU, dev.LedV, false,
            LightingEngine.FullFrameStripBreadth);
        var disabled = dev.LedDisabled;
        for (var i = 0; i < ledCount; i++)
        {
            if (disabled is not null && i < disabled.Length && disabled[i])
            {
                continue;
            }
            if (areaBoxes is not null)
            {
                geo.MapBox(areaBoxes[i * 4], areaBoxes[i * 4 + 1], areaBoxes[i * 4 + 2], areaBoxes[i * 4 + 3], out var bx0, out var by0, out var bx1, out var by1);
                AreaSat.PixelBox(bx0, by0, bx1, by1, cw, ch, out var ax0, out var ay0, out var ax1, out var ay1);
                visit(i, ax0, ay0, ax1, ay1);
                continue;
            }
            geo.Center(i, out var sx, out var sy);
            LedSpotGeometry.SpotRect(sx, sy, geo.HalfW, geo.HalfH, cw, ch, LightingEngine.SpotSideOfCanvasWidth, out var x0, out var y0, out var x1, out var y1);
            visit(i, x0, y0, x1, y1);
        }
        return true;
    }
}
