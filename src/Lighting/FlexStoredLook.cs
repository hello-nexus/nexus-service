using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>Turns a unit's stored-effect settings into the package the receiver plays.</summary>
public static class FlexStoredLook
{
    public sealed class Package
    {
        public required byte[] Compressed { get; init; }
        public required int Frames { get; init; }
        public required int Leds { get; init; }
        public required double IntervalMs { get; init; }
    }

    public static IReadOnlyList<RgbColor> ColorsOf(FlexFamily family, string key, IReadOnlyList<string> hex)
    {
        if (hex.Count == 0)
        {
            return FlexDefaultPalettes.For(family, key);
        }
        var list = new List<RgbColor>(hex.Count);
        foreach (var h in hex)
        {
            var s = h.TrimStart('#');
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v));
            }
        }
        return list.Count == 0 ? FlexDefaultPalettes.For(family, key) : list;
    }

    public static Dictionary<string, FlexZoneLook> ZoneLooks(FlexFamily family, LianLiFlexUnitSettings look)
    {
        var result = new Dictionary<string, FlexZoneLook>(StringComparer.Ordinal);
        if (look.Mode == LianLiFlexUnitSettings.ModePerZone)
        {
            var zones = FlexLightingDeviceProvider.ZonesOf(family);
            for (var i = 0; i < zones.Count; i++)
            {
                var z = i < look.Zones.Count ? look.Zones[i] : new LianLiFlexZoneSettings();
                result[zones[i]] = new FlexZoneLook(z.Mode, Math.Clamp(z.Speed, 0, 4), z.Direction != 0 ? 1 : 0,
                    Math.Clamp(z.Brightness, 0, 4), ColorsOf(family, z.Mode, z.Colors));
            }
        }
        else
        {
            result["all"] = new FlexZoneLook(look.Mode, Math.Clamp(look.Speed, 0, 4), look.Direction != 0 ? 1 : 0,
                Math.Clamp(look.Brightness, 0, 4), ColorsOf(family, look.Mode, look.Colors));
        }
        return result;
    }

    /// <summary>Everything that changes the uploaded bytes, as one comparable string.</summary>
    public static string Signature(FlexUnit unit, LianLiFlexUnitSettings look)
    {
        var sb = new StringBuilder();
        sb.Append(unit.Family).Append('|').Append(unit.Fans).Append('|').Append(look.UpDownSwap).Append('|').Append(look.Merge)
            .Append('|').Append(look.SortIndex);
        foreach (var (zone, z) in ZoneLooks(unit.Family, look))
        {
            sb.Append('|').Append(zone).Append(':').Append(z.Key).Append(':').Append(z.Speed).Append(':').Append(z.Direction)
                .Append(':').Append(z.Brightness);
            foreach (var c in z.Colors)
            {
                sb.Append(':').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
            }
        }
        return sb.ToString();
    }

    public static Package? Build(FlexUnit unit, LianLiFlexUnitSettings look)
    {
        try
        {
            var anim = FlexAnimationComposer.Compose(unit.Family, unit.Fans, ZoneLooks(unit.Family, look), look.UpDownSwap);
            return new Package
            {
                Compressed = TinyUz.Compress(anim.Raw),
                Frames = anim.Frames,
                Leds = anim.Leds,
                IntervalMs = anim.IntervalMs,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[lianli-flex] package build failed for {unit.Mac}: {ex.Message}");
            return null;
        }
    }

    public static LianLiFlexUnitSettings WithPackage(LianLiFlexUnitSettings u, string signature, string effectIndex) => new()
    {
        Mode = u.Mode,
        EffectMode = u.EffectMode,
        Speed = u.Speed,
        Direction = u.Direction,
        Brightness = u.Brightness,
        Colors = u.Colors,
        Merge = u.Merge,
        UpDownSwap = u.UpDownSwap,
        SortIndex = u.SortIndex,
        Zones = u.Zones,
        PackageSignature = signature,
        PackageEffectIndex = effectIndex,
    };
}
