using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3StrimerEffects
{
    private static readonly IReadOnlyDictionary<string, Func<EffectContext, RawAnimation>> ModeRenderers =
        new Dictionary<string, Func<EffectContext, RawAnimation>>(StringComparer.Ordinal)
        {
            ["rainbow"] = Rainbow,
            ["rainbowWave"] = RainbowWave,
            ["rainbowMorph"] = RainbowMorph,
            ["static"] = StaticFill,
            ["breathing"] = Breathing,
            ["wave"] = Wave,
            ["painting"] = Painting,
            ["colorTransfer"] = ColorTransfer,
            ["fadeOut"] = FadeOut,
            ["contest"] = Contest,
            ["crossOver"] = CrossOver,
            ["bulletStack"] = BulletStack,
            ["twinkle"] = Twinkle,
            ["parallel"] = Parallel,
            ["shockWave"] = ShockWave,
            ["ripple"] = Ripple,
            ["voice"] = Voice,
            ["drizzling"] = Drizzling,
            ["endless"] = Endless,
            ["shuttleRun"] = ShuttleRun,
            ["river"] = River,
            ["hourglass"] = Hourglass,
            ["pioneer"] = Pioneer,
            ["electricCurrent"] = ElectricCurrent,
            ["transformation"] = Transformation,
            ["gradientRibbon"] = GradientRibbon,
            ["snooker"] = Snooker,
            ["mixing"] = Mixing,
            ["pingPong"] = PingPong,
            ["runway"] = Runway,
            ["tide"] = Tide,
            ["blowUp"] = BlowUp,
            ["meteor"] = Meteor,
            ["stack"] = Stack,
        };

    private static RawAnimation Rainbow(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 22.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var hue = (double)pos / ctx.LedsPerLane + (double)f / frameCount;
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Hue(hue));
                }
            }
        });
    }

    private static RawAnimation RainbowWave(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 20.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var hue = (double)pos / ctx.LedsPerLane + (double)f / frameCount;
                    var wave = (Math.Sin(pos / (double)ctx.LedsPerLane * 4 * Math.PI - (double)f / frameCount * 2 * Math.PI) + 1.0) / 2.0;
                    var brightness = 0.4 + 0.6 * wave;
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Scale(Hue(hue), brightness));
                }
            }
        });
    }

    private static RawAnimation RainbowMorph(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 22.0), (buf, f) =>
        {
            var sign = ctx.Direction != 0 ? -1.0 : 1.0;
            var color = Hue(sign * f / frameCount);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation StaticFill(EffectContext ctx) =>
        Loop(1, ctx.Lanes, ctx.LedsPerLane, 1000.0, (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[0]);
                }
            }
        });

    private static RawAnimation Breathing(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 20.0), (buf, f) =>
        {
            var brightness = 0.05 + 0.95 * Breath((double)f / frameCount);
            var color = Scale(ctx.Colors[0], brightness);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation Wave(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 22.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var t = (double)pos / ctx.LedsPerLane - (double)f / frameCount;
                    var mix = (Math.Sin(t * 2 * Math.PI) + 1.0) / 2.0;
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Lerp(ctx.Colors[0], ctx.Colors[1], mix));
                }
            }
        });
    }

    private static RawAnimation Painting(EffectContext ctx)
    {
        var segments = Math.Min(6, ctx.LedsPerLane);
        var segLen = ctx.LedsPerLane / segments;
        var order = new int[segments];
        for (var i = 0; i < segments; i++)
        {
            order[i] = i;
        }
        var rng = new DeterministicRandom(0xC0FFEEUL);
        for (var i = segments - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        const int holdFrames = 6;
        var frameCount = segments * holdFrames;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 18.0), (buf, f) =>
        {
            var revealed = f / holdFrames + 1;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var s = 0; s < revealed && s < segments; s++)
                {
                    var start = order[s] * segLen;
                    var end = order[s] == segments - 1 ? ctx.LedsPerLane : start + segLen;
                    var color = ctx.Colors[order[s] % 6];
                    for (var led = start; led < end; led++)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                    }
                }
            }
        });
    }

    private static RawAnimation ColorTransfer(EffectContext ctx)
    {
        const int steps = 4;
        const int colorCount = 6;
        const int frameCount = colorCount * steps;
        var total = ctx.Lanes * ctx.LedsPerLane;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 24.0), (buf, f) =>
        {
            var colorIndex = f / steps;
            var step = f % steps;
            var edge = (int)Math.Round((double)total * (step + 1) / steps);
            var current = ctx.Colors[colorIndex % 6];
            var previous = ctx.Colors[(colorIndex + 5) % 6];
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var globalPos = Pos(lane * ctx.LedsPerLane + led, total, ctx.Direction);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, globalPos < edge ? current : previous);
                }
            }
        });
    }

    private static RawAnimation FadeOut(EffectContext ctx)
    {
        const int perColor = 16;
        const int frameCount = 6 * perColor;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var colorIndex = f / perColor;
            var t = (double)(f % perColor) / perColor;
            var envelope = t < 0.5 ? t * 2 : (1 - t) * 2;
            var color = Scale(ctx.Colors[colorIndex % 6], envelope);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation Contest(EffectContext ctx)
    {
        const int frameCount = 48;
        const double trail = 1.5;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.3), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var m = 0; m < 6; m++)
                {
                    var phase = (double)f / frameCount + (double)m / 6;
                    phase -= Math.Floor(phase);
                    var center = phase * (ctx.LedsPerLane - 1);
                    PaintMarker(buf, f, ctx, lane, center, trail, ctx.Colors[m], ctx.Direction);
                }
            }
        });
    }

    private static RawAnimation CrossOver(EffectContext ctx)
    {
        const int frameCount = 24;
        var half = ctx.LedsPerLane / 2;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var swapped = f / (frameCount / 2) % 2 == 1;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                var colorA = ctx.Colors[lane * 2 % 6];
                var colorB = ctx.Colors[(lane * 2 + 1) % 6];
                if (swapped)
                {
                    (colorA, colorB) = (colorB, colorA);
                }
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, led < half ? colorA : colorB);
                }
            }
        });
    }

    private static RawAnimation BulletStack(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.3), (buf, f) =>
        {
            var travel = BounceTravel((double)f / frameCount);
            var center = travel * (ctx.LedsPerLane - 1);
            var colorIndex = (int)(f / (frameCount / 6.0)) % 6;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                PaintMarker(buf, f, ctx, lane, center, 3.0, ctx.Colors[colorIndex], ctx.Direction);
            }
        });
    }

    private static RawAnimation Twinkle(EffectContext ctx)
    {
        const int frameCount = 48;
        var rng = new DeterministicRandom(0xA5A5A5A5UL);
        var baseColor = new int[ctx.Lanes, ctx.LedsPerLane];
        var phase = new double[ctx.Lanes, ctx.LedsPerLane];
        for (var lane = 0; lane < ctx.Lanes; lane++)
        {
            for (var led = 0; led < ctx.LedsPerLane; led++)
            {
                baseColor[lane, led] = rng.NextInt(6);
                phase[lane, led] = rng.NextDouble();
            }
        }
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var t = (double)f / frameCount + phase[lane, led];
                    var brightness = 0.15 + 0.85 * Breath(t);
                    var color = Scale(ctx.Colors[baseColor[lane, led]], brightness);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation Parallel(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var g = 0; g < 3; g++)
                {
                    var phase = (double)f / frameCount + (double)g / 3 + (double)lane / ctx.Lanes;
                    phase -= Math.Floor(phase);
                    var center = phase * (ctx.LedsPerLane - 1);
                    var color = ctx.Colors[(g * 2 + lane % 2) % 6];
                    PaintMarker(buf, f, ctx, lane, center, 2.0, color, ctx.Direction);
                }
            }
        });
    }

    private static RawAnimation ShockWave(EffectContext ctx)
    {
        const int frameCount = 36;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var travel = (double)f / frameCount;
            var center = travel * (ctx.LedsPerLane - 1);
            var color = ctx.Colors[f * 6 / frameCount % 6];
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                PaintMarker(buf, f, ctx, lane, center, 6.0, color, ctx.Direction);
            }
        });
    }

    private static RawAnimation Ripple(EffectContext ctx)
    {
        const int frameCount = 32;
        var white = new RgbColor(255, 255, 255);
        var center = (ctx.LedsPerLane - 1) / 2.0;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var radius = (double)f / frameCount * (ctx.LedsPerLane / 2.0 + 2);
            var fade = 1.0 - (double)f / frameCount;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var dist = Math.Abs(Math.Abs(led - center) - radius);
                    if (dist <= 1.5)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Scale(white, fade * (1.0 - dist / 1.5)));
                    }
                }
            }
        });
    }

    private static RawAnimation Voice(EffectContext ctx)
    {
        const int frameCount = 40;
        var rng = new DeterministicRandom(0xF00DBEEFUL);
        var offsets = new double[ctx.Lanes];
        for (var lane = 0; lane < ctx.Lanes; lane++)
        {
            offsets[lane] = rng.NextDouble();
        }
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                var t = (double)f / frameCount;
                var height = 0.3 + 0.7 * Math.Abs(Math.Sin((t + lane * 0.13) * 2 * Math.PI * 3 + offsets[lane] * 2 * Math.PI));
                var lit = (int)(height * ctx.LedsPerLane);
                for (var led = 0; led < lit; led++)
                {
                    var colorIndex = led * 6 / ctx.LedsPerLane % 6;
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[colorIndex]);
                }
            }
        });
    }

    private static RawAnimation Drizzling(EffectContext ctx)
    {
        const int frameCount = 48;
        const int dropsPerLane = 4;
        var rng = new DeterministicRandom(0xD1220EUL);
        var starts = new double[dropsPerLane];
        for (var i = 0; i < dropsPerLane; i++)
        {
            starts[i] = rng.NextDouble();
        }
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var d = 0; d < dropsPerLane; d++)
                {
                    var phase = (double)f / frameCount + starts[d] + (double)lane / ctx.Lanes;
                    phase -= Math.Floor(phase);
                    var center = phase * (ctx.LedsPerLane + 4) - 4;
                    if (center < 0 || center > ctx.LedsPerLane - 1)
                    {
                        continue;
                    }
                    PaintMarker(buf, f, ctx, lane, center, 2.0, ctx.Colors[0], 0);
                }
            }
        });
    }

    private static RawAnimation Endless(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var t = (double)pos / ctx.LedsPerLane - (double)f / frameCount;
                    t -= Math.Floor(t);
                    var idx = (int)(t * 6);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[idx % 6]);
                }
            }
        });
    }

    private static RawAnimation ShuttleRun(EffectContext ctx)
    {
        const int frameCount = 40;
        const double blockLen = 4.0;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var travel = BounceTravel((double)f / frameCount);
            var center = travel * (ctx.LedsPerLane - 1);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                var color = ctx.Colors[lane % 6];
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    if (Math.Abs(led - center) <= blockLen)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                    }
                }
            }
        });
    }

    private static RawAnimation River(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var t = (double)pos / ctx.LedsPerLane + (double)f / frameCount;
                    t -= Math.Floor(t);
                    var scaled = t * 6;
                    var idx = (int)scaled;
                    var frac = scaled - idx;
                    var color = Lerp(ctx.Colors[idx % 6], ctx.Colors[(idx + 1) % 6], frac);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation Hourglass(EffectContext ctx)
    {
        const int frameCount = 32;
        var half = ctx.LedsPerLane / 2;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var filled = (int)(t * half);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    if (led < half && led >= half - filled)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[0]);
                    }
                    else if (led >= half && led < half + filled)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[1]);
                    }
                }
            }
        });
    }

    private static RawAnimation Pioneer(EffectContext ctx)
    {
        const int frameCount = 40;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var lead = t * (ctx.LedsPerLane - 1);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    if (pos <= lead)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, ctx.Colors[1]);
                    }
                }
                PaintMarker(buf, f, ctx, lane, lead, 2.0, ctx.Colors[0], ctx.Direction);
            }
        });
    }

    private static RawAnimation ElectricCurrent(EffectContext ctx)
    {
        const int frameCount = 30;
        var rng = new DeterministicRandom(0xE1EC70UL);
        var flicker = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            flicker[i] = i == 0 ? 1.0 : 0.6 + 0.4 * rng.NextDouble();
        }
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var center = t * (ctx.LedsPerLane - 1);
            var color = Scale(ctx.Colors[0], flicker[f]);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                PaintMarker(buf, f, ctx, lane, center, 1.5, color, ctx.Direction);
            }
        });
    }

    private static RawAnimation Transformation(EffectContext ctx)
    {
        const int frameCount = 32;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var mix = Breath((double)f / frameCount);
            var color = Lerp(ctx.Colors[0], ctx.Colors[1], mix);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation GradientRibbon(EffectContext ctx)
    {
        const int frameCount = 40;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var t = (double)pos / ctx.LedsPerLane + (double)f / frameCount * 0.25;
                    t -= Math.Floor(t);
                    var scaled = t * 3;
                    var idx = (int)scaled;
                    var frac = scaled - idx;
                    var color = Lerp(ctx.Colors[idx % 3], ctx.Colors[(idx + 1) % 3], frac);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation Snooker(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var b = 0; b < 6; b++)
                {
                    var speedFactor = 1.0 + b * 0.4;
                    var travel = BounceTravel((double)f / frameCount * speedFactor + (double)b / 6);
                    var center = travel * (ctx.LedsPerLane - 1);
                    PaintMarker(buf, f, ctx, lane, center, 1.5, ctx.Colors[b], 0);
                }
            }
        });
    }

    private static RawAnimation Mixing(EffectContext ctx)
    {
        const int frameCount = 32;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 8.5), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var meet = t * (ctx.LedsPerLane / 2.0);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    RgbColor color;
                    if (pos < meet)
                    {
                        color = ctx.Colors[0];
                    }
                    else if (pos > ctx.LedsPerLane - 1 - meet)
                    {
                        color = ctx.Colors[1];
                    }
                    else
                    {
                        color = Lerp(ctx.Colors[0], ctx.Colors[1], 0.5);
                    }
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation PingPong(EffectContext ctx)
    {
        const int frameCount = 40;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var phase = (double)f / frameCount;
            var travel = BounceTravel(phase);
            var goingForward = phase - Math.Floor(phase) < 0.5;
            var color = ctx.Colors[goingForward ? 0 : 1];
            var center = travel * (ctx.LedsPerLane - 1);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                PaintMarker(buf, f, ctx, lane, center, 2.0, color, 0);
            }
        });
    }

    private static RawAnimation Runway(EffectContext ctx)
    {
        const int frameCount = 40;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var dim = Scale(ctx.Colors[1], 0.15);
            var t = (double)f / frameCount;
            var center = t * (ctx.LedsPerLane - 1);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, dim);
                }
                PaintMarker(buf, f, ctx, lane, center, 1.0, ctx.Colors[0], ctx.Direction);
            }
        });
    }

    private static RawAnimation Tide(EffectContext ctx)
    {
        const int frameCount = 48;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var wash = Breath((double)f / frameCount) * ctx.LedsPerLane;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var idx = (int)((double)pos / ctx.LedsPerLane * 6) % 6;
                    var color = pos < wash ? ctx.Colors[idx] : Scale(ctx.Colors[idx], 0.1);
                    SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, color);
                }
            }
        });
    }

    private static RawAnimation BlowUp(EffectContext ctx)
    {
        const int frameCount = 30;
        var center = (ctx.LedsPerLane - 1) / 2.0;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 15.5), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var radius = t * (ctx.LedsPerLane / 2.0 + 2);
            var fade = 1.0 - t;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    if (Math.Abs(led - center) <= radius)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Scale(ctx.Colors[0], fade));
                    }
                }
            }
        });
    }

    private static RawAnimation Meteor(EffectContext ctx)
    {
        const int frameCount = 30;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 15.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var center = t * (ctx.LedsPerLane + 8) - 4;
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var led = 0; led < ctx.LedsPerLane; led++)
                {
                    var pos = Pos(led, ctx.LedsPerLane, ctx.Direction);
                    var dist = center - pos;
                    if (dist >= 0 && dist <= 8)
                    {
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, led, Scale(ctx.Colors[0], 1.0 - dist / 8.0));
                    }
                }
            }
        });
    }

    private static RawAnimation Stack(EffectContext ctx)
    {
        const int perBlock = 6;
        var blocks = Math.Min(6, ctx.LedsPerLane);
        var blockLen = ctx.LedsPerLane / blocks;
        var frameCount = blocks * perBlock + perBlock;
        return Loop(frameCount, ctx.Lanes, ctx.LedsPerLane, ScaledInterval(ctx.Speed, 16.0), (buf, f) =>
        {
            var filled = Math.Min(blocks, f / perBlock);
            for (var lane = 0; lane < ctx.Lanes; lane++)
            {
                for (var b = 0; b < filled; b++)
                {
                    var start = b * blockLen;
                    var end = b == blocks - 1 ? ctx.LedsPerLane : start + blockLen;
                    for (var logical = start; logical < end; logical++)
                    {
                        var bufIdx = Pos(logical, ctx.LedsPerLane, ctx.Direction);
                        SetLed(buf, f, ctx.Lanes, ctx.LedsPerLane, lane, bufIdx, ctx.Colors[b % 6]);
                    }
                }
            }
        });
    }
}
