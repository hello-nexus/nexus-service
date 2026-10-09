using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

public static class LedUvComputer
{
    public static (float[] ledU, float[] ledV) ComputeDefaults(RgbDevice device)
    {
        if (device.LedCount <= 0)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }
        if (UniversalScreenRing.Matches(device))
        {
            return UniversalScreenRing.Positions();
        }

        var ledU = new float[device.LedCount];
        var ledV = new float[device.LedCount];
        var populated = new bool[device.LedCount];

        var hasMatrix = false;
        var hasLinear = false;
        foreach (var z in device.Zones)
        {
            if (z.MatrixMap is not null && z.MatrixWidth > 0 && z.MatrixHeight > 0)
            { hasMatrix = true; }
            if (z.ZoneType == 1)
            { hasLinear = true; }
        }

        if (!hasMatrix)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }

        int zoneOffset = 0;
        foreach (var z in device.Zones)
        {
            if (z.LedCount <= 0)
            {
                continue;
            }
            if (z.MatrixMap is not null && z.MatrixWidth > 0 && z.MatrixHeight > 0)
            {
                var mw = z.MatrixWidth;
                var mh = z.MatrixHeight;
                var map = z.MatrixMap;
                for (int row = 0; row < mh; row++)
                {
                    for (int col = 0; col < mw; col++)
                    {
                        var localIdx = map[row * mw + col];
                        if (localIdx < 0 || localIdx >= z.LedCount)
                        {
                            continue;
                        }
                        var global = zoneOffset + localIdx;
                        if (global < 0 || global >= device.LedCount)
                        {
                            continue;
                        }
                        ledU[global] = mw > 1 ? col / (float)(mw - 1) : 0.5f;
                        ledV[global] = mh > 1 ? row / (float)(mh - 1) : 0.5f;
                        populated[global] = true;
                    }
                }
            }
            zoneOffset += z.LedCount;
        }

        if (hasLinear && hasMatrix)
        {
            DistributePerimeter(ledU, ledV, populated, device);
        }
        else
        {
            DistributeLinearFallback(ledU, ledV, populated, device.LedCount);
        }

        return (ledU, ledV);
    }

    /// <summary>
    /// Evenly distribute positions along the unit-square perimeter, walking
    /// clockwise from top-left: top edge, right edge, bottom edge, left edge.
    /// Open mode (default) walks endpoint-inclusive, so the last position of
    /// a span closes back onto the start corner; closed-loop mode steps by
    /// perimeter over count, keeping every position distinct - use it for
    /// physical rings whose last LED sits next to the first. Spans of one
    /// land on the origin in both modes.
    /// </summary>
    public static void FillPerimeter(Span<float> u, Span<float> v, bool closedLoop = false)
    {
        var n = Math.Min(u.Length, v.Length);
        for (int i = 0; i < n; i++)
        {
            // Perimeter length is 4 in the unit square.
            var t = closedLoop
                ? i * 4f / n
                : (n > 1 ? i / (float)(n - 1) * 4f : 0f);
            if (t <= 1f)
            {
                // Top edge: (0,0) -> (1,0)
                u[i] = t;
                v[i] = 0f;
            }
            else if (t <= 2f)
            {
                // Right edge: (1,0) -> (1,1)
                u[i] = 1f;
                v[i] = t - 1f;
            }
            else if (t <= 3f)
            {
                // Bottom edge: (1,1) -> (0,1)
                u[i] = 1f - (t - 2f);
                v[i] = 1f;
            }
            else
            {
                // Left edge: (0,1) -> (0,0)
                u[i] = 0f;
                v[i] = 1f - (t - 3f);
            }
        }
    }

    private static void DistributePerimeter(float[] ledU, float[] ledV, bool[] populated, RgbDevice device)
    {
        var unpopulated = new List<int>();
        for (int i = 0; i < device.LedCount; i++)
        {
            if (!populated[i])
            {
                unpopulated.Add(i);
            }
        }

        if (unpopulated.Count == 0)
        {
            return;
        }

        var n = unpopulated.Count;
        var walkU = new float[n];
        var walkV = new float[n];
        FillPerimeter(walkU, walkV);
        for (int i = 0; i < n; i++)
        {
            var idx = unpopulated[i];
            ledU[idx] = walkU[i];
            ledV[idx] = walkV[i];
        }
    }

    private static void DistributeLinearFallback(float[] ledU, float[] ledV, bool[] populated, int ledCount)
    {
        var unpopCount = 0;
        for (int i = 0; i < ledCount; i++)
        {
            if (!populated[i])
            {
                unpopCount++;
            }
        }
        if (unpopCount == 0)
        {
            return;
        }

        var idx = 0;
        for (int i = 0; i < ledCount; i++)
        {
            if (populated[i])
            {
                continue;
            }
            ledU[i] = unpopCount > 1 ? idx / (float)(unpopCount - 1) : 0.5f;
            ledV[i] = 0.5f;
            idx++;
        }
    }
}
