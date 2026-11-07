using System;
using System.Collections.Generic;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// The other twenty TL effects, which L-Connect hands to a lighting library of its own. They are
/// drawn here the way that library draws them on a TL chain: along the whole chain (13 LEDs per fan,
/// fan after fan), some on both halves alike, some going round the chain as one loop (along the
/// top halves, then back along the bottom ones), most of them stepping through the user colours
/// one after another, at the same frame step as the other TL effects.
/// </summary>
public static partial class Slv3FanEffects
{
    // The colours the library picks from on its own (Voice, Meteor Shower, Kaleidoscope): the
    // colour wheel in twelve steps, and white.
    private static readonly RgbColor[] TlPalette =
    {
        new(255, 0, 0), new(255, 128, 0), new(255, 255, 0), new(128, 255, 0), new(0, 255, 0), new(0, 255, 128),
        new(0, 255, 255), new(0, 128, 255), new(0, 0, 255), new(128, 0, 255), new(255, 0, 255), new(255, 0, 128),
        new(255, 255, 255),
    };

    // Collide steps every 16 ms times the speed level instead of 11.
    private const double TlCollideStepMs = 16.0;

    // Frames per colour (or per loop) for 1 to 4 fans, as L-Connect runs them.
    private static readonly int[] TlTidePeriods = { 10, 21, 32, 43 };
    private static readonly int[] TlVoiceFrames = { 83, 160, 206, 266 };
    private static readonly int[] TlDoorClose = { 10, 21, 32, 42 };
    private static readonly int[] TlDoorHold = { 5, 5, 6, 7 };
    private static readonly int[] TlDoorOpen = { 10, 20, 30, 41 };
    private static readonly int[] TlReflectPeriods = { 11, 23, 34, 46 };
    private static readonly int[] TlPingPongPeriods = { 11, 20, 29, 38 };
    private static readonly int[] TlCoverPeriods = { 38, 66, 89, 107 };
    private static readonly int[] TlWavePeriods = { 41, 71, 95, 114 };
    private static readonly int[] TlRacingFrames = { 50, 84, 110, 130 };
    private static readonly int[] TlIntertwineFrames = { 22, 43, 64, 85 };
    private static readonly int[] TlCollidePasses = { 5, 9, 13, 18 };
    private static readonly int[] TlElectricPeriods = { 14, 27, 37, 46 };

    // Comet brightness from the head back.
    private static readonly int[] TlShortTail = { 255, 230, 60 };
    private static readonly int[] TlLongTail = { 255, 255, 230, 120, 30 };
    private static readonly int[] TlReflectTail = { 255, 190, 125, 60 };
    private static readonly int[] TlCollideTail = { 255, 170, 85 };
    private static readonly int[] TlCurrentTail = { 255, 255, 120 };

    // The effects that step through the user colours take up to four, as many as L-Connect offers;
    // with none chosen they go through the first four defaults.
    private const int TlCycleColours = 4;

    private static int CycleCount(FanEffectContext ctx) =>
        ctx.ColorCount == 0 ? TlCycleColours : Math.Min(ctx.ColorCount, TlCycleColours);

    private static RgbColor Level(RgbColor c, int level) =>
        new((byte)(c.R * level / 255), (byte)(c.G * level / 255), (byte)(c.B * level / 255));

    private static RgbColor Add(RgbColor a, RgbColor b) =>
        new((byte)Math.Min(255, a.R + b.R), (byte)Math.Min(255, a.G + b.G), (byte)Math.Min(255, a.B + b.B));

    /// <summary>
    /// Lays each frame's two chain lines on the fans: the first <c>n</c> colours go on the top
    /// halves and the next <c>n</c> on the bottom ones, 13 LEDs per fan, fan after fan.
    /// <paramref name="mirror"/> turns both lines end for end, which is how most of these effects
    /// run the other way. A painter may reuse its array between frames; each frame is copied.
    /// </summary>
    private static RawAnimation TlSides(
        FanEffectContext ctx, IEnumerable<RgbColor[]> sides, bool mirror = false, double stepMs = TlStepMs)
    {
        var n = ChainLength(ctx);
        var frames = new List<RgbColor[]>();
        foreach (var frame in sides)
        {
            frames.Add((RgbColor[])frame.Clone());
        }
        var buf = Slv3WirelessEffectMath.AllocateFrames(frames.Count, ctx.FanCount, ctx.LedsPerFan);
        for (var f = 0; f < frames.Count; f++)
        {
            for (var half = 0; half < 2; half++)
            {
                for (var j = 0; j < n; j++)
                {
                    var pos = mirror ? n - 1 - j : j;
                    Slv3WirelessEffectMath.SetLed(
                        buf, f, ctx.FanCount, ctx.LedsPerFan, pos / ctx.RingLen, half * ctx.RingLen + pos % ctx.RingLen, frames[f][half * n + j]);
                }
            }
        }
        return new RawAnimation(buf, frames.Count, Slv3StrimerEffects.ScaledInterval(ctx.Speed, stepMs));
    }

    /// <summary>Where loop position <paramref name="p"/> (top halves left to right, then bottom ones back) sits in a sides array.</summary>
    private static int LoopSide(int n, int p)
    {
        p = ((p % (2 * n)) + 2 * n) % (2 * n);
        return p < n ? p : 3 * n - 1 - p;
    }

    /// <summary>Adds a comet on the loop: its head at <paramref name="head"/>, the rest of <paramref name="tail"/> behind it against <paramref name="step"/>.</summary>
    private static void LoopComet(RgbColor[] sides, int n, int head, int step, RgbColor colour, int[] tail, bool wrap = true)
    {
        for (var t = 0; t < tail.Length; t++)
        {
            var p = head - t * step;
            if (!wrap && (p < 0 || p >= 2 * n))
            {
                continue;
            }
            var i = LoopSide(n, p);
            sides[i] = Add(sides[i], Level(colour, tail[t]));
        }
    }

    /// <summary>Adds a comet to a line at <paramref name="offset"/>, its head at <paramref name="head"/> and the rest behind it, where it falls in [first, last].</summary>
    private static void LineComet(RgbColor[] line, int offset, int head, int step, RgbColor colour, int[] tail, int first, int last, int level = 255)
    {
        for (var t = 0; t < tail.Length; t++)
        {
            var p = head - t * step;
            if (p >= first && p <= last)
            {
                line[offset + p] = Add(line[offset + p], Level(colour, tail[t] * level / 255));
            }
        }
    }

    /// <summary>
    /// How far LED <paramref name="j"/> of an <paramref name="n"/> LED line is from its middle, in
    /// whole LEDs: 0 for the middle LED (or the two middle ones on an even line).
    /// </summary>
    private static int FromMiddle(int n, int j) => n % 2 == 1 ? Math.Abs(j - n / 2) : (j < n / 2 ? n / 2 - 1 - j : j - n / 2);

    /// <summary>Both chain ends light in one colour and every fan goes the other way: the top half of one fan, the bottom half of the next.</summary>
    private static RawAnimation TlStaggered(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        return TlSides(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var c = 0; c < count; c++)
            {
                for (var phase = 0; phase < 2; phase++)
                {
                    // Five steps up from nearly dark, then seven at full.
                    for (var s = 0; s < 12; s++)
                    {
                        Array.Clear(sides);
                        var colour = Level(ctx.Colors[c], s < 5 ? 1 + 40 * s : 255);
                        for (var fan = 0; fan < ctx.FanCount; fan++)
                        {
                            var bottom = (fan + phase) % 2 == 1;
                            Array.Fill(sides, colour, (bottom ? n : 0) + fan * ctx.RingLen, ctx.RingLen);
                        }
                        yield return sides;
                    }
                }
            }
        }
    }

    /// <summary>Each colour flows in from both ends of the chain over the last one until they meet in the middle.</summary>
    private static RawAnimation TlTide(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = TlTidePeriods[ctx.FanCount - 1];
        var half = (n + 1) / 2;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < count; c++)
            {
                var colour = ctx.Colors[c];
                var under = ctx.Colors[(c + count - 1) % count];
                for (var s = 0; s < period; s++)
                {
                    var reach = Math.Min(half, 2 + (half - 2) * s / (period - 1));
                    for (var j = 0; j < n; j++)
                    {
                        line[j] = Math.Min(j, n - 1 - j) < reach ? colour : under;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// The first colour comes in from one end and the second from the other; they meet in the
    /// middle, and the two mixed burst out from there and fade away from the middle out.
    /// </summary>
    private static RawAnimation TlMixing(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var fans = ctx.FanCount;
        var left = ctx.Colors[0];
        var right = ctx.Colors[1];
        var mix = Add(left, right);
        var mid = (n - 1) / 2;
        var body = 2 * fans + 1;
        var tail = new int[body + 2];
        Array.Fill(tail, 255, 0, body);
        tail[body] = 120;
        tail[body + 1] = 30;
        int approach = 5 * fans, gap = 2 * fans - 1, burst = 2 * fans + 1, fade = 3 * fans + 1, rest = 3 * fans - 2;
        var reach = n / 2.0;
        var soft = n / 4.0;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            var mirrored = new RgbColor[n];
            for (var s = 0; s < approach; s++)
            {
                Array.Clear(line);
                Array.Clear(mirrored);
                var head = body - 1 + s * mid / (approach - 1);
                LineComet(line, 0, head, 1, left, tail, 0, mid);
                LineComet(mirrored, 0, head, 1, right, tail, 0, mid);
                for (var j = 0; j < n; j++)
                {
                    line[j] = Add(line[j], mirrored[n - 1 - j]);
                }
                yield return line;
            }
            Array.Clear(line);
            for (var s = 0; s < gap; s++)
            {
                yield return line;
            }
            for (var s = 0; s < burst; s++)
            {
                var radius = (s + 1) * reach / burst;
                for (var j = 0; j < n; j++)
                {
                    line[j] = Math.Abs(j - (n - 1) / 2.0) + 0.5 <= radius ? mix : default;
                }
                yield return line;
            }
            for (var s = 0; s < fade; s++)
            {
                // A soft dark edge runs from the middle out to the ends.
                var front = (s + 1) * (reach + soft) / fade - soft;
                for (var j = 0; j < n; j++)
                {
                    var lit = Math.Clamp((Math.Abs(j - (n - 1) / 2.0) - front) / soft, 0.0, 1.0);
                    line[j] = Level(mix, (int)(lit * 255));
                }
                yield return line;
            }
            Array.Clear(line);
            for (var s = 0; s < rest; s++)
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// A level meter in the middle of the chain: bars of random height in random colours grow out
    /// to both sides and fall back, one after another. A pre-rendered loop has no sound to follow,
    /// so the heights are the same every time round.
    /// </summary>
    private static RawAnimation TlVoice(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var half = (n + 1) / 2;
        var total = TlVoiceFrames[ctx.FanCount - 1];
        var random = new DeterministicRandom(0x0C0E_5EEDUL);
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            var made = 1;
            yield return line;
            while (made < total)
            {
                var peak = 2 + random.NextInt(half - 1);
                var colour = TlPalette[random.NextInt(TlPalette.Length)];
                var sizes = new List<int>();
                for (var k = 0; ; k++)
                {
                    var size = Math.Min(peak, 2 + (int)Math.Round(k * 1.6));
                    sizes.Add(size);
                    if (size == peak)
                    {
                        break;
                    }
                }
                for (var k = sizes.Count - 2; k >= 0; k--)
                {
                    sizes.Add(sizes[k]);
                }
                sizes.Add(0);
                foreach (var size in sizes)
                {
                    for (var j = 0; j < n; j++)
                    {
                        line[j] = FromMiddle(n, j) < size ? colour : default;
                    }
                    made++;
                    yield return line;
                }
            }
        }
    }

    /// <summary>Each colour closes in from both ends of the chain like two doors, holds, and opens back out.</summary>
    private static RawAnimation TlDoor(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var close = TlDoorClose[ctx.FanCount - 1];
        var hold = TlDoorHold[ctx.FanCount - 1];
        var open = TlDoorOpen[ctx.FanCount - 1];
        var half = n / 2.0;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < count; c++)
            {
                var colour = ctx.Colors[c];
                for (var s = 0; s < close + hold + open; s++)
                {
                    var reach = s < close ? (int)Math.Ceiling(half * s / close)
                        : s < close + hold ? n
                        : (int)Math.Ceiling(half * (open - 1 - (s - close - hold)) / open);
                    for (var j = 0; j < n; j++)
                    {
                        line[j] = Math.Min(j, n - 1 - j) < reach ? colour : default;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// The next colour spreads over the last from the middle of one half at a time, going round
    /// the chain: along the top halves, then back along the bottom ones.
    /// </summary>
    private static RawAnimation TlRender(FanEffectContext ctx)
    {
        const int segmentFrames = 13;
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var segments = 2 * ctx.FanCount;
        var middle = ctx.RingLen / 2;
        return TlSides(ctx, Frames(), mirror: ctx.Direction != 0);

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var c = 0; c < count; c++)
            {
                var from = ctx.Colors[c];
                var to = ctx.Colors[(c + 1) % count];
                for (var g = 0; g < segments; g++)
                {
                    for (var s = 0; s < segmentFrames; s++)
                    {
                        var radius = s / 2;
                        for (var k = 0; k < segments; k++)
                        {
                            var start = k < ctx.FanCount ? k * ctx.RingLen : n + (segments - 1 - k) * ctx.RingLen;
                            for (var i = 0; i < ctx.RingLen; i++)
                            {
                                var done = k < g || (k == g && Math.Abs(i - middle) <= radius);
                                sides[start + i] = done ? to : from;
                            }
                        }
                        yield return sides;
                    }
                }
            }
        }
    }

    /// <summary>Rings leave the middle of the chain every six or seven frames and run out to both ends, a few in each colour.</summary>
    private static RawAnimation TlRipple(FanEffectContext ctx)
    {
        var spacing = ctx.FanCount == 1 ? 6 : 7;
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = 19 * ctx.FanCount;
        var travel = (n + 1) / 2;
        var waves = (period - travel) / spacing + 1;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < count; c++)
            {
                for (var s = 0; s < period; s++)
                {
                    for (var j = 0; j < n; j++)
                    {
                        var r = s - FromMiddle(n, j);
                        line[j] = r >= 0 && r % spacing == 0 && r / spacing < waves ? ctx.Colors[c] : default;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>Two comets come in from the ends of the chain and meet in the middle; the other way, they leave the middle for the ends.</summary>
    private static RawAnimation TlReflect(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = TlReflectPeriods[ctx.FanCount - 1];
        var side = (n + 1) / 2;
        var moving = period - 4;
        var travel = side - 1 + TlReflectTail.Length - 1;
        var inward = ctx.Direction == 0;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            var half = new RgbColor[side];
            for (var c = 0; c < count; c++)
            {
                for (var s = 0; s < period; s++)
                {
                    Array.Clear(half);
                    if (s >= 1 && s <= moving)
                    {
                        LineComet(half, 0, (s - 1) * travel / (moving - 1), 1, ctx.Colors[c], TlReflectTail, 0, side - 1);
                    }
                    for (var j = 0; j < n; j++)
                    {
                        // Counted from the nearer end coming in, from the middle going out.
                        line[j] = half[inward ? Math.Min(j, n - 1 - j) : FromMiddle(n, j)];
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>Two comets, one in each of two colours, chase each other round the chain: along the top halves and back along the bottom ones.</summary>
    private static RawAnimation TlTailChasing(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var frames = 25 * ctx.FanCount - 1;
        var reverse = ctx.Direction != 0;
        var first = ctx.Colors[reverse ? 1 : 0];
        var second = ctx.Colors[reverse ? 0 : 1];
        return TlSides(ctx, Frames(), mirror: reverse);

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var f = 0; f < frames; f++)
            {
                Array.Clear(sides);
                var head = f * 2 * n / frames + TlShortTail.Length - 1;
                LoopComet(sides, n, head, 1, first, TlShortTail);
                LoopComet(sides, n, head + n, 1, second, TlShortTail);
                yield return sides;
            }
        }
    }

    /// <summary>Each colour wipes over the last along the chain, one way and then the other.</summary>
    private static RawAnimation TlPaint(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = 17 * ctx.FanCount / 2;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < count; c++)
            {
                var colour = ctx.Colors[c];
                var under = ctx.Colors[(c + count - 1) % count];
                var forward = (c % 2 == 0) == (ctx.Direction == 0);
                for (var s = 0; s < period; s++)
                {
                    var painted = (s + 1) * n / period;
                    for (var j = 0; j < n; j++)
                    {
                        line[j] = (forward ? j : n - 1 - j) < painted ? colour : under;
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>A comet crosses the chain in each colour, every time from the end the last one left at.</summary>
    private static RawAnimation TlPingPong(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = TlPingPongPeriods[ctx.FanCount - 1];
        var travel = n - 1 + TlLongTail.Length;
        // An odd number of colours would start two passes from the same end; going round twice keeps them turning.
        var passes = count % 2 == 0 ? count : 2 * count;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var c = 0; c < passes; c++)
            {
                var forward = (c % 2 == 0) == (ctx.Direction == 0);
                for (var s = 0; s < period; s++)
                {
                    Array.Clear(line);
                    LineComet(line, 0, s * travel / (period - 1), 1, ctx.Colors[c % count], TlLongTail, 0, n - 1);
                    if (!forward)
                    {
                        Array.Reverse(line);
                    }
                    yield return line;
                }
            }
        }
    }

    /// <summary>Each colour covers the last going round the chain: along the top halves, then back along the bottom ones.</summary>
    private static RawAnimation TlCoverCycle(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = TlCoverPeriods[ctx.FanCount - 1];
        return TlSides(ctx, Frames(), mirror: ctx.Direction != 0);

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var c = 0; c < count; c++)
            {
                var colour = ctx.Colors[c];
                var under = ctx.Colors[(c + count - 1) % count];
                for (var s = 0; s < period; s++)
                {
                    var covered = Math.Min(2 * n, s * 2 * n / (period - 2));
                    for (var p = 0; p < 2 * n; p++)
                    {
                        sides[LoopSide(n, p)] = p < covered ? colour : under;
                    }
                    yield return sides;
                }
            }
        }
    }

    /// <summary>A comet goes once round the chain in each colour: along the top halves, then back along the bottom ones.</summary>
    private static RawAnimation TlWave(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var period = TlWavePeriods[ctx.FanCount - 1];
        var travel = 2 * n + TlLongTail.Length - 1;
        return TlSides(ctx, Frames(), mirror: ctx.Direction != 0);

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var c = 0; c < count; c++)
            {
                for (var s = 0; s < period; s++)
                {
                    Array.Clear(sides);
                    if (s > 0)
                    {
                        LoopComet(sides, n, (s - 1) * travel / (period - 2), 1, ctx.Colors[c], TlLongTail, wrap: false);
                    }
                    yield return sides;
                }
            }
        }
    }

    /// <summary>
    /// Two comets, one in each of two colours, race to the end of the chain and back on the two
    /// halves, crossing over to the other half at every fan.
    /// </summary>
    private static RawAnimation TlRacing(FanEffectContext ctx)
    {
        const int lag = 2;
        var n = ChainLength(ctx);
        var pass = TlRacingFrames[ctx.FanCount - 1] / 2;
        var travel = n + TlLongTail.Length - 1;
        var moving = pass - 1 - lag;
        return TlSides(ctx, Frames(), mirror: ctx.Direction != 0);

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var leg = 0; leg < 2; leg++)
            {
                for (var s = 0; s < pass; s++)
                {
                    Array.Clear(sides);
                    for (var half = 0; half < 2; half++)
                    {
                        var k = s - 1 - half * lag;
                        if (k >= 0 && k < moving)
                        {
                            LineComet(sides, half * n, k * travel / (moving - 1), 1, ctx.Colors[half], TlLongTail, 0, n - 1);
                        }
                    }
                    if (leg == 1)
                    {
                        Array.Reverse(sides, 0, n);
                        Array.Reverse(sides, n, n);
                    }
                    for (var fan = 1; fan < ctx.FanCount; fan += 2)
                    {
                        for (var i = 0; i < ctx.RingLen; i++)
                        {
                            var top = fan * ctx.RingLen + i;
                            (sides[top], sides[n + top]) = (sides[n + top], sides[top]);
                        }
                    }
                    yield return sides;
                }
            }
        }
    }

    /// <summary>Blocks of the first and second colour turn round the chain: along the top halves and back along the bottom ones.</summary>
    private static RawAnimation TlLottery(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var loop = 2 * n;
        var frames = 29 * ctx.FanCount;
        var pairs = Math.Max(1, (int)Math.Round(loop / 10.0));
        var turn = ctx.Direction == 0 ? 1 : -1;
        return TlSides(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[loop];
            for (var f = 0; f < frames; f++)
            {
                var shift = f * loop / frames;
                for (var p = 0; p < loop; p++)
                {
                    var u = ((p - turn * shift) % loop + loop) % loop;
                    sides[LoopSide(n, p)] = ctx.Colors[u * 2 * pairs / loop % 2];
                }
                yield return sides;
            }
        }
    }

    /// <summary>
    /// Two comets go round the chain against each other, the first colour one way and the second
    /// the other, mixing where they cross.
    /// </summary>
    private static RawAnimation TlIntertwine(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var frames = TlIntertwineFrames[ctx.FanCount - 1];
        return TlSides(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var f = 0; f < frames; f++)
            {
                Array.Clear(sides);
                var travelled = f * 2 * n / frames;
                LoopComet(sides, n, travelled, 1, ctx.Colors[0], TlLongTail);
                LoopComet(sides, n, n - 1 - travelled, -1, ctx.Colors[1], TlLongTail);
                yield return sides;
            }
        }
    }

    /// <summary>Meteors of random length and colour stream along the top and the bottom halves of the chain, each half its own stream.</summary>
    private static RawAnimation TlMeteorShower(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var frames = 64 * ctx.FanCount;
        var random = new DeterministicRandom(0x5407_E0A5UL);
        var streams = new RgbColor[2][];
        for (var half = 0; half < 2; half++)
        {
            // One stretch of meteors as long as the loop, so the stream comes round seamlessly.
            var stream = new RgbColor[frames];
            var at = random.NextInt(4);
            while (at < frames)
            {
                var length = 3 + random.NextInt(4);
                var colour = TlPalette[random.NextInt(TlPalette.Length)];
                for (var k = 0; k < length && at + k < frames; k++)
                {
                    stream[at + k] = colour;
                }
                at += length + (random.NextDouble() < 0.3 ? 8 + random.NextInt(12) : random.NextInt(3));
            }
            streams[half] = stream;
        }
        var step = ctx.Direction == 0 ? 1 : -1;
        return TlSides(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var sides = new RgbColor[2 * n];
            for (var f = 0; f < frames; f++)
            {
                for (var half = 0; half < 2; half++)
                {
                    for (var j = 0; j < n; j++)
                    {
                        sides[half * n + j] = streams[half][((j - step * f) % frames + frames) % frames];
                    }
                }
                yield return sides;
            }
        }
    }

    /// <summary>
    /// Comets come in from both ends and meet in the middle, then the next colour bounces back
    /// out to the ends from there and comes in again.
    /// </summary>
    private static RawAnimation TlCollide(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var count = CycleCount(ctx);
        var pass = TlCollidePasses[ctx.FanCount - 1];
        var side = (n + 1) / 2;
        var travel = side - 1 + TlCollideTail.Length - 1;
        return TlLine(ctx, Frames(), TlCollideStepMs);

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            var half = new RgbColor[side];
            for (var c = 0; c < count; c++)
            {
                for (var leg = 0; leg < 2; leg++)
                {
                    // In with this colour, then out with the next.
                    var colour = ctx.Colors[(c + leg) % count];
                    for (var s = 0; s < pass; s++)
                    {
                        Array.Clear(half);
                        if (s > 0)
                        {
                            var head = (s - 1) * travel / Math.Max(1, pass - 2);
                            // Coming in, the comets fade as they near each other.
                            var level = leg == 0 ? 255 - 140 * (s - 1) / Math.Max(1, pass - 2) : 255;
                            LineComet(half, 0, head, 1, colour, TlCollideTail, 0, side - 1, level);
                        }
                        for (var j = 0; j < n; j++)
                        {
                            line[j] = leg == 0 ? half[Math.Min(j, n - 1 - j)] : half[FromMiddle(n, j)];
                        }
                        yield return line;
                    }
                }
            }
        }
    }

    /// <summary>Sparks run in from both ends to the middle, and the charge flashes back out from there to the ends.</summary>
    private static RawAnimation TlElectricCurrent(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var fans = ctx.FanCount;
        var count = CycleCount(ctx);
        var period = TlElectricPeriods[fans - 1];
        var side = (n + 1) / 2;
        var inFrames = (int)Math.Round(period * 0.57);
        var flash = period - 1 - inFrames - 2 * fans;
        var travel = side - 1 + TlCurrentTail.Length - 1;
        var width = side * 0.6;
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            var half = new RgbColor[side];
            for (var c = 0; c < count; c++)
            {
                var colour = ctx.Colors[c];
                Array.Clear(line);
                yield return line;
                for (var s = 0; s < inFrames; s++)
                {
                    Array.Clear(half);
                    LineComet(half, 0, s * travel / (inFrames - 1), 1, colour, TlCurrentTail, 0, side - 1);
                    for (var j = 0; j < n; j++)
                    {
                        line[j] = half[Math.Min(j, n - 1 - j)];
                    }
                    yield return line;
                }
                Array.Clear(line);
                for (var s = 0; s < fans; s++)
                {
                    yield return line;
                }
                for (var s = 0; s < flash; s++)
                {
                    // A bright front with a fading wake behind it, racing out from the middle.
                    var front = (s + 1) * (side + width) / flash;
                    for (var j = 0; j < n; j++)
                    {
                        var behind = front - FromMiddle(n, j);
                        line[j] = behind >= 0 && behind <= width ? Level(colour, (int)(255 * Math.Min(1.0, 2 * (1 - behind / width)))) : default;
                    }
                    yield return line;
                }
                Array.Clear(line);
                for (var s = 0; s < fans; s++)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>Bands of random colours flow out from the middle of the chain to both ends, the two sides mirroring each other.</summary>
    private static RawAnimation TlKaleidoscope(FanEffectContext ctx)
    {
        var n = ChainLength(ctx);
        var frames = 36 * ctx.FanCount;
        var random = new DeterministicRandom(0x6A1E_1D05UL);
        var bands = new RgbColor[frames];
        var at = 0;
        var last = -1;
        while (at < frames)
        {
            // Wider bands on a longer chain.
            var width = ctx.FanCount + 2 + random.NextInt(ctx.FanCount + 1);
            int pick;
            do
            {
                pick = random.NextInt(TlPalette.Length);
            }
            while (pick == last);
            last = pick;
            for (var k = 0; k < width && at + k < frames; k++)
            {
                bands[at + k] = TlPalette[pick];
            }
            at += width;
        }
        return TlLine(ctx, Frames());

        IEnumerable<RgbColor[]> Frames()
        {
            var line = new RgbColor[n];
            for (var f = 0; f < frames; f++)
            {
                for (var j = 0; j < n; j++)
                {
                    line[j] = bands[((FromMiddle(n, j) - f) % frames + frames) % frames];
                }
                yield return line;
            }
        }
    }
}
