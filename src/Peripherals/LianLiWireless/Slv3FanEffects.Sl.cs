using System;
using System.Collections.Generic;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3FanEffects
{
    private static Slv3StrimerEffectInfo[] BuildSlCatalog() => new[]
    {
        new Slv3StrimerEffectInfo { Key = "rainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "rainbowMorph", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "static", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "breathing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "runway", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1, Mergeable = true },
        new Slv3StrimerEffectInfo { Key = "meteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "colorCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 3 },
        new Slv3StrimerEffectInfo { Key = "staggered", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "tide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2, Mergeable = true },
        new Slv3StrimerEffectInfo { Key = "mixing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "render", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "pingPong", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "stack", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "ripple", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "collide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "reflect", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "electricCurrent", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "endless", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "river", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "duel", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "hourglass", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "pioneer", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "shuttleRun", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "gradientRibbon", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "twinkle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
    };

    private static IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> BuildSlRenderers() =>
        new Dictionary<string, Func<FanEffectContext, RawAnimation>>(StringComparer.Ordinal)
        {
            ["rainbow"] = SlRainbow,
            ["rainbowMorph"] = SlRainbowMorph,
            ["static"] = SlStatic,
            ["breathing"] = SlBreathing,
            ["runway"] = SlRunway,
            ["meteor"] = SlMeteor,
            ["colorCycle"] = SlColorCycle,
            ["staggered"] = SlStaggered,
            ["tide"] = SlTide,
            ["mixing"] = SlMixing,
            ["render"] = SlRender,
            ["pingPong"] = SlPingPong,
            ["stack"] = SlStack,
            ["ripple"] = SlRipple,
            ["collide"] = SlCollide,
            ["reflect"] = SlReflect,
            ["electricCurrent"] = SlElectricCurrent,
            ["endless"] = SlEndless,
            ["river"] = SlRiver,
            ["duel"] = SlDuel,
            ["hourglass"] = SlHourglass,
            ["pioneer"] = SlPioneer,
            ["shuttleRun"] = SlShuttleRun,
            ["gradientRibbon"] = SlGradientRibbon,
            ["twinkle"] = SlTwinkle,
        };

    /// <summary>Writes the same colour to a ring position in both the inner and outer wire halves of one fan.</summary>
    private static void SetRing(byte[] buf, int frame, FanEffectContext ctx, int fan, int ringPos, RgbColor c)
    {
        Slv3WirelessEffectMath.SetLed(buf, frame, ctx.FanCount, ctx.LedsPerFan, fan, ringPos, c);
        Slv3WirelessEffectMath.SetLed(buf, frame, ctx.FanCount, ctx.LedsPerFan, fan, ringPos + ctx.RingLen, c);
    }

    private static void PaintRingMarker(
        byte[] buf, int frame, FanEffectContext ctx, int fan, double centerPos, double trailLen, RgbColor color, int direction)
    {
        for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
        {
            var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, direction);
            var dist = Math.Abs(pos - centerPos);
            if (dist > trailLen)
            {
                continue;
            }
            var intensity = 1.0 - dist / (trailLen + 1);
            SetRing(buf, frame, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(color, intensity));
        }
    }

    private static RawAnimation RingLoop(int frameCount, FanEffectContext ctx, double intervalMs, Action<byte[], int> paint)
    {
        var buf = Slv3WirelessEffectMath.AllocateFrames(frameCount, ctx.FanCount, ctx.LedsPerFan);
        for (var f = 0; f < frameCount; f++)
        {
            paint(buf, f);
        }
        return new RawAnimation(buf, frameCount, intervalMs);
    }

    private static RawAnimation SlRainbow(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 22.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var hue = (double)pos / ctx.RingLen + (double)f / frameCount;
                    SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Hue(hue));
                }
            }
        });
    }

    private static RawAnimation SlRainbowMorph(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 22.0), (buf, f) =>
        {
            var color = Slv3WirelessEffectMath.Hue((double)f / frameCount);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    private static RawAnimation SlStatic(FanEffectContext ctx) =>
        RingLoop(1, ctx, 1000.0, (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                }
            }
        });

    private static RawAnimation SlBreathing(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 20.0), (buf, f) =>
        {
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.05 + 0.95 * Slv3WirelessEffectMath.Breath((double)f / frameCount));
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    private static RawAnimation SlRunway(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var dim = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.12);
            var center = (double)f / frameCount * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, dim);
                }
                PaintRingMarker(buf, f, ctx, fan, center, 1.0, ctx.Colors[0], 0);
            }
        });
    }

    /// <summary>One runway marker travelling fan to fan along the chain, each fan taking the time one per-fan sweep takes.</summary>
    private static RawAnimation SlMergedRunway(FanEffectContext ctx)
    {
        const int framesPerFan = 40;
        var frameCount = framesPerFan * ctx.FanCount;
        var lineLen = ctx.FanCount * ctx.RingLen;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var dim = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.12);
            var center = (double)f / frameCount * (lineLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, dim);
                }
                PaintRingMarker(buf, f, ctx, fan, center - fan * ctx.RingLen, 1.0, ctx.Colors[0], 0);
            }
        });
    }

    private static RawAnimation SlMeteor(FanEffectContext ctx)
    {
        const int frameCount = 30;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 15.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var center = t * (ctx.RingLen + 8) - 4;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var dist = center - pos;
                    if (dist >= 0 && dist <= 8)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], 1.0 - dist / 8.0));
                    }
                }
            }
        });
    }

    /// <summary>Three user colours as discrete arcs, spaced apart, rotating around the ring.</summary>
    private static RawAnimation SlColorCycle(FanEffectContext ctx)
    {
        const int frameCount = 48;
        var segLen = Math.Max(1, ctx.RingLen / 6);
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 16.0), (buf, f) =>
        {
            var shift = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var offset = (pos + shift) % ctx.RingLen;
                    var band = (int)(offset / (2 * segLen));
                    var withinBand = offset % (2 * segLen);
                    var color = withinBand < segLen ? ctx.Colors[band % 3] : default;
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>Odd and even fans breathe out of phase, one group per colour.</summary>
    private static RawAnimation SlStaggered(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 18.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                var group = fan % 2;
                var phase = (double)f / frameCount + group * 0.5;
                var color = Slv3WirelessEffectMath.Scale(ctx.Colors[group], 0.1 + 0.9 * Slv3WirelessEffectMath.Breath(phase));
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    private static RawAnimation SlTide(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var wash = Slv3WirelessEffectMath.Breath((double)f / frameCount) * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var color = ringPos < wash ? ctx.Colors[0] : Slv3WirelessEffectMath.Scale(ctx.Colors[1], 0.1);
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>One wash filling the chain fan by fan and draining back, each fan taking one per-fan tide's time.</summary>
    private static RawAnimation SlMergedTide(FanEffectContext ctx)
    {
        const int framesPerFan = 48;
        var frameCount = framesPerFan * ctx.FanCount;
        var lineLen = ctx.FanCount * ctx.RingLen;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var wash = Slv3WirelessEffectMath.Breath((double)f / frameCount) * lineLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var color = fan * ctx.RingLen + ringPos < wash ? ctx.Colors[0] : Slv3WirelessEffectMath.Scale(ctx.Colors[1], 0.1);
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    private static RawAnimation SlMixing(FanEffectContext ctx)
    {
        const int frameCount = 32;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 9.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var meet = t * (ctx.RingLen / 2.0);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    RgbColor color;
                    if (ringPos < meet)
                    {
                        color = ctx.Colors[0];
                    }
                    else if (ringPos > ctx.RingLen - 1 - meet)
                    {
                        color = ctx.Colors[1];
                    }
                    else
                    {
                        color = Slv3WirelessEffectMath.Lerp(ctx.Colors[0], ctx.Colors[1], 0.5);
                    }
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A single colour pulses outward from the ring's midpoint and back.</summary>
    private static RawAnimation SlRender(FanEffectContext ctx)
    {
        const int frameCount = 36;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 13.0), (buf, f) =>
        {
            var phase = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount);
            var travel = ctx.Direction != 0 ? 1.0 - phase : phase;
            var radius = travel * (ctx.RingLen / 2.0);
            var center = (ctx.RingLen - 1) / 2.0;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (Math.Abs(ringPos - center) <= radius)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    private static RawAnimation SlPingPong(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var phase = (double)f / frameCount;
            var travel = Slv3WirelessEffectMath.BounceTravel(phase);
            var goingForward = phase - Math.Floor(phase) < 0.5;
            var color = ctx.Colors[goingForward ? 0 : 1];
            var center = travel * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, center, 2.0, color, 0);
            }
        });
    }

    private static RawAnimation SlStack(FanEffectContext ctx)
    {
        const int perBlock = 6;
        var blocks = Math.Min(6, ctx.RingLen);
        var blockLen = ctx.RingLen / blocks;
        var frameCount = blocks * perBlock + perBlock;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 16.0), (buf, f) =>
        {
            var filled = Math.Min(blocks, f / perBlock);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var b = 0; b < filled; b++)
                {
                    var start = b * blockLen;
                    var end = b == blocks - 1 ? ctx.RingLen : start + blockLen;
                    for (var logical = start; logical < end; logical++)
                    {
                        var ringPos = Slv3WirelessEffectMath.Pos(logical, ctx.RingLen, ctx.Direction);
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[b % 6]);
                    }
                }
            }
        });
    }

    private static RawAnimation SlRipple(FanEffectContext ctx)
    {
        const int frameCount = 32;
        var center = (ctx.RingLen - 1) / 2.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var radius = (double)f / frameCount * (ctx.RingLen / 2.0 + 2);
            var fade = 1.0 - (double)f / frameCount;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var dist = Math.Abs(Math.Abs(ringPos - center) - radius);
                    if (dist <= 1.5)
                    {
                        var intensity = dist <= 0.5 ? 1.0 : 1.0 - (dist - 0.5) / 1.0;
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], fade * intensity));
                    }
                }
            }
        });
    }

    /// <summary>Two markers start at opposite ends of the ring and travel toward a collision at the midpoint.</summary>
    private static RawAnimation SlCollide(FanEffectContext ctx)
    {
        const int frameCount = 30;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var half = (ctx.RingLen - 1) / 2.0;
            var advance = t * half;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, advance, 1.5, ctx.Colors[0], 0);
                PaintRingMarker(buf, f, ctx, fan, ctx.RingLen - 1 - advance, 1.5, ctx.Colors[1], 0);
            }
        });
    }

    /// <summary>A single marker bounces end to end along the ring, leaving a fading trail.</summary>
    private static RawAnimation SlReflect(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var travel = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount);
            var center = travel * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, center, 3.0, ctx.Colors[0], 0);
            }
        });
    }

    private static RawAnimation SlElectricCurrent(FanEffectContext ctx)
    {
        const int frameCount = 30;
        var rng = new DeterministicRandom(0xE1EC70UL);
        var flicker = new double[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            flicker[i] = i == 0 ? 1.0 : 0.6 + 0.4 * rng.NextDouble();
        }
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var center = t * (ctx.RingLen - 1);
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], flicker[f]);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, center, 1.5, color, 0);
            }
        });
    }

    private static RawAnimation SlEndless(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var t = (double)ringPos / ctx.RingLen - (double)f / frameCount;
                    t -= Math.Floor(t);
                    var idx = (int)(t * 6);
                    SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[idx % 6]);
                }
            }
        });
    }

    private static RawAnimation SlRiver(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var t = (double)pos / ctx.RingLen + (double)f / frameCount;
                    t -= Math.Floor(t);
                    var scaled = t * 6;
                    var idx = (int)scaled;
                    var frac = scaled - idx;
                    var color = Slv3WirelessEffectMath.Lerp(ctx.Colors[idx % 6], ctx.Colors[(idx + 1) % 6], frac);
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>Two colours race around the ring from opposite starting points, each claiming the ground it crosses.</summary>
    private static RawAnimation SlDuel(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var advance = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var fromStart = (ringPos + advance) % ctx.RingLen;
                    var fromEnd = (ctx.RingLen - 1 - ringPos + advance) % ctx.RingLen;
                    var color = fromStart <= fromEnd ? ctx.Colors[0] : ctx.Colors[1];
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    private static RawAnimation SlHourglass(FanEffectContext ctx)
    {
        const int frameCount = 32;
        var half = ctx.RingLen / 2;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var filled = (int)(t * half);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (ringPos < half && ringPos >= half - filled)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                    else if (ringPos >= half && ringPos < half + filled)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[1]);
                    }
                }
            }
        });
    }

    private static RawAnimation SlPioneer(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var lead = t * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (ringPos <= lead)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[1]);
                    }
                }
                PaintRingMarker(buf, f, ctx, fan, lead, 2.0, ctx.Colors[0], 0);
            }
        });
    }

    private static RawAnimation SlShuttleRun(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double blockLen = 4.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var travel = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount);
            var center = travel * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                var color = ctx.Colors[fan % 6];
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (Math.Abs(ringPos - center) <= blockLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, color);
                    }
                }
            }
        });
    }

    private static RawAnimation SlGradientRibbon(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var hue = (double)pos / ctx.RingLen + (double)f / frameCount * 0.25;
                    SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Hue(hue));
                }
            }
        });
    }

    private static RawAnimation SlTwinkle(FanEffectContext ctx)
    {
        const int frameCount = 48;
        var rng = new DeterministicRandom(0xA5A5A5A5UL);
        var baseColor = new int[ctx.FanCount, 32];
        var phase = new double[ctx.FanCount, 32];
        var ringLen = Math.Min(ctx.RingLen, 32);
        for (var fan = 0; fan < ctx.FanCount; fan++)
        {
            for (var ringPos = 0; ringPos < ringLen; ringPos++)
            {
                baseColor[fan, ringPos] = rng.NextInt(6);
                phase[fan, ringPos] = rng.NextDouble();
            }
        }
        var sign = ctx.Direction != 0 ? -1.0 : 1.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var slot = ringPos % ringLen;
                    var t = sign * (double)f / frameCount + phase[fan, slot];
                    var brightness = 0.15 + 0.85 * Slv3WirelessEffectMath.Breath(t);
                    var color = Slv3WirelessEffectMath.Scale(ctx.Colors[baseColor[fan, slot]], brightness);
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }
}
