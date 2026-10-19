using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One receiver taking part in a merged effect. Order in the list is the light sequence.</summary>
/// <param name="SliceDirection">1 mirrors this receiver's slice of the strip; 0 leaves it.</param>
/// <param name="InfRightAttach">SL-INF only: the receiver reports a fan count of 10 or more (right-attached cable).</param>
public readonly record struct FlexMergeParticipant(
    string Mac, FlexFamily Family, int Fans, int SliceDirection = 0, bool InfRightAttach = false);

/// <summary>A participant's share of a merged effect, shaped like a stored animation input.</summary>
public sealed class FlexMergeSlice
{
    public required string Mac { get; init; }

    /// <summary>FrameCount * Leds * 3 final wire bytes, frame-major, R,G,B per LED.</summary>
    public required byte[] Frames { get; init; }

    public required int FrameCount { get; init; }

    public required int Leds { get; init; }

    public required double IntervalMs { get; init; }
}

/// <summary>One effect over a virtual strip (13 dots per TL fan, 8 per SL-INF fan, at least 24) that each receiver slices; brightness is baked in and only the input colors are power-capped.</summary>
public static class FlexMergeEffects
{
    public const int TlDotsPerFan = 13;
    public const int SlInfDotsPerFan = 8;
    public const int MinStripDots = 24;
    public const int MaxStripDots = 720;
    public const double BaseIntervalMs = 12;
    private const int ColorSlots = 6;
    private const int ColorSumCap = 600;

    private static readonly Dictionary<string, int> Modes = new(StringComparer.Ordinal)
    {
        ["rainbow"] = 1,
        ["rainbowMorph"] = 2,
        ["static"] = 3,
        ["breathing"] = 4,
        ["runway"] = 5,
        ["meteor"] = 6,
        ["stack"] = 7,
        ["twinkle"] = 8,
        ["colorCycle"] = 9,
        ["coverCycle"] = 10,
        ["wave"] = 11,
        ["meteorShower"] = 12,
    };

    private static readonly string[] OrderedKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "stack",
        "coverCycle", "wave", "meteorShower", "twinkle",
    };

    private static readonly int[] BrightnessBytes = { 0, 64, 128, 192, 255 };

    /// <summary>The merge effect list in UI order. The same twelve apply to both families.</summary>
    public static IReadOnlyList<string> MergeKeys(FlexFamily family) => OrderedKeys;

    public static bool IsMergeable(FlexFamily family, string key) => Modes.ContainsKey(key);

    /// <summary>Rainbow Morph and Twinkle play per receiver as a whole-fan single effect; <see cref="Render"/> refuses them.</summary>
    public static bool IsPerReceiver(string key) => key is "rainbowMorph" or "twinkle";

    /// <summary>Speed level 0..4 (slowest to fastest) to the interval multiplier 7..3.</summary>
    public static int SpeedMultiplier(int speedLevel) => 7 - Math.Clamp(speedLevel, 0, 4);

    /// <summary>Brightness level 0..4 to the generator byte 0, 64, 128, 192, 255.</summary>
    public static byte BrightnessByte(int brightnessLevel) => (byte)BrightnessBytes[Math.Clamp(brightnessLevel, 0, 4)];

    public static int DotsOf(FlexMergeParticipant p) =>
        p.Fans * (p.Family == FlexFamily.SlInfFlex ? SlInfDotsPerFan : TlDotsPerFan);

    public static IReadOnlyList<FlexMergeSlice> Render(
        string key, IReadOnlyList<(string Mac, FlexFamily Family, int Fans)> participantsInSequenceOrder,
        int speedLevel, int direction, int brightnessLevel, IReadOnlyList<RgbColor> colors)
    {
        var list = new List<FlexMergeParticipant>(participantsInSequenceOrder.Count);
        foreach (var p in participantsInSequenceOrder)
        {
            list.Add(new FlexMergeParticipant(p.Mac, p.Family, p.Fans));
        }
        return Render(key, list, speedLevel, direction, brightnessLevel, colors);
    }

    public static IReadOnlyList<FlexMergeSlice> Render(
        string key, IReadOnlyList<FlexMergeParticipant> participantsInSequenceOrder,
        int speedLevel, int direction, int brightnessLevel, IReadOnlyList<RgbColor> colors)
    {
        if (!Modes.TryGetValue(key, out var mode))
        {
            throw new ArgumentException("Unknown merge effect", nameof(key));
        }
        if (IsPerReceiver(key))
        {
            throw new NotSupportedException("Rainbow Morph and Twinkle play per receiver, not over the shared strip.");
        }
        var total = 0;
        foreach (var p in participantsInSequenceOrder)
        {
            if (p.Fans < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(participantsInSequenceOrder), "A participant needs at least one fan.");
            }
            total += DotsOf(p);
        }
        if (total < 1 || total > MaxStripDots)
        {
            throw new ArgumentOutOfRangeException(nameof(participantsInSequenceOrder), "Strip length out of range.");
        }

        var dir = direction == 1 ? 1 : 0;
        var bright = BrightnessByte(brightnessLevel);
        var strip = BuildStrip(mode, Math.Max(total, MinStripDots), bright, dir, CapColors(colors));
        var interval = SpeedMultiplier(speedLevel) * BaseIntervalMs;

        var result = new List<FlexMergeSlice>(participantsInSequenceOrder.Count);
        var start = 0;
        foreach (var p in participantsInSequenceOrder)
        {
            var dots = DotsOf(p);
            var stop = start + dots - 1;
            var slice = p.Family == FlexFamily.SlInfFlex
                ? SliceInf(strip, start, stop, p.SliceDirection == 1 ? 1 : 0, p.InfRightAttach ? 1 : 0)
                : SliceTl(strip, start, stop, p.SliceDirection == 1 ? 1 : 0);
            result.Add(new FlexMergeSlice
            {
                Mac = p.Mac,
                Frames = slice,
                FrameCount = strip.Count,
                Leds = p.Fans * FlexProtocol.LedsPerFan(p.Family),
                IntervalMs = interval,
            });
            start = stop + 1;
        }
        return result;
    }

    private static byte[,] CapColors(IReadOnlyList<RgbColor> colors)
    {
        var c = new byte[ColorSlots, 3];
        for (var i = 0; i < Math.Min(colors.Count, ColorSlots); i++)
        {
            var r = colors[i].R;
            var g = colors[i].G;
            var b = colors[i].B;
            while (r + g + b > ColorSumCap)
            {
                r = (byte)(r * 0.95);
                g = (byte)(g * 0.95);
                b = (byte)(b * 0.95);
            }
            c[i, 0] = r;
            c[i, 1] = g;
            c[i, 2] = b;
        }
        return c;
    }

    /// <summary>Frames of a strip, each 3 planes (R, G, B) of Leds entries.</summary>
    private sealed class Strip
    {
        private readonly List<byte[]> _frames = new();

        public Strip(int leds) => Leds = leds;

        public int Leds { get; }

        public int Count => _frames.Count;

        public byte[] Add()
        {
            var f = new byte[3 * Leds];
            _frames.Add(f);
            return f;
        }

        public byte Get(int frame, int channel, int led) => _frames[frame][(channel * Leds) + led];
    }

    private static byte Scale(int component, int bright) => (byte)((component * bright) >> 8);

    private static void Put(byte[] frame, int leds, int led, byte r, byte g, byte b)
    {
        frame[led] = r;
        frame[leds + led] = g;
        frame[(2 * leds) + led] = b;
    }

    private static Strip BuildStrip(int mode, int leds, int bright, int dir, byte[,] col)
    {
        return mode switch
        {
            1 => Rainbow(leds, bright, dir),
            3 => StaticColor(leds, bright, col),
            4 => Breathing(leds, bright, col),
            5 => Runway(leds, bright, col),
            6 => Meteor(leds, bright, dir, col),
            7 => Stack(leds, bright, dir, col),
            9 => ColorCycle(leds, bright, dir, col),
            10 => CoverCycle(leds, bright, dir, col),
            11 => Wave(leds, bright, dir, col),
            12 => MeteorShower(leds, bright, dir, col),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static readonly byte[,] RainbowRing =
    {
        { 255, 224, 192, 160, 128, 96, 64, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 32, 64, 96, 128, 160, 192, 224 },
        { 0, 32, 64, 96, 128, 160, 192, 224, 255, 224, 192, 160, 128, 96, 64, 32, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 0, 0, 0, 32, 64, 96, 128, 160, 192, 224, 255, 224, 192, 160, 128, 96, 64, 32 },
    };

    private static Strip Rainbow(int leds, int bright, int dir)
    {
        var strip = new Strip(leds);
        var cell = new byte[3, 24];
        for (var i = 0; i < 24; i++)
        {
            var src = i;
            for (var j = 0; j < 24; j++)
            {
                var at = dir == 0 ? 24 - j - 1 : j;
                for (var ch = 0; ch < 3; ch++)
                {
                    cell[ch, at] = Scale(RainbowRing[ch, src], bright);
                }
                src++;
                if (src >= 24)
                {
                    src -= 24;
                }
            }
            var f = strip.Add();
            for (var led = 0; led < leds; led++)
            {
                var j = led % 24;
                Put(f, leds, led, cell[0, j], cell[1, j], cell[2, j]);
            }
        }
        return strip;
    }

    private static Strip StaticColor(int leds, int bright, byte[,] col)
    {
        var strip = new Strip(leds);
        var f = strip.Add();
        for (var led = 0; led < leds; led++)
        {
            Put(f, leds, led, Scale(col[0, 0], bright), Scale(col[0, 1], bright), Scale(col[0, 2], bright));
        }
        return strip;
    }

    private static Strip Breathing(int leds, int bright, byte[,] col)
    {
        var strip = new Strip(leds);
        var level = 0;
        for (var i = 0; i < 170; i++)
        {
            var b = (byte)(level * 3);
            var f = strip.Add();
            for (var led = 0; led < leds; led++)
            {
                Put(
                    f, leds, led,
                    Scale(Scale(col[0, 0], b), bright), Scale(Scale(col[0, 1], b), bright), Scale(Scale(col[0, 2], b), bright));
            }
            level = i >= 85 ? level - 1 : level + 1;
        }
        return strip;
    }

    private static Strip Runway(int leds, int bright, byte[,] col)
    {
        const int width = 12;
        var strip = new Strip(leds);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var j = 0; j < leds + width - 1; j++)
            {
                var f = strip.Add();
                for (var k = 0; k < leds; k++)
                {
                    var slot = k <= j && k + width > j ? 0 : 1;
                    var at = pass != 0 ? leds - k - 1 : k;
                    Put(f, leds, at, Scale(col[slot, 0], bright), Scale(col[slot, 1], bright), Scale(col[slot, 2], bright));
                }
            }
        }
        return strip;
    }

    private static readonly byte[] MeteorTail = { 12, 16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 255 };

    private static Strip Meteor(int leds, int bright, int dir, byte[,] col)
    {
        const int tail = 12;
        var strip = new Strip(leds);
        var line = new byte[3, leds + tail];
        for (var i = 0; i < tail; i++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                line[ch, i] = Scale(col[0, ch], MeteorTail[i]);
            }
        }
        var origin = tail;
        for (var j = 0; j < leds + tail; j++)
        {
            var f = strip.Add();
            for (var i = 0; i < leds; i++)
            {
                var at = dir == 1 ? leds - i - 1 : i;
                var src = origin + i;
                if (src >= leds + tail)
                {
                    src = src - leds - tail;
                }
                Put(f, leds, at, Scale(line[0, src], bright), Scale(line[1, src], bright), Scale(line[2, src], bright));
            }
            origin = origin != 0 ? origin - 1 : leds + tail - 1;
        }
        return strip;
    }

    private static Strip Stack(int leds, int bright, int dir, byte[,] col)
    {
        const int width = 12;
        var strip = new Strip(leds);
        var held = new byte[3, leds];
        var passes = 1;
        for (var span = width; span < leds; span += width)
        {
            passes++;
        }
        var trimmed = 0;
        for (var pass = 0; pass < passes; pass++)
        {
            for (var i = 0; i < leds - trimmed; i++)
            {
                for (var k = 0; k < leds - trimmed; k++)
                {
                    var lit = k <= i && k + width > i;
                    var at = dir == 1 ? leds - k - 1 : k;
                    held[0, at] = Scale(lit ? col[0, 0] : 0, bright);
                    held[1, at] = Scale(lit ? col[0, 1] : 0, bright);
                    held[2, at] = Scale(lit ? col[0, 2] : 0, bright);
                }
                var f = strip.Add();
                for (var led = 0; led < leds; led++)
                {
                    Put(f, leds, led, held[0, led], held[1, led], held[2, led]);
                }
            }
            trimmed += width;
        }
        return strip;
    }

    private static readonly byte[] ColorCycleMask =
    {
        1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    private static Strip ColorCycle(int leds, int bright, int dir, byte[,] col)
    {
        const int period = 48;
        var strip = new Strip(leds);
        var cell = new byte[3, period];
        for (var i = 0; i < period; i++)
        {
            var src = i;
            for (var j = 0; j < period; j++)
            {
                var at = dir == 0 ? period - j - 1 : j;
                var m = ColorCycleMask[src];
                for (var ch = 0; ch < 3; ch++)
                {
                    cell[ch, at] = m != 0 ? Scale(col[m - 1, ch], bright) : (byte)0;
                }
                src++;
                if (src >= period)
                {
                    src -= period;
                }
            }
            FillPeriodic(strip.Add(), leds, period, cell);
        }
        return strip;
    }

    private static void FillPeriodic(byte[] frame, int leds, int period, byte[,] cell)
    {
        for (var led = 0; led < leds; led++)
        {
            var j = led % period;
            Put(frame, leds, led, cell[0, j], cell[1, j], cell[2, j]);
        }
    }

    private static Strip CoverCycle(int leds, int bright, int dir, byte[,] col)
    {
        var strip = new Strip(leds);
        var line = new byte[3, leds];
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < leds; j++)
            {
                for (var k = 0; k < j; k++)
                {
                    line[0, k] = Scale(col[i, 0], bright);
                    line[1, k] = Scale(col[i, 1], bright);
                    line[2, k] = Scale(col[i, 2], bright);
                }
                var other = i == 0 ? 1 : 0;
                for (var k = j; k < leds; k++)
                {
                    line[0, k] = Scale(col[other, 0], bright);
                    line[1, k] = Scale(col[other, 1], bright);
                    line[2, k] = Scale(col[other, 2], bright);
                }
                var f = strip.Add();
                for (var k = 0; k < leds; k++)
                {
                    var from = dir == 1 ? leds - k - 1 : k;
                    Put(f, leds, k, line[0, from], line[1, from], line[2, from]);
                }
            }
        }
        return strip;
    }

    private static readonly byte[] WaveProfile =
    {
        0, 0, 4, 8, 16, 32, 64, 96, 128, 160, 192, 255, 255, 192, 160, 128, 96, 64, 32, 16, 8, 4, 0, 0,
    };

    private static Strip Wave(int leds, int bright, int dir, byte[,] col)
    {
        const int period = 24;
        var strip = new Strip(leds);
        var cell = new byte[3, period];
        for (var i = 0; i < period; i++)
        {
            var src = i;
            for (var j = 0; j < period; j++)
            {
                var at = dir == 0 ? period - j - 1 : j;
                for (var ch = 0; ch < 3; ch++)
                {
                    cell[ch, at] = Scale(col[0, ch], WaveProfile[src]);
                }
                src++;
                if (src >= period)
                {
                    src -= period;
                }
            }
            var f = strip.Add();
            for (var led = 0; led < leds; led++)
            {
                var j = led % period;
                Put(f, leds, led, Scale(cell[0, j], bright), Scale(cell[1, j], bright), Scale(cell[2, j], bright));
            }
        }
        return strip;
    }

    private static readonly byte[] ShowerLevel =
    {
        255, 128, 64, 32, 16, 8, 0, 0, 0, 0, 255, 64, 32, 16, 0, 0, 0, 0, 0, 255,
        192, 168, 128, 96, 64, 32, 16, 8, 0, 0, 0, 0, 0, 0, 0, 255, 128, 64, 32, 16,
        8, 0, 0, 0, 0, 0, 0, 0,
    };

    private static readonly byte[] ShowerColor =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3,
    };

    private static Strip MeteorShower(int leds, int bright, int dir, byte[,] col)
    {
        const int period = 48;
        var strip = new Strip(leds);
        var cell = new byte[3, period];
        for (var i = 0; i < period; i++)
        {
            var src = i;
            for (var j = 0; j < period; j++)
            {
                var at = dir == 0 ? period - j - 1 : j;
                for (var ch = 0; ch < 3; ch++)
                {
                    cell[ch, at] = Scale(col[ShowerColor[src], ch], ShowerLevel[src]);
                }
                src++;
                if (src >= period)
                {
                    src -= period;
                }
            }
            var f = strip.Add();
            for (var led = 0; led < leds; led++)
            {
                var j = led % period;
                Put(f, leds, led, Scale(cell[0, j], bright), Scale(cell[1, j], bright), Scale(cell[2, j], bright));
            }
        }
        return strip;
    }

    /// <summary>Each fan's 13 dots fill both halves of its 26-LED ring; slice direction 1 reads the receiver's dots back to front.</summary>
    private static byte[] SliceTl(Strip strip, int start, int stop, int sliceDirection)
    {
        var fans = (stop + 1 - start) / TlDotsPerFan;
        var leds = fans * FlexProtocol.TlLedsPerFan;
        var output = new byte[strip.Count * leds * 3];
        for (var frame = 0; frame < strip.Count; frame++)
        {
            for (var k = 0; k < fans; k++)
            {
                for (var half = 0; half < 2; half++)
                {
                    for (var n = 0; n < TlDotsPerFan; n++)
                    {
                        var dot = start + (k * TlDotsPerFan) + n;
                        if (sliceDirection == 1)
                        {
                            dot = stop + start - dot;
                        }
                        var led = (k * FlexProtocol.TlLedsPerFan) + (half * TlDotsPerFan) + n;
                        var o = ((frame * leds) + led) * 3;
                        output[o] = strip.Get(frame, 0, dot);
                        output[o + 1] = strip.Get(frame, 1, dot);
                        output[o + 2] = strip.Get(frame, 2, dot);
                    }
                }
            }
        }
        return output;
    }

    private static readonly byte[] InfLeftOrder = { 3, 4, 2, 5, 1, 6, 8, 7 };
    private static readonly byte[] InfRightOrder = { 7, 8, 6, 1, 5, 2, 4, 3 };

    /// <summary>Each fan's 8 dots feed the permuted centre, then inner (two dots doubled), outer, inner, outer rings.</summary>
    private static byte[] SliceInf(Strip strip, int start, int stop, int sliceDirection, int rightAttach)
    {
        var fans = (stop + 1 - start) / SlInfDotsPerFan;
        var leds = fans * FlexProtocol.SlInfLedsPerFan;
        var output = new byte[strip.Count * leds * 3];
        var pn = rightAttach == 0 ? 1 : 0;
        var dots = new byte[3, 8];
        for (var frame = 0; frame < strip.Count; frame++)
        {
            for (var k = 0; k < fans; k++)
            {
                void Load()
                {
                    for (var j = 0; j < 8; j++)
                    {
                        var dot = start + (k * 8) + j;
                        if (sliceDirection == 1)
                        {
                            dot = stop + start - dot;
                        }
                        dots[0, j] = strip.Get(frame, 0, dot);
                        dots[1, j] = strip.Get(frame, 1, dot);
                        dots[2, j] = strip.Get(frame, 2, dot);
                    }
                }

                void Write(int led, int dotIndex)
                {
                    var o = ((frame * leds) + led) * 3;
                    output[o] = dots[0, dotIndex];
                    output[o + 1] = dots[1, dotIndex];
                    output[o + 2] = dots[2, dotIndex];
                }

                var baseLed = k * FlexProtocol.SlInfLedsPerFan;

                for (var j = 0; j < 8; j++)
                {
                    var dot = start + (k * 8) + j;
                    if (sliceDirection == 1)
                    {
                        dot = stop + start - dot;
                    }
                    var at = (pn == 0 ? InfLeftOrder[j] : InfRightOrder[j]) - 1;
                    dots[0, at] = strip.Get(frame, 0, dot);
                    dots[1, at] = strip.Get(frame, 1, dot);
                    dots[2, at] = strip.Get(frame, 2, dot);
                }
                for (var j = 0; j < 8; j++)
                {
                    Write(baseLed + j, j);
                }

                foreach (var (from, to, doubled) in new[] { (8, 18, true), (18, 26, false), (26, 36, true), (36, 44, false) })
                {
                    Load();
                    var n = 0;
                    for (var j = baseLed + from; j < baseLed + to; j++)
                    {
                        var src = pn == 0 ? 8 - n - 1 : n;
                        Write(j, src);
                        if (doubled && (n == 2 || n == 6))
                        {
                            j++;
                            Write(j, src);
                        }
                        n++;
                    }
                }
            }
        }
        return output;
    }
}
