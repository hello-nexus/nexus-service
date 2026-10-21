using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine
{
    private sealed class AreaEntry
    {
        public int Count;
        public int Lo;
        public int Hi;
        public float[]? U;
        public float[]? V;
        public bool[]? Disabled;
        public float[] Boxes = Array.Empty<float>();
    }

    // Card ids whose LEDs read the mean of a box instead of a spot. Replaced wholesale, never mutated.
    private volatile HashSet<string>? _areaIds;
    // Boxes per (card, run), rebuilt only when the layout content changes. Shared by the loop, the look-ahead builder and the DTO.
    private readonly ConcurrentDictionary<(string Id, int Lo, int Hi), AreaEntry> _areaBoxes = new();
    // Loop thread only: one summed-area table per canvas read this pass, reused across passes.
    private readonly List<(CanvasBuffer Canvas, AreaSat Sat)> _areaSats = new();
    private int _areaSatCount;

    /// <summary>True when any card has Area sampling on.</summary>
    internal bool AnyAreaSampling => _areaIds is { Count: > 0 };

    /// <summary>Replaces the set of card ids sampled by area.</summary>
    public void SetAreaSampling(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        _areaIds = set.Count == 0 ? null : set;
        _areaBoxes.Clear();
    }

    internal bool IsAreaSampled(string id) => _areaIds?.Contains(id) == true;

    /// <summary>
    /// The boxes of LEDs [<paramref name="lo"/>, <paramref name="hi"/>) of a card, or null when the card is not area sampled.
    /// Cached: the layout arrays are swapped whole on change, and a transient editor layout arrives as fresh arrays of equal
    /// content each tick, so content is compared when the references differ.
    /// </summary>
    internal float[]? AreaBoxesFor(string id, int ledCount, float[]? u, float[]? v, bool[]? disabled, int lo, int hi, bool requireOn = true)
    {
        if (requireOn && !IsAreaSampled(id))
        {
            return null;
        }
        var key = (id, lo, hi);
        if (_areaBoxes.TryGetValue(key, out var e) && e.Count == ledCount
            && Same(e.U, u) && Same(e.V, v) && Same(e.Disabled, disabled))
        {
            return e.Boxes;
        }
        var fresh = new AreaEntry
        {
            Count = ledCount, Lo = lo, Hi = hi, U = u, V = v, Disabled = disabled,
            Boxes = AreaBoxes.Compute(u, v, disabled, ledCount, lo, hi),
        };
        _areaBoxes[key] = fresh;
        return fresh.Boxes;
    }

    private static bool Same(float[]? a, float[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    private static bool Same(bool[]? a, bool[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    /// <summary>Boxes of a whole frame, as the sampler resolves its layout (editor preview included).</summary>
    internal float[]? AreaBoxesFor(DeviceFrame dev, bool requireOn = true)
    {
        if (requireOn && !IsAreaSampled(dev.Id))
        {
            return null;
        }
        var count = dev.PreviewLedCount is { } pc ? Math.Min(pc, dev.LedCount) : dev.LedCount;
        ResolveLayout(dev, count, out var u, out var v, out var disabled);
        return AreaBoxesFor(dev.Id, count, u, v, disabled, 0, count, requireOn);
    }

    /// <summary>The wire boxes of a card sampled from <paramref name="frame"/> (null frame = never sampled): exactly 4 x <paramref name="ledCount"/> floats, cut or padded with empty boxes.</summary>
    internal float[] AreaBoxesForCard(DeviceFrame? frame, int ledCount)
    {
        var boxes = (frame is null ? null : AreaBoxesFor(frame, requireOn: false)) ?? Array.Empty<float>();
        if (boxes.Length != ledCount * 4)
        {
            Array.Resize(ref boxes, ledCount * 4);
        }
        return boxes;
    }

    /// <summary>The per-LED layout arrays the sampler reads: the editor draft when one is live, else the saved layout.</summary>
    private static void ResolveLayout(DeviceFrame dev, int ledCount, out float[]? u, out float[]? v, out bool[]? disabled)
    {
        var preview = dev.PreviewLayout;
        if (preview is not null && preview.Length > 0)
        {
            var pu = new float[ledCount];
            var pv = new float[ledCount];
            bool[]? pd = null;
            foreach (var pos in preview)
            {
                if (pos.Index < 0 || pos.Index >= ledCount)
                {
                    continue;
                }
                pu[pos.Index] = pos.U;
                pv[pos.Index] = pos.V;
                if (pos.Disabled)
                {
                    pd ??= new bool[ledCount];
                    pd[pos.Index] = true;
                }
            }
            u = pu;
            v = pv;
            disabled = pd;
            return;
        }
        u = dev.LedU;
        v = dev.LedV;
        disabled = dev.LedDisabled;
    }

    private AreaSat AreaSatFor(CanvasBuffer canvas)
    {
        for (var i = 0; i < _areaSatCount; i++)
        {
            if (ReferenceEquals(_areaSats[i].Canvas, canvas))
            {
                return _areaSats[i].Sat;
            }
        }
        AreaSat sat;
        if (_areaSatCount < _areaSats.Count)
        {
            sat = _areaSats[_areaSatCount].Sat;
            _areaSats[_areaSatCount] = (canvas, sat);
        }
        else
        {
            sat = new AreaSat();
            _areaSats.Add((canvas, sat));
        }
        _areaSatCount++;
        sat.Build(canvas);
        return sat;
    }

    /// <summary>Linear-light mean of one LED's box (frame space) on the current sample canvas.</summary>
    private (byte r, byte g, byte b) SampleAreaBox(in LedSpotGeometry geo, float[] boxes, int k)
    {
        var o = k * 4;
        geo.MapBox(boxes[o], boxes[o + 1], boxes[o + 2], boxes[o + 3], out var x0, out var y0, out var x1, out var y1);
        AreaSat.PixelBox(x0, y0, x1, y1, _sampleCanvas.Width, _sampleCanvas.Height, out var ix0, out var iy0, out var ix1, out var iy1);
        return AreaSatFor(_sampleCanvas).Mean(ix0, iy0, ix1, iy1);
    }

    /// <summary>Scene variant: the box's corners are laid on the LED's surface, projected through the camera, and the canvas rect they span is read.</summary>
    private (byte r, byte g, byte b) SampleSceneAreaBox(
        in Nexus.Service.Lighting.Scene.SceneCameraBasis cam, Nexus.Service.Lighting.Scene.SceneQuad quad, float[] boxes, int k,
        float u0, float uSpan, float v0, float vSpan, float kx, float ky)
    {
        var o = k * 4;
        // Black only when the box centre is off screen, as the spot read does; a corner that fails to project is ignored.
        var cu = (boxes[o] + boxes[o + 2]) * 0.5f;
        var cv = (boxes[o + 1] + boxes[o + 3]) * 0.5f;
        if (!cam.Project(quad.At(
                uSpan > 1e-6f ? Math.Clamp((cu - u0) / uSpan, 0f, 1f) : 0.5f,
                vSpan > 1e-6f ? Math.Clamp((cv - v0) / vSpan, 0f, 1f) : 0.5f), out var mx, out var my))
        {
            return (0, 0, 0);
        }
        float minX = mx, minY = my, maxX = mx, maxY = my;
        for (var c = 0; c < 4; c++)
        {
            var bu = boxes[o + ((c & 1) == 0 ? 0 : 2)];
            var bv = boxes[o + ((c & 2) == 0 ? 1 : 3)];
            var u = uSpan > 1e-6f ? Math.Clamp((bu - u0) / uSpan, 0f, 1f) : 0.5f;
            var v = vSpan > 1e-6f ? Math.Clamp((bv - v0) / vSpan, 0f, 1f) : 0.5f;
            if (!cam.Project(quad.At(u, v), out var px, out var py))
            {
                continue;
            }
            minX = MathF.Min(minX, px);
            maxX = MathF.Max(maxX, px);
            minY = MathF.Min(minY, py);
            maxY = MathF.Max(maxY, py);
        }
        AreaSat.PixelBox(minX * kx, minY * ky, maxX * kx, maxY * ky, _sampleCanvas.Width, _sampleCanvas.Height, out var ix0, out var iy0, out var ix1, out var iy1);
        return AreaSatFor(_sampleCanvas).Mean(ix0, iy0, ix1, iy1);
    }
}
