using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// Reaction state for one keyboard: presses in, a premultiplied colour layer
/// out, composited over whatever the engine sampled for that board. Not
/// thread-safe; the overlay drives each instance from the engine thread, the
/// preview from its own request.
/// </summary>
public sealed class KeyReactionRenderer
{
    // Bounds the per-frame cost when keys are mashed: the oldest reaction goes first.
    private const int MaxReactions = 64;

    // Effect clocks and reach at speed 1 / size 1, in milliseconds and key units.
    private const float FadeMs = 650f;
    private const float FadeRadius = 0.35f;
    private const float SoftEdge = 0.5f;
    private const float SweepKeysPerMs = 0.028f;
    private const float SweepLength = 2.5f;
    private const float LaneHalfWidth = 0.5f;
    private const float RippleKeysPerMs = 0.018f;
    private const float RippleWidth = 1.6f;
    // Keeb's ripple: one hue band per key of distance, sliding by this fraction of the radius.
    private const float RippleHueSlide = 0.375f;
    private const float HeatPerPress = 0.32f;
    private const float HeatSpread = 1.6f;
    private const float HeatNeighbourShare = 0.35f;
    private const float HeatMax = 1.6f;
    private const float HeatDecayMs = 3500f;
    private const float SparkLifeMs = 260f;
    private const float SparkAttackMs = 40f;
    private const float SparkMaxDelayMs = 220f;
    private const float SparkRadius = 2.6f;
    private const int SparksPerPress = 7;
    private const float BoltMs = 420f;
    private const float BoltGrowMsPerSegment = 22f;
    private const float BoltStep = 1.2f;
    private const float BoltWidth = 0.55f;
    private const float TraceMs = 700f;
    private const float TraceTravelMs = 140f;
    private const float TraceLinkMs = 1500f;
    private const float TraceWidth = 0.6f;
    private const float RainbowStepDegrees = 37f;
    private const float DimLevel = 0.18f;

    private static readonly (float X, float Y)[] RowDirs = { (1, 0), (-1, 0) };
    private static readonly (float X, float Y)[] ColumnDirs = { (0, 1), (0, -1) };
    private static readonly (float X, float Y)[] CrossDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (float X, float Y)[] StarDirs = BuildStarDirs();

    private sealed class Reaction
    {
        public string Effect = "";
        public int Led;
        public float X, Y;
        public double StartMs;
        public float R, G, B;
        /// <summary>Distance to the farthest LED; the effect is done once its front passes it.</summary>
        public float Reach;
        // Sparks: target LED, start delay and colour per spark.
        public int[]? SparkLeds;
        public float[]? SparkDelays;
        public float[]? SparkRgb;
        // Lightning: segments as x1,y1,x2,y2 quads, each with the step it appears on.
        public float[]? Bolt;
        public int[]? BoltOrder;
        // Trace: where the link starts.
        public float FromX, FromY;
    }

    private readonly List<Reaction> _active = new();
    private readonly Random _rng;
    private float[] _heat = Array.Empty<float>();
    private double _heatAtMs = double.NaN;
    private float _hueCursor;
    private float _lastX, _lastY;
    private double _lastPressMs = double.NegativeInfinity;

    private float[] _r = Array.Empty<float>();
    private float[] _g = Array.Empty<float>();
    private float[] _b = Array.Empty<float>();
    private float[] _a = Array.Empty<float>();

    public KeyReactionRenderer(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    /// <summary>True while nothing would draw: no live reaction and no heat left.</summary>
    public bool Idle => _active.Count == 0 && !HasHeat();

    /// <summary>Drops every reaction and the heat field; used when the effect changes.</summary>
    public void Reset()
    {
        _active.Clear();
        Array.Clear(_heat);
        _lastPressMs = double.NegativeInfinity;
    }

    public void Press(KeyboardGeometry geo, int led, KeyReaction cfg, double nowMs)
    {
        if (!geo.IsActive(led)) return;
        var x = geo.X[led];
        var y = geo.Y[led];
        var (r, g, b) = PressColor(cfg);
        var size = cfg.Size;
        var effect = cfg.Effect;

        if (effect == KeyReactionCatalog.Heatmap)
        {
            AddHeat(geo, x, y, size);
            return;
        }
        if (effect == KeyReactionCatalog.Fade)
        {
            // A held-down repeat or a second tap restarts the key rather than stacking.
            foreach (var existing in _active)
            {
                if (existing.Led == led && existing.Effect == effect)
                {
                    existing.StartMs = nowMs;
                    (existing.R, existing.G, existing.B) = (r, g, b);
                    return;
                }
            }
        }

        var reaction = new Reaction
        {
            Effect = effect, Led = led, X = x, Y = y, StartMs = nowMs, R = r, G = g, B = b,
            Reach = geo.MaxDistanceFrom(x, y),
        };
        switch (effect)
        {
            case KeyReactionCatalog.Sparks:
                SeedSparks(geo, reaction, size, cfg);
                break;
            case KeyReactionCatalog.Lightning:
                SeedBolt(geo, reaction, size);
                break;
            case KeyReactionCatalog.Trace:
                var linked = (nowMs - _lastPressMs) * cfg.Speed <= TraceLinkMs
                    && (_lastX != x || _lastY != y);
                reaction.FromX = linked ? _lastX : x;
                reaction.FromY = linked ? _lastY : y;
                break;
        }
        _lastX = x;
        _lastY = y;
        _lastPressMs = nowMs;
        if (_active.Count >= MaxReactions) _active.RemoveAt(0);
        _active.Add(reaction);
    }

    /// <summary>Paints the reaction layer for <paramref name="nowMs"/> and prunes finished reactions. False when the layer is empty.</summary>
    public bool Render(KeyboardGeometry geo, KeyReaction cfg, double nowMs)
    {
        var n = geo.LedCount;
        EnsureLayer(n);
        Array.Clear(_r, 0, n);
        Array.Clear(_g, 0, n);
        Array.Clear(_b, 0, n);
        Array.Clear(_a, 0, n);
        var speed = cfg.Speed;
        var size = cfg.Size;
        var any = false;

        if (cfg.Effect == KeyReactionCatalog.Heatmap)
        {
            DecayHeat(nowMs, speed);
            any |= PaintHeat(geo);
        }
        else
        {
            _heatAtMs = double.NaN;
        }

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var reaction = _active[i];
            var t = (float)((nowMs - reaction.StartMs) * speed);
            if (t < 0) t = 0;
            bool live = reaction.Effect switch
            {
                KeyReactionCatalog.Fade => PaintFade(geo, reaction, t, size),
                KeyReactionCatalog.RowSweep => PaintRays(geo, reaction, t, size, RowDirs),
                KeyReactionCatalog.ColumnSweep => PaintRays(geo, reaction, t, size, ColumnDirs),
                KeyReactionCatalog.Crosshair => PaintRays(geo, reaction, t, size, CrossDirs),
                KeyReactionCatalog.Starburst => PaintRays(geo, reaction, t, size, StarDirs),
                KeyReactionCatalog.Ripple => PaintRipple(geo, reaction, t, size, cfg.ColorMode == KeyReactionCatalog.ColorRainbow),
                KeyReactionCatalog.Sparks => PaintSparks(reaction, t),
                KeyReactionCatalog.Lightning => PaintBolt(geo, reaction, t, size),
                KeyReactionCatalog.Trace => PaintTrace(geo, reaction, t, size),
                _ => false,
            };
            if (live) any = true;
            else _active.RemoveAt(i);
        }
        return any;
    }

    /// <summary>
    /// Composites the layer from the last <see cref="Render"/> over
    /// <paramref name="rgb"/>, which holds the engine's sampled colours (3 bytes
    /// per LED) and is overwritten in place.
    /// </summary>
    public void Composite(Span<byte> rgb, int ledCount, string background)
    {
        var n = Math.Min(ledCount, Math.Min(_a.Length, rgb.Length / 3));
        var baseScale = background switch
        {
            KeyReactionCatalog.BackgroundDim => DimLevel,
            KeyReactionCatalog.BackgroundDark => 0f,
            _ => 1f,
        };
        var reveal = background == KeyReactionCatalog.BackgroundReveal;
        for (var i = 0; i < n; i++)
        {
            var o = i * 3;
            var a = Math.Min(_a[i], 1f);
            if (reveal)
            {
                rgb[o] = (byte)(rgb[o] * a);
                rgb[o + 1] = (byte)(rgb[o + 1] * a);
                rgb[o + 2] = (byte)(rgb[o + 2] * a);
                continue;
            }
            var keep = baseScale * (1f - a);
            rgb[o] = ToByte(rgb[o] * keep + _r[i] * 255f);
            rgb[o + 1] = ToByte(rgb[o + 1] * keep + _g[i] * 255f);
            rgb[o + 2] = ToByte(rgb[o + 2] * keep + _b[i] * 255f);
        }
    }

    // ── Effects ──

    private bool PaintFade(KeyboardGeometry geo, Reaction r, float t, float size)
    {
        if (t >= FadeMs) return false;
        var k = 1f - t / FadeMs;
        var level = k * k;
        var radius = FadeRadius * size;
        PaintDisc(geo, r.X, r.Y, radius, level, r.R, r.G, r.B);
        return true;
    }

    private bool PaintRays(KeyboardGeometry geo, Reaction r, float t, float size, (float X, float Y)[] dirs)
    {
        var head = t * SweepKeysPerMs;
        var length = SweepLength * size;
        if (head - length > r.Reach) return false;
        var travel = Math.Clamp(head / Math.Max(r.Reach, 1f), 0f, 1f);
        var fade = 1f - 0.5f * travel;
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var dx = geo.X[i] - r.X;
            var dy = geo.Y[i] - r.Y;
            var best = 0f;
            foreach (var (ux, uy) in dirs)
            {
                var along = dx * ux + dy * uy;
                if (along < -LaneHalfWidth || along > head) continue;
                var behind = head - along;
                if (behind > length) continue;
                var perp = MathF.Abs(dx * uy - dy * ux);
                var lane = LaneHalfWidth * (ux != 0 && uy != 0 ? 1.25f : 1f);
                if (perp > lane) continue;
                best = Math.Max(best, 1f - behind / length);
            }
            if (best > 0) Add(i, best * fade, r.R, r.G, r.B);
        }
        return true;
    }

    private bool PaintRipple(KeyboardGeometry geo, Reaction r, float t, float size, bool rainbow)
    {
        var radius = t * RippleKeysPerMs;
        var width = RippleWidth * size;
        if (radius - width > r.Reach) return false;
        var fade = 1f - Math.Clamp(radius / (r.Reach + width), 0f, 1f);
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var dx = geo.X[i] - r.X;
            var dy = geo.Y[i] - r.Y;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            var k = 1f - MathF.Abs(d - radius) / width;
            if (k <= 0) continue;
            if (rainbow)
            {
                var (cr, cg, cb) = Hsv((d - radius * RippleHueSlide) * 30f, 1f, 1f);
                Add(i, k * fade, cr, cg, cb);
            }
            else
            {
                Add(i, k * fade, r.R, r.G, r.B);
            }
        }
        return true;
    }

    private bool PaintSparks(Reaction r, float t)
    {
        var leds = r.SparkLeds!;
        var delays = r.SparkDelays!;
        var rgb = r.SparkRgb!;
        var live = false;
        for (var s = 0; s < leds.Length; s++)
        {
            var local = t - delays[s];
            if (local >= SparkLifeMs) continue;
            live = true;
            if (local < 0) continue;
            var level = local < SparkAttackMs
                ? local / SparkAttackMs
                : Square(1f - (local - SparkAttackMs) / (SparkLifeMs - SparkAttackMs));
            Add(leds[s], level, rgb[s * 3], rgb[s * 3 + 1], rgb[s * 3 + 2]);
        }
        return live;
    }

    private bool PaintBolt(KeyboardGeometry geo, Reaction r, float t, float size)
    {
        if (t >= BoltMs) return false;
        var bolt = r.Bolt!;
        var order = r.BoltOrder!;
        var grown = (int)(t / BoltGrowMsPerSegment);
        // Deterministic flicker so a bolt replays the same in the preview.
        var flick = Hash((int)(t / 45f) + r.Led * 131) * 0.35f + 0.65f;
        var level = MathF.Pow(1f - t / BoltMs, 1.5f) * flick;
        var width = BoltWidth * (0.6f + 0.4f * size);
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var best = float.MaxValue;
            for (var s = 0; s < order.Length; s++)
            {
                if (order[s] > grown) continue;
                var d = SegmentDistance(geo.X[i], geo.Y[i], bolt[s * 4], bolt[s * 4 + 1], bolt[s * 4 + 2], bolt[s * 4 + 3], out _);
                best = Math.Min(best, d);
            }
            if (best >= width) continue;
            var core = 1f - best / width;
            // The core of the bolt runs hot toward white.
            var white = 0.55f * core;
            Add(i, level * core, Lerp(r.R, 1f, white), Lerp(r.G, 1f, white), Lerp(r.B, 1f, white));
        }
        return true;
    }

    private bool PaintTrace(KeyboardGeometry geo, Reaction r, float t, float size)
    {
        if (t >= TraceMs) return false;
        var level = MathF.Pow(1f - t / TraceMs, 1.3f);
        var head = Math.Min(1f, t / TraceTravelMs);
        var width = TraceWidth * size;
        var linked = r.FromX != r.X || r.FromY != r.Y;
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            float k;
            if (linked)
            {
                var d = SegmentDistance(geo.X[i], geo.Y[i], r.FromX, r.FromY, r.X, r.Y, out var s);
                if (d >= width || s > head) continue;
                // Brightest at the key just pressed, fading back toward the one before it.
                k = (1f - d / width) * (0.35f + 0.65f * s);
            }
            else
            {
                var dx = geo.X[i] - r.X;
                var dy = geo.Y[i] - r.Y;
                k = Soft(MathF.Sqrt(dx * dx + dy * dy), FadeRadius * size);
            }
            if (k > 0) Add(i, k * level, r.R, r.G, r.B);
        }
        return true;
    }

    // ── Heat field ──

    private void AddHeat(KeyboardGeometry geo, float x, float y, float size)
    {
        EnsureHeat(geo.LedCount);
        var spread = HeatSpread * size;
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var dx = geo.X[i] - x;
            var dy = geo.Y[i] - y;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            var add = d < 0.5f ? HeatPerPress : d < spread ? HeatPerPress * HeatNeighbourShare * (1f - d / spread) : 0f;
            if (add > 0) _heat[i] = Math.Min(HeatMax, _heat[i] + add);
        }
    }

    private void DecayHeat(double nowMs, float speed)
    {
        if (!double.IsNaN(_heatAtMs) && _heat.Length > 0)
        {
            var dt = (float)Math.Max(0, (nowMs - _heatAtMs) * speed);
            var keep = MathF.Exp(-dt / HeatDecayMs);
            for (var i = 0; i < _heat.Length; i++)
            {
                _heat[i] *= keep;
                if (_heat[i] < 0.002f) _heat[i] = 0f;
            }
        }
        _heatAtMs = nowMs;
    }

    private bool PaintHeat(KeyboardGeometry geo)
    {
        var any = false;
        var n = Math.Min(_heat.Length, geo.LedCount);
        for (var i = 0; i < n; i++)
        {
            var h = _heat[i];
            if (h <= 0) continue;
            any = true;
            var (r, g, b) = Thermal(Math.Min(1f, h / 1.2f));
            Add(i, Math.Min(1f, h * 4f), r, g, b);
        }
        return any;
    }

    private bool HasHeat()
    {
        foreach (var h in _heat) if (h > 0) return true;
        return false;
    }

    // ── Seeding ──

    private void SeedSparks(KeyboardGeometry geo, Reaction reaction, float size, KeyReaction cfg)
    {
        var count = Math.Max(3, (int)MathF.Round(SparksPerPress * size));
        var leds = new int[count + 1];
        var delays = new float[count + 1];
        var rgb = new float[(count + 1) * 3];
        // Spark 0 is the pressed key itself, lit at once.
        leds[0] = reaction.Led;
        (rgb[0], rgb[1], rgb[2]) = (reaction.R, reaction.G, reaction.B);
        var baseHue = Hue(reaction.R, reaction.G, reaction.B);
        for (var s = 1; s <= count; s++)
        {
            var angle = (float)(_rng.NextDouble() * Math.PI * 2);
            var dist = MathF.Sqrt((float)_rng.NextDouble()) * SparkRadius * size;
            var led = geo.Nearest(reaction.X + MathF.Cos(angle) * dist, reaction.Y + MathF.Sin(angle) * dist, 1.2f);
            leds[s] = led < 0 ? reaction.Led : led;
            delays[s] = (float)_rng.NextDouble() * SparkMaxDelayMs;
            var hue = cfg.ColorMode == KeyReactionCatalog.ColorCustom
                ? baseHue + ((float)_rng.NextDouble() - 0.5f) * 50f
                : (float)_rng.NextDouble() * 360f;
            (rgb[s * 3], rgb[s * 3 + 1], rgb[s * 3 + 2]) = Hsv(hue, 1f, 1f);
        }
        reaction.SparkLeds = leds;
        reaction.SparkDelays = delays;
        reaction.SparkRgb = rgb;
    }

    private void SeedBolt(KeyboardGeometry geo, Reaction reaction, float size)
    {
        var segments = new List<(float X1, float Y1, float X2, float Y2, int Order)>();
        // Boards are wide, so bolts head left or right and wander vertically.
        var heading = (_rng.NextDouble() < 0.5 ? 0f : MathF.PI) + ((float)_rng.NextDouble() - 0.5f) * 1.2f;
        var steps = (int)MathF.Round((5 + _rng.Next(4)) * MathF.Sqrt(size));
        var main = Walk(geo, reaction.X, reaction.Y, heading, steps, 0, segments);
        if (main.Count > 3 && _rng.NextDouble() < 0.6)
        {
            var from = 1 + _rng.Next(main.Count - 2);
            var fork = heading + (_rng.NextDouble() < 0.5 ? -0.9f : 0.9f);
            Walk(geo, main[from].X, main[from].Y, fork, 2 + _rng.Next(3), from, segments);
        }
        var bolt = new float[segments.Count * 4];
        var order = new int[segments.Count];
        for (var s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            bolt[s * 4] = seg.X1; bolt[s * 4 + 1] = seg.Y1; bolt[s * 4 + 2] = seg.X2; bolt[s * 4 + 3] = seg.Y2;
            order[s] = seg.Order;
        }
        reaction.Bolt = bolt;
        reaction.BoltOrder = order;
    }

    private List<(float X, float Y)> Walk(KeyboardGeometry geo, float x, float y, float heading, int steps, int firstOrder,
        List<(float, float, float, float, int)> segments)
    {
        var points = new List<(float X, float Y)> { (x, y) };
        var angle = heading;
        for (var s = 0; s < steps; s++)
        {
            angle = 0.6f * angle + 0.4f * heading + ((float)_rng.NextDouble() - 0.5f) * 1.4f;
            var nx = x + MathF.Cos(angle) * BoltStep;
            var ny = y + MathF.Sin(angle) * BoltStep * 0.7f;
            segments.Add((x, y, nx, ny, firstOrder + s));
            x = nx;
            y = ny;
            points.Add((x, y));
            if (x < geo.MinX - 1 || x > geo.MaxX + 1 || y < geo.MinY - 1 || y > geo.MaxY + 1) break;
        }
        return points;
    }

    // ── Colour ──

    private (float R, float G, float B) PressColor(KeyReaction cfg)
    {
        switch (cfg.ColorMode)
        {
            case KeyReactionCatalog.ColorRainbow:
                _hueCursor = (_hueCursor + RainbowStepDegrees) % 360f;
                return Hsv(_hueCursor, 1f, 1f);
            case KeyReactionCatalog.ColorRandom:
                return Hsv((float)_rng.NextDouble() * 360f, 1f, 1f);
            default:
                return StaticColorHex.TryParse(cfg.Color, out var r, out var g, out var b)
                    ? (r / 255f, g / 255f, b / 255f)
                    : (1f, 0.18f, 0.33f);
        }
    }

    /// <summary>Blue through cyan, green, yellow and red to white as heat rises over [0, 1].</summary>
    private static (float R, float G, float B) Thermal(float h)
    {
        ReadOnlySpan<float> stops = stackalloc float[] { 0f, 0.25f, 0.45f, 0.65f, 0.85f, 1f };
        ReadOnlySpan<float> rs = stackalloc float[] { 0f, 0f, 0f, 1f, 1f, 1f };
        ReadOnlySpan<float> gs = stackalloc float[] { 0f, 0.78f, 1f, 0.9f, 0.24f, 1f };
        ReadOnlySpan<float> bs = stackalloc float[] { 1f, 1f, 0.3f, 0f, 0f, 1f };
        for (var i = 1; i < stops.Length; i++)
        {
            if (h > stops[i]) continue;
            var k = (h - stops[i - 1]) / (stops[i] - stops[i - 1]);
            return (Lerp(rs[i - 1], rs[i], k), Lerp(gs[i - 1], gs[i], k), Lerp(bs[i - 1], bs[i], k));
        }
        return (1f, 1f, 1f);
    }

    internal static (float R, float G, float B) Hsv(float hueDegrees, float s, float v)
    {
        var h = ((hueDegrees % 360f) + 360f) % 360f / 60f;
        var c = v * s;
        var x = c * (1f - MathF.Abs(h % 2f - 1f));
        var m = v - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (r + m, g + m, b + m);
    }

    private static float Hue(float r, float g, float b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d <= 1e-5f) return 0f;
        float h;
        if (max == r) h = (g - b) / d % 6f;
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;
        return h * 60f;
    }

    // ── Helpers ──

    private void PaintDisc(KeyboardGeometry geo, float x, float y, float radius, float level, float r, float g, float b)
    {
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (!geo.IsActive(i)) continue;
            var dx = geo.X[i] - x;
            var dy = geo.Y[i] - y;
            var k = Soft(MathF.Sqrt(dx * dx + dy * dy), radius);
            if (k > 0) Add(i, k * level, r, g, b);
        }
    }

    private void Add(int i, float level, float r, float g, float b)
    {
        if (level <= 0 || i < 0 || i >= _a.Length) return;
        _r[i] += r * level;
        _g[i] += g * level;
        _b[i] += b * level;
        if (level > _a[i]) _a[i] = level;
    }

    private static float Soft(float d, float radius) =>
        d <= radius ? 1f : Math.Max(0f, 1f - (d - radius) / SoftEdge);

    private static float SegmentDistance(float px, float py, float x1, float y1, float x2, float y2, out float s)
    {
        var vx = x2 - x1;
        var vy = y2 - y1;
        var len2 = vx * vx + vy * vy;
        s = len2 > 1e-6f ? Math.Clamp(((px - x1) * vx + (py - y1) * vy) / len2, 0f, 1f) : 0f;
        var cx = x1 + vx * s - px;
        var cy = y1 + vy * s - py;
        return MathF.Sqrt(cx * cx + cy * cy);
    }

    private static float Hash(int n)
    {
        unchecked
        {
            var h = (uint)n * 2654435761u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (h & 0xFFFF) / 65535f;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Square(float v) => v * v;
    private static byte ToByte(float v) => v >= 255f ? (byte)255 : v <= 0f ? (byte)0 : (byte)v;

    private void EnsureLayer(int n)
    {
        if (_a.Length >= n) return;
        _r = new float[n];
        _g = new float[n];
        _b = new float[n];
        _a = new float[n];
    }

    private void EnsureHeat(int n)
    {
        if (_heat.Length == n) return;
        var next = new float[n];
        Array.Copy(_heat, next, Math.Min(n, _heat.Length));
        _heat = next;
    }

    private static (float X, float Y)[] BuildStarDirs()
    {
        var dirs = new (float, float)[8];
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4f;
            dirs[i] = (MathF.Round(MathF.Cos(a), 4), MathF.Round(MathF.Sin(a), 4));
        }
        return dirs;
    }
}
