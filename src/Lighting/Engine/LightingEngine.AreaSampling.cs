using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine
{
    // What an owner map was built from; a lookup rebuilds it only when one of these changes.
    private sealed class AreaMapEntry
    {
        public int Count;
        public float[]? U;
        public float[]? V;
        public bool[]? Disabled;
        public float[] Geom = Array.Empty<float>();
        public object? Scene;
        public AreaOwnerMap Map = null!;
    }

    private sealed class AreaCellsEntry
    {
        public int Count;
        public float W;
        public float H;
        public float[]? U;
        public float[]? V;
        public bool[]? Disabled;
        public float[][] Cells = Array.Empty<float[]>();
    }

    // Card ids whose LEDs read the mean of their nearest-LED cell instead of a spot. Replaced wholesale, never mutated.
    private volatile HashSet<string>? _areaIds;
    private readonly ConcurrentDictionary<(string Id, int Lo, int Hi), AreaMapEntry> _areaMaps = new();
    private readonly ConcurrentDictionary<(string Id, int Lo, int Hi), AreaCellsEntry> _areaCells = new();

    /// <summary>True when any card has Area sampling on.</summary>
    internal bool AnyAreaSampling => _areaIds is { Count: > 0 };

    /// <summary>Replaces the set of card ids sampled by area.</summary>
    public void SetAreaSampling(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        _areaIds = set.Count == 0 ? null : set;
        _areaMaps.Clear();
        _areaCells.Clear();
    }

    internal bool IsAreaSampled(string id) => _areaIds?.Contains(id) == true;

    private static bool Same(float[]? a, float[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    private static bool Same(bool[]? a, bool[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    /// <summary>
    /// Owner map of a flat frame sampled from the given rect, or null when the card is not area sampled. Cached on the layout
    /// content and geometry: the layout arrays are swapped whole on change, and an editor draft arrives as fresh arrays of equal
    /// content each tick, so content is compared when the references differ.
    /// </summary>
    private AreaOwnerMap? AreaMapFor(
        string id, int ledCount, float[]? u, float[]? v, bool[]? disabled, float rectX, float rectY, float rectW, float rectH,
        float frameCx, float frameCy, int rotation, int cw, int ch, in LedSpotGeometry geo, bool fullFrame)
    {
        if (!IsAreaSampled(id))
        {
            return null;
        }
        var rad = fullFrame ? 0f : (((rotation % 360) + 360) % 360) * (MathF.PI / 180f);
        float[] geom = { rectX, rectY, rectW, rectH, frameCx, frameCy, rad, cw, ch };
        var key = (id, 0, ledCount);
        if (_areaMaps.TryGetValue(key, out var e) && e.Scene is null && e.Count == ledCount && e.Geom.AsSpan().SequenceEqual(geom)
            && Same(e.U, u) && Same(e.V, v) && Same(e.Disabled, disabled))
        {
            return e.Map;
        }
        AreaCells.Positions(u, v, disabled, ledCount, 0, ledCount, out var pu, out var pv, out var on);
        var map = AreaOwnerMap.BuildFrame(pu, pv, on, geo, rectX, rectY, rectW, rectH, frameCx, frameCy, MathF.Cos(rad), MathF.Sin(rad), cw, ch);
        _areaMaps[key] = new AreaMapEntry { Count = ledCount, U = u, V = v, Disabled = disabled, Geom = geom, Map = map };
        return map;
    }

    /// <summary>Owner map of a frame the way the look-ahead builder reads it (own rect, no slot, not full-frame, no editor draft).</summary>
    internal AreaOwnerMap? AreaMapFor(DeviceFrame dev, int cw, int ch)
    {
        if (!IsAreaSampled(dev.Id) || dev.LedCount <= 0)
        {
            return null;
        }
        var frameCx = dev.X + dev.W * 0.5f;
        var frameCy = dev.Y + dev.H * 0.5f;
        var geo = LedSpotGeometry.Create(
            dev.X, dev.Y, dev.W, dev.H, frameCx, frameCy, dev.Rotation, cw, ch, dev.LedCount, dev.LedU, dev.LedV, false, FullFrameStripBreadth);
        return AreaMapFor(dev.Id, dev.LedCount, dev.LedU, dev.LedV, dev.LedDisabled, dev.X, dev.Y, dev.W, dev.H, frameCx, frameCy, dev.Rotation, cw, ch, geo, false);
    }

    /// <summary>Owner map of one 3D-scene surface run; see <see cref="AreaOwnerMap.BuildScene"/>.</summary>
    private AreaOwnerMap? AreaSceneMapFor(
        string id, int ledCount, float[]? ledU, float[]? ledV, bool[]? disabled, int lo, int hi, object sceneRef,
        in Nexus.Service.Lighting.Scene.SceneCameraBasis cam, Nexus.Service.Lighting.Scene.SceneQuad quad,
        bool hasUv, float u0, float uSpan, float v0, float vSpan, float kx, float ky, int cw, int ch)
    {
        if (!IsAreaSampled(id))
        {
            return null;
        }
        float[] geom = { cw, ch, u0, uSpan, v0, vSpan };
        var key = (id, lo, hi);
        if (_areaMaps.TryGetValue(key, out var e) && ReferenceEquals(e.Scene, sceneRef) && e.Count == ledCount && e.Geom.AsSpan().SequenceEqual(geom)
            && Same(e.U, ledU) && Same(e.V, ledV) && Same(e.Disabled, disabled))
        {
            return e.Map;
        }
        var count = hi - lo;
        var sx = new float[count];
        var sy = new float[count];
        var ok = new bool[count];
        for (var k = 0; k < count; k++)
        {
            var i = lo + k;
            if (disabled is not null && i < disabled.Length && disabled[i])
            {
                continue;
            }
            float u, v;
            if (hasUv)
            {
                u = uSpan > 1e-6f ? (ledU![i] - u0) / uSpan : 0.5f;
                v = vSpan > 1e-6f ? (ledV![i] - v0) / vSpan : 0.5f;
            }
            else
            {
                u = count > 1 ? (float)k / (count - 1) : 0.5f;
                v = 0.5f;
            }
            if (cam.Project(quad.At(u, v), out var cx, out var cy))
            {
                sx[k] = cx * kx;
                sy[k] = cy * ky;
                ok[k] = true;
            }
        }
        var corners = new (float X, float Y)[4];
        var quadOk = true;
        for (var c = 0; c < 4; c++)
        {
            if (cam.Project(quad.At(c & 1, c >> 1), out var px, out var py))
            {
                corners[c] = (px * kx, py * ky);
            }
            else
            {
                quadOk = false;
            }
        }
        var map = AreaOwnerMap.BuildScene(sx, sy, ok, corners, quadOk, cw, ch);
        _areaMaps[key] = new AreaMapEntry { Count = ledCount, U = ledU, V = ledV, Disabled = disabled, Geom = geom, Scene = sceneRef, Map = map };
        return map;
    }

    /// <summary>
    /// The cell polygons of a card sampled from <paramref name="frame"/> (null frame = never sampled), exactly
    /// <paramref name="ledCount"/> entries: <c>[u,v,u,v,...]</c> per LED, empty for a disabled LED or one past the draft count.
    /// Aspect is the frame's own size.
    /// </summary>
    internal float[][] AreaCellsForCard(DeviceFrame? frame, int ledCount)
    {
        var cells = new float[ledCount][];
        float[][] src = Array.Empty<float[]>();
        if (frame is not null)
        {
            var count = frame.PreviewLedCount is { } pc ? Math.Min(pc, frame.LedCount) : frame.LedCount;
            ResolveLayout(frame, count, out var u, out var v, out var disabled);
            var key = (frame.Id, 0, count);
            if (_areaCells.TryGetValue(key, out var e) && e.Count == count && e.W == frame.W && e.H == frame.H
                && Same(e.U, u) && Same(e.V, v) && Same(e.Disabled, disabled))
            {
                src = e.Cells;
            }
            else
            {
                src = AreaCells.Compute(u, v, disabled, count, 0, count, frame.W, frame.H);
                _areaCells[key] = new AreaCellsEntry { Count = count, W = frame.W, H = frame.H, U = u, V = v, Disabled = disabled, Cells = src };
            }
        }
        for (var i = 0; i < ledCount; i++)
        {
            cells[i] = i < src.Length ? src[i] : Array.Empty<float>();
        }
        return cells;
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

    private (byte r, byte g, byte b) SampleAreaCell(AreaOwnerMap map, int led)
    {
        map.Sample(_sampleCanvas.Pixels, led, out var r, out var g, out var b);
        return (r, g, b);
    }
}
