using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>One SL-INF zone render: planar [frame, channel, led] data and its interval.</summary>
internal sealed class FlexSlInfZone
{
    public required byte[,,] Frames { get; init; }
    public required int FrameCount { get; init; }
    public required double IntervalBase { get; init; }
    public double Interval { get; init; }
}

/// <summary>Merges the inner, outer and center zone renders of a 44-LED fan into one animation.</summary>
internal static class FlexSlInfCombiner
{
    public const int LedsPerFan = 44;

    public static (byte[,,] Frames, int FrameCount, double Interval) Combine(
        FlexSlInfZone inner, FlexSlInfZone outer, FlexSlInfZone center, int fans, int speedMultiplier)
    {
        var ledCount = fans * LedsPerFan;
        var baseMs = center.IntervalBase > 0.0 ? center.IntervalBase : Math.Max(outer.IntervalBase, inner.IntervalBase);
        var interval = speedMultiplier * baseMs;
        var centerInterval = speedMultiplier * center.IntervalBase;

        var inA = inner.Frames;
        var outA = outer.Frames;
        var ceA = center.Frames;
        int nIn = inner.FrameCount, nOut = outer.FrameCount, nCe = center.FrameCount;

        byte[,,] target;
        int total;
        double result;
        if (nIn >= nOut && nIn >= nCe)
        {
            total = nIn;
            if (total / 2 >= nOut)
            {
                (nOut, outA) = FillArr(outA, total, nOut, ledCount);
            }
            if (total / 2 >= nCe)
            {
                (nCe, ceA) = FillArr(ceA, total, nCe, ledCount);
            }
            Merge(inA, total, interval, ledCount, (outA, nOut, 18, 8), (outA, nOut, 36, 8), (ceA, nCe, 0, 8));
            target = inA;
            result = centerInterval != 0.0 ? centerInterval * nCe / total : interval;
        }
        else if (nOut >= nIn && nOut >= nCe)
        {
            total = nOut;
            if (total / 2 >= nIn)
            {
                (nIn, inA) = FillArr(inA, total, nIn, ledCount);
            }
            if (total / 2 >= nCe)
            {
                (nCe, ceA) = FillArr(ceA, total, nCe, ledCount);
            }
            Merge(outA, total, interval, ledCount, (inA, nIn, 8, 10), (inA, nIn, 26, 10), (ceA, nCe, 0, 8));
            target = outA;
            result = centerInterval != 0.0 ? Math.Ceiling(centerInterval * nCe / total) : interval;
        }
        else
        {
            total = nCe;
            if (total / 2 >= nIn)
            {
                (nIn, inA) = FillArr(inA, total, nIn, ledCount);
            }
            if (total / 2 >= nOut)
            {
                (nOut, outA) = FillArr(outA, total, nOut, ledCount);
            }
            Merge(ceA, total, interval, ledCount, (outA, nOut, 18, 8), (outA, nOut, 36, 8), (inA, nIn, 8, 10), (inA, nIn, 26, 10));
            target = ceA;
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

    private static (int, byte[,,]) FillArr(byte[,,] minData, int maxFrames, int minFrames, int ledCount)
    {
        var times = maxFrames / minFrames;
        var arr = new byte[maxFrames, 3, ledCount];
        var block = 3 * ledCount * minFrames;
        for (var i = 0; i < times; i++)
        {
            Array.Copy(minData, 0, arr, i * block, block);
        }
        return (times * minFrames, arr);
    }
}
