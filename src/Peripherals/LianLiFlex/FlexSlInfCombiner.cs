using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One SL-INF zone render: planar [frame, channel, led] data and its interval.</summary>
internal sealed class FlexSlInfZone
{
    public required byte[,,] Frames { get; init; }
    public required int FrameCount { get; init; }
    public required double IntervalBase { get; init; }
    public double Interval { get; init; }
    public int SpeedMultiplier { get; init; } = 4;
}

/// <summary>Merges the inner, outer and center zone renders of a 44-LED fan into one animation.</summary>
internal static class FlexSlInfCombiner
{
    public const int LedsPerFan = 44;

    public static (byte[,,] Frames, int FrameCount, double Interval) Combine(
        FlexSlInfZone inner, FlexSlInfZone outer, FlexSlInfZone center, int fans)
    {
        var ledCount = fans * LedsPerFan;
        var baseMs = center.IntervalBase > 0.0 ? center.IntervalBase : Math.Max(outer.IntervalBase, inner.IntervalBase);
        var interval = inner.SpeedMultiplier * baseMs;
        var centerIntervalMs = center.Interval;

        var innerFrames = inner.Frames;
        var outerFrames = outer.Frames;
        var centerFrames = center.Frames;
        int innerCount = inner.FrameCount, outerCount = outer.FrameCount, centerCount = center.FrameCount;

        byte[,,] target;
        int total;
        double result;
        if (innerCount >= outerCount && innerCount >= centerCount)
        {
            total = innerCount;
            if (total / 2 >= outerCount)
            {
                (outerCount, outerFrames) = TileFrames(outerFrames, total, outerCount, ledCount);
            }
            if (total / 2 >= centerCount)
            {
                (centerCount, centerFrames) = TileFrames(centerFrames, total, centerCount, ledCount);
            }
            Merge(innerFrames, total, interval, ledCount, (outerFrames, outerCount, 18, 8), (outerFrames, outerCount, 36, 8), (centerFrames, centerCount, 0, 8));
            target = innerFrames;
            result = centerIntervalMs != 0.0 ? centerIntervalMs * centerCount / total : interval;
        }
        else if (outerCount >= innerCount && outerCount >= centerCount)
        {
            total = outerCount;
            if (total / 2 >= innerCount)
            {
                (innerCount, innerFrames) = TileFrames(innerFrames, total, innerCount, ledCount);
            }
            if (total / 2 >= centerCount)
            {
                (centerCount, centerFrames) = TileFrames(centerFrames, total, centerCount, ledCount);
            }
            Merge(outerFrames, total, interval, ledCount, (innerFrames, innerCount, 8, 10), (innerFrames, innerCount, 26, 10), (centerFrames, centerCount, 0, 8));
            target = outerFrames;
            result = centerIntervalMs != 0.0 ? Math.Ceiling(centerIntervalMs * centerCount / total) : interval;
        }
        else
        {
            total = centerCount;
            if (total / 2 >= innerCount)
            {
                (innerCount, innerFrames) = TileFrames(innerFrames, total, innerCount, ledCount);
            }
            if (total / 2 >= outerCount)
            {
                (outerCount, outerFrames) = TileFrames(outerFrames, total, outerCount, ledCount);
            }
            Merge(centerFrames, total, interval, ledCount, (outerFrames, outerCount, 18, 8), (outerFrames, outerCount, 36, 8), (innerFrames, innerCount, 8, 10), (innerFrames, innerCount, 26, 10));
            target = centerFrames;
            result = interval;
        }
        return (target, total, result);
    }

    private static void Merge(
        byte[,,] target, int total, double interval, int ledCount,
        params (byte[,,] Src, int Count, int Start, int Len)[] parts)
    {
        var span = (int)interval * total;
        for (var i = 0; i < total; i++)
        {
            foreach (var (src, count, start, len) in parts)
            {
                var step = span / count;
                var idx = (int)(i * interval / step);
                if (idx >= count)
                {
                    idx = count - 1;
                }
                for (var j = 0; j < ledCount; j += LedsPerFan)
                {
                    for (var k = j + start; k < j + start + len; k++)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            target[i, c, k] = src[idx, c, k];
                        }
                    }
                }
            }
        }
    }

    private static (int, byte[,,]) TileFrames(byte[,,] shortFrames, int totalFrames, int shortCount, int ledCount)
    {
        var repeats = totalFrames / shortCount;
        var tiled = new byte[totalFrames, 3, ledCount];
        var block = 3 * ledCount * shortCount;
        for (var i = 0; i < repeats; i++)
        {
            Array.Copy(shortFrames, 0, tiled, i * block, block);
        }
        return (repeats * shortCount, tiled);
    }
}
