using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>Broad capability class of a connected LINK device, derived from its (type, model).</summary>
public enum CorsairLinkClass
{
    Other,
    Fan,
    Aio,
    Pump,
    CpuBlock,
    GpuBlock,
    Case,
    Adapter,
}

/// <summary>Static metadata for one connected device model.</summary>
public sealed class CorsairLinkModel
{
    public string Name { get; init; } = "iCUE LINK Device";
    public int LedCount { get; init; }
    public CorsairLinkClass Class { get; init; } = CorsairLinkClass.Other;

    /// <summary>Reports RPM and accepts a duty (fans, AIO/standalone pumps).</summary>
    public bool HasSpeed { get; init; }

    /// <summary>Carries a temperature probe (QX fans, liquid loops, CPU/pump blocks).</summary>
    public bool HasTemperature { get; init; }

    /// <summary>Per-LED u in [0..1], in wire order; null = linear. Shared, never mutated.</summary>
    public float[]? LedU { get; init; }

    /// <summary>Paired with <see cref="LedU"/>.</summary>
    public float[]? LedV { get; init; }
}

/// <summary>
/// Maps the (type, model) bytes the hub returns in its device enumeration to a
/// human name, LED count, and capability class. Table merged from OpenLinkHub
/// (database/external/lsh.json) and OpenRGB (CorsairICueLinkProtocol.h). A nonzero
/// LedCount is the fixed per-model value (QX 34, LX 18, ...); a LedCount of 0 means
/// the count is variable (adapters, strips, Commander Duo) and CorsairLinkHub
/// resolves it at runtime from the hub's getLeds read.
/// </summary>
public static class CorsairLinkModels
{
    // Type-26 devices describe themselves in a per-device record (resource 0x40,
    // addressed by channel): a grid size and each wire LED's position on it.
    // Read off a TITAN II, a 5" LCD Screen Module and an RX360 II RGB (fw 4.1.656);
    // single-LED highlights on camera confirm wire order.
    private static readonly (float[] U, float[] V) TitanIiLayout = HubLayout(66, 66,
        new (int, int)[]
        {
            (60, 44), (54, 54), (44, 60), (33, 60), (22, 60), (12, 54), (6, 44), (6, 33),
            (6, 22), (12, 12), (22, 6), (33, 6), (44, 6), (54, 12), (60, 22), (60, 33),
            (36, 25), (41, 36), (30, 41), (25, 30),
        });

    private static readonly (float[] U, float[] V) LcdScreenModuleLayout = HubLayout(80, 130,
        new (int, int)[]
        {
            (40, 10), (50, 10), (60, 10), (70, 10), (70, 20), (70, 30), (70, 40), (70, 50),
            (70, 60), (70, 70), (70, 80), (70, 90), (70, 100), (70, 110), (70, 120), (58, 120),
            (46, 120), (34, 120), (22, 120), (10, 120), (10, 109), (10, 98), (10, 87), (10, 76),
            (10, 65), (10, 54), (10, 43), (10, 32), (10, 21), (10, 10), (20, 10), (30, 10),
        });

    // Mapped LED by LED on camera on mounted fans seen from the front. Front rim 1-12
    // clockwise from 1:30; back rim 13-24 on the same circle counter-clockwise from
    // 3:30; front hub 25-30 clockwise from 3:30; back hub 31-34 a cross
    // counter-clockwise from 2:30.
    private static readonly (float[] U, float[] V) QxLayout = ClockLayout(
        Ring(0.45f, 1.5f, 1, 12), Ring(0.45f, 3.5f, -1, 12), Ring(0.22f, 3.5f, 2, 6), Ring(0.1f, 2.5f, -3, 4));

    // One device for all three radiator fans: one LED ring per fan, left to right.
    private static readonly (float[] U, float[] V) Rx360IiLayout = HubLayout(360, 120,
        new (int, int)[]
        {
            (81, 60), (74, 75), (60, 81), (45, 74), (39, 60), (46, 45), (60, 39), (75, 46),
            (201, 60), (194, 75), (180, 81), (165, 74), (159, 60), (166, 45), (180, 39), (195, 46),
            (321, 60), (314, 75), (300, 81), (285, 74), (279, 60), (286, 45), (300, 39), (315, 46),
        });

    private static readonly Dictionary<(int type, int model), CorsairLinkModel> Table = new()
    {
        [(1, 0)] = new() { Name = "iCUE LINK QX RGB", LedCount = 34, Class = CorsairLinkClass.Fan, HasSpeed = true, HasTemperature = true, LedU = QxLayout.U, LedV = QxLayout.V },
        [(2, 0)] = new() { Name = "iCUE LINK LX RGB", LedCount = 18, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(3, 0)] = new() { Name = "iCUE LINK RX RGB MAX", LedCount = 8, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(4, 0)] = new() { Name = "iCUE LINK RX MAX", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(5, 0)] = new() { Name = "iCUE LINK Adapter", LedCount = 0, Class = CorsairLinkClass.Adapter },
        [(5, 1)] = new() { Name = "iCUE LINK 9000D Airflow", LedCount = 22, Class = CorsairLinkClass.Case },
        [(5, 2)] = new() { Name = "iCUE LINK 5000T", LedCount = 160, Class = CorsairLinkClass.Case },
        [(6, 0)] = new() { Name = "iCUE LINK Cooler Pump LCD", LedCount = 24, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(7, 0)] = new() { Name = "iCUE LINK H100i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 1)] = new() { Name = "iCUE LINK H115i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 2)] = new() { Name = "iCUE LINK H150i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 3)] = new() { Name = "iCUE LINK H170i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(9, 0)] = new() { Name = "iCUE LINK XC7 Elite", LedCount = 24, Class = CorsairLinkClass.CpuBlock, HasTemperature = true },
        [(9, 1)] = new() { Name = "iCUE LINK XC7 Elite", LedCount = 24, Class = CorsairLinkClass.CpuBlock, HasTemperature = true },
        [(10, 0)] = new() { Name = "iCUE LINK XG3 Hybrid", LedCount = 22, Class = CorsairLinkClass.GpuBlock, HasSpeed = true },
        [(12, 0)] = new() { Name = "iCUE LINK XD5 Elite", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(13, 0)] = new() { Name = "iCUE LINK XG7 RGB", LedCount = 16, Class = CorsairLinkClass.GpuBlock },
        [(14, 0)] = new() { Name = "iCUE LINK XD5 Elite LCD", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(15, 0)] = new() { Name = "iCUE LINK RX RGB", LedCount = 8, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(16, 0)] = new() { Name = "VRM Cooler Module", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(17, 0)] = new() { Name = "iCUE LINK Titan 240", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 1)] = new() { Name = "iCUE LINK Titan 280", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 2)] = new() { Name = "iCUE LINK Titan 360", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 3)] = new() { Name = "iCUE LINK Titan 420", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 4)] = new() { Name = "iCUE LINK Titan 240", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 5)] = new() { Name = "iCUE LINK Titan 360", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(19, 0)] = new() { Name = "iCUE LINK RX", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(25, 0)] = new() { Name = "iCUE LINK XD6 Elite", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(26, 0)] = new() { Name = "iCUE LINK RX360 II RGB", LedCount = 24, Class = CorsairLinkClass.Fan, HasSpeed = true, HasTemperature = true, LedU = Rx360IiLayout.U, LedV = Rx360IiLayout.V },
        [(26, 1)] = new() { Name = "iCUE LINK TITAN II", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true, LedU = TitanIiLayout.U, LedV = TitanIiLayout.V },
        [(26, 8)] = new() { Name = "iCUE LINK 5\" LCD Screen Module", LedCount = 32, Class = CorsairLinkClass.Fan, HasSpeed = true, HasTemperature = true, LedU = LcdScreenModuleLayout.U, LedV = LcdScreenModuleLayout.V },
        [(27, 0)] = new() { Name = "iCUE Commander Duo", LedCount = 0, Class = CorsairLinkClass.Adapter, HasSpeed = true, HasTemperature = true },
    };

    private static (float[] U, float[] V) HubLayout(int width, int height, (int X, int Y)[] positions)
    {
        var u = new float[positions.Length];
        var v = new float[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            u[i] = (float)positions[i].X / width;
            v[i] = (float)positions[i].Y / height;
        }
        return (u, v);
    }

    // count LEDs on a circle of radius r (canvas units, centre 0.5), the first at
    // clock hour `start`, each next `step` hours on (negative = counter-clockwise).
    private static (float R, float Start, float Step, int Count) Ring(float r, float start, float step, int count) =>
        (r, start, step, count);

    private static (float[] U, float[] V) ClockLayout(params (float R, float Start, float Step, int Count)[] rings)
    {
        var u = new List<float>();
        var v = new List<float>();
        foreach (var (r, start, step, count) in rings)
        {
            for (var i = 0; i < count; i++)
            {
                var a = (start + step * i) * MathF.PI / 6f;
                u.Add(0.5f + r * MathF.Sin(a));
                v.Add(0.5f - r * MathF.Cos(a));
            }
        }
        return (u.ToArray(), v.ToArray());
    }

    public static CorsairLinkModel Lookup(int type, int model)
    {
        if (Table.TryGetValue((type, model), out var m)) return m;
        // Unknown model of a known type: fall back to model 0 so a new revision
        // still surfaces with the right name/class rather than as fully unknown.
        // Type 26 is a family of unrelated products, so its model 0 is no fallback.
        if (type != 26 && Table.TryGetValue((type, 0), out var baseM)) return baseM;
        return new CorsairLinkModel { Name = $"iCUE LINK Device {type}.{model}", Class = CorsairLinkClass.Other };
    }
}
