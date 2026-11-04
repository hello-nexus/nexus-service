using System;
using System.Collections.Generic;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// One zone's look. Speed and Brightness are the 0..4 levels of the settings.
/// </summary>
public sealed record FlexZoneLook(string Key, int Speed, int Direction, int Brightness, IReadOnlyList<RgbColor> Colors);

/// <summary>Final animation bytes (frames * leds * 3, R,G,B) and the frame interval the unit plays them at.</summary>
public sealed class FlexComposedAnimation
{
    public required byte[] Raw { get; init; }
    public required int Frames { get; init; }
    public required int Leds { get; init; }
    public required double IntervalMs { get; init; }
}

/// <summary>
/// Renders each zone look with the family generators and merges the zone animations into one,
/// reconciling frame counts and the interval the way the unit's own software does.
/// </summary>
public static class FlexAnimationComposer
{
    private static readonly int[] BrightnessLevels = { 0, 64, 128, 192, 255 };

    public static IReadOnlyList<string> Keys(FlexFamily family) => family switch
    {
        FlexFamily.SlInfFlex => FlexSlInfEffects.Keys,
        FlexFamily.P28V2 => Array.Empty<string>(),
        _ => FlexTlEffects.Keys,
    };

    /// <summary>Speed index 0..4 to the interval multiplier; Static ignores the speed slider and runs at the slowest.</summary>
    public static int SpeedMultiplier(string key, int speed) =>
        string.Equals(key, "static", StringComparison.Ordinal) ? 7 : 7 - Math.Clamp(speed, 0, 4);

    public static int BrightnessLevel(int brightness) => BrightnessLevels[Math.Clamp(brightness, 0, 4)];

    /// <param name="infAttachFlag">SL-INF generator flag: 1 for a unit whose identity reports a fan count under 10, 0 for a right-attached cable.</param>
    public static FlexComposedAnimation Compose(
        FlexFamily family, int fans, IReadOnlyDictionary<string, FlexZoneLook> zones, bool upDownSwap, int infAttachFlag = 1)
    {
        if (family == FlexFamily.P28V2)
        {
            throw new NotSupportedException("P28 V2 has no stored effects");
        }
        fans = fans is < 1 or > 4 ? 4 : fans;
        return family == FlexFamily.SlInfFlex ? ComposeSlInf(fans, zones, infAttachFlag) : ComposeTl(fans, zones, upDownSwap);
    }

    private static FlexEffectRequest Request(FlexScope scope, int fans, FlexZoneLook look, int attachFlag = 1) =>
        new(scope, fans, look.Direction, BrightnessLevel(look.Brightness), look.Colors, SpeedMultiplier(look.Key, look.Speed), attachFlag);

    private static FlexComposedAnimation ComposeSlInf(int fans, IReadOnlyDictionary<string, FlexZoneLook> zones, int attachFlag)
    {
        var leds = fans * FlexSlInfEffects.LedsPerFan;
        if (zones.TryGetValue("all", out var all))
        {
            var anim = FlexSlInfEffects.Render(all.Key, Request(FlexScope.All, fans, all, attachFlag));
            return new FlexComposedAnimation
            {
                Raw = anim.Frames,
                Frames = anim.FrameCount,
                Leds = leds,
                IntervalMs = anim.IntervalBaseMs * SpeedMultiplier(all.Key, all.Speed),
            };
        }
        var inner = FlexSlInfEffects.RenderZone(zones["inner"].Key, 1, Request(FlexScope.Inner, fans, zones["inner"], attachFlag));
        var outer = FlexSlInfEffects.RenderZone(zones["outer"].Key, 0, Request(FlexScope.Outer, fans, zones["outer"], attachFlag));
        var center = FlexSlInfEffects.RenderZone(zones["center"].Key, 2, Request(FlexScope.Center, fans, zones["center"], attachFlag));
        var (frames, count, interval) = FlexSlInfCombiner.Combine(inner, outer, center, fans);
        return new FlexComposedAnimation
        {
            Raw = FlexPalette.Flatten(frames, count, leds),
            Frames = count,
            Leds = leds,
            IntervalMs = interval,
        };
    }

    private static FlexRawAnimation RenderTl(FlexZoneLook look, FlexScope scope, int fans)
    {
        var req = Request(scope, fans, look);
        return FlexTlEffects.IsImplemented(look.Key)
            ? FlexTlEffects.Render(look.Key, req)
            : FlexTlExtendedEffects.Render(look.Key, req, FlexTlExtendedEffects.SeedFor(look.Key));
    }

    private static FlexComposedAnimation ComposeTl(int fans, IReadOnlyDictionary<string, FlexZoneLook> zones, bool flip)
    {
        var leds = fans * FlexTlEffects.LedsPerFan;
        if (zones.TryGetValue("all", out var all))
        {
            var anim = RenderTl(all, FlexScope.All, fans);
            var raw = flip ? SwapHalves(anim.Frames, anim.FrameCount, leds) : anim.Frames;
            return new FlexComposedAnimation
            {
                Raw = raw,
                Frames = anim.FrameCount,
                Leds = leds,
                IntervalMs = anim.IntervalBaseMs * SpeedMultiplier(all.Key, all.Speed),
            };
        }
        var upLook = zones["up"];
        var downLook = zones["down"];
        var up = RenderTl(upLook, FlexScope.Up, fans);
        var down = RenderTl(downLook, FlexScope.Down, fans);
        var upInterval = up.IntervalBaseMs * SpeedMultiplier(upLook.Key, upLook.Speed);
        var downInterval = down.IntervalBaseMs * SpeedMultiplier(downLook.Key, downLook.Speed);
        var interval = Math.Max(upInterval, downInterval);

        var upData = flip ? SwapHalves(up.Frames, up.FrameCount, leds) : up.Frames;
        var downData = flip ? SwapHalves(down.Frames, down.FrameCount, leds) : down.Frames;
        byte[] highSlot, lowSlot;
        int highFrames, lowFrames;
        if (flip)
        {
            highSlot = upData;
            highFrames = up.FrameCount;
            lowSlot = downData;
            lowFrames = down.FrameCount;
        }
        else
        {
            highSlot = downData;
            highFrames = down.FrameCount;
            lowSlot = upData;
            lowFrames = up.FrameCount;
        }

        byte[] result;
        int total;
        if (highFrames > lowFrames)
        {
            total = highFrames;
            var shorter = lowFrames;
            if (total / 2 >= shorter)
            {
                (shorter, lowSlot) = FillArr(lowSlot, total, shorter, leds);
            }
            result = highSlot;
            CopyHalf(result, lowSlot, total, shorter, leds, interval, 0);
        }
        else
        {
            total = lowFrames;
            var shorter = highFrames;
            if (total / 2 >= shorter)
            {
                (shorter, highSlot) = FillArr(highSlot, total, shorter, leds);
            }
            result = lowSlot;
            CopyHalf(result, highSlot, total, shorter, leds, interval, FlexTlEffects.HalfLeds);
        }
        return new FlexComposedAnimation { Raw = result, Frames = total, Leds = leds, IntervalMs = interval };
    }

    /// <summary>Copies one 13-LED half of every fan from the resampled source animation into the base.</summary>
    private static void CopyHalf(byte[] target, byte[] source, int total, int sourceFrames, int leds, double interval, int halfStart)
    {
        var step = (int)interval * total / sourceFrames;
        for (var i = 0; i < total; i++)
        {
            var idx = (int)(i * interval / step);
            if (idx >= sourceFrames)
            {
                idx = sourceFrames - 1;
            }
            for (var j = 0; j < leds; j += FlexTlEffects.LedsPerFan)
            {
                var o = (i * leds + j + halfStart) * 3;
                var s = (idx * leds + j + halfStart) * 3;
                Buffer.BlockCopy(source, s, target, o, FlexTlEffects.HalfLeds * 3);
            }
        }
    }

    private static (int, byte[]) FillArr(byte[] data, int maxFrames, int minFrames, int leds)
    {
        var times = maxFrames / minFrames;
        var block = minFrames * leds * 3;
        var arr = new byte[maxFrames * leds * 3];
        for (var i = 0; i < times; i++)
        {
            Buffer.BlockCopy(data, 0, arr, i * block, block);
        }
        return (times * minFrames, arr);
    }

    private static byte[] SwapHalves(byte[] raw, int frames, int leds)
    {
        var half = FlexTlEffects.HalfLeds * 3;
        var result = new byte[raw.Length];
        for (var f = 0; f < frames; f++)
        {
            for (var j = 0; j < leds; j += FlexTlEffects.LedsPerFan)
            {
                var o = (f * leds + j) * 3;
                Buffer.BlockCopy(raw, o, result, o + half, half);
                Buffer.BlockCopy(raw, o + half, result, o, half);
            }
        }
        return result;
    }
}
