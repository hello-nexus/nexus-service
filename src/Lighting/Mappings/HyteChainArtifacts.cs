using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Mappings;

/// <summary>
/// The first-party ARGB accessories, authored here rather than carried in the
/// generated catalog: that file is derived from third-party plugin data, and
/// nothing in it describes our own products. Counts come from Nexus 2's
/// LEDCountMapForControlBox (the Smart Hub LED Layout tab), the only source
/// validated against these products on hardware.
///
/// The Smart Hub's ports and a motherboard ARGB header are the same problem -
/// the wire cannot say what is plugged into it - so both pickers read this one
/// list and a chain built on either surface means the same thing.
/// </summary>
public static class HyteChainArtifacts
{
    /// <summary>A fixed-count accessory: the product decides its LED count, the user only picks it.</summary>
    public sealed class Product
    {
        public Product(string key, string name, int ledCount, int fanCount, string type)
        {
            Key = key;
            Name = name;
            LedCount = ledCount;
            FanCount = fanCount;
            Type = type;
        }

        /// <summary>Virtual product key, in the same <c>product:</c> space as the catalogued rows.</summary>
        public string Key { get; }
        /// <summary>Product name. A proper noun - never localized.</summary>
        public string Name { get; }
        public int LedCount { get; }
        /// <summary>Fans the accessory is; 0 lays it out as a plain strip.</summary>
        public int FanCount { get; }
        /// <summary>Picker category, matching the catalog's vocabulary.</summary>
        public string Type { get; }
    }

    public const string Brand = "HYTE";

    public static readonly IReadOnlyList<Product> All = new[]
    {
        new Product("product:hyte-fr12", "FR12", 33, 1, "Fan"),
        // 68 across 3 fans does not divide; the rings split it 23/23/22.
        new Product("product:hyte-fr12-trio", "FR12 Trio", 68, 3, "Fan"),
        new Product("product:hyte-ln80", "LN80", 45, 0, "AIO"),
        new Product("product:hyte-y50-solo", "Y50 Solo Fan", 8, 1, "Fan"),
        new Product("product:hyte-y50-trio", "Y50 Trio Fan", 24, 3, "Fan"),
    };

    /// <summary>Ring radius in UV space, matching <see cref="GenericChainArtifacts"/>'s single fan.</summary>
    private const float RingRadius = 0.42f;

    public static Product? Find(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        foreach (var p in All)
        {
            if (string.Equals(p.Key, key, StringComparison.Ordinal))
                return p;
        }
        return null;
    }

    /// <summary>The artifact for a first-party key, or null when the key is not one of ours.</summary>
    public static MappingArtifact? Build(string key)
    {
        var product = Find(key);
        if (product is null)
            return null;

        var isFan = product.FanCount > 0;
        var leds = new List<MappingLed>(product.LedCount);
        if (isFan)
        {
            // Fans side by side along u, each its own ring. LEDs split as
            // evenly as the count allows, remainder to the earlier fans.
            var baseCount = product.LedCount / product.FanCount;
            var remainder = product.LedCount % product.FanCount;
            var i = 0;
            for (int f = 0; f < product.FanCount; f++)
            {
                var n = baseCount + (f < remainder ? 1 : 0);
                var centerU = (f + 0.5f) / product.FanCount;
                for (int k = 0; k < n; k++)
                {
                    // From 12 o'clock, clockwise. The products do not document
                    // where index 0 sits; a wrong guess is correctable in the
                    // LED map editor, and the count is what has to be right.
                    var angle = -MathF.PI / 2 + 2 * MathF.PI * k / n;
                    var u = centerU + (RingRadius / product.FanCount) * MathF.Cos(angle);
                    var v = 0.5f + RingRadius * MathF.Sin(angle);
                    leds.Add(new MappingLed { I = i, U = Round(u), V = Round(v) });
                    i++;
                }
            }
        }
        else
        {
            for (int i = 0; i < product.LedCount; i++)
            {
                leds.Add(new MappingLed
                {
                    I = i,
                    U = Round((i + 0.5f) / product.LedCount),
                    V = 0.5f,
                });
            }
        }

        return new MappingArtifact
        {
            SchemaVersion = MappingSchema.Version,
            Name = product.Name,
            Description = isFan
                ? $"{Brand} {product.Name}: {product.LedCount} LEDs across {product.FanCount} fan(s)."
                : $"{Brand} {product.Name}: {product.LedCount} LEDs.",
            Device = new MappingDeviceInfo
            {
                Key = product.Key,
                Match = new MappingDeviceMatch
                {
                    NameHint = $"{Brand} {product.Name}",
                    VendorHint = Brand,
                    ZoneSignature = new List<MappingZoneSignature>
                    {
                        // A single-fan ring is a plane; a strip and a
                        // side-by-side trio are declared linear so the
                        // registry's collapse check does not reject them.
                        new() { Type = isFan && product.FanCount == 1 ? 2 : 1, DefaultLeds = product.LedCount },
                    },
                },
            },
            Zones =
            {
                new MappingZone
                {
                    ZoneIndex = 0,
                    LedCount = product.LedCount,
                    AspectRatio = isFan ? product.FanCount : Math.Max(1f, product.LedCount),
                    Leds = leds,
                },
            },
        };
    }

    // 5 decimals matches the packed catalog, so an authored artifact hashes
    // the same way a catalogued one does.
    private static float Round(float value) => MathF.Round(value, 5);
}
