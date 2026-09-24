using System;
using System.Collections.Generic;
using DeterministicRandom = Nexus.Service.Peripherals.LianLiWireless.Slv3WirelessEffectMath.DeterministicRandom;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3FanEffects
{
    private static Slv3StrimerEffectInfo[] BuildSlInfCatalog() => new[]
    {
        new Slv3StrimerEffectInfo { Key = "rainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "rainbowMorph", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "static", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "breathing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "runway", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "meteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "twinkle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "taichi", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "colorCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 3 },
        new Slv3StrimerEffectInfo { Key = "mopUp", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "meteorRainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "colorfulMeteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "lottery", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "warning", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "voice", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "mixing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "tide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "scan", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "doubleMeteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "meteorContest", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "meteorMix", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "returnArc", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "doubleArc", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "door", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "heartBeat", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "heartBeatRunway", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "disco", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
        new Slv3StrimerEffectInfo { Key = "electricCurrent", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "reflect", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "gradientRibbon", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0 },
        new Slv3StrimerEffectInfo { Key = "wing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "drumming", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 1 },
        new Slv3StrimerEffectInfo { Key = "boomerang", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2 },
        new Slv3StrimerEffectInfo { Key = "candyBox", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 6 },
    };

    private static IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> BuildSlInfRenderers() =>
        new Dictionary<string, Func<FanEffectContext, RawAnimation>>(StringComparer.Ordinal)
        {
            ["rainbow"] = SlRainbow,
            ["rainbowMorph"] = SlRainbowMorph,
            ["static"] = SlStatic,
            ["breathing"] = SlBreathing,
            ["runway"] = SlRunway,
            ["meteor"] = SlMeteor,
            ["twinkle"] = SlTwinkle,
            ["taichi"] = InfTaichi,
            ["colorCycle"] = SlColorCycle,
            ["mopUp"] = InfMopUp,
            ["meteorRainbow"] = InfMeteorRainbow,
            ["colorfulMeteor"] = InfColorfulMeteor,
            ["lottery"] = TlLottery,
            ["warning"] = InfWarning,
            ["voice"] = TlVoice,
            ["mixing"] = SlMixing,
            ["tide"] = SlTide,
            ["scan"] = InfScan,
            ["doubleMeteor"] = InfDoubleMeteor,
            ["meteorContest"] = InfMeteorContest,
            ["meteorMix"] = InfMeteorMix,
            ["returnArc"] = InfReturnArc,
            ["doubleArc"] = InfDoubleArc,
            ["door"] = TlDoor,
            ["heartBeat"] = InfHeartBeat,
            ["heartBeatRunway"] = InfHeartBeatRunway,
            ["disco"] = InfDisco,
            ["electricCurrent"] = SlElectricCurrent,
            ["reflect"] = SlReflect,
            ["gradientRibbon"] = SlGradientRibbon,
            ["wing"] = InfWing,
            ["drumming"] = InfDrumming,
            ["boomerang"] = InfBoomerang,
            ["candyBox"] = InfCandyBox,
        };

    /// <summary>A split point between two colours travels once around the ring.</summary>
    private static RawAnimation InfTaichi(FanEffectContext ctx)
    {
        const int frameCount = 48;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var split = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var color = pos < split ? ctx.Colors[0] : ctx.Colors[1];
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A boundary sweeps around the ring, wiping between colour and dim.</summary>
    private static RawAnimation InfMopUp(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var boundary = (double)f / frameCount * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var color = pos < boundary ? ctx.Colors[0] : Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.08);
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A meteor trail whose hue shifts along its own length.</summary>
    private static RawAnimation InfMeteorRainbow(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double trailLen = 10.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var head = t * (ctx.RingLen + trailLen) - trailLen / 2;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var dist = head - pos;
                    if (dist >= 0 && dist <= trailLen)
                    {
                        var color = Slv3WirelessEffectMath.Hue(pos / ctx.RingLen + t);
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(color, 1.0 - dist / trailLen));
                    }
                }
            }
        });
    }

    /// <summary>One meteor trail per fan, each carrying a different palette colour.</summary>
    private static RawAnimation InfColorfulMeteor(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double trailLen = 8.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var head = t * (ctx.RingLen + trailLen) - trailLen / 2;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                var color = ctx.Colors[fan % 6];
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var dist = head - pos;
                    if (dist >= 0 && dist <= trailLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(color, 1.0 - dist / trailLen));
                    }
                }
            }
        });
    }

    /// <summary>The ring strobes between full colour and off, like a hazard beacon.</summary>
    private static RawAnimation InfWarning(FanEffectContext ctx)
    {
        const int frameCount = 20;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 20.0), (buf, f) =>
        {
            var on = f % 10 < 3;
            var color = on ? ctx.Colors[0] : default;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A single narrow marker sweeps back and forth with a hard edge, like a radar scan.</summary>
    private static RawAnimation InfScan(FanEffectContext ctx)
    {
        const int frameCount = 32;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 13.0), (buf, f) =>
        {
            var travel = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount);
            var center = travel * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (Math.Abs(ringPos - center) <= 0.6)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>Two meteor trails cross the ring travelling in opposite directions.</summary>
    private static RawAnimation InfDoubleMeteor(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double trailLen = 8.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var headA = t * (ctx.RingLen + trailLen) - trailLen / 2;
            var headB = ctx.RingLen - 1 - headA;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var distA = headA - pos;
                    var distB = pos - headB;
                    if (distA >= 0 && distA <= trailLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], 1.0 - distA / trailLen));
                    }
                    else if (distB >= 0 && distB <= trailLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[1], 1.0 - distB / trailLen));
                    }
                }
            }
        });
    }

    /// <summary>Two meteors of different colours race the ring in the same direction, offset in phase.</summary>
    private static RawAnimation InfMeteorContest(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double trailLen = 7.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var headA = t * (ctx.RingLen + trailLen) - trailLen / 2;
            var headB = ((t + 0.3) % 1.0) * (ctx.RingLen + trailLen) - trailLen / 2;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = ringPos;
                    var distA = headA - pos;
                    var distB = headB - pos;
                    if (distA >= 0 && distA <= trailLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[0], 1.0 - distA / trailLen));
                    }
                    else if (distB >= 0 && distB <= trailLen)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(ctx.Colors[1], 1.0 - distB / trailLen));
                    }
                }
            }
        });
    }

    /// <summary>A meteor trail blends from one user colour to another along its length.</summary>
    private static RawAnimation InfMeteorMix(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double trailLen = 10.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var head = t * (ctx.RingLen + trailLen) - trailLen / 2;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    var dist = head - pos;
                    if (dist >= 0 && dist <= trailLen)
                    {
                        var color = Slv3WirelessEffectMath.Lerp(ctx.Colors[0], ctx.Colors[1], dist / trailLen);
                        SetRing(buf, f, ctx, fan, ringPos, Slv3WirelessEffectMath.Scale(color, 1.0 - dist / trailLen));
                    }
                }
            }
        });
    }

    /// <summary>An arc grows out from one end of the ring, then retreats back the way it came.</summary>
    private static RawAnimation InfReturnArc(FanEffectContext ctx)
    {
        const int frameCount = 32;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var reach = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount) * ctx.RingLen;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var pos = Slv3WirelessEffectMath.Pos(ringPos, ctx.RingLen, ctx.Direction);
                    if (pos < reach)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>Two arcs grow from both ends toward the middle, then retreat, in two colours.</summary>
    private static RawAnimation InfDoubleArc(FanEffectContext ctx)
    {
        const int frameCount = 32;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var reach = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount) * (ctx.RingLen / 2.0);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    if (ringPos < reach)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                    else if (ringPos >= ctx.RingLen - reach)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[1]);
                    }
                }
            }
        });
    }

    /// <summary>Brightness follows a two-beat pulse envelope, like a heartbeat.</summary>
    private static RawAnimation InfHeartBeat(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var beat = Math.Max(Beat(t, 0.0), Beat(t, 0.18));
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.08 + 0.92 * beat);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A heartbeat brightness pulse riding on a runway marker sweeping the ring.</summary>
    private static RawAnimation InfHeartBeatRunway(FanEffectContext ctx)
    {
        const int frameCount = 40;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var t = (double)f / frameCount;
            var beat = Math.Max(Beat(t, 0.0), Beat(t, 0.18));
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.08 + 0.92 * beat);
            var center = t * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, center, 3.0, color, 0);
            }
        });
    }

    /// <summary>A short pulse repeated twice within one beat period, decaying between pulses.</summary>
    private static double Beat(double t, double offset)
    {
        var phase = t - offset;
        phase -= Math.Floor(phase);
        const double width = 0.08;
        return phase <= width ? 1.0 - phase / width : 0.0;
    }

    /// <summary>Every LED takes a fresh random palette colour each frame, with no persistence between frames.</summary>
    private static RawAnimation InfDisco(FanEffectContext ctx)
    {
        const int frameCount = 24;
        var rng = new DeterministicRandom(0xD15C0UL);
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[rng.NextInt(6)]);
                }
            }
        });
    }

    /// <summary>Two colour arcs spread outward from the centre to the ring's ends, like wings opening.</summary>
    private static RawAnimation InfWing(FanEffectContext ctx)
    {
        const int frameCount = 32;
        var half = (ctx.RingLen - 1) / 2.0;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var reach = Slv3WirelessEffectMath.BounceTravel((double)f / frameCount) * half;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var distFromCenter = Math.Abs(ringPos - half);
                    if (distFromCenter <= reach)
                    {
                        SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[0]);
                    }
                }
            }
        });
    }

    /// <summary>A sharp-attack, fast-decay brightness envelope repeats like successive drum hits.</summary>
    private static RawAnimation InfDrumming(FanEffectContext ctx)
    {
        const int frameCount = 24;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var phase = (double)f / frameCount;
            var hit = phase < 0.2 ? 1.0 - phase / 0.2 : 0.0;
            var color = Slv3WirelessEffectMath.Scale(ctx.Colors[0], 0.05 + 0.95 * hit);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    SetRing(buf, f, ctx, fan, ringPos, color);
                }
            }
        });
    }

    /// <summary>A marker travels out to the far end of the ring fast, then eases back slowly, changing colour on the return leg.</summary>
    private static RawAnimation InfBoomerang(FanEffectContext ctx)
    {
        const int frameCount = 40;
        const double outFrac = 0.35;
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 12.0), (buf, f) =>
        {
            var phase = (double)f / frameCount;
            double travel;
            RgbColor color;
            if (phase < outFrac)
            {
                travel = phase / outFrac;
                color = ctx.Colors[0];
            }
            else
            {
                travel = 1.0 - (phase - outFrac) / (1.0 - outFrac);
                color = ctx.Colors[1];
            }
            var center = travel * (ctx.RingLen - 1);
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                PaintRingMarker(buf, f, ctx, fan, center, 2.0, color, 0);
            }
        });
    }

    /// <summary>Fixed-size blocks each pick a fresh random palette colour every few frames.</summary>
    private static RawAnimation InfCandyBox(FanEffectContext ctx)
    {
        const int framesPerPick = 10;
        const int picks = 8;
        const int frameCount = framesPerPick * picks;
        var blockLen = Math.Max(1, ctx.RingLen / 6);
        var blocks = (ctx.RingLen + blockLen - 1) / blockLen;
        var rng = new DeterministicRandom(0xCA4D1E5UL);
        var palette = new int[picks, blocks];
        for (var p = 0; p < picks; p++)
        {
            for (var b = 0; b < blocks; b++)
            {
                palette[p, b] = rng.NextInt(6);
            }
        }
        return RingLoop(frameCount, ctx, Slv3StrimerEffects.ScaledInterval(ctx.Speed, 14.0), (buf, f) =>
        {
            var pick = f / framesPerPick;
            for (var fan = 0; fan < ctx.FanCount; fan++)
            {
                for (var ringPos = 0; ringPos < ctx.RingLen; ringPos++)
                {
                    var block = ringPos / blockLen;
                    SetRing(buf, f, ctx, fan, ringPos, ctx.Colors[palette[pick, block]]);
                }
            }
        });
    }
}
