using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// 20 position-model TL effects; frame count is data driven. Output already has brightness applied
/// (palette scaled by the level, and a second scaling for the effects flagged ScalesOutput), so the
/// shared brightness step must not run over it. Voice, meteorShower and kaleidoscope draw seeded random numbers.
/// </summary>
public static class FlexTlExtendedEffects
{
    public const int DefaultSeed = 1;

    private const double DefaultIntervalBase = 11.0;
    private const double CollideIntervalBase = 16.0;

    private sealed record Entry(int Mode, int MinColors, bool ScalesOutput, RgbColor[] Defaults);

    private static RgbColor C(byte r, byte g, byte b) => new(r, g, b);

    private static readonly RgbColor[] RedBlue = { C(255, 0, 0), C(0, 0, 255) };

    private static readonly RgbColor[] RedBlueGreenYellow =
        { C(255, 0, 0), C(0, 0, 255), C(0, 128, 0), C(255, 255, 0) };

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.Ordinal)
    {
        ["staggered"] = new(8, 1, false, new[] { C(0, 215, 255), C(255, 0, 150) }),
        ["tide"] = new(9, 1, false, new[] { C(255, 0, 150), C(0, 0, 255) }),
        ["mixing"] = new(10, 1, true, RedBlue),
        ["voice"] = new(11, 0, true, Array.Empty<RgbColor>()),
        ["door"] = new(12, 1, false, RedBlueGreenYellow),
        ["render"] = new(13, 1, false, new[] { C(255, 0, 150), C(0, 0, 255), C(0, 255, 0), C(255, 240, 38) }),
        ["ripple"] = new(14, 1, false, RedBlueGreenYellow),
        ["reflect"] = new(15, 1, false, RedBlueGreenYellow),
        ["tailChasing"] = new(16, 2, false, RedBlue),
        ["paint"] = new(17, 1, false, RedBlueGreenYellow),
        ["pingPong"] = new(18, 1, false, RedBlueGreenYellow),
        ["coverCycle"] = new(20, 1, false, new[] { C(255, 0, 0), C(0, 0, 255), C(0, 128, 0) }),
        ["wave"] = new(21, 1, false, RedBlueGreenYellow),
        ["racing"] = new(22, 2, false, RedBlue),
        ["lottery"] = new(23, 1, false, RedBlue),
        ["intertwine"] = new(24, 2, true, RedBlue),
        ["meteorShower"] = new(25, 0, true, new[] { C(255, 0, 150), C(0, 215, 255), C(0, 255, 0), C(255, 215, 0) }),
        ["collide"] = new(26, 2, false, RedBlueGreenYellow),
        ["electricCurrent"] = new(27, 1, false, RedBlueGreenYellow),
        ["kaleidoscope"] = new(28, 0, true, Array.Empty<RgbColor>()),
    };

    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        "staggered", "tide", "mixing", "voice", "door", "render", "ripple", "reflect", "tailChasing", "paint",
        "pingPong", "coverCycle", "wave", "racing", "lottery", "intertwine", "meteorShower", "collide",
        "electricCurrent", "kaleidoscope",
    };

    public static bool IsImplemented(string key) => Table.ContainsKey(key);

    /// <summary>Colors a request falls back to when it carries none (or too few) for the effect.</summary>
    public static IReadOnlyList<RgbColor> DefaultColors(string key) =>
        Table.TryGetValue(key, out var e) ? e.Defaults : Array.Empty<RgbColor>();

    public static double IntervalBaseFor(string key) =>
        string.Equals(key, "collide", StringComparison.Ordinal) ? CollideIntervalBase : DefaultIntervalBase;

    /// <summary>Scope Up keeps LED 0..12 of each fan, Down keeps 13..25; the effect is computed on all 26 first, so the halves are cuts of one animation.</summary>
    public static FlexRawAnimation Render(string key, FlexEffectRequest req, int seed = DefaultSeed)
    {
        if (!Table.TryGetValue(key, out var entry))
        {
            throw new NotSupportedException($"TL effect '{key}' has no extended generator.");
        }
        if (req.Scope is FlexScope.Inner or FlexScope.Center or FlexScope.Outer)
        {
            throw new ArgumentException($"TL scope {req.Scope} is not a TL zone.", nameof(req));
        }

        var fans = req.Fans is < 1 or > 4 ? 4 : req.Fans;
        var level = Math.Clamp(req.BrightnessLevel, 0, 255);
        var palette = BuildPalette(entry, req.Colors, level);
        var gen = new TlGenerator(entry.Mode, fans, req.Direction != 0, palette, seed);
        var frames = gen.Run();

        var leds = fans * TlGenerator.PerFan;
        var buf = new byte[frames.Count * leds * 3];
        for (var f = 0; f < frames.Count; f++)
        {
            var frame = frames[f];
            for (var k = 0; k < leds; k++)
            {
                var half = k % TlGenerator.PerFan < TlGenerator.PerFan / 2;
                var masked = req.Scope == FlexScope.Up ? !half : req.Scope == FlexScope.Down && half;
                if (masked)
                {
                    continue;
                }
                var o = (f * leds + k) * 3;
                var c = frame[k];
                buf[o] = entry.ScalesOutput ? (byte)(c.R * level >> 8) : c.R;
                buf[o + 1] = entry.ScalesOutput ? (byte)(c.G * level >> 8) : c.G;
                buf[o + 2] = entry.ScalesOutput ? (byte)(c.B * level >> 8) : c.B;
            }
        }
        return new FlexRawAnimation
        {
            Frames = buf,
            FrameCount = frames.Count,
            Leds = leds,
            IntervalBaseMs = IntervalBaseFor(key),
        };
    }

    private static List<TlRgb> BuildPalette(Entry entry, IReadOnlyList<RgbColor> requested, int level)
    {
        var list = new List<TlRgb>();
        if (entry.Defaults.Length == 0)
        {
            return list;
        }
        var count = Math.Max(requested.Count, Math.Max(entry.MinColors, requested.Count == 0 ? entry.Defaults.Length : 0));
        for (var i = 0; i < count; i++)
        {
            RgbColor c;
            if (i < requested.Count)
            {
                c = requested[i];
            }
            else
            {
                c = entry.Defaults[Math.Min(i, entry.Defaults.Length - 1)];
            }
            list.Add(new TlRgb((byte)(c.R * level >> 8), (byte)(c.G * level >> 8), (byte)(c.B * level >> 8)));
        }
        return list;
    }

    internal readonly record struct TlRgb(byte R, byte G, byte B);

    private struct Pt
    {
        public int X;
        public int Y;
    }

    private sealed class Fan
    {
        public int Index;
        public int Width;
        public Pt[] Points = Array.Empty<Pt>();
        public List<TlRgb> Colors = new();

        public Fan Clone() => new()
        {
            Index = Index,
            Width = Width,
            Points = (Pt[])Points.Clone(),
            Colors = new List<TlRgb>(Colors),
        };
    }

    private sealed class Track
    {
        public float Step;
        public float Speed;
        public int Width;
        public int Count;
        public TlRgb Color;
    }

    private sealed class TlGenerator
    {
        public const int PerFan = 26;
        private const int FanWidth = 203;
        private const int StepCap = 4096;

        private static readonly TlRgb[] WheelColors =
        {
            new(255, 0, 0), new(255, 128, 0), new(255, 255, 0), new(128, 255, 0), new(0, 255, 0),
            new(0, 255, 128), new(0, 255, 255), new(0, 128, 255), new(0, 0, 255), new(128, 0, 255),
            new(255, 0, 255), new(255, 0, 128), new(255, 255, 255),
        };

        private static readonly Pt[] BasePoints =
        {
            new() { X = 0, Y = 63 }, new() { X = 13, Y = 46 }, new() { X = 27, Y = 29 }, new() { X = 44, Y = 17 },
            new() { X = 61, Y = 7 }, new() { X = 77, Y = 0 }, new() { X = 94, Y = 0 }, new() { X = 111, Y = 0 },
            new() { X = 127, Y = 7 }, new() { X = 144, Y = 17 }, new() { X = 161, Y = 29 }, new() { X = 173, Y = 46 },
            new() { X = 187, Y = 63 }, new() { X = 0, Y = 99 }, new() { X = 13, Y = 116 }, new() { X = 27, Y = 133 },
            new() { X = 44, Y = 146 }, new() { X = 61, Y = 156 }, new() { X = 77, Y = 166 }, new() { X = 94, Y = 166 },
            new() { X = 111, Y = 166 }, new() { X = 127, Y = 156 }, new() { X = 144, Y = 146 }, new() { X = 161, Y = 133 },
            new() { X = 173, Y = 116 }, new() { X = 187, Y = 99 },
        };

        private readonly int mode;
        private readonly bool anticlockwise;
        private readonly List<TlRgb> colors;
        private readonly int seed;
        private readonly int speed = 10;
        private readonly List<Fan> fans = new();
        private readonly List<TlRgb> ribbon1 = new();
        private readonly List<TlRgb> ribbon2 = new();
        private readonly List<TlRgb> ribbon3 = new();
        private readonly List<Track> tracks1 = new();
        private readonly List<Track> tracks2 = new();
        private readonly List<TlRgb[]> frames = new();
        private List<Fan> fanList = new();
        private Random? random;
        private float step;
        private float stepBack;
        private int minX;
        private int minY;
        private int maxX;
        private int maxY;
        private int centerX;
        private int centerY;
        private int colorIndex;
        private bool isBack;
        private int counter;
        private int maxStep;
        private int fanIndex;
        private bool isInit;
        private int mWidth;
        private bool running;

        public TlGenerator(int mode, int fanCount, bool anticlockwise, List<TlRgb> colors, int seed)
        {
            this.mode = mode;
            this.anticlockwise = anticlockwise;
            this.colors = new List<TlRgb>(colors);
            this.seed = seed;
            var offset = 0;
            for (var i = 0; i < fanCount; i++)
            {
                var pts = (Pt[])BasePoints.Clone();
                for (var j = 0; j < pts.Length; j++)
                {
                    pts[j].X += offset;
                }
                fans.Add(new Fan { Index = i, Width = FanWidth, Points = pts });
                offset += FanWidth;
            }
        }

        public List<TlRgb[]> Run()
        {
            InitMatrix(fans);
            InitRibbon();
            running = true;
            var guard = 0;
            while (running && guard++ < StepCap)
            {
                Exec();
            }
            return frames;
        }

        private void Exec()
        {
            switch (mode)
            {
                case 8: ExecStaggered(); break;
                case 9: ExecTide(); break;
                case 10: ExecMixing(); break;
                case 11: ExecVoice(); break;
                case 12: ExecDoor(); break;
                case 13: ExecRender(); break;
                case 14: ExecRipple(); break;
                case 15: ExecReflect(); break;
                case 16: ExecTailChasing(); break;
                case 17: ExecPaint(); break;
                case 18: ExecPingPong(); break;
                case 20: ExecCoverCycle(); break;
                case 21: ExecWave(); break;
                case 22: ExecRacing(); break;
                case 23: ExecLottery(); break;
                case 24: ExecIntertwine(); break;
                case 25: ExecMeteorShower(); break;
                case 26: ExecCollide(); break;
                case 27: ExecElectric(); break;
                case 28: ExecKaleidoscope(); break;
                default: running = false; break;
            }
        }

        private void InitMatrix(List<Fan> list)
        {
            minX = int.MaxValue;
            minY = int.MaxValue;
            maxX = int.MinValue;
            maxY = int.MinValue;
            foreach (var fan in list)
            {
                foreach (var p in fan.Points)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }
            }
            centerX = (maxX - minX) / 2;
            centerY = (maxY - minY) / 2;
        }

        private static List<Fan> CloneAll(List<Fan> src)
        {
            var copy = new List<Fan>(src.Count);
            foreach (var f in src)
            {
                copy.Add(f.Clone());
            }
            return copy;
        }

        private void InitRibbon()
        {
            ribbon1.Clear();
            switch (mode)
            {
                case 8:
                    InitCruiseData();
                    break;
                case 9:
                    InitGather();
                    break;
                case 10:
                    InitBlend();
                    break;
                case 11:
                    random = new Random(seed);
                    maxStep = random.Next(minX, centerX);
                    break;
                case 13:
                    InitRender();
                    break;
                case 15:
                    InitReflect();
                    break;
                case 16:
                    InitCruiseData();
                    InitChase();
                    break;
                case 17:
                    InitPaint();
                    break;
                case 18:
                    InitPingPong();
                    break;
                case 20:
                    InitCoverCycle();
                    break;
                case 21:
                    InitWave();
                    break;
                case 22:
                    InitRacing();
                    break;
                case 23:
                    InitCruiseData();
                    InitLottery();
                    break;
                case 24:
                    InitCruiseData();
                    InitIntertwine();
                    break;
                case 25:
                    InitMeteorShower();
                    break;
                case 26:
                    InitCollide();
                    break;
                case 27:
                    InitElectric(0);
                    break;
                case 28:
                    InitKaleidoscope();
                    break;
            }
        }

        private static List<TlRgb> Gradient(TlRgb start, TlRgb end, int steps)
        {
            var list = new List<TlRgb>();
            for (var i = 0; i <= steps; i++)
            {
                var num = i / (float)steps;
                list.Add(new TlRgb(
                    unchecked((byte)(start.R + (int)((end.R - start.R) * num))),
                    unchecked((byte)(start.G + (int)((end.G - start.G) * num))),
                    unchecked((byte)(start.B + (int)((end.B - start.B) * num)))));
            }
            return list;
        }

        private static TlRgb Blend(TlRgb a, TlRgb b)
        {
            var r = a.R * 0.5 + b.R * 0.5;
            var g = a.G * 0.5 + b.G * 0.5;
            var bl = a.B * 0.5 + b.B * 0.5;
            var max = Math.Max(r, Math.Max(g, bl));
            if (max <= 0.0)
            {
                return default;
            }
            var scale = 255.0 / max;
            return new TlRgb((byte)(int)(r * scale), (byte)(int)(g * scale), (byte)(int)(bl * scale));
        }

        private static TlRgb Black => default;

        private TlRgb ColorOr(int index, TlRgb fallback) => colors.Count > index ? colors[index] : fallback;

        private static int Clamp(int value, int count) => value >= 0 ? (value >= count ? count - 1 : value) : 0;

        private void InitCruiseData()
        {
            fanList = CloneAll(fans);
            foreach (var fan in fanList)
            {
                MirrorBottomRow(fan);
            }
            InitMatrix(fanList);
        }

        private void MirrorBottomRow(Fan fan)
        {
            for (var j = 0; j < fan.Points.Length; j++)
            {
                if (fan.Points[j].Y > centerY)
                {
                    fan.Points[j].X = maxX + (maxX - fan.Points[j].X) + 10;
                }
            }
        }

        private void InitBlend()
        {
            var num = (maxX - minX) / 6;
            step = -(num / 2);
            ribbon1.Clear();
            ribbon2.Clear();
            ribbon3.Clear();
            var c1 = ColorOr(0, Black);
            var c2 = ColorOr(1, Black);
            var mix = Blend(c1, c2);
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c1, n2));
            ribbon1.AddRange(Gradient(c1, c1, num - n2));
            ribbon2.AddRange(Gradient(Black, c2, n2));
            ribbon2.AddRange(Gradient(c2, c2, num - n2));
            n2 = num * 4 / 5 * 2;
            ribbon3.AddRange(Gradient(mix, mix, num * 4 - n2));
            ribbon3.AddRange(Gradient(mix, Black, n2));
        }

        private void InitGather()
        {
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var unused in fan.Points)
                {
                    fan.Colors.Add(Black);
                }
            }
        }

        private void FillAll(IEnumerable<Fan> list, TlRgb color)
        {
            foreach (var fan in list)
            {
                fan.Colors.Clear();
                foreach (var unused in fan.Points)
                {
                    fan.Colors.Add(color);
                }
            }
        }

        private void InitRender()
        {
            if (anticlockwise)
            {
                fanIndex = fans.Count - 1;
            }
            else
            {
                fanIndex = 0;
            }
            isBack = false;
            colorIndex = 0;
            if (colorIndex >= colors.Count)
            {
                return;
            }
            FillAll(fans, colors[colorIndex]);
            colorIndex++;
        }

        private void InitReflect()
        {
            step = anticlockwise ? (int)(maxX * 0.7) : 0f;
            InitReflectRibbon();
        }

        private void InitReflectRibbon()
        {
            var steps = 25 + 15 * fans.Count;
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            ribbon1.Clear();
            ribbon1.AddRange(Gradient(Black, colors[colorIndex], steps));
        }

        private void InitPaint()
        {
            if (anticlockwise)
            {
                step = maxX;
                isBack = true;
            }
            else
            {
                step = 0f;
                isBack = false;
            }
            colorIndex = 0;
            if (colors.Count <= 0)
            {
                return;
            }
            FillAll(fans, colors[colors.Count - 1]);
        }

        private void InitChase()
        {
            ribbon1.Clear();
            ribbon2.Clear();
            if (colors.Count < 2)
            {
                return;
            }
            var num = 40 * fans.Count;
            var c1 = colors[0];
            var c2 = colors[1];
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c1, n2));
            ribbon1.AddRange(Gradient(c1, c1, num - n2));
            ribbon2.AddRange(Gradient(Black, c2, n2));
            ribbon2.AddRange(Gradient(c2, c2, num - n2));
            step = anticlockwise ? maxX : num;
        }

        private void InitPingPong()
        {
            colorIndex = 0;
            if (anticlockwise)
            {
                step = maxX;
                isBack = true;
            }
            else
            {
                step = 0f;
                isBack = false;
            }
            InitPingPongRibbon();
        }

        private void InitPingPongRibbon()
        {
            if (colors.Count <= 0)
            {
                return;
            }
            var num = 50 + 20 * fans.Count;
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var c = colors[colorIndex];
            ribbon1.Clear();
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c, n2));
            ribbon1.AddRange(Gradient(c, c, num - n2));
        }

        private void MirrorX(List<Fan> list)
        {
            foreach (var fan in list)
            {
                for (var j = 0; j < fan.Points.Length; j++)
                {
                    fan.Points[j].X = maxX - fan.Points[j].X;
                }
            }
        }

        private void InitCoverCycle()
        {
            if (anticlockwise)
            {
                fanList = CloneAll(fans);
                MirrorX(fanList);
                foreach (var fan in fanList)
                {
                    MirrorBottomRow(fan);
                }
                InitMatrix(fanList);
            }
            else
            {
                InitCruiseData();
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                var c = colors.Count > 0 ? colors[colors.Count - 1] : Black;
                foreach (var unused in fan.Points)
                {
                    fan.Colors.Add(c);
                }
            }
        }

        private void InitWave()
        {
            fanList = CloneAll(fans);
            var even = fanList.Count % 2 == 0;
            if (anticlockwise)
            {
                MirrorX(fanList);
                for (var k = 0; k < fanList.Count; k++)
                {
                    if (even ? k % 2 == 0 : k % 2 != 0)
                    {
                        FlipY(fanList[k]);
                    }
                }
                foreach (var fan in fanList)
                {
                    MirrorBottomRow(fan);
                }
                InitMatrix(fanList);
            }
            else
            {
                for (var k = 0; k < fanList.Count; k++)
                {
                    if (k % 2 != 0)
                    {
                        FlipY(fanList[k]);
                    }
                }
                foreach (var fan in fanList)
                {
                    MirrorBottomRow(fan);
                }
                InitMatrix(fanList);
            }
            InitWaveRibbon();
        }

        private void FlipY(Fan fan)
        {
            for (var j = 0; j < fan.Points.Length; j++)
            {
                fan.Points[j].Y = maxY - fan.Points[j].Y;
            }
        }

        private void InitWaveRibbon()
        {
            if (colors.Count <= 0)
            {
                return;
            }
            var num = 30 + 25 * fans.Count;
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var c = colors[colorIndex];
            ribbon1.Clear();
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c, n2));
            ribbon1.AddRange(Gradient(c, c, num - n2));
        }

        private void InitRacing()
        {
            var num = 30 + 25 * fans.Count;
            if (anticlockwise)
            {
                step = maxX + num + num / 2;
                isBack = true;
            }
            else
            {
                step = 0f;
                isBack = false;
            }
            fanList = CloneAll(fans);
            for (var i = 0; i < fanList.Count; i++)
            {
                if (i % 2 != 0)
                {
                    FlipY(fanList[i]);
                }
            }
            InitMatrix(fanList);
            if (colors.Count < 2)
            {
                return;
            }
            ribbon1.Clear();
            ribbon2.Clear();
            var c1 = colors[0];
            var c2 = colors[1];
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c1, n2));
            ribbon1.AddRange(Gradient(c1, c1, num - n2));
            ribbon2.AddRange(Gradient(Black, c2, n2));
            ribbon2.AddRange(Gradient(c2, c2, num - n2));
        }

        private void InitLottery()
        {
            if (colors.Count <= 0)
            {
                return;
            }
            ribbon1.Clear();
            var num = fans.Count * 4;
            var c1 = colors[0];
            var c2 = colors.Count > 1 ? colors[1] : Black;
            for (var i = 0; i < num; i++)
            {
                var c = i % 2 == 0 ? c1 : c2;
                for (var j = 0; j < 60; j++)
                {
                    ribbon1.Add(c);
                }
            }
        }

        private void InitIntertwine()
        {
            isInit = true;
            stepBack = centerX;
            if (colors.Count < 2)
            {
                return;
            }
            ribbon1.Clear();
            ribbon2.Clear();
            var num = 30 + 20 * fans.Count;
            var c1 = colors[0];
            var c2 = colors[1];
            var n2 = num / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c1, n2));
            ribbon1.AddRange(Gradient(c1, c1, num - n2));
            ribbon2.AddRange(Gradient(Black, c2, n2));
            ribbon2.AddRange(Gradient(c2, c2, num - n2));
        }

        private Track NewShowerTrack(int i, int num)
        {
            var t = new Track { Step = anticlockwise ? maxX + i * num : -(i * num) };
            t.Speed = random!.Next(10, 23) + 0.3f;
            t.Width = random.Next(40, 80);
            t.Color = WheelColors[random.Next(0, WheelColors.Length)];
            return t;
        }

        private void InitMeteorShower()
        {
            random ??= new Random(seed);
            var num = (maxX - minX) / 8;
            tracks1.Clear();
            for (var i = 0; i < 4; i++)
            {
                tracks1.Add(NewShowerTrack(i, num));
            }
            tracks2.Clear();
            for (var j = 0; j < 4; j++)
            {
                tracks2.Add(NewShowerTrack(j, num));
            }
        }

        private void InitCollide()
        {
            if (colors.Count < 2)
            {
                return;
            }
            var steps = 25 + 15 * fans.Count;
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            ribbon1.Clear();
            ribbon1.AddRange(Gradient(Black, colors[colorIndex], steps));
        }

        private void InitElectric(int width)
        {
            if (width <= 0)
            {
                width = 25 + 15 * fans.Count;
                mWidth = width;
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            ribbon1.Clear();
            var c = colors[colorIndex];
            var num = width / 5 * 2;
            ribbon1.AddRange(Gradient(Black, c, num));
            ribbon1.AddRange(Gradient(c, c, width - num));
        }

        private void InitKaleidoscope()
        {
            random ??= new Random(seed);
            var num = (maxX - minX) / 5;
            tracks1.Clear();
            for (var i = 0; i < 5; i++)
            {
                tracks1.Add(new Track
                {
                    Step = num * i,
                    Speed = 13f,
                    Width = num,
                    Color = WheelColors[random.Next(0, WheelColors.Length)],
                });
            }
            FillAll(fans, Black);
        }

        private void PushFrame()
        {
            var list = new List<TlRgb>();
            foreach (var fan in fans)
            {
                list.AddRange(fan.Colors);
            }
            frames.Add(list.ToArray());
        }

        private void ShareColors()
        {
            foreach (var fan in fanList)
            {
                foreach (var target in fans)
                {
                    if (target.Index == fan.Index)
                    {
                        target.Colors = fan.Colors;
                        break;
                    }
                }
            }
        }

        private void ExecStaggered()
        {
            if (counter >= 12 + (20 - speed * 2))
            {
                isBack = !isBack;
                counter = 0;
                if (!isBack)
                {
                    colorIndex++;
                    if (colorIndex >= colors.Count)
                    {
                        colorIndex = 0;
                        running = false;
                        return;
                    }
                }
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var item = colors[colorIndex];
            if (counter < 5)
            {
                var num = (counter * 40 + 1f) / 255f;
                item = new TlRgb((byte)(int)(item.R * num), (byte)(int)(item.G * num), (byte)(int)(item.B * num));
            }
            for (var i = 0; i < fanList.Count; i++)
            {
                var fan = fanList[i];
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    var left = p.X < centerX;
                    var lit = i % 2 == 0 ? (isBack ? !left : left) : (isBack ? left : !left);
                    fan.Colors.Add(lit ? item : Black);
                }
            }
            ShareColors();
            PushFrame();
            counter++;
        }

        private void ExecTide()
        {
            if (step > centerX)
            {
                step = 0f;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            var num = maxX - minX;
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var value = colors[colorIndex];
            foreach (var fan in fans)
            {
                for (var i = 0; i < fan.Points.Length; i++)
                {
                    var x = fan.Points[i].X;
                    if (x <= step + 15f || x >= num - step - 15f)
                    {
                        fan.Colors[i] = value;
                    }
                }
            }
            PushFrame();
            step += speed * 0.3f + 6.3f;
        }

        private void ExecMixing()
        {
            var num = (maxX - minX) / 4;
            if (!isBack && step > centerX - num / 2)
            {
                step = centerX + num / 3 * 2;
                isBack = true;
            }
            if (step < -(num * 4))
            {
                step = -num;
                isBack = false;
                running = false;
                return;
            }
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (isBack)
                    {
                        if (p.X < centerX)
                        {
                            if (p.X > step && p.X < step + num * 8)
                            {
                                var i = Clamp((int)(p.X - step), ribbon3.Count);
                                fan.Colors.Add(ribbon3[i]);
                            }
                            else
                            {
                                fan.Colors.Add(Black);
                            }
                        }
                        else if (p.X < maxX - step && p.X > maxX - step - num * 8)
                        {
                            var i = Clamp((int)(maxX - p.X - step), ribbon3.Count);
                            fan.Colors.Add(ribbon3[i]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X < centerX)
                    {
                        if (p.X > step && p.X < step + num)
                        {
                            var i = Clamp((int)(p.X - step), ribbon1.Count);
                            fan.Colors.Add(ribbon1[i]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X < maxX - step && p.X > maxX - step - num)
                    {
                        var i = Clamp((int)(maxX - p.X - step), ribbon2.Count);
                        fan.Colors.Add(ribbon2[i]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed * 1.5f + 20.3f;
            }
            else
            {
                step += speed * 0.8f + 10.3f;
            }
        }

        private void ExecVoice()
        {
            if (step > maxStep)
            {
                isBack = true;
            }
            if (step < 0f)
            {
                step = 0f;
                isBack = false;
                maxStep = random!.Next(centerX / 5, centerX);
                colorIndex = random.Next(0, WheelColors.Length);
                if (counter == WheelColors.Length)
                {
                    running = false;
                    return;
                }
                counter++;
            }
            var item = WheelColors[colorIndex];
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    bool lit;
                    if (isBack)
                    {
                        lit = p.X > centerX
                            ? !(p.X >= centerX + step)
                            : p.X >= centerX - step && p.X < centerX;
                    }
                    else
                    {
                        lit = p.X > centerX ? !(p.X >= centerX + step) : p.X > centerX - step;
                    }
                    fan.Colors.Add(lit ? item : Black);
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed + 15.3f;
            }
            else
            {
                step += speed + 15.3f;
            }
        }

        private void ExecDoor()
        {
            if (step > centerX + 15)
            {
                isBack = true;
            }
            if (step < 0f)
            {
                step = 0f;
                isBack = false;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var item = colors[colorIndex];
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    bool lit = p.X > centerX ? p.X > maxX - step : p.X < step;
                    fan.Colors.Add(lit ? item : Black);
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed * 0.4f + 5.3f;
            }
            else
            {
                step += speed * 0.4f + 5.3f;
            }
        }

        private static int FanCenter(Fan fan)
        {
            var lo = int.MaxValue;
            var hi = int.MinValue;
            foreach (var p in fan.Points)
            {
                hi = Math.Max(hi, p.X);
                lo = Math.Min(lo, p.X);
            }
            return (hi - lo) / 2 + lo;
        }

        private void ExecRender()
        {
            Fan? fan = null;
            if (fanIndex < fans.Count)
            {
                fan = fans[fanIndex];
            }
            if (fan != null)
            {
                var fanCenter = FanCenter(fan);
                if (step > fan.Width / 2)
                {
                    step = 0f;
                    if (isBack)
                    {
                        if (anticlockwise)
                        {
                            fanIndex++;
                            if (fanIndex >= fans.Count)
                            {
                                isBack = false;
                                fanIndex = fans.Count - 1;
                                colorIndex++;
                                if (colorIndex >= colors.Count)
                                {
                                    colorIndex = 0;
                                    running = false;
                                }
                                return;
                            }
                            fan = fans[fanIndex];
                        }
                        else
                        {
                            fanIndex--;
                            if (fanIndex < 0)
                            {
                                isBack = false;
                                fanIndex = 0;
                                colorIndex++;
                                if (colorIndex >= colors.Count)
                                {
                                    colorIndex = 0;
                                    running = false;
                                }
                                return;
                            }
                            fan = fans[fanIndex];
                        }
                    }
                    else if (anticlockwise)
                    {
                        fanIndex--;
                        if (fanIndex < 0)
                        {
                            isBack = true;
                            fanIndex = 0;
                            return;
                        }
                        fan = fans[fanIndex];
                    }
                    else
                    {
                        fanIndex++;
                        if (fanIndex >= fans.Count)
                        {
                            isBack = true;
                            fanIndex = fans.Count - 1;
                            return;
                        }
                        fan = fans[fanIndex];
                    }
                }
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                }
                var value = colors[colorIndex];
                for (var i = 0; i < fan.Points.Length; i++)
                {
                    var p = fan.Points[i];
                    var rowMatches = isBack ? p.Y > centerY : p.Y < centerY;
                    if (!rowMatches)
                    {
                        continue;
                    }
                    if (p.X < fanCenter + 5 && p.X >= fanCenter - step - 5f)
                    {
                        fan.Colors[i] = value;
                    }
                    if (p.X > fanCenter && p.X < fanCenter + step + 5f)
                    {
                        fan.Colors[i] = value;
                    }
                }
            }
            PushFrame();
            step += speed * 0.4f + 4.3f;
        }

        private void ExecRipple()
        {
            if (step > maxX * 1.5)
            {
                step = 0f;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var item = colors[colorIndex];
            var num = 8 * fans.Count;
            var num2 = 50 * fans.Count;
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    bool lit = false;
                    if (p.X > centerX)
                    {
                        for (var k = 0; k < 4 && !lit; k++)
                        {
                            lit = p.X >= centerX + step - num2 * k && p.X < centerX + step + num - num2 * k;
                        }
                    }
                    else
                    {
                        for (var k = 0; k < 4 && !lit; k++)
                        {
                            lit = p.X < centerX - step + num2 * k && p.X >= centerX - step - num + num2 * k;
                        }
                    }
                    fan.Colors.Add(lit ? item : Black);
                }
            }
            PushFrame();
            step += speed * 0.7f + 8.3f;
        }

        private void ExecReflect()
        {
            if (anticlockwise && step < 0f)
            {
                step = (int)(maxX * 0.7);
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitReflectRibbon();
            }
            if (!anticlockwise && step > maxX * 0.7)
            {
                step = 0f;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitReflectRibbon();
            }
            var num = 25 + 15 * fans.Count;
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.X > centerX)
                    {
                        if (p.X > maxX - step && p.X <= maxX - step + num)
                        {
                            var i = (int)(p.X - (maxX - step));
                            if (!anticlockwise)
                            {
                                i = ribbon1.Count - 1 - (int)(p.X - (maxX - step));
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X >= step - num && p.X < step)
                    {
                        var i = (int)(step - p.X);
                        if (!anticlockwise)
                        {
                            i = ribbon1.Count - 1 - (int)(step - p.X);
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            PushFrame();
            if (anticlockwise)
            {
                step -= speed * 0.6f + 6.3f;
            }
            else
            {
                step += speed * 0.6f + 6.3f;
            }
        }

        private void ExecTailChasing()
        {
            if (colors.Count < 2)
            {
                running = false;
                return;
            }
            var num = 40 * fans.Count;
            if (anticlockwise && step < 0f)
            {
                step = maxX;
                running = false;
                return;
            }
            if (!anticlockwise && step > maxX + num)
            {
                step = num;
                running = false;
                return;
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.X < step && p.X >= step - num)
                    {
                        var i = (int)(step - p.X);
                        if (!anticlockwise)
                        {
                            i = ribbon1.Count - 1 - (int)(step - p.X);
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else if (p.X < centerX + step && p.X >= centerX + step - num)
                    {
                        var i = (int)(centerX + step - p.X);
                        if (!anticlockwise)
                        {
                            i = ribbon2.Count - 1 - (int)(centerX + step - p.X);
                        }
                        fan.Colors.Add(ribbon2[Clamp(i, ribbon2.Count)]);
                    }
                    else if (p.X < step - centerX && p.X >= step - centerX - num)
                    {
                        var i = (int)(step - centerX - p.X);
                        if (!anticlockwise)
                        {
                            i = ribbon2.Count - 1 - (int)(step - centerX - p.X);
                        }
                        fan.Colors.Add(ribbon2[Clamp(i, ribbon2.Count)]);
                    }
                    else if (step > maxX)
                    {
                        var num5 = step - maxX;
                        if (p.X >= 0 && p.X < num5)
                        {
                            fan.Colors.Add(ribbon1[Clamp(ribbon1.Count - 1 - p.X, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (step < num)
                    {
                        if (anticlockwise && p.X > maxX - (num - step) && p.X < maxX + num)
                        {
                            fan.Colors.Add(ribbon1[Clamp(p.X, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            ShareColors();
            PushFrame();
            if (anticlockwise)
            {
                if (step <= 0f)
                {
                    step = maxX;
                }
                step -= speed * 0.8f + 8.3f;
            }
            else
            {
                step += speed * 0.8f + 8.3f;
            }
        }

        private void ExecPaint()
        {
            if (step > maxX)
            {
                isBack = true;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            if (step < 0f)
            {
                isBack = false;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var value = colors[colorIndex];
            foreach (var fan in fans)
            {
                for (var i = 0; i < fan.Points.Length; i++)
                {
                    var x = fan.Points[i].X;
                    if (isBack ? x >= step : x < step)
                    {
                        fan.Colors[i] = value;
                    }
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed + 14.3f;
            }
            else
            {
                step += speed + 14.3f;
            }
        }

        private void ExecPingPong()
        {
            var num = 50 + 20 * fans.Count;
            if (step > maxX)
            {
                isBack = true;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitPingPongRibbon();
            }
            if (step < -num)
            {
                isBack = false;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitPingPongRibbon();
            }
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.X > step && p.X <= step + num)
                    {
                        var i = (int)(p.X - step);
                        if (isBack)
                        {
                            i = ribbon1.Count - 1 - (int)(p.X - step);
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed + 14.3f;
            }
            else
            {
                step += speed + 14.3f;
            }
        }

        private void ExecCoverCycle()
        {
            if (step > maxX + 20)
            {
                step = 0f;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
            }
            if (colorIndex >= colors.Count)
            {
                colorIndex = 0;
            }
            var value = colors[colorIndex];
            foreach (var fan in fanList)
            {
                for (var i = 0; i < fan.Points.Length; i++)
                {
                    if (fan.Points[i].X < step)
                    {
                        fan.Colors[i] = value;
                    }
                }
            }
            ShareColors();
            PushFrame();
            step += 2f + speed * 0.73f + 1.5f * fans.Count;
        }

        private void ExecWave()
        {
            var num = 30 + 25 * fans.Count;
            if (step > maxX + num)
            {
                step = 0f;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitWaveRibbon();
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.X > step - num && p.X < step)
                    {
                        var i = Clamp(ribbon1.Count - 1 - (int)(step - p.X), ribbon1.Count);
                        fan.Colors.Add(ribbon1[i]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            ShareColors();
            PushFrame();
            step += 2f + speed * 0.73f + 1.5f * fans.Count;
        }

        private void ExecRacing()
        {
            var num = 30 + 25 * fans.Count;
            if (step > maxX + num + num / 2)
            {
                isBack = true;
                if (anticlockwise)
                {
                    running = false;
                    return;
                }
            }
            if (step < 0f)
            {
                isBack = false;
                if (!anticlockwise)
                {
                    running = false;
                    return;
                }
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.Y < centerY)
                    {
                        if (p.X > step - num && p.X < step)
                        {
                            var i = ribbon1.Count - 1 - (int)(step - p.X);
                            if (isBack)
                            {
                                i = (int)(step - p.X);
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X > step - num - num / 2 && p.X < step - num / 2)
                    {
                        var i = ribbon2.Count - 1 - (int)(step - num / 2 - p.X);
                        if (isBack)
                        {
                            i = (int)(step - num / 2 - p.X);
                        }
                        fan.Colors.Add(ribbon2[Clamp(i, ribbon2.Count)]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            ShareColors();
            PushFrame();
            if (isBack)
            {
                step -= 2f + speed * 0.73f + 1.5f * fans.Count;
            }
            else
            {
                step += 2f + speed * 0.73f + 1.5f * fans.Count;
            }
        }

        private void ExecLottery()
        {
            if (anticlockwise && step >= ribbon1.Count)
            {
                step = 0f;
                running = false;
                return;
            }
            if (!anticlockwise && step < 0f)
            {
                step = ribbon1.Count - 1;
                running = false;
                return;
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    var index = (int)((p.X / 8 * 6) + step) % ribbon1.Count;
                    fan.Colors.Add(ribbon1[index]);
                }
            }
            ShareColors();
            PushFrame();
            if (anticlockwise)
            {
                step += speed * 0.4f + 4.3f;
            }
            else
            {
                if (step <= 0f)
                {
                    step = ribbon1.Count - 1;
                }
                step -= speed * 0.4f + 4.3f;
            }
        }

        private void ExecIntertwine()
        {
            if (colors.Count < 2)
            {
                running = false;
                return;
            }
            var num = 30 + 20 * fans.Count;
            if (step > maxX + num)
            {
                step = num;
                running = false;
                return;
            }
            if (stepBack < -num)
            {
                stepBack = maxX - num;
                isInit = false;
            }
            foreach (var fan in fanList)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (p.X > step - num && p.X <= step && p.X >= stepBack && p.X < stepBack + num)
                    {
                        fan.Colors.Add(Blend(ribbon1[^1], ribbon2[^1]));
                    }
                    else if (p.X > step - num && p.X <= step)
                    {
                        var i = Clamp((int)(p.X - (step - num)), ribbon1.Count);
                        fan.Colors.Add(ribbon1[i]);
                    }
                    else if (p.X >= stepBack && p.X < stepBack + num)
                    {
                        if (isInit && p.X > centerX)
                        {
                            fan.Colors.Add(Black);
                            continue;
                        }
                        var i = ribbon1.Count - 1 - (int)(p.X - stepBack);
                        i = Clamp(i, ribbon2.Count);
                        fan.Colors.Add(ribbon2[i]);
                    }
                    else if (step > maxX && p.X < step - maxX && stepBack < 0f && p.X > maxX + stepBack)
                    {
                        fan.Colors.Add(Blend(ribbon1[^1], ribbon2[^1]));
                    }
                    else if (step > maxX && p.X < step - maxX)
                    {
                        var i = Clamp(ribbon1.Count - 1 - (int)(step - maxX - p.X), ribbon1.Count);
                        fan.Colors.Add(ribbon1[i]);
                    }
                    else if (stepBack < 0f && p.X > maxX + stepBack)
                    {
                        var i = Clamp((int)(p.X - (maxX + stepBack)), ribbon2.Count);
                        fan.Colors.Add(ribbon2[i]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            ShareColors();
            PushFrame();
            step += speed + 10.3f;
            stepBack -= speed + 10.3f;
        }

        private void ShowerRespawn(Track t)
        {
            t.Speed = 10f + (float)random!.NextDouble() * (speed * 1.3f) + 0.3f;
            t.Width = random.Next(40, 80);
            t.Color = WheelColors[random.Next(0, WheelColors.Length)];
            t.Count++;
        }

        private void ShowerWrap(List<Track> list)
        {
            foreach (var t in list)
            {
                if (anticlockwise && t.Step < 0f)
                {
                    t.Step = maxX;
                    ShowerRespawn(t);
                }
                if (!anticlockwise && t.Step > maxX)
                {
                    t.Step = 0f;
                    ShowerRespawn(t);
                }
            }
        }

        private TlRgb TrackColor(List<Track> list, Pt p)
        {
            foreach (var t in list)
            {
                if (t.Count < 5 && p.X > t.Step - t.Width && p.X <= t.Step)
                {
                    return t.Color;
                }
            }
            return Black;
        }

        private void ExecMeteorShower()
        {
            ShowerWrap(tracks1);
            ShowerWrap(tracks2);
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    fan.Colors.Add(TrackColor(p.Y < centerY ? tracks1 : tracks2, p));
                }
            }
            PushFrame();
            var allBlack = true;
            foreach (var fan in fans)
            {
                foreach (var c in fan.Colors)
                {
                    if (c != Black)
                    {
                        allBlack = false;
                    }
                }
            }
            if (allBlack)
            {
                running = false;
                return;
            }
            var sign = anticlockwise ? -1f : 1f;
            foreach (var t in tracks1)
            {
                t.Step += sign * t.Speed;
            }
            foreach (var t in tracks2)
            {
                t.Step += sign * t.Speed;
            }
        }

        private void ExecCollide()
        {
            var num = 25 + 15 * fans.Count;
            if (step > centerX + num)
            {
                isBack = true;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitCollide();
            }
            if (step < 0f)
            {
                isBack = false;
            }
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (step > centerX)
                    {
                        if (p.X < centerX && p.X > step - num)
                        {
                            var i = (int)(p.X - (step - num));
                            if (isBack)
                            {
                                i = ribbon1.Count - 1 - (int)(p.X - (step - num));
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else if (p.X > centerX && p.X < maxX - step + num)
                        {
                            var i = ribbon1.Count - 1 - (int)(p.X - (maxX - step));
                            if (isBack)
                            {
                                i = (int)(p.X - (maxX - step));
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X > step - num && p.X < step)
                    {
                        var i = (int)(p.X - (step - num));
                        if (isBack)
                        {
                            i = ribbon1.Count - 1 - (int)(p.X - (step - num));
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else if (p.X < maxX - step + num && p.X > maxX - step)
                    {
                        var i = ribbon1.Count - 1 - (int)(p.X - (maxX - step));
                        if (isBack)
                        {
                            i = (int)(p.X - (maxX - step));
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed * 1.6f + 12.3f;
            }
            else
            {
                step += speed * 1.6f + 12.3f;
            }
        }

        private void ExecElectric()
        {
            if (step > centerX + mWidth)
            {
                isBack = true;
                mWidth = 30 + 50 * fans.Count;
                step = centerX + mWidth;
                InitElectric(mWidth);
            }
            if (isBack && step < 0f)
            {
                isBack = false;
                mWidth = 25 + 15 * fans.Count;
                colorIndex++;
                if (colorIndex >= colors.Count)
                {
                    colorIndex = 0;
                    running = false;
                    return;
                }
                InitElectric(mWidth);
            }
            foreach (var fan in fans)
            {
                fan.Colors.Clear();
                foreach (var p in fan.Points)
                {
                    if (step > centerX)
                    {
                        if (p.X < centerX && p.X > step - mWidth)
                        {
                            var i = (int)(p.X - (step - mWidth));
                            if (isBack)
                            {
                                i = ribbon1.Count - 1 - (int)(p.X - (step - mWidth));
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else if (p.X > centerX && p.X < maxX - step + mWidth)
                        {
                            var i = ribbon1.Count - 1 - (int)(p.X - (maxX - step));
                            if (isBack)
                            {
                                i = (int)(p.X - (maxX - step));
                            }
                            fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                        }
                        else
                        {
                            fan.Colors.Add(Black);
                        }
                    }
                    else if (p.X > step - mWidth && p.X < step)
                    {
                        var i = (int)(p.X - (step - mWidth));
                        if (isBack)
                        {
                            i = ribbon1.Count - 1 - (int)(p.X - (step - mWidth));
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else if (p.X < maxX - step + mWidth && p.X > maxX - step)
                    {
                        var i = ribbon1.Count - 1 - (int)(p.X - (maxX - step));
                        if (isBack)
                        {
                            i = (int)(p.X - (maxX - step));
                        }
                        fan.Colors.Add(ribbon1[Clamp(i, ribbon1.Count)]);
                    }
                    else
                    {
                        fan.Colors.Add(Black);
                    }
                }
            }
            PushFrame();
            if (isBack)
            {
                step -= speed * 2 + 25.3f;
            }
            else
            {
                step += speed * 0.8f + 7.3f;
            }
        }

        private void ExecKaleidoscope()
        {
            if (counter >= WheelColors.Length)
            {
                running = false;
                return;
            }
            foreach (var t in tracks1)
            {
                if (t.Step < -t.Width - centerX % t.Width)
                {
                    t.Step = centerX;
                    t.Color = WheelColors[random!.Next(0, WheelColors.Length)];
                    counter++;
                }
            }
            foreach (var fan in fans)
            {
                for (var i = 0; i < fan.Points.Length; i++)
                {
                    var p = fan.Points[i];
                    foreach (var t in tracks1)
                    {
                        if (p.X > t.Step - 13f && p.X < t.Step + t.Width + 13f && p.X < centerX)
                        {
                            fan.Colors[i] = t.Color;
                        }
                        if (p.X > maxX - t.Step - t.Width - 13f && p.X < maxX - t.Step + 13f && p.X > centerX)
                        {
                            fan.Colors[i] = t.Color;
                        }
                    }
                }
            }
            foreach (var t in tracks1)
            {
                t.Step -= t.Speed;
            }
            PushFrame();
        }
    }
}
