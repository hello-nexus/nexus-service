using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine
{
    // The scalars an owner map depends on, compared field by field so a steady-state lookup allocates nothing.
    private readonly record struct AreaGeom(float A, float B, float C, float D, float E, float F, float G, float H, float I, int Mode);

    // What an owner map was built from; a lookup rebuilds it only when one of these changes.
    private sealed class AreaMapEntry
    {
        public int Count;
        public float[]? U;
        public float[]? V;
        public bool[]? Disabled;
        public AreaGeom Geom;
        public object? Scene;
        public AreaOwnerMap Map = null!;
    }

    private sealed class AreaCellsEntry
    {
        public int Count;
        public SamplingMode Mode;
        public float W;
        public float H;
        public float SlotX;
        public float SlotY;
        public float[]? U;
        public float[]? V;
        public bool[]? Disabled;
        public float[][] Cells = Array.Empty<float[]>();
    }

    // Per-card sampling mode, only for cards not on the plain spot read. Replaced wholesale, never mutated.
    private volatile Dictionary<string, SamplingMode>? _areaModes;
    private readonly ConcurrentDictionary<(string Id, int Lo, int Hi), AreaMapEntry> _areaMaps = new();
    private readonly ConcurrentDictionary<(string Id, int Lo, int Hi), AreaCellsEntry> _areaCells = new();

    /// <summary>True when any card has Area sampling on.</summary>
    internal bool AnyAreaSampling => _areaModes is { Count: > 0 };

    /// <summary>Where the live per-card modes (card id to wire name) come from. When set, the engine re-reads it once per pass and adopts a replaced dictionary, so every writer that swaps <c>Devices.SamplingModes</c> (route, chain reorder, zone drop, profile switch) reaches sampling without a call here.</summary>
    public Func<IReadOnlyDictionary<string, string>?>? SamplingModeSource { get; set; }

    private object? _modesSrc;
    private bool _modesSrcSet;

    internal void RefreshSamplingModes()
    {
        var source = SamplingModeSource;
        if (source is null)
        {
            return;
        }
        var src = source();
        if (_modesSrcSet && ReferenceEquals(src, _modesSrc))
        {
            return;
        }
        SetSamplingModes(src);
    }

    /// <summary>Replaces the per-card sampling modes (card id to wire name); <c>led</c> and unknown names are the default and are not kept.</summary>
    public void SetSamplingModes(IReadOnlyDictionary<string, string>? modes)
    {
        _modesSrc = modes;
        _modesSrcSet = true;
        Dictionary<string, SamplingMode>? next = null;
        if (modes is not null)
        {
            foreach (var kv in modes)
            {
                if (SamplingModes.TryParse(kv.Value, out var m) && m != SamplingMode.Led)
                {
                    (next ??= new Dictionary<string, SamplingMode>(StringComparer.Ordinal))[kv.Key] = m;
                }
            }
        }
        _areaModes = next;
        _areaMaps.Clear();
        _areaCells.Clear();
    }

    /// <summary>The card's sampling mode.</summary>
    internal SamplingMode ModeOf(string id) =>
        _areaModes is { } modes && modes.TryGetValue(id, out var m) ? m : SamplingMode.Led;

    internal bool IsAreaSampled(string id) => ModeOf(id) != SamplingMode.Led;

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
        var mode = ModeOf(id);
        if (mode == SamplingMode.Led)
        {
            return null;
        }
        var rad = fullFrame ? 0f : (((rotation % 360) + 360) % 360) * (MathF.PI / 180f);
        var geom = new AreaGeom(rectX, rectY, rectW, rectH, frameCx, frameCy, rad, cw, ch, (int)mode);
        var key = (id, 0, ledCount);
        if (_areaMaps.TryGetValue(key, out var e) && e.Scene is null && e.Count == ledCount && e.Geom == geom
            && Same(e.U, u) && Same(e.V, v) && Same(e.Disabled, disabled))
        {
            return e.Map;
        }
        AreaCells.Positions(u, v, disabled, ledCount, 0, ledCount, out var pu, out var pv, out var on);
        var map = AreaOwnerMap.BuildFrame(pu, pv, on, geo, rectX, rectY, rectW, rectH, frameCx, frameCy, MathF.Cos(rad), MathF.Sin(rad), cw, ch, mode);
        _areaMaps[key] = new AreaMapEntry { Count = ledCount, U = u, V = v, Disabled = disabled, Geom = geom, Map = map };
        EvictStale(id, ledCount, scene: false, key);
        return map;
    }

    // Entries of the same card for another LED count, or built for the other kind of read, can never be hit again.
    private void EvictStale(string id, int ledCount, bool scene, (string Id, int Lo, int Hi) keep)
    {
        foreach (var kv in _areaMaps)
        {
            if (kv.Key.Id == id && kv.Key != keep && (kv.Value.Count != ledCount || (kv.Value.Scene is not null) != scene))
            {
                _areaMaps.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>Drops cached maps and cells of cards that are no longer in the device list.</summary>
    private void PruneAreaCaches(DeviceFrame[] devices)
    {
        if (_areaMaps.IsEmpty && _areaCells.IsEmpty)
        {
            return;
        }
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in devices)
        {
            live.Add(d.Id);
        }
        foreach (var k in _areaMaps.Keys)
        {
            if (!live.Contains(k.Id)) _areaMaps.TryRemove(k, out _);
        }
        foreach (var k in _areaCells.Keys)
        {
            if (!live.Contains(k.Id)) _areaCells.TryRemove(k, out _);
        }
    }

    /// <summary>Runs one canvas sampling pass over <paramref name="devices"/> on the current sample canvas.</summary>
    internal void SampleForTest(DeviceFrame[] devices) => SampleDevicesFromCanvas(devices);

    /// <summary>Owner map of a frame the way the look-ahead builder reads it (own rect, no slot, not full-frame, no editor draft).</summary>
    internal AreaOwnerMap? AreaMapFor(DeviceFrame dev, int cw, int ch)
    {
        // An editor draft is the canvas sampler's alone; the ahead builder bails on it before reaching here.
        if (!IsAreaSampled(dev.Id) || dev.LedCount <= 0 || dev.PreviewLedCount is not null || dev.PreviewLayout is { Length: > 0 })
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
        var mode = ModeOf(id);
        var geom = new AreaGeom(cw, ch, u0, uSpan, v0, vSpan, 0, 0, 0, (int)mode);
        var key = (id, lo, hi);
        if (_areaMaps.TryGetValue(key, out var e) && ReferenceEquals(e.Scene, sceneRef) && e.Count == ledCount && e.Geom == geom
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
        var map = AreaOwnerMap.BuildScene(sx, sy, ok, corners, quadOk, cw, ch, mode);
        _areaMaps[key] = new AreaMapEntry { Count = ledCount, U = ledU, V = ledV, Disabled = disabled, Geom = geom, Scene = sceneRef, Map = map };
        EvictStale(id, ledCount, scene: true, key);
        return map;
    }

    /// <summary>
    /// The cell polygons of a card sampled from <paramref name="frame"/> (null frame = never sampled), exactly
    /// <paramref name="ledCount"/> entries: <c>[u,v,u,v,...]</c> per LED, empty for a disabled LED or one past the draft count.
    /// Cells are laid out in the rect the sampler reads: the frame's own, or its stack slot's share of it, in which case the
    /// polygons are mapped back into the frame's normalized space and cover only that share. A full-frame (sweep) read samples
    /// the whole canvas instead and is not reflected here.
    /// </summary>
    internal float[][] AreaCellsForCard(DeviceFrame? frame, int ledCount, SamplingMode mode)
    {
        var cells = new float[ledCount][];
        float[][] src = Array.Empty<float[]>();
        if (frame is not null)
        {
            var sx = 0f; var sy = 0f; var sw = 1f; var sh = 1f;
            if (_stackSlots is { } slots && slots.TryGetValue(frame.Id, out var slot))
            {
                (sx, sy, sw, sh) = Nexus.Service.Lighting.StackSlots.Slice(0f, 0f, 1f, 1f, slot);
            }
            var aw = frame.W * sw;
            var ah = frame.H * sh;
            var count = frame.PreviewLedCount is { } pc ? Math.Min(pc, frame.LedCount) : frame.LedCount;
            ResolveLayout(frame, count, out var u, out var v, out var disabled);
            var key = (frame.Id, 0, count);
            if (_areaCells.TryGetValue(key, out var e) && e.Count == count && e.Mode == mode && e.W == aw && e.H == ah && e.SlotX == sx && e.SlotY == sy
                && Same(e.U, u) && Same(e.V, v) && Same(e.Disabled, disabled))
            {
                src = e.Cells;
            }
            else
            {
                src = AreaCells.Compute(u, v, disabled, count, 0, count, aw, ah, mode);
                if (sw != 1f || sh != 1f)
                {
                    // New arrays, never in place: LEDs at one position share a polygon, which must be remapped once.
                    var remapped = new Dictionary<float[], float[]>(ReferenceEqualityComparer.Instance);
                    var mapped = new float[src.Length][];
                    for (var i = 0; i < src.Length; i++)
                    {
                        if (!remapped.TryGetValue(src[i], out var m))
                        {
                            m = new float[src[i].Length];
                            for (var k = 0; k < m.Length; k += 2)
                            {
                                m[k] = sx + src[i][k] * sw;
                                m[k + 1] = sy + src[i][k + 1] * sh;
                            }
                            remapped[src[i]] = m;
                        }
                        mapped[i] = m;
                    }
                    src = mapped;
                }
                _areaCells[key] = new AreaCellsEntry { Count = count, Mode = mode, W = aw, H = ah, SlotX = sx, SlotY = sy, U = u, V = v, Disabled = disabled, Cells = src };
                foreach (var kv in _areaCells)
                {
                    if (kv.Key.Id == frame.Id && kv.Key != key) _areaCells.TryRemove(kv.Key, out _);
                }
            }
        }
        for (var i = 0; i < ledCount; i++)
        {
            cells[i] = i < src.Length ? src[i] : Array.Empty<float>();
        }
        return cells;
    }

    /// <summary>The cells of a card in the engine's own mode for it, or null when the card reads the plain spot.</summary>
    internal float[][]? AreaCellsForCard(DeviceFrame? frame, int ledCount)
    {
        RefreshSamplingModes();
        return frame is not null && ModeOf(frame.Id) is var m && m != SamplingMode.Led ? AreaCellsForCard(frame, ledCount, m) : null;
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
