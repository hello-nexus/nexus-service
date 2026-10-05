using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// Catalog rows redrawn with the layout our wired Uni Hub draws for the same
/// fan, which was checked on hardware: the SL-Infinity whole fan (inner ring
/// then edge, the order its hub plays a motherboard ARGB input in) and its two
/// halves. The generated geometry for these rows does not match the hardware.
/// Names, counts and match data stay the catalog's; only positions change.
/// </summary>
public static class LianLiChainArtifacts
{
    public const string SlInfinityKey = "product:lianli-lian-li-sl120-infinity";

    private enum Part { WholeFan, Inner, Edge }

    private static readonly Dictionary<string, Part> Rows = new(StringComparer.Ordinal)
    {
        [SlInfinityKey] = Part.WholeFan,
        ["product:lianli-lian-li-sl140-infinity"] = Part.WholeFan,
        ["product:lianli-sl120-infinity-inner"] = Part.Inner,
        ["product:lianli-sl140-infinity-inner"] = Part.Inner,
        ["product:lianli-sl120-infinity-outter"] = Part.Edge,
        ["product:lianli-sl140-infinity-outter"] = Part.Edge,
    };

    /// <summary>Redraws a catalog row in place when it is one of these products and carries the hub's LED count; false leaves it untouched.</summary>
    public static bool Apply(string key, MappingArtifact artifact)
    {
        if (!Rows.TryGetValue(key, out var part) || artifact.Zones.Count != 1)
            return false;

        var (u, v) = LianLiZoneSupport.SlInfinityFanUV();
        var inner = LianLiProtocol.InnerLedsPerFan;
        var (start, count) = part switch
        {
            Part.Inner => (0, inner),
            Part.Edge => (inner, u.Length - inner),
            _ => (0, u.Length),
        };
        var zone = artifact.Zones[0];
        if (zone.LedCount != count)
            return false;

        zone.Leds = new List<MappingLed>(count);
        for (var i = 0; i < count; i++)
            zone.Leds.Add(new MappingLed { I = i, U = Round(u[start + i]), V = Round(v[start + i]) });
        // The hub draws each fan in a square card.
        zone.AspectRatio = 1f;
        return true;
    }

    // 5 decimals matches the packed catalog.
    private static float Round(float value) => MathF.Round(value, 5);
}
