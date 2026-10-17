using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting.Bench;

/// <summary>A fixed synthetic device set, so every backend samples identical geometry: a key matrix
/// with per-LED UVs, rotated fan rings, a long strip, and small peripherals.</summary>
internal static class BenchDevices
{
    public static DeviceFrame[] Create()
    {
        var list = new System.Collections.Generic.List<DeviceFrame>();
        var index = 0;

        const int cols = 15;
        const int rows = 6;
        var keys = new DeviceFrame(index++, "bench-keyboard", cols * rows - 3, 120, 380, 520, 180)
        {
            Archetype = "keyboard",
        };
        var u = new float[keys.LedCount];
        var v = new float[keys.LedCount];
        for (var i = 0; i < keys.LedCount; i++)
        {
            u[i] = (i % cols + 0.5f) / cols;
            v[i] = (i / cols + 0.5f) / rows;
        }
        keys.LedU = u;
        keys.LedV = v;
        list.Add(keys);

        for (var f = 0; f < 3; f++)
        {
            list.Add(new DeviceFrame(index++, $"bench-fan-{f}", 16, 700 + f * 90, 120 + f * 40, 80, 80, rotation: f * 45));
        }

        list.Add(new DeviceFrame(index++, "bench-strip", 144, 100, 40, 800, 20));
        list.Add(new DeviceFrame(index++, "bench-ram-a", 8, 760, 400, 24, 140));
        list.Add(new DeviceFrame(index++, "bench-ram-b", 8, 800, 400, 24, 140));
        list.Add(new DeviceFrame(index++, "bench-mouse", 12, 680, 480, 50, 80, rotation: 90));
        list.Add(new DeviceFrame(index++, "bench-pad", 20, 60, 250, 300, 40));
        return list.ToArray();
    }

    public static int TotalLeds()
    {
        var total = 0;
        foreach (var d in Create())
        {
            total += d.LedCount;
        }
        return total;
    }
}
