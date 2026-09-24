using System;
using System.Globalization;

namespace Nexus.Service.Migration;

/// <summary>Value-mapping tables shared by the Y70 and Q60 translators: colour,
/// gauge design, semantic sensor id, clock/weather/gallery enums, and the
/// background shader table.</summary>
internal static class Nexus2WidgetMapping
{
    /// <summary>Nexus 2 theme colours are "r, g, b" decimal triplets (no '#',
    /// no rgb() wrapper). Returns null when the string does not parse.</summary>
    public static string? RgbTripletToHex(string? triplet)
    {
        if (string.IsNullOrWhiteSpace(triplet))
        {
            return null;
        }
        var parts = triplet.Split(',');
        if (parts.Length != 3)
        {
            return null;
        }
        Span<byte> rgb = stackalloc byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 255)
            {
                return null;
            }
            rgb[i] = (byte)v;
        }
        return $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}";
    }

    /// <summary>Y70 clock design 'flip'|'analog'|'digital' -> the Nexus 3 clock
    /// widget's design key. Unrecognized values fall to the widget default.</summary>
    public static string? MapClockDesign(string? n2Design) => n2Design switch
    {
        "flip" => "splitflap",
        "analog" => "analog",
        "digital" => "digital",
        _ => null,
    };

    /// <summary>Q60 clock design 'analog'|'default'|'gradient' -> the same
    /// clock widget vocabulary as Y70 ('gradient' has no match).</summary>
    public static string? MapQ60ClockDesign(string? n2Design) => n2Design switch
    {
        "analog" => "analog",
        "default" => "digital",
        _ => null,
    };

    /// <summary>N2 TimeFormat '12'|'24' -> the clock widget's format key.</summary>
    public static string? MapTimeFormat(string? n2TimeFormat) => n2TimeFormat switch
    {
        "12" => "12h",
        "24" => "24h",
        _ => null,
    };

    /// <summary>N2 WeatherUnits 'c'|'f' -> the weather widget's unit key.</summary>
    public static string? MapWeatherUnit(string? n2Units) => n2Units switch
    {
        "c" => "C",
        "f" => "F",
        _ => null,
    };

    /// <summary>Gauge designs shared 1:1 between Y70's Y70SystemStatDesign and
    /// Q60's Q60PerformanceDesign vocabularies; every other design (CatDog,
    /// Radiate, LittleGuy, ToeNail, HalfDog, glow, graph, VoidLevel, None)
    /// falls to the widget's own default.</summary>
    public static string? MapGaugeDesign(string? n2Design) => n2Design switch
    {
        "WaterLevel" => "waterLevel",
        "Catapillar" => "caterpillar",
        "text" => "text",
        _ => null,
    };

    /// <summary>The monitoring widget's simple (non-Micro) slot counts.</summary>
    public static int ClampSlotCount(int available)
    {
        if (available >= 4)
        {
            return 4;
        }
        return available >= 2 ? 2 : 1;
    }

    /// <summary>Y70 performance slots carry a typed (device, sensor.type, sensor.name);
    /// resolves to Nexus 3's stable summary/* semantic id, or null when no
    /// summary sensor of that shape exists (the widget then keeps its own default).</summary>
    public static string? ResolveSummarySensorId(string? device, string? sensorType, string? sensorName)
    {
        var name = sensorName ?? "";
        if (device == "gpu" && name.Contains("memory", StringComparison.OrdinalIgnoreCase))
        {
            return "summary/vram-usage";
        }
        return (device, sensorType) switch
        {
            ("cpu", "Load") => "summary/cpu-usage",
            ("cpu", "Temperature") => "summary/cpu-temp",
            ("gpu", "Load") => "summary/gpu-usage",
            ("gpu", "Temperature") => "summary/gpu-temp",
            ("memory", "Load") => "summary/memory-usage",
            _ => null,
        };
    }

    /// <summary>Q60 performance slots carry only a raw hardware sensorId string
    /// (no type field); device plus a temp/memory substring hint approximate
    /// the same summary/* kind. The q60 pump device has no summary equivalent.</summary>
    public static string? ResolveSummarySensorIdFromRawId(string? device, string? sensorId)
    {
        var id = (sensorId ?? "").ToLowerInvariant();
        return device switch
        {
            "cpu" => id.Contains("temp") ? "summary/cpu-temp" : "summary/cpu-usage",
            "gpu" => id.Contains("mem") || id.Contains("vram") ? "summary/vram-usage"
                : id.Contains("temp") ? "summary/gpu-temp" : "summary/gpu-usage",
            "memory" => "summary/memory-usage",
            _ => null,
        };
    }

    /// <summary>N2 named Y70 backgrounds -> the closest Nexus 3 shader effect key.
    /// Unmapped names fall to the panel background default.</summary>
    public static string MapBackgroundEffect(string? bgName) => bgName switch
    {
        "hue" => "huewheel",
        "cyber" => "matrix",
        "vortex" => "spiral",
        "wires" => "interference",
        "oil" => "watercolor",
        "earth" => "nebula",
        "particles" => "starfield",
        _ => "plasma",
    };

    private static readonly (string N2, int Ms)[] GalleryIntervalMs =
    {
        ("Every 2 seconds", 2_000),
        ("Every 5 seconds", 5_000),
        ("Every 15 seconds", 15_000),
        ("Every 30 seconds", 30_000),
        ("Every minute", 60_000),
        ("Every 5 minutes", 300_000),
        ("Every 10 minutes", 600_000),
        ("Every 15 minutes", 900_000),
        ("Every 30 minutes", 1_800_000),
        ("Every hour", 3_600_000),
        ("Every day", 86_400_000),
    };

    private static readonly int[] AllowedGalleryIntervalSeconds = { 5, 10, 15, 30, 60 };

    /// <summary>N2's GalleryInterval string enum -> seconds, snapped to the
    /// nearest of the gallery widget's allowed interval steps.</summary>
    public static int MapGalleryIntervalSeconds(string? n2Interval)
    {
        var ms = Array.Find(GalleryIntervalMs, e => e.N2 == n2Interval).Ms;
        if (ms == 0)
        {
            return AllowedGalleryIntervalSeconds[0];
        }
        var seconds = ms / 1000.0;
        var best = AllowedGalleryIntervalSeconds[0];
        var bestDiff = double.MaxValue;
        foreach (var candidate in AllowedGalleryIntervalSeconds)
        {
            var diff = Math.Abs(candidate - seconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = candidate;
            }
        }
        return best;
    }
}
