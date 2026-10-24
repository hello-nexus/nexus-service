using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine
{
    /// <summary>One look-ahead window in every this-many is also rendered the canvas way and compared pixel for pixel.</summary>
    internal const int AheadSpotCheckEvery = 32;
    /// <summary>Largest share of the canvas the Area sampling taps of one source may cover before the window falls back to full canvas renders.</summary>
    internal const float AheadAreaMaxTapFraction = 0.5f;

    private enum FastWindow { NotUsed, Done, SpotCheck }

    private sealed class AheadSpot
    {
        public AheadRequest Req = null!;
        public LedTapSet BaseTaps = null!;
        public byte[]? BaseRgba;
        public ShaderEffect? BaseFx;
        public readonly Dictionary<string, (ShaderEffect Fx, LedTapSet Taps, byte[] Rgba)> Pins = new(StringComparer.Ordinal);
        public bool Reported;
    }

    /// <summary>Test seam: true runs the window path where the gate is closed because there is no GPU, false closes it where the gate is open, null defers to the gate.</summary>
    internal bool? AheadFastForTest;

    private AheadSpot? _aheadSpot;
    private long _aheadCounterLogMs;
    private int _aheadCanvasWindows;
    private int _aheadCanvasLogged;
    private int _aheadFastLogged;
    private int _aheadFastCount;
    private int _aheadFastWindows;
    private int _aheadSpotChecks;
    private int _aheadSpotMismatches;

    /// <summary>Look-ahead windows rendered through the LED-window path.</summary>
    internal int AheadFastWindows => Volatile.Read(ref _aheadFastWindows);
    internal int AheadSpotChecks => Volatile.Read(ref _aheadSpotChecks);
    internal int AheadSpotMismatches => Volatile.Read(ref _aheadSpotMismatches);

    private static bool SameLayout(DeviceFrame a, DeviceFrame b) =>
        a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H && a.Rotation == b.Rotation && a.LedCount == b.LedCount
        && ReferenceEquals(a.LedU, b.LedU) && ReferenceEquals(a.LedV, b.LedV) && ReferenceEquals(a.LedDisabled, b.LedDisabled);

    /// <summary>
    /// Renders a whole look-ahead window from the pixels its devices read instead of from full canvases, when every device and
    /// source qualifies and the exactness gate has proven the sources byte-identical here. Anything else is NotUsed before any
    /// state changed, so the canvas path then runs the same window unchanged. SpotCheck means the LED-window result was computed
    /// and is held for comparison while the canvas path delivers the window.
    /// </summary>
    private FastWindow TryFastWindow(AheadRequest req, IEffect effect)
    {
        FastWindow result;
        try
        {
            result = TryFastWindowCore(req, effect);
        }
        catch
        {
            // Same exit as the canvas path: the window is over, and no frame of it came from the other path.
            _aheadActive = null;
            req.Done.Set();
            throw;
        }
        if (result != FastWindow.Done)
        {
            _aheadCanvasWindows++;
        }
        LogAheadCounters();
        return result;
    }

    /// <summary>At most once a minute, and only when the counts moved: how many windows went through each path. Silent where the window path cannot run.</summary>
    private void LogAheadCounters()
    {
        if (!LedSpaceGate.Enabled)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (now - _aheadCounterLogMs < 60_000)
        {
            return;
        }
        _aheadCounterLogMs = now;
        var fast = AheadFastWindows;
        if (fast == _aheadFastLogged && _aheadCanvasWindows == _aheadCanvasLogged)
        {
            return;
        }
        _aheadFastLogged = fast;
        _aheadCanvasLogged = _aheadCanvasWindows;
        GpuContext.Log($"[ledspace] windows so far: led-space {fast}, canvas {_aheadCanvasWindows}, spot-checks {AheadSpotChecks}, spot-check mismatches {AheadSpotMismatches}");
    }

    private FastWindow TryFastWindowCore(AheadRequest req, IEffect effect)
    {
        _aheadSpot = null;
        var fullFrameAtStart = FullFrameSampling;
        RefreshSamplingModes();
        var areaAtStart = _areaModes;
        if (!(AheadFastForTest ?? LedSpaceGate.Enabled) || fullFrameAtStart)
        {
            return FastWindow.NotUsed;
        }
        var frames = req.TickMs.Length;
        if (frames is < 1 or > LedWindowShader.MaxFrames)
        {
            return FastWindow.NotUsed;
        }
        var cw = _aheadCanvas.Width;
        var ch = _aheadCanvas.Height;
        var shadows = new DeviceFrame[frames][];
        for (var i = 0; i < frames; i++)
        {
            shadows[i] = req.NewShadowFrames();
        }
        var first = shadows[0];
        var scene = _scene;
        var slots = _stackSlots;
        var pins = Pins is { Any: true } tracker ? tracker.Snapshot() : new Dictionary<string, Nexus.Service.Persistence.DevicePin>(StringComparer.Ordinal);
        var baseTaps = new LedTapSet(cw, ch);
        var pinTaps = new Dictionary<string, (ShaderEffect Fx, LedTapSet Taps)>(StringComparer.Ordinal);
        var pinned = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var d = 0; d < first.Length; d++)
        {
            var dev = first[d];
            if ((scene is not null && scene.Placements.ContainsKey(dev.Id))
                || (slots is not null && slots.ContainsKey(dev.Id)))
            {
                return FastWindow.NotUsed;
            }
            for (var i = 1; i < frames; i++)
            {
                if (!SameLayout(dev, shadows[i][d]))
                {
                    return FastWindow.NotUsed;
                }
            }
            var kind = ResolveAheadPin(dev, pins, effect, out var key, out var fx, out var sweep);
            if (sweep)
            {
                return FastWindow.NotUsed;
            }
            switch (kind)
            {
                case AheadPinKind.Base:
                case AheadPinKind.FollowActive:
                    if (kind == AheadPinKind.FollowActive)
                    {
                        pinned[dev.Id] = null;
                    }
                    if (req.NeedsGlobal && !baseTaps.AddDevice(dev, cw, ch, AreaMapFor(dev, cw, ch)))
                    {
                        return FastWindow.NotUsed;
                    }
                    break;
                default:
                    if (fx is not ShaderEffect shader)
                    {
                        return FastWindow.NotUsed;
                    }
                    if (!pinTaps.TryGetValue(key, out var entry))
                    {
                        entry = (shader, new LedTapSet(cw, ch));
                        pinTaps[key] = entry;
                    }
                    if (!entry.Taps.AddDevice(dev, cw, ch, AreaMapFor(dev, cw, ch)))
                    {
                        return FastWindow.NotUsed;
                    }
                    pinned[dev.Id] = key;
                    break;
            }
        }

        if (areaAtStart is not null)
        {
            var limit = cw * ch * AheadAreaMaxTapFraction;
            if (baseTaps.Count > limit)
            {
                return FastWindow.NotUsed;
            }
            foreach (var kv in pinTaps)
            {
                if (kv.Value.Taps.Count > limit)
                {
                    return FastWindow.NotUsed;
                }
            }
        }

        var ticks = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            ticks[i] = req.TickMs[i];
        }
        byte[] baseRgba = Array.Empty<byte>();
        ShaderEffect? baseFx = null;
        if (baseTaps.Count > 0)
        {
            baseFx = effect as ShaderEffect;
            if (baseFx is null || !baseFx.TryRenderLedWindow(baseTaps.X, baseTaps.Y, ticks, frames, ref baseRgba))
            {
                return FastWindow.NotUsed;
            }
        }
        var pinResults = new Dictionary<string, (ShaderEffect Fx, LedTapSet Taps, byte[] Rgba)>(StringComparer.Ordinal);
        foreach (var kv in pinTaps)
        {
            var rgba = Array.Empty<byte>();
            if (!kv.Value.Fx.TryRenderLedWindow(kv.Value.Taps.X, kv.Value.Taps.Y, ticks, frames, ref rgba))
            {
                return FastWindow.NotUsed;
            }
            pinResults[kv.Key] = (kv.Value.Fx, kv.Value.Taps, rgba);
        }

        var count = _aheadFastCount++;
        if (count % AheadSpotCheckEvery == 0)
        {
            var spot = new AheadSpot { Req = req, BaseTaps = baseTaps, BaseRgba = baseFx is null ? null : baseRgba, BaseFx = baseFx };
            foreach (var kv in pinResults)
            {
                spot.Pins[kv.Key] = kv.Value;
            }
            _aheadSpot = spot;
            Interlocked.Increment(ref _aheadSpotChecks);
            return FastWindow.SpotCheck;
        }

        var sampled = new DeviceFrame[frames][];
        try
        {
            _aheadPinned.Clear();
            _aheadPinFullFrame.Clear();
            foreach (var kv in pinned)
            {
                _aheadPinned[kv.Key] = kv.Value is null ? null : PinCanvasFor(kv.Value);
            }
            _aheadCanvas.Clear();
            foreach (var kv in pinResults)
            {
                PinCanvasFor(kv.Key).Clear();
            }
            for (var i = 0; i < frames; i++)
            {
                if (baseFx is not null)
                {
                    Scatter(_aheadCanvas, baseTaps, baseRgba, i);
                }
                foreach (var kv in pinResults)
                {
                    Scatter(PinCanvasFor(kv.Key), kv.Value.Taps, kv.Value.Rgba, i);
                }
                var shadow = shadows[i];
                _sampleCanvas = _aheadCanvas;
                SampleDevicesFromCanvas(shadow, pinned: _aheadPinned, pinFullFrame: _aheadPinFullFrame);
                ApplyLedColorLocks(shadow);
                sampled[i] = shadow;
            }
        }
        finally
        {
            _sampleCanvas = _canvas;
        }
        // Sampling reads these live. Nothing is published yet, so a flip during the window sends it to the canvas path whole.
        if (FullFrameSampling != fullFrameAtStart || !ReferenceEquals(_scene, scene) || !ReferenceEquals(_stackSlots, slots)
            || !ReferenceEquals(_areaModes, areaAtStart))
        {
            return FastWindow.NotUsed;
        }
        for (var i = 0; i < frames; i++)
        {
            foreach (var dev in sampled[i])
            {
                dev.Publish();
            }
            req.Rendered[req.NextFrame++] = sampled[i];
        }
        Interlocked.Increment(ref _aheadFastWindows);
        return FastWindow.Done;
    }

    private CanvasBuffer PinCanvasFor(string key)
    {
        if (!_aheadPinCanvases.TryGetValue(key, out var canvas))
        {
            canvas = new CanvasBuffer(_canvas.Width, _canvas.Height);
            _aheadPinCanvases[key] = canvas;
        }
        return canvas;
    }

    private static void Scatter(CanvasBuffer canvas, LedTapSet taps, byte[] rgba, int frame)
    {
        var n = taps.Count;
        var x = taps.X;
        var y = taps.Y;
        var o = frame * n * 4;
        for (var t = 0; t < n; t++)
        {
            canvas.SetPixel(x[t], y[t], rgba[o], rgba[o + 1], rgba[o + 2]);
            o += 4;
        }
    }

    /// <summary>Compares the held LED-window pixels of one frame against the canvases the canvas path just filled. A difference closes the source's fast path for good.</summary>
    private void CheckSpot(AheadRequest req, int frame)
    {
        if (_aheadSpot is not { } spot || !ReferenceEquals(spot.Req, req) || spot.Reported)
        {
            return;
        }
        if (spot.BaseFx is not null && spot.BaseRgba is not null && Differs(_aheadCanvas, spot.BaseTaps, spot.BaseRgba, frame, out var where))
        {
            Mismatch(spot, spot.BaseFx, frame, where);
            return;
        }
        foreach (var kv in spot.Pins)
        {
            if (_aheadPinCanvases.TryGetValue(kv.Key, out var canvas) && Differs(canvas, kv.Value.Taps, kv.Value.Rgba, frame, out var at))
            {
                Mismatch(spot, kv.Value.Fx, frame, at);
                return;
            }
        }
    }

    private void Mismatch(AheadSpot spot, ShaderEffect fx, int frame, string where)
    {
        spot.Reported = true;
        Interlocked.Increment(ref _aheadSpotMismatches);
        LedSpaceGate.Reject(fx, $"runtime spot-check mismatch, frame {frame} {where}");
    }

    private static bool Differs(CanvasBuffer canvas, LedTapSet taps, byte[] rgba, int frame, out string where)
    {
        var n = taps.Count;
        var x = taps.X;
        var y = taps.Y;
        var px = canvas.Pixels;
        var o = frame * n * 4;
        for (var t = 0; t < n; t++)
        {
            var c = (y[t] * canvas.Width + x[t]) * 3;
            if (px[c] != rgba[o] || px[c + 1] != rgba[o + 1] || px[c + 2] != rgba[o + 2])
            {
                where = $"pixel ({x[t]},{y[t]}): canvas {px[c]},{px[c + 1]},{px[c + 2]} window {rgba[o]},{rgba[o + 1]},{rgba[o + 2]}";
                return true;
            }
            o += 4;
        }
        where = "";
        return false;
    }
}
