using System;
using System.Collections.Generic;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3FanEffects
{
    private static Slv3StrimerEffectInfo[] BuildTlCatalog() => new[]
    {
        new Slv3StrimerEffectInfo { Key = "rainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "rainbowMorph", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "static", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "breathing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "runway", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "meteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "colorCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 3 },
        new Slv3StrimerEffectInfo { Key = "staggered", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "tide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "mixing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "voice", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "door", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "render", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "ripple", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "reflect", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "tailChasing", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "paint", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "pingPong", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "stack", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "coverCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "wave", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "racing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "lottery", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "intertwine", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "meteorShower", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "collide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "electricCurrent", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "kaleidoscope", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "twinkle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
    };

    private static IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> BuildTlRenderers() =>
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
            ["voice"] = TlVoice,
            ["door"] = TlDoor,
            ["render"] = SlRender,
            ["ripple"] = SlRipple,
            ["reflect"] = SlReflect,
            ["tailChasing"] = TlTailChasing,
            ["paint"] = TlPaint,
            ["pingPong"] = SlPingPong,
            ["stack"] = SlStack,
            ["coverCycle"] = TlCoverCycle,
            ["wave"] = TlWave,
            ["racing"] = TlRacing,
            ["lottery"] = TlLottery,
            ["intertwine"] = TlIntertwine,
            ["meteorShower"] = TlMeteorShower,
            ["collide"] = SlCollide,
            ["electricCurrent"] = SlElectricCurrent,
            ["kaleidoscope"] = TlKaleidoscope,
            ["twinkle"] = SlTwinkle,
        };

    /// <summary>No live audio input is available to a pre-rendered loop; stands in with an irregular pulse sequence.</summary>
    private static RawAnimation TlVoice(FanEffectContext ctx)
    {
        const int frameCount = 32;
        var rng = new DeterministicRandom(0x707CE1CEUL);
        var level = new double[frameCount];
        var current = 0.3;
        var peak = 0.0;
        for (var i = 0; i < frameCount; i++)
        {
            current = current * 0.5 + rng.NextDouble() * 0.5;
            level[i] = current;
            peak = Math.Max(peak, current);
        }
        for (var i = 0; i < frameCount; i++)
        {
            level[i] /= peak;
        }
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 16.0), (buf, f) =>
        {
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.1 + 0.9 * level[f]);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>Two halves of the ring split open from the center and close again, like sliding doors.</summary>
    private static RawAnimation TlDoor(FanEffectContext ctx)
    {
        const int frameCount = 40;
        var half = ctx.RingLen / 2.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var opened = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount) * half;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var distFromCenter = Math.Abs(ringPos - (ctx.RingLen - 1) / 2.0);
                    if (distFromCenter <= opened)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>A fixed-length arc chases continuously around the ring rather than sweeping once and resetting.</summary>
    private static RawAnimation TlTailChasing(FanEffectContext ctx)
    {
        const int frameCount = 40;
        var arcLen = Math.Max(2, ctx.RingLen / 3);
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var head = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var behind = head - pos;
                    behind -= ctx.RingLen * Math.Floor(behind / ctx.RingLen);
                    if (behind <= arcLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], 1.0 - behind / arcLen));
                    }
                }
            }
        });
    }

    /// <summary>Fills the ring from its start position to the end, then repeats.</summary>
    private static RawAnimation TlPaint(FanEffectContext ctx)
    {
        const int frameCount = 32;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var filled = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (ringPos < filled)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>The whole ring switches, one solid colour at a time, through the user palette.</summary>
    private static RawAnimation TlCoverCycle(FanEffectContext ctx)
    {
        const int framesPerColor = 8;
        const int frameCount = framesPerColor * 6;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var slot = f / framesPerColor;
            var idx = ctx.Direction != 0 ? 5 - slot : slot;
            var color = ctx.Colors[idx % 6];
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A single colour's brightness follows a sine wave travelling along the ring.</summary>
    private static RawAnimation TlWave(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var t = (double)pos / ctx.RingLen - (double)f / frameCount;
                    var brightness = 0.1 + 0.9 * Slv3WirelessEffectMath.Breath(t);
                    SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], brightness));
                }
            }
        });
    }

    /// <summary>Two markers travel the same direction at different speeds, the faster one lapping the slower.</summary>
    private static RawAnimation TlRacing(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var leadPos = (t * 1.5 * ctx.RingLen) % ctx.RingLen;
            var trailPos = (t * ctx.RingLen) % ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, trailPos, 1.0, ctx.Colors[1], 0);
                PaintRingMarker(buf, f, ctx, fan, leadPos, 1.0, ctx.Colors[0], 0);
            }
        });
    }

    /// <summary>Random colours settle, slot-machine style, into one final colour by the end of the loop.</summary>
    private static RawAnimation TlLottery(FanEffectContext ctx)
    {
        const int frameCount = 40;
        var rng = new DeterministicRandom(0x107E5701UL);
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var settleFrac = (double)f / (frameCount - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var settled = rng.NextDouble() < settleFrac * settleFrac;
                    var color = settled ? ctx.Colors[0] : ctx.Colors[rng.NextInt(6)];
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>Two colours alternate LED by LED, the alternation shifting by one position each frame.</summary>
    private static RawAnimation TlIntertwine(FanEffectContext ctx)
    {
        const int frameCount = 24;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var band = (ringPos + f) % 2;
                    if (ctx.Direction != 0)
                    {
                        band = 1 - band;
                    }
                    SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[band]);
                }
            }
        });
    }

    /// <summary>Several meteor trails fall around the ring at staggered offsets instead of one at a time.</summary>
    private static RawAnimation TlMeteorShower(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const int trailCount = 3;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var trail = 0; trail < trailCount; trail++)
                {
                    var t = (double)f / frameCount + (double)trail / trailCount;
                    t -= Math.Floor(t);
                    var head = t * (ctx.RingLen + 6) - 3;
                    for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                    {
                        var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                        var dist = head - pos;
                        if (dist >= 0 && dist <= 4)
                        {
                            SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], 1.0 - dist / 4.0));
                        }
                    }
                }
            }
        });
    }

    /// <summary>Hue mirrored around the ring's midpoint, the mirror axis rotating over time.</summary>
    private static RawAnimation TlKaleidoscope(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 11.0), (buf, f) =>
        {
            var rotate = (ctx.Direction != 0 ? -1.0 : 1.0) * f / frameCount;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var mirrored = Math.Abs(ringPos - (ctx.RingLen - 1) / 2.0);
                    var hue = mirrored / ctx.RingLen + rotate;
                    SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Hue(hue));
                }
            }
        });
    }
}
