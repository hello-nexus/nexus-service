using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// The two chain links whose geometry is a function of their LED count rather
/// than a catalogued product: a generic fan (a ring) and a generic strip (a
/// line). A user with an unrecognised fan picks "Generic Fan", types 12, and
/// gets a twelve-point ring; type 24 and the ring simply gets denser.
///
/// Generated rather than catalogued because the discrete alternative was 60-odd
/// near-identical rows the user had to scroll past to reach a real product.
/// </summary>
public static class GenericChainArtifacts
{
    public const string FanKey = "generic:fan";
    public const string StripKey = "generic:strip";

    /// <summary>Per-zone LED cap the artifact schema enforces, shared with the chain POST's own zone and total ceilings.</summary>
    public const int MaxArtifactLedCount = 4096;

    /// <summary>Ring radius in UV space, leaving a margin inside the fan frame.</summary>
    private const float RingRadius = 0.42f;

    public static bool IsGeneric(string? key) => key is FanKey or StripKey;

    public static string NameFor(string key) => key == FanKey ? "Generic Fan" : "Generic Strip";

    /// <summary>Null when the key is not generic or the count is out of range.</summary>
    public static MappingArtifact? Build(string key, int ledCount)
    {
        if (!IsGeneric(key) || ledCount <= 0 || ledCount > MaxArtifactLedCount)
            return null;

        var isFan = key == FanKey;
        var leds = new List<MappingLed>(ledCount);
        for (int i = 0; i < ledCount; i++)
        {
            float u, v;
            if (isFan)
            {
                // From 12 o'clock, clockwise, matching how a fan's wire runs.
                var angle = -MathF.PI / 2 + 2 * MathF.PI * i / ledCount;
                u = 0.5f + RingRadius * MathF.Cos(angle);
                v = 0.5f + RingRadius * MathF.Sin(angle);
            }
            else
            {
                // Cell-centred, so a one-LED strip sits mid-frame rather than
                // on the left edge.
                u = (i + 0.5f) / ledCount;
                v = 0.5f;
            }
            leds.Add(new MappingLed { I = i, U = Round(u), V = Round(v) });
        }

        return new MappingArtifact
        {
            SchemaVersion = MappingSchema.Version,
            Name = NameFor(key),
            Description = isFan
                ? $"Ring of {ledCount} LEDs, generated for an unlisted fan."
                : $"Line of {ledCount} LEDs, generated for an unlisted strip.",
            Device = new MappingDeviceInfo
            {
                Key = key,
                Match = new MappingDeviceMatch
                {
                    NameHint = NameFor(key),
                    VendorHint = "Generic",
                    // A strip is legitimately colinear, so it must declare
                    // itself linear or the registry's collapse check rejects it.
                    ZoneSignature = new List<MappingZoneSignature>
                    {
                        new() { Type = isFan ? 2 : 1, DefaultLeds = ledCount },
                    },
                },
            },
            Zones =
            {
                new MappingZone
                {
                    ZoneIndex = 0,
                    LedCount = ledCount,
                    AspectRatio = isFan ? 1f : Math.Max(1f, ledCount),
                    Leds = leds,
                },
            },
        };
    }

    // 5 decimals matches what the packed catalog carries, so a generated
    // artifact hashes the same way a catalogued one does.
    private static float Round(float value) => MathF.Round(value, 5);
}
