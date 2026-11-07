using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// The TL effects L-Connect draws with code of its own (Rainbow, Rainbow Morph, Static,
/// Breathing, Runway, Meteor, Color Cycle, Stack and Twinkle), drawn the way L-Connect does
/// on a TL chain. L-Connect lays them along the whole chain as one line of 13 LEDs per fan,
/// fan after fan, gives each fan its own user colour (fan 1 the first colour, fan 2 the
/// second, ...) and steps every 11 ms times the speed level. The line goes on both halves
/// of each fan; <see cref="Slv3FanScope"/> can keep it to one of them, as L-Connect does.
/// </summary>
public static partial class Slv3FanEffects
{
    // L-Connect's TL frame step; the speed level multiplies it (7 slowest .. 3 fastest).
    private const double TlStepMs = 11.0;

    // L-Connect keeps a user colour's R + G + B at or under this, taking 5% off at a time.
    private const int TlColourSumCap = 600;

    // Rainbow walks the colour wheel in three thirds of 5, 9, 13 or 18 steps for 1 to 4 fans,
    // each step this much brighter or darker.
    private static readonly int[] TlWheelThirds = { 5, 9, 13, 18 };
    private static readonly int[] TlWheelSteps = { 51, 29, 20, 14 };

    // Meteor: the trail's brightness from its tail to its head, two LEDs per fan long.
    private static readonly byte[][] TlMeteorTrails =
    {
        new byte[] { 32, 255 },
        new byte[] { 16, 64, 128, 255 },
        new byte[] { 8, 16, 32, 64, 128, 255 },
        new byte[] { 6, 10, 16, 32, 64, 96, 168, 255 },
    };

    // Color Cycle: LEDs lit in each of the three colours, and dark LEDs after each.
    private static readonly int[] TlCycleLit = { 1, 2, 5, 8 };
    private static readonly int[] TlCycleDark = { 3, 6, 8, 9 };

    private const int TlTwinkleFrames = 200;

    /// <summary>A user colour as L-Connect sends it to a TL chain: stepped down until R + G + B is at most 600.</summary>
    internal static RgbColor CapColourSum(RgbColor c)
    {
        while (c.R + c.G + c.B > TlColourSumCap)
        {
            c = new RgbColor((byte)(c.R * 0.95), (byte)(c.G * 0.95), (byte)(c.B * 0.95));
        }
        return c;
    }

    private static RgbColor TlColour(FanEffectContext ctx, int index) => CapColourSum(ctx.Colors[index]);

    private static RgbColor Dim(RgbColor c, int level) =>
        new((byte)(c.R * level >> 8), (byte)(c.G * level >> 8), (byte)(c.B * level >> 8));

    /// <summary>
    /// Lays each frame's chain line (13 LEDs per fan, fan after fan) on both halves of every
    /// fan. A painter may reuse its array between frames; each frame is copied as it comes.
    /// </summary>
    private static RawAnimation TlLine(FanEffectContext ctx, IEnumerable<RgbColor[]> lines, double stepMs = TlStepMs)
    {
        var frames = new List<RgbColor[]>();
        foreach (var line in lines)
        {
            frames.Add((RgbColor[])line.Clone());
        }
        var buf = Slv3WirelessEffectMath.AllocateFrames(frames.Count, ctx.FanCount, ctx.LedsPerFan);
        for (var f = 0; f < frames.Count; f++)
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var i = 0; i < ctx.RingLen; i++)
                {
                    SetRing(buf, f, ctx, fan, i, frames[f][fan * ctx.RingLen + i]);
                }
            }
        }
        return new RawAnimation(buf, frames.Count, Slv3StrimerEffects.ScaledInterval(ctx.Speed, stepMs));
    }

    private static int ChainLength(FanEffectContext ctx) => ctx.FanCount * ctx.RingLen;

    // The colour each fan of the chain takes from the user colours, by its place on the line.
    private static int FanOf(FanEffectContext ctx, int linePos) => Math.Min(linePos / ctx.RingLen, 3);

    /// <summary>A colour wheel position: red, green, blue and back, as L-Connect's TL Rainbow steps it.</summary>
    private static RgbColor TlWheel(int index, int third, int step)
    {
        int Falling(int k) => 255 - step * k;
        int Rising(int k) => k == 0 ? 0 : Falling(third - k);
        var k = index % third;
        return (index / third) switch
        {
            0 => new RgbColor((byte)Falling(k), (byte)Rising(k), 0),
            1 => new RgbColor(0, (byte)Falling(k), (byte)Rising(k)),
            _ => new RgbColor((byte)Rising(k), 0, (byte)Falling(k)),
        };
    }

    /// <summary>One rainbow along the whole chain, moving one LED a frame.</summary>
    private static RawAnimation TlRainbow(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var third = TlWheelThirds[ctx.FanCount - 1];
        var step = TlWheelSteps[ctx.FanCount - 1];
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var f = 0; f < n; f++)
            {
                for (var j = 0; j < n; j++)
                {
                    line[ctx.Direction == 0 ? n - j - 1 : j] = TlWheel((f + j) % (third * 3), third, step);
                }
                yield return line;
            }
        }
    }

    /// <summary>The whole chain in one colour, going round the colour wheel 3 levels a step, every second step shown.</summary>
    private static RawAnimation TlRainbowMorph(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            int r = 255, g = 0, b = 0;
            for (var i = 0; i < 254; i++)
            {
                if (i % 2 == 0)
                {
                    Array.Fill(line, new RgbColor((byte)r, (byte)g, (byte)b));
                    yield return line;
                }
                if (i < 85)
                {
                    r -= 3;
                    g += 3;
                }
                else if (i < 170)
                {
                    g -= 3;
                    b += 3;
                }
                else
                {
                    r += 3;
                    b -= 3;
                }
            }
        }
    }

    /// <summary>Each fan lit in its own user colour.</summary>
    private static RawAnimation TlStatic(FanEffectContext ctx)
    {
        var line = new RgbColor[ChainLength(ctx)];
        for (var j = 0; j < line.Length; j++)
        {
            line[j] = TlColour(ctx, FanOf(ctx, j));
        }
        return TlLine(ctx, new[] { line });
    }

    /// <summary>Each fan breathes in its own user colour: 85 steps up to full and 84 back down.</summary>
    private static RawAnimation TlBreathing(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var i = 0; i < 170; i++)
            {
                var level = (i <= 85 ? i : 170 - i) * 3 & 0xFF;
                for (var j = 0; j < n; j++)
                {
                    line[j] = Dim(TlColour(ctx, FanOf(ctx, j)), level);
                }
                yield return line;
            }
        }
    }

    /// <summary>A runner two LEDs per fan long, in the first colour over the second, along the chain and back.</summary>
    private static RawAnimation TlRunway(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var length = ctx.FanCount * 2;
        var runner = TlColour(ctx, 0);
        var ground = TlColour(ctx, 1);
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var pass = 0; pass < 2; pass++)
            {
                for (var j = 0; j < n + length - 1; j++)
                {
                    for (var k = 0; k < n; k++)
                    {
                        line[pass == 0 ? k : n - k - 1] = k <= j && k + length > j ? runner : ground;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>A meteor with a fading trail crosses the chain once in each of the four colours.</summary>
    private static RawAnimation TlMeteor(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var trail = TlMeteorTrails[ctx.FanCount - 1];
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < 4; c++)
            {
                var colour = TlColour(ctx, c);
                for (var j = 0; j < n + trail.Length - 1; j++)
                {
                    var t = 0;
                    for (var k = 0; k < n; k++)
                    {
                        var lit = k <= j && k + trail.Length > j;
                        line[ctx.Direction != 0 ? n - k - 1 : k] = lit ? Dim(colour, trail[t++]) : default;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// Three colour blocks with dark gaps between them go round the chain. L-Connect builds
    /// the blocks a little differently on the top half than on the bottom one (the gaps
    /// are one LED shorter for 1 and 4 fans and one longer for 2), which is kept here.
    /// </summary>
    private static RawAnimation TlColorCycle(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var fans = ctx.FanCount;
        var top = ctx.Scope != Slv3FanScope.Bottom;
        var pattern = new RgbColor[Math.Max(n, 3 * (TlCycleLit[3] + TlCycleDark[3]) + 1)];
        var end = 0;
        for (var c = 0; c < 3; c++)
        {
            var colour = TlColour(ctx, c);
            for (var i = 0; i < TlCycleLit[fans - 1]; i++)
            {
                pattern[end++] = colour;
            }
            for (var i = 0; i < TlCycleDark[fans - 1]; i++)
            {
                pattern[end++] = default;
            }
            if (top && (fans == 1 || fans == 4))
            {
                end--;
            }
            if (top && fans == 2)
            {
                pattern[end++] = default;
            }
        }
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var k = 0; k < n; k++)
            {
                for (var j = 0; j < n; j++)
                {
                    line[ctx.Direction == 0 ? n - j - 1 : j] = pattern[(k + j) % n];
                }
                yield return line;
            }
        }
    }

    /// <summary>
    /// Blocks one LED per fan long slide down the chain and pile up at its far end until it is
    /// full, then the pile empties from the start; once in each of the four colours.
    /// </summary>
    private static RawAnimation TlStack(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var block = ctx.FanCount;
        return TlLine(ctx, Frames());

        int At(int k) => ctx.Direction != 0 ? n - k - 1 : k;

        IEnumerable<RgbColor[]> Frames()
        {
            // The pile is whatever the sliding never reaches again, so the line keeps it between frames.
            var line = new RgbColor[n];
            for (var c = 0; c < 4; c++)
            {
                var colour = TlColour(ctx, c);
                for (var piled = 0; piled < n; piled += block)
                {
                    var room = n - piled;
                    for (var j = 0; j < room; j++)
                    {
                        for (var k = 0; k < room; k++)
                        {
                            line[At(k)] = k <= j && k + block > j ? colour : default;
                        }
                        yield return line;
                    }
                }
                for (var j = 0; j < n; j++)
                {
                    for (var k = 0; k < n; k++)
                    {
                        line[At(k)] = k > j ? colour : default;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// Every LED of both halves flashes once a loop, at its own moment and in one of the four
    /// colours: ten steps up to full and nine back down, the way L-Connect's TL Twinkle does.
    /// </summary>
    private static RawAnimation TlTwinkle(FanEffectContext ctx)
    {
        var flash = new int[19];
        for (var k = 0; k < 10; k++)
        {
            flash[k] = (int)Math.Round(5 + 250.0 * k / 9);
            flash[18 - k] = flash[k];
        }
        var leds = ctx.FanCount * ctx.LedsPerFan;
        var random = new Slv3WirelessEffectMath.DeterministicRandom(0x7419_2961UL);
        var start = new int[leds];
        var colourOf = new int[leds];
        for (var i = 0; i < leds; i++)
        {
            start[i] = random.NextInt(TlTwinkleFrames);
            colourOf[i] = random.NextInt(4);
        }
        var buf = Slv3WirelessEffectMath.AllocateFrames(TlTwinkleFrames, ctx.FanCount, ctx.LedsPerFan);
        for (var f = 0; f < TlTwinkleFrames; f++)
        {
            for (var i = 0; i < leds; i++)
            {
                var age = (f - start[i] + TlTwinkleFrames) % TlTwinkleFrames;
                var level = age < flash.Length ? flash[age] : 0;
                Slv3WirelessEffectMath.SetLed(
                    buf, f, ctx.FanCount, ctx.LedsPerFan, i / ctx.LedsPerFan, i % ctx.LedsPerFan, Dim(TlColour(ctx, colourOf[i]), level));
            }
        }
        return new RawAnimation(buf, TlTwinkleFrames, Slv3StrimerEffects.ScaledInterval(ctx.Speed, TlStepMs));
    }
}
